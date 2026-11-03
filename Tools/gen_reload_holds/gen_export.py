# Filters the generated holds, adds each magazine type's length (for borrowing a hold) and writes the file the mod loads.
import json, math, os, re, sys
import numpy as np, UnityPy
ITEMS='F:/SPT4.1/SPT_Runtime/SPT_Data/database/templates/items.json'
BUNDLES='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/'
gen=json.load(open('gen_all.json')); R=gen['result']
items=json.load(open(ITEMS,encoding='utf-8')); guns=json.load(open('guns.json')); census=json.load(open('clip_census.json'))
famFolder={}
for folder,v in census.items():
    for c in v.get('clips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',c)
        if m: famFolder[m.group(1).lower()]=folder
lengthCache={}
def magLength(templateId):
    if templateId in lengthCache: return lengthCache[templateId]
    length=0.0
    try:
        env=UnityPy.load(BUNDLES+items[templateId]['_props']['Prefab']['path'])
        best=0
        for o in env.objects:
            if o.type.name=='Mesh':
                e=o.read().m_LocalAABB.m_Extent; s=sorted([e.x*2,e.y*2,e.z*2]); v=s[0]*s[1]*s[2]
                if v>best: best=v; length=s[2]
    except Exception: pass
    lengthCache[templateId]=length
    return length
def lengthFor(family,typ):
    folder=famFolder.get(family)
    for g in guns:
        if g['folder']!=folder: continue
        slot=[s for s in items[g['id']]['_props'].get('Slots',[]) if s['_name']=='mod_magazine']
        if not slot: continue
        for i in slot[0]['_props']['filters'][0]['Filter']:
            if i in items and items[i]['_props'].get('magAnimationIndex')==typ:
                l=magLength(i)
                if l>0: return round(float(l),3)
    return 0.0
out=dict(grips={},handles={},gunHandHandles={},handleAlternates=R['handleAlternates'],gunHandHandleAlternates=R['gunHandHandleAlternates'],handleMovesWithBolt=sorted(set(R['handleMovesWithBolt'])),handleNotches=R['handleNotches'],thumbCatches=R['thumbCatches'],wellAxes={},slides={},rockIn={},palms={}); dropped=[]
for k,v in sorted(R['grips'].items()):
    family,typ=k.split('|'); e={}
    for half,h in v.items():
        if h['palmOff']>0.03 or h['frames']<4:
            dropped.append((k,half,h['palmOff'],h['frames'])); continue
        e[half]=dict(position=h['position'],rotation=h['rotation'],fingers=h['fingers'],magazineLength=lengthFor(family,int(typ)))
    if e: out['grips'][k]=e
for f,h in sorted(R['handles'].items()):
    if h['palmOff']>0.03:
        dropped.append((f,'handle',h['palmOff'])); continue
    out['handles'][f]=dict(position=h['position'],rotation=h['rotation'],fingers=h['fingers'],travel=round(h['travel']*0.08/0.101,3))
for f,h in sorted(R['gunHandHandles'].items()):
    out['gunHandHandles'][f]=dict(position=h['position'],rotation=h['rotation'],fingers=h['fingers'],travel=round(h['travel']*0.08/0.101,3))
for f,h in sorted(R['rockIn'].items()):
    out['rockIn'][f]=dict(axis=h['axis'],angle=h['angle'],thumb=h['thumb'])
for f,h in sorted(R['palms'].items()):
    out['palms'][f]=dict(newMagazine=h['newMagazine'],oldMagazine=h['oldMagazine'],fingers=h['fingers'],thumb=h.get('thumb'))
# Pistols only for now: other guns with no handle keep Tarkov's chambering
pistolFolders={g['folder'] for g in guns if items[g['id']]['_props'].get('weapClass')=='pistol'}
for f,axis in sorted(R['wellAxes'].items()):
    if famFolder.get(f) in pistolFolders: out['wellAxes'][f]=axis
for f,h in sorted(R['slides'].items()):
    if famFolder.get(f) in pistolFolders:
        out['slides'][f]=dict(bone=h['bone'],position=h['position'],rotation=h['rotation'],fingers=h['fingers'],travel=h['travel'])
# The other grips on each pistol's slide: same place against the back and top of that slide, same turn of the hand
def conj4(q): return np.array([-q[0],-q[1],-q[2],q[3]])
def mul4(a,b):
    x1,y1,z1,w1=a;x2,y2,z2,w2=b
    return np.array([w1*x2+x1*w2+y1*z2-z1*y2,w1*y2-x1*z2+y1*w2+z1*x2,w1*z2+x1*y2-y1*x2+z1*w2,w1*w2-x1*x2-y1*y2-z1*z2])
def turn(q,v):
    u=np.array(q[:3]); return v+2*np.cross(u,np.cross(u,v)+q[3]*v)
def eulerOf(q):
    x,y,z,w=q
    sinX=2*(w*x-y*z)
    ex=math.degrees(math.asin(max(-1.0,min(1.0,sinX))))
    if abs(sinX)<0.9999:
        ey=math.degrees(math.atan2(2*(w*y+x*z),1-2*(x*x+y*y))); ez=math.degrees(math.atan2(2*(w*z+x*y),1-2*(x*x+z*z)))
    else:
        ey=math.degrees(math.atan2(-2*(x*z-w*y),1-2*(y*y+z*z))); ez=0.0
    return [round(ex%360,2),round(ey%360,2),round(ez%360,2)]
# Many guns' meshes live in the bundles their prefab depends on (Windows.json lists them)
bundleManifest=json.load(open(BUNDLES+'Windows.json',encoding='utf-8'))
def loadWithMeshes(key):
    dependencies=[d for d in bundleManifest.get(key,{}).get('Dependencies',[]) if d.endswith('.bundle') and not re.search('audio|animations|textures|physics|hands',d) and os.path.exists(BUNDLES+d)]
    return UnityPy.load(BUNDLES+key,*[BUNDLES+d for d in dependencies])
def meshBox(env,underBone=None,nameStart=None):
    best=None
    for o in env.objects:
        if o.type.name!='MeshFilter': continue
        filterObject=o.read()
        try: mesh=filterObject.m_Mesh.read(); go=filterObject.m_GameObject.read()
        except Exception: continue
        if nameStart is not None and not go.m_Name.startswith(nameStart): continue
        center=np.array([mesh.m_LocalAABB.m_Center.x,mesh.m_LocalAABB.m_Center.y,mesh.m_LocalAABB.m_Center.z]); extent=np.array([mesh.m_LocalAABB.m_Extent.x,mesh.m_LocalAABB.m_Extent.y,mesh.m_LocalAABB.m_Extent.z])
        if underBone is not None:
            # Through the transforms up to the bone
            t=go.m_Transform.read(); chain=[]; found=False
            while True:
                name=t.m_GameObject.read().m_Name
                if name==underBone:
                    found=True; break
                chain.append(t)
                if not t.m_Father.path_id: break
                t=t.m_Father.read()
            if not found: continue
            corners=[center+extent*np.array(s) for s in ((1,1,1),(1,1,-1),(1,-1,1),(1,-1,-1),(-1,1,1),(-1,1,-1),(-1,-1,1),(-1,-1,-1))]
            for link in chain:
                q=np.array([link.m_LocalRotation.x,link.m_LocalRotation.y,link.m_LocalRotation.z,link.m_LocalRotation.w]); p=np.array([link.m_LocalPosition.x,link.m_LocalPosition.y,link.m_LocalPosition.z])
                corners=[turn(q,c)+p for c in corners]
            low=np.min(corners,axis=0); high=np.max(corners,axis=0); center=(low+high)/2; extent=(high-low)/2
        size=float(np.prod(extent))
        if best is None or size>best[0]: best=(size,center,extent)
    return best
def slideBack(family,boneName):
    folder=famFolder.get(family)
    gun=next((g for g in guns if g['folder']==folder),None)
    slot=[s for s in items[gun['id']]['_props'].get('Slots',[]) if s['_name']=='mod_reciever'] if gun else []
    box=None
    if slot:
        # A fitted slide's mesh comes out in the bone's frame (the mesh's -90 and its ModPlacer's +90 cancel)
        for i in slot[0]['_props']['filters'][0]['Filter']:
            if i in items:
                # Its own: the bundles it depends on bring the gun's frame along too
                try: box=meshBox(loadWithMeshes(items[i]['_props']['Prefab']['path']),nameStart=items[i]['_name'])
                except Exception: box=None
                if box: break
    if box is None and gun:
        d=ROOT_WEAPONS+folder+'/'
        for c in sorted(os.listdir(d)):
            if c.endswith('_container.bundle') and c.startswith('weapon_'):
                box=meshBox(loadWithMeshes('assets/content/weapons/'+folder+'/'+c),boneName)
                if box: break
    if box is None: return None
    return box[1]+box[2]   # x: the side of no use, y: its back, z: its top
ROOT_WEAPONS='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/weapons/'
backs={f:slideBack(f,h['bone']) for f,h in R['slides'].items() if f in out['slides']}
sourceBacks={k:slideBack(v['family'],R['slides'][v['family']]['bone']) for k,v in R['slideGrips'].items()}
print('slide back and top, cm:',{f:(None if b is None else [round(float(b[1])*100,1),round(float(b[2])*100,1)]) for f,b in backs.items()})
for f in out['slides']:
    grips=[]
    for key,grip in sorted(R['slideGrips'].items()):
        if backs.get(f) is None or sourceBacks.get(key) is None: continue
        position=np.array(grip['position'],float); shift=backs[f]-sourceBacks[key]; shift[0]=0.0
        boneInGun=np.array(R['slideBoneInGun'][f],float)
        grips.append(dict(position=[round(float(x),4) for x in turn(conj4(boneInGun),position+shift)],rotation=eulerOf(mul4(conj4(boneInGun),np.array(grip['rotation'],float))),fingers=grip['fingers'],travel=0))
    if grips: out['handleAlternates'][f]=grips
# The off hand on an AR-style T handle the gun's own clips never show it on: the Spear's racks its side handle (fingertips 7 cm and more
# off the T, headset: "locks to a position but it's not correct"), the Mk47's only with the right. Their handle bone sits in the gun like
# the M4's (0 deg apart) and the M4's fingertips land on either T as on its own (thumb 0.3-0.8 cm, index 0), so the M4's grip, moved
# so the two handles' back ends line up. The travel stays the gun's own.
T_HANDLE_FROM_M4=('spear','mk47')
def handleBack(family):
    folder=famFolder.get(family)
    gun=next((g for g in guns if g['folder']==folder),None)
    slot=[s for s in items[gun['id']]['_props'].get('Slots',[]) if s['_name']=='mod_charge'] if gun else []
    for i in (slot[0]['_props']['filters'][0]['Filter'] if slot else []):
        if i in items:
            try: box=meshBox(UnityPy.load(BUNDLES+items[i]['_props']['Prefab']['path']))
            except Exception: box=None
            if box: return float(box[1][1]+box[2][1])
    return None
m4Back=handleBack('m4a1'); m4Grip=out['handles']['m4a1']
for f in T_HANDLE_FROM_M4:
    back=handleBack(f)
    if back is None or m4Back is None: continue
    own=R['handles'].get(f) or R['gunHandHandles'].get(f)
    position=list(m4Grip['position']); position[1]=round(position[1]+back-m4Back,4)
    out['handles'][f]=dict(position=position,rotation=m4Grip['rotation'],fingers=m4Grip['fingers'],travel=round(own['travel']*0.08/0.101,3) if own else m4Grip['travel'])
print('T handle grips from the M4:',{f:out['handles'][f] for f in T_HANDLE_FROM_M4 if f in out['handles']})
# Rocked in like an AK though the gun's own clips only tilt the magazine 9 deg (headset, Mk47: "mag should tilt in like an AK"): it takes
# AK magazines, and its seated magazine bone sits in the gun like the AKM's (both square to the gun), so the AKM's rock carries over as is
ROCK_IN_FROM={'mk47':'akm'}
for f,source in ROCK_IN_FROM.items():
    if source in out['rockIn']: out['rockIn'][f]=out['rockIn'][source]
# gen_extras.py: bolts the handle moves itself, and side handles. A side handle's travel is scaled like the others, and its slider's lag with it.
extras=json.load(open('extras.json')) if os.path.exists('extras.json') else dict(boltBones={},sideHandles={})
out['boltBones']=extras['boltBones']
out['rockIn'].update(extras.get('rockIn',{}))
out['sideHandles']={f:dict({k:v for k,v in h.items() if k!='clip'},travel=round(h['travel']*0.08/0.101,3),sliderLag=round(h['sliderLag']*0.08/0.101,3)) for f,h in extras['sideHandles'].items()}
print('bolts the handle moves:',out['boltBones'],'side handles:',{f:(h['travel'],h['sliderLag']) for f,h in out['sideHandles'].items()})
# Guns in one folder share a rig and a handle: AK-101 and 103 have their magazine clips but rack with the AK-100's
borrowed=[]
for holds in (out['handles'],out['gunHandHandles'],out['handleAlternates'],out['gunHandHandleAlternates'],out['rockIn'],out['palms'],out['slides'],out['wellAxes']):
    for family,folder in sorted(famFolder.items()):
        mates=[f for f in sorted(holds) if famFolder.get(f)==folder]
        if family not in holds and mates:
            holds[family]=holds[mates[0]]; borrowed.append((family,mates[0]))
print('handle holds borrowed in a folder',borrowed)
os.makedirs('out',exist_ok=True)
json.dump(out,open('out/ReloadHolds.json','w'),separators=(',',':'))
print('grips',len(out['grips']),'handles',len(out['handles']),'gun hand handles',len(out['gunHandHandles']),'rock-in',len(out['rockIn']),'palms',len(out['palms']),'dropped',len(dropped),'bytes',os.path.getsize('out/ReloadHolds.json'))
print('dropped:',dropped[:14])
known=sum(1 for v in out['grips'].values() for h in v.values() if h['magazineLength']>0); total=sum(len(v) for v in out['grips'].values())
print('holds with a known magazine length',known,'of',total)
print('families',len(set(k.split('|')[0] for k in out['grips'])))
