# Handle data gen_holds.py doesn't read. Run after it (needs its gen_all.json), before gen_export.py, which merges extras.json in.
#  - boltBones: guns with no locked-back pose (no *catch_on clip), so blending that pose in can't open their bolt as the handle comes back
#    (headset, Mk47: "the bolt doesn't move"): the rig bones the gun's own rack clip moves back with the handle, and how far per metre
#    of handle. Not where the handle rides the carrier (AKs): moving the carrier already moves the bolt.
#  - sideHandles: a second handle that isn't mod_charge, on a part of the gun (the Spear's folding side handle on its upper receiver):
#    the knob folds out, the base slides back under the hand, the slider it runs in follows once the base has come sliderLag back.
#    The grip is the palm in the base's frame at full pull.
#  - rockIn: guns whose magazine tilts out less than gen_holds' 15 deg but still tilts (AK-50: 9.9 deg about the AKs' axis, then away
#    down; headset: its straight track ran toward the stock)
#  - handles / gunHandHandles / handleBones: guns whose handle bone has another name than mod_charge (MP7)
import sys, json, re, os, zlib
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])
gen=json.load(open('gen_all.json')); R=gen['result']; census=json.load(open('clip_census.json'))
famFolder={}
for folder,v in census.items():
    for c in v.get('clips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',magClipName(folder,c))
        if m: famFolder[m.group(1).lower()]=folder
ITEM_BUNDLES='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/items/mods/'

def loadFolder(folder):
    d=ROOT+folder+'/'; clips={}
    for o in UnityPy.load(d+'client_assets.bundle').objects:
        if o.type.name=='AnimationClip':
            try:
                c=o.read(); clips[c.m_Name]=c
            except Exception: pass
    for name in list(clips): clips.setdefault(magClipName(folder,name),clips[name])
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

# Guns with no slot for a handle whose rig bone isn't mod_charge, the only name gen_holds looks for: by rig (a substring of its
# container), the bone each one's own rack clips move back, every animation set in that folder (checked 2026-11-05: each bone moves
# 6-13 cm straight back and carries the handle's meshes). MP7 7.2 cm, SA58 11.1, UMP 8.1, Vector 6.2 (its knob's fold not shown),
# PKM 13.3, RFB 12.5 (its forward handle), Uzi 9.3, Mk-18 Mjolnir 13.2, RSASS 12.3, SR-2M (its magazine clips spelt reload_in/out,
# clip_names), AA-12 14.5, MDR 13.0, M1A 12.0 (the op rod's handle). Second scan (the first one's printout was cut off): knobs riding
# the bolt or carrier that moves (9A-91 10.2, ASh-12 16.9, G36 10.9: HANDLE_CARRIERS, the part the runtime moves), and bolts that are
# the handle themselves, with no knob of their own (PP-91 8.8, PPSh 8.3, Uzi Pro 5.3, VPO-101 12.1: the back of the bolt is inside
# the receiver, so the grab zone is where the clip's hand is, HANDLE_GRAB_AT_HOLD). AVT-40 / SVT-40 (2026-11-07, both rigs one
# animation set avt40): the carrier itself, 10 cm straight back in avt40_charge_* (left hand over the top) and rechamber (right hand),
# its knob part of the carrier mesh (HANDLE_KNOB_SIDE). Left out: the RPD (no clip moves its handle
# bones), the M60 (no rack clip), the VSK-94 (no clips of its own), the AGS-30 and NSV (mounted).
# gen_holds' own left/right hold search on that bone, merged into its handles by gen_export; handleBones tells the runtime the bone.
HANDLE_BONE_RIGS={'hk_mp7':'weapon_charghing_handle','dsa_sa58':'weapon_charghing_handle','hk_ump':'weapon_charge','tdi_kriss_vector':'weapon_charge',
                  'zid_pkm':'weapon_charge','kel_tec_rfb':'weapon_charge','iwi_uzi_9x19':'weapon_charge','sword_int_mk_18':'weapon_charge','r11_rsass':'weapon_charghing_000',
                  'tochmash_sr2m':'weapon_charging_handle','mps_aa_12':'weapon_charge','dt_mdr':'weapon_charge_000','springfield_m1a':'weapon_bolthandle',
                  'kbp_9a91':'weapon_bolt_charge','ckib_ash_12':'weapon_charging_handle','hk_g36':'weapon_charge_handle',
                  'zmz_pp-9':'weapon_bolt','zis_ppsh41':'weapon_bolt','iwi_uzi_pro':'weapon_bolt','vepr_hunter_vpo-101':'weapon_bolt',
                  'toz_avt_40':'weapon_bolt_carrier','toz_svt_40':'weapon_bolt_carrier'}
HANDLE_CARRIERS={'kbp_9a91':'weapon_bolt','ckib_ash_12':'weapon_bolt','hk_g36':'weapon_bolt_carrier'}
# (the 9A-91's and ASh-12's knob bones carry a mesh named bolt_rotation, not a handle's: their zone where the clip's hand is too)
# The MDR's handle part is a rod running back into the gun with the knob at its front: the back of it is nowhere near the knob
# (headset: "the collision position is not correct for when you grab it", the grab pose itself right)
HANDLE_GRAB_AT_HOLD=('zmz_pp-9','zis_ppsh41','iwi_uzi_pro','vepr_hunter_vpo-101','kbp_9a91','ckib_ash_12','dt_mdr')
# A knob sticking out of the carrier where the clip's palm bone is far off it (AVT/SVT: the left hand reaches over the top, its palm
# 15 cm from the knob; the fingertips on it): the grab zone on the knob itself, the centre of the bone's mesh vertices within 1 cm of
# its furthest point to that side, in the bone's frame. (axis, sign): the AVT/SVT knob is the carrier's +x, (4.5,-4.6,-1.5) cm.
HANDLE_KNOB_SIDE={'toz_avt_40':(0,1),'toz_svt_40':(0,1)}
# The gun hand's hold from Tarkov's own right-hand rack where ridingHold turns it down: the AVT/SVT's charge_0/1 and rechamber rack with
# the RIGHT hand (index 2-3 cm off the knob all the way back; charge_2 is the left-hand one), 10 cm in 4 frames with the palm pivoting
# ~2 cm about the fingers - over the 1.5 cm limit, so it fell to jam_hardslide (8.1 cm, a stuck slide). Headset 2026-11-07, a picture of
# the SVT's rack: that pose. Read at full pull (fullBackTime: the middle of the frames within 2 mm of all the way back).
GUN_HAND_HOLD_CLIP={'toz_avt_40':'avt40_charge_1','toz_svt_40':'avt40_charge_1'}
def holdAtFullBack(g,charge,wp,side):
    P=g['prefabLocal']; palm=bone(P,f'/Base Human{side}Palm'); t,top=fullBackTime(g,charge,wp); c={}
    cp,cr,_=g['worldAt'](charge,t,c); pp,pr,_=g['worldAt'](palm,t,c)
    qs=[np.array(g['localAt'](j,t)[1],float) for j in fingerPaths(P,side)]
    return dict(position=[round(float(x),4) for x in g['rotate'](conj(cr),pp-cp)],rotation=toUnityEuler(qmul(conj(cr),pr)),fingers=eulerList(qs),
                travel=round(float(top),3),t=t,palmOff=0.0)
def meshKnobPoint(folder,container,boneLeaf,axis,sign,shell=0.01):
    from UnityPy.helpers.MeshHelper import MeshHandler
    env=UnityPy.load(ROOT+folder+'/'+container,ROOT+folder+'/client_assets.bundle')
    def toParent(t,p):
        s=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition; q=np.array([r.x,r.y,r.z,r.w]); v=p*np.array([s.x,s.y,s.z])
        return v+2*np.cross(q[:3],np.cross(q[:3],v)+q[3]*v)+np.array([a.x,a.y,a.z])
    # The rig can hold the bone more than once: the copy with the most vertices (LOD0)
    best=[]
    for o in env.objects:
        if o.type.name!='Transform': continue
        t=o.read()
        if t.m_GameObject.read().m_Name!=boneLeaf: continue
        points=[]
        def walk(tr,chain):
            for c in tr.m_GameObject.read().m_Components:
                ptr=c.component if hasattr(c,'component') else c
                try: comp=ptr.read()
                except Exception: continue
                if type(comp).__name__!='MeshFilter': continue
                try:
                    h=MeshHandler(comp.m_Mesh.read()); h.process()
                except Exception: continue
                for p in np.array(h.m_Vertices,float).reshape(-1,3):
                    for link in chain: p=toParent(link,p)
                    points.append(p)
            for ch in tr.m_Children:
                c=ch.read(); walk(c,[c]+chain)
        walk(t,[])
        if len(points)>len(best): best=points
    if not best: return None
    v=np.array(best); side=v[:,axis]*sign
    return [round(float(x),4) for x in v[side>=side.max()-shell].mean(0)]
HANDLE_BONE_GUNS={}
for folder,v in census.items():
    if not os.path.isdir(ROOT+folder): continue
    containers=[c for c in sorted(os.listdir(ROOT+folder)) if c.endswith('_container.bundle') and c.startswith('weapon_')]
    matching=[(c,b,k) for c in containers for k,b in HANDLE_BONE_RIGS.items() if k in c]
    for clip in v.get('clips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',magClipName(folder,clip))
        if matching and m:
            family=m.group(1).lower()
            HANDLE_BONE_GUNS[family]=(folder,)+next(((c,b,k) for c,b,k in matching if family in c),matching[0])
extraHandles={}; extraGunHandHandles={}; handleBones={}; handleCarriers={}; handleGrabPoints={}; extraMovesWithBolt=[]; namedTrees={}
for family,(folder,container,boneName,rig) in sorted(HANDLE_BONE_GUNS.items()):
    _,clips=loadFolder(folder)
    tree=loadTree(ROOT+folder+'/'+container); P=tree[0]; J=fingerPaths(P)
    charge=bone(P,'/'+boneName); wp=bone(P,'Weapon_root_anim/weapon')
    namedTrees[family]=(tree,clips,charge)
    prefix=next(n for n in clips if n.lower().startswith(family+'_mag_in')).split('_mag_in')[0]
    names=[n for n in clips if n.startswith(prefix+'_')]
    best=None
    for n in sorted(names):
        rank=rackClipRank(n[len(prefix)+1:])
        if rank is None: continue
        g=setup(tree,clips[n])
        if rank==0:
            h=handleHold(g,J,charge)
            if h is None: continue
            moved=leftHandTravel(g,charge)
            if moved<0.03: continue
        else:
            h=ridingHold(g,charge,'L')
            if h is None: continue
            moved=h['moved']
        if best is None or (rank,-moved)<best[0]:
            h['clip']=n; h['leftHandMoved']=round(moved,3); best=((rank,-moved),h)
    # Nothing rides it: a fast yank (the Uzi's charge_0, ~4 frames back) leaves the palm pivoting about the fingertips, which
    # ridingHold rejects though the hand does the pull (headset: "it pulls the handle on top with the left hand"). The left IK
    # marker, where the game puts that palm, as gen_holds reads a reload_charge clip: at full pull, with the marker travelling back.
    if best is None:
        for n in sorted(names):
            rank=rackClipRank(n[len(prefix)+1:])
            if rank is None: continue
            g=setup(tree,clips[n]); h=handleHold(g,J,charge)
            if h is None: continue
            moved=leftHandTravel(g,charge)
            if moved>=0.03 and (best is None or (rank,-moved)<best[0]):
                h['clip']=n; h['leftHandMoved']=round(moved,3); best=((rank,-moved),h)
    if best: extraHandles[family]=best[1]
    best=None
    for n in sorted(names):
        rank=rackClipRank(n[len(prefix)+1:])
        if rank is None: continue
        h=ridingHold(setup(tree,clips[n]),charge,'R')
        if h and (best is None or (rank,h['drift'])<best[0]):
            h['clip']=n; best=((rank,h['drift']),h)
    if best: extraGunHandHandles[family]=best[1]
    if rig in GUN_HAND_HOLD_CLIP:
        h=holdAtFullBack(setup(tree,clips[GUN_HAND_HOLD_CLIP[rig]]),charge,wp,'R'); h['clip']=GUN_HAND_HOLD_CLIP[rig]; extraGunHandHandles[family]=h
    # The locked-back pose moves the handle too (gen_holds' handleMovesWithBolt test)
    g=setup(tree,clips[next(n for n in names if '_mag_in' in n)]); c={}
    restAt,_,_=g['worldAt'](charge,0.0,c); gunAt,gunRotation,_=g['worldAt'](wp,0.0,c); restInGun=g['rotate'](conj(gunRotation),restAt-gunAt)
    for n in sorted(x for x in names if re.search(r'(catch_on|on_catch)$',x)):
        held=setup(tree,clips[n]); c2={}
        heldAt,_,_=held['worldAt'](charge,0.0,c2); gunHeld,gunHeldRotation,_=held['worldAt'](wp,0.0,c2)
        if np.linalg.norm(held['rotate'](conj(gunHeldRotation),heldAt-gunHeld)-restInGun)>=0.02: extraMovesWithBolt.append(family)
        break
    handleBones[family]=boneName
    if rig in HANDLE_CARRIERS: handleCarriers[family]=HANDLE_CARRIERS[rig]
    grabHold=extraHandles.get(family) or extraGunHandHandles.get(family)
    if rig in HANDLE_GRAB_AT_HOLD and grabHold: handleGrabPoints[family]=grabHold['position']
    if rig in HANDLE_KNOB_SIDE:
        knob=meshKnobPoint(folder,container,boneName,*HANDLE_KNOB_SIDE[rig])
        if knob: handleGrabPoints[family]=knob
    print(f'{family} handle bone {boneName}: left',{k:v for k,v in (extraHandles.get(family) or {}).items() if k!='fingers'},
          'right',{k:v for k,v in (extraGunHandHandles.get(family) or {}).items() if k!='fingers'},'moves with bolt',family in extraMovesWithBolt,flush=True)

NOT_BOLT=re.compile(r'Human|marker|Bend_Goal|Camera|IK|patron|shellport',re.I)
boltBones={}
for family in sorted(set(R['handles'])|set(R['gunHandHandles'])|set(extraHandles)|set(extraGunHandHandles)):
    folder=famFolder.get(family)
    if family in R['handleMovesWithBolt'] or family in extraMovesWithBolt or folder is None: continue
    if any(n.lower().startswith(family+'_') and re.search(r'(catch_on|on_catch)$',n) for n in census[folder]['clips']): continue
    hold=R['handles'].get(family) or R['gunHandHandles'].get(family) or extraHandles.get(family) or extraGunHandHandles.get(family)
    if family in namedTrees: tree,clips,_=namedTrees[family]
    else: tree,clips=loadFolder(folder)
    if tree is None or hold['clip'] not in clips: continue
    P,parentOf=tree[0],tree[1]; ch=namedTrees[family][2] if family in namedTrees else bone(P,'/mod_charge'); wp=bone(P,'Weapon_root_anim/weapon')
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
json.dump(dict(boltBones=boltBones,sideHandles=sideHandles,rockIn=rockIns,handles=extraHandles,gunHandHandles=extraGunHandHandles,
               handleBones=handleBones,handleCarriers=handleCarriers,handleGrabPoints=handleGrabPoints,handleMovesWithBolt=extraMovesWithBolt),open('extras.json','w'))
