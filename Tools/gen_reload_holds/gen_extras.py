# Handle data gen_holds.py doesn't read. Run after it (needs its gen_all.json), before gen_export.py, which merges extras.json in.
#  - boltBones: guns with no locked-back pose (no *catch_on clip), so blending that pose in can't open their bolt as the handle comes back
#    (headset, Mk47: "the bolt doesn't move"): the rig bones the gun's own rack clip moves back with the handle, and how far per metre
#    of handle. Not where the handle rides the carrier (AKs): moving the carrier already moves the bolt.
#  - sideHandles: a second handle that isn't mod_charge, on a part of the gun (the Spear's folding side handle on its upper receiver):
#    the knob folds out, the base slides back under the hand, the slider it runs in follows once the base has come sliderLag back.
#    The grip is the palm in the base's frame at full pull.
#  - rockIn: guns whose magazine tilts out less than gen_holds' 15 deg but still tilts (AK-50: 9.9 deg about the AKs' axis, then away
#    down; headset: its straight track ran toward the stock)
import sys, json, re, os, zlib
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])
gen=json.load(open('gen_all.json')); R=gen['result']; census=json.load(open('clip_census.json'))
famFolder={}
for folder,v in census.items():
    for c in v.get('clips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',c)
        if m: famFolder[m.group(1).lower()]=folder
ITEM_BUNDLES='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/items/mods/'

def loadFolder(folder):
    d=ROOT+folder+'/'; clips={}
    for o in UnityPy.load(d+'client_assets.bundle').objects:
        if o.type.name=='AnimationClip':
            try:
                c=o.read(); clips[c.m_Name]=c
            except Exception: pass
    for c in sorted(os.listdir(d)):
        if c.endswith('_container.bundle') and c.startswith('weapon_'):
            tree=loadTree(d+c)
            if bone(tree[0],'/mod_charge') and bone(tree[0],'/Base HumanLPalm'): return tree,clips
    return None,clips

def fullBackTime(g,path,wp):
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rest=None; rows=[]
    for i in range(int(stop*30)+1):
        t=i/30; c={}; p,_,_=g['worldAt'](path,t,c); wpp,wr,_=g['worldAt'](wp,t,c); inGun=g['rotate'](conj(wr),p-wpp)
        if rest is None: rest=inGun
        rows.append((t,float(inGun[1]-rest[1])))
    top=max(r[1] for r in rows); plateau=[r[0] for r in rows if r[1]>=top-0.002]
    return plateau[len(plateau)//2],top

NOT_BOLT=re.compile(r'Human|marker|Bend_Goal|Camera|IK|patron|shellport',re.I)
boltBones={}
for family in sorted(set(R['handles'])|set(R['gunHandHandles'])):
    folder=famFolder.get(family)
    if family in R['handleMovesWithBolt'] or folder is None: continue
    if any(n.lower().startswith(family+'_') and re.search(r'(catch_on|on_catch)$',n) for n in census[folder]['clips']): continue
    hold=R['handles'].get(family) or R['gunHandHandles'].get(family)
    tree,clips=loadFolder(folder)
    if tree is None or hold['clip'] not in clips: continue
    P,parentOf=tree[0],tree[1]; ch=bone(P,'/mod_charge'); wp=bone(P,'Weapon_root_anim/weapon')
    g=setup(tree,clips[hold['clip']]); t,top=fullBackTime(g,ch,wp)
    def inGun(path,time):
        c={}; p,_,_=g['worldAt'](path,time,c); wpp,wr,_=g['worldAt'](wp,time,c); return g['rotate'](conj(wr),p-wpp)
    moved={}
    for p in P:
        if not p.startswith(wp+'/') or p==ch or p.startswith(ch+'/') or NOT_BOLT.search(p.split('/')[-1]): continue
        move=inGun(p,t)-inGun(p,0.0); lateral=float(np.linalg.norm([move[0],move[2]]))
        if move[1]>=0.3*top and lateral<=0.25*move[1]: moved[p]=float(move[1])
    # The handle rides a part that moves: that's the carrier, the runtime moves it already
    if parentOf[ch] in moved: continue
    top_most=[p for p in moved if parentOf[p] not in moved]
    if top_most:
        boltBones[family]=[dict(bone=p.split('/')[-1],ratio=round(moved[p]/top,3)) for p in sorted(top_most)]
        print(f'{family}: {hold["clip"]} handle back {top*100:.1f} cm ->',boltBones[family],flush=True)

SIDE_HANDLE_GUNS={'spear':dict(receiver='recievers/reciever_mcx_sig_spear_upper',clip='spear_reload_charge_1',side='L',slider='weapon_charge',base='weapon_charge_base',knob='weapon_charge_handle')}
sideHandles={}
for family,spec in SIDE_HANDLE_GUNS.items():
    tree,clips=loadFolder(famFolder[family]); P,parentOf,_=tree
    # The receiver's own parts, put on its slot the way the clip's bindings name them
    receiverName=spec['receiver'].split('/')[-1]
    up,upParent,_=loadTree(ITEM_BUNDLES+spec['receiver']+'.bundle')
    rec=bone(P,'/mod_reciever'); root=rec+'/'+receiverName
    P[root]=(np.zeros(3),np.array([0,0,0,1.0]),np.ones(3)); parentOf[root]=rec
    for p,v in up.items():
        P[root+'/'+p]=v; parentOf[root+'/'+p]=root+('/'+upParent[p] if upParent[p] else '')
    tree=(P,parentOf,{zlib.crc32(p.encode())&0xffffffff:p for p in P})
    g=setup(tree,clips[spec['clip']]); wp=bone(P,'Weapon_root_anim/weapon')
    slider=bone(P,'/'+spec['slider']); base=bone(P,'/'+spec['base']); knob=bone(P,'/'+spec['knob'])
    t,top=fullBackTime(g,base,wp)
    # Each in the gun's frame at its own moment: the gun moves through the clip
    def sliderInGun(time):
        c={}; p,_,_=g['worldAt'](slider,time,c); wpp,wr,_=g['worldAt'](wp,time,c); return g['rotate'](conj(wr),p-wpp)
    sliderBack=float(sliderInGun(t)[1]-sliderInGun(0.0)[1]); c={}
    q0=np.array(g['localAt'](knob,0.0)[1],float); q1=np.array(g['localAt'](knob,t)[1],float); fold=qmul(conj(q0),q1)
    if fold[3]<0: fold=-fold
    bp,br,_=g['worldAt'](base,t,c); pp,pr,_=g['worldAt'](bone(P,f'/Base Human{spec["side"]}Palm'),t,c)
    sideHandles[family]=dict(sliderBone=spec['slider'],baseBone=spec['base'],knobBone=spec['knob'],travel=round(top,3),sliderLag=round(top-sliderBack,3),
        foldAxis=[round(float(x),4) for x in fold[:3]/max(np.linalg.norm(fold[:3]),1e-9)],foldAngle=round(2*math.degrees(math.acos(min(1.0,float(fold[3])))),1),
        position=[round(float(x),4) for x in g['rotate'](conj(br),pp-bp)],rotation=toUnityEuler(qmul(conj(br),pr)),
        fingers=eulerList([np.array(g['localAt'](j,t)[1],float) for j in fingerPaths(P,spec['side'])]),clip=spec['clip'])
    print(f'{family} side handle:',{k:v for k,v in sideHandles[family].items() if k!='fingers'},flush=True)
LOW_ROCK_GUNS=('ak50',)
rockIns={}
for family in LOW_ROCK_GUNS:
    tree,clips=loadFolder(famFolder[family]); mag=bone(tree[0],'/mod_magazine')
    rocks=rockIn(tree,clips,family,mag,minAngle=5) if mag else None
    if rocks:
        rockIns[family]=dict(axis=rocks['axis'],angle=rocks['angle'],thumb=rocks['thumb'])
        print(f'{family} rock-in:',{k:v for k,v in rocks.items() if k!='thumb'},flush=True)
json.dump(dict(boltBones=boltBones,sideHandles=sideHandles,rockIn=rockIns),open('extras.json','w'))
