# Bolt-action rifles' bolts, from each gun's own after-shot bolt clip (<family>_fire_chambering_0, the Sako's rechamber_0, the TOZ-106's
# fire_reload_0): the bone
# that slides the bolt back and how far (in its parent's frame), the bone that turns the handle up and about which axis how far (in
# its own rest frame; the DVL-10's is one bone that does both), and the dominant hand's hold on the knob (ridingHold on the turning
# bone, the palm in its frame). The runtime turns then slides those bones by the hand, from their own rest pose at the grab.
# Writes bolts.json; gen_export.py merges it in. Run from this folder.
import sys, os, re, json, zlib
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])

from game_paths import BUNDLES as GAME_BUNDLES
CHARGES=GAME_BUNDLES+'assets/content/items/mods/charges/'
# folder, bolt clip, the bolt part in mod_charge when the gun's own rig has none (its bones then carry the part's names)
GUNS=[('mosin','mosin_fire_chambering_0',None),('mosin','mosin_inf_fire_chambering_0',None),('sv98','sv98_fire_chambering_0',None),
      ('m700','m700_fire_chambering_0',None),('t5000','t5000_fire_chambering_0',None),('dvl-10','dvl_10_fire_chambering_0',None),
      ('axmc','axmc_fire_chambering_0','charge_ax_accuracy_internation_axmc_86x70'),('sako','sako_rechamber_0','charge_trg_sako_trg_m10_bolt_86x70'),
      ('vpo215','vpo215_fire_chambering_0',None),('toz106','toz106_fire_reload_0',None)]
NOT_BOLT=re.compile(r'Human|marker|Bend_Goal|Camera|IK|patron|shellport|LOD|aim_camera|weapon_hammer|mod_charge_hammer',re.I)

def withCharge(tree,charge):
    P,parentOf,_=tree; up,upParent,_=loadTree(CHARGES+charge+'_pb.bundle' if os.path.isfile(CHARGES+charge+'_pb.bundle') else CHARGES+charge+'.bundle')
    mc=bone(P,'/mod_charge'); root=mc+'/'+charge
    P=dict(P); parentOf=dict(parentOf); P[root]=(np.zeros(3),np.array([0,0,0,1.0]),np.ones(3)); parentOf[root]=mc
    for p,v in up.items(): P[root+'/'+p]=v; parentOf[root+'/'+p]=root+('/'+upParent[p] if upParent[p] else '')
    return P,parentOf,{zlib.crc32(p.encode())&0xffffffff:p for p in P}

# The knob: the turning bone's meshes (MeshFilters on it and under it, read from the gun's client_assets.bundle, where the
# Windows.json manifest says the meshes of the gun AND of its charge parts live), in its frame; the vertices furthest from the turn
# axis (through its origin) are the end of the handle - their centroid within KNOB_SHELL of the furthest is the knob's middle.
KNOB_SHELL=0.012
def meshKnob(folder,container,charge,turnerLeaf,axis):
    from UnityPy.helpers.MeshHelper import MeshHandler
    bundle=(CHARGES+charge+'_pb.bundle' if os.path.isfile(CHARGES+charge+'_pb.bundle') else CHARGES+charge+'.bundle') if charge else ROOT+folder+'/'+container
    env=UnityPy.load(bundle,ROOT+folder+'/client_assets.bundle')
    def toParent(t,p):
        s=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition
        return g_rot(np.array([r.x,r.y,r.z,r.w]),p*np.array([s.x,s.y,s.z]))+np.array([a.x,a.y,a.z])
    # The rig can hold the bone more than once (the DVL-10: 4, one with collider-only children): the copy with the most vertices (LOD0)
    best=[]
    for o in env.objects:
        if o.type.name!='Transform': continue
        t=o.read()
        if t.m_GameObject.read().m_Name!=turnerLeaf: continue
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
    points=np.array(best); a=np.array(axis,float); a/=np.linalg.norm(a)
    radial=np.linalg.norm(points-np.outer(points@a,a),axis=1)
    tip=points[radial>=radial.max()-KNOB_SHELL].mean(axis=0)
    print(f'  knob from {len(points)} mesh vertices: {np.round(tip,4)} ({radial.max()*100:.1f} cm out from the turn axis)')
    return [round(float(x),4) for x in tip]

def g_rot(q,v):
    u=q[:3]; return v+2*np.cross(u,np.cross(u,v)+q[3]*v)

# The dominant hand on the turning bone where the bolt first reaches full travel: the frame ridingHold takes, without its steadiness
# test. The DVL-10's hand rolls about the knob as it pulls (its palm 6 cm along the bolt, the fingers 2-3 cm from the knob all the way).
def holdAtFirstBack(g,turner,wp):
    P=g['prefabLocal']; palm=bone(P,'/Base HumanRPalm'); J=fingerPaths(P,'R')
    if palm is None or J is None: return None
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rows=[]; rest=None
    for i in range(int(stop*30)+1):
        t=i/30; c={}; tp,_,_=g['worldAt'](turner,t,c); wpp,wr,_=g['worldAt'](wp,t,c); inGun=g['rotate'](conj(wr),tp-wpp)
        if rest is None: rest=inGun
        rows.append((t,float(np.linalg.norm(inGun-rest))))
    top=max(back for _,back in rows)
    if top<0.03: return None
    t=next(t for t,back in rows if back>=top-0.002); c={}
    tp,tr,_=g['worldAt'](turner,t,c); pp,pr,_=g['worldAt'](palm,t,c)
    qs=[np.array(g['localAt'](j,t)[1],float) for j in J]
    return dict(position=[round(float(x),4) for x in g['rotate'](conj(tr),pp-tp)],rotation=toUnityEuler(qmul(conj(tr),pr)),fingers=eulerList(qs),t=t)

def axisAngle(q):
    q=q if q[3]>=0 else -q; angle=2*math.degrees(math.acos(min(1.0,float(q[3])))); s=math.sqrt(max(1e-12,1-float(q[3])**2))
    return [float(x)/s for x in q[:3]],angle

bolts={}; folderClips={}
for folder,clipName,charge in GUNS:
    if folder not in folderClips:
        clips={}
        for o in UnityPy.load(ROOT+folder+'/client_assets.bundle').objects:
            if o.type.name=='AnimationClip':
                try: c=o.read(); clips[c.m_Name]=c
                except Exception: pass
        folderClips[folder]=clips
    clip=folderClips[folder][clipName]; family=re.sub(r'_(fire_chambering|rechamber|fire_reload)_0$','',clipName)
    hashes=[b.path for b in clip.m_ClipBindingConstant.genericBindings]; best=None
    for c in sorted(os.listdir(ROOT+folder)):
        if c.endswith('_container.bundle') and c.startswith('weapon_'):
            tree=loadTree(ROOT+folder+'/'+c)
            if charge: tree=withCharge(tree,charge)
            n=sum(h in tree[2] for h in hashes)
            if best is None or n>best[0]: best=(n,tree,c)
    tree=best[1]; container=best[2]; P,parentOf,H=tree; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime)
    wp=bone(P,'Weapon_root_anim/weapon'); steps=int(stop*60)+1
    moved={}
    for h in set(hashes):
        p=H.get(h)
        if p is None or not p.startswith(wp+'/') or NOT_BOLT.search(p.split('/')[-1]): continue
        p0,q0=[np.array(v,float) for v in g['localAt'](p,0.0)[:2]]; travel=(0,0); turn=(0,0)
        for i in range(steps):
            t=i/60; pt,qt=[np.array(v,float) for v in g['localAt'](p,t)[:2]]
            d=float(np.linalg.norm(pt-p0)); r=ang(q0,qt)
            if d>travel[0]: travel=(d,t)
            if r>turn[0]: turn=(r,t)
        if travel[0]>0.004 or turn[0]>4: moved[p]=dict(travel=travel,turn=turn)
    sliders=[p for p in moved if moved[p]['travel'][0]>=0.05]
    turners=[p for p in moved if moved[p]['turn'][0]>=30]
    if not sliders or not turners:
        print(family,'no bolt found',{p.split('/')[-1]:v for p,v in moved.items()}); continue
    slider=max(sliders,key=lambda p:moved[p]['travel'][0]); turner=max(turners,key=lambda p:moved[p]['turn'][0])
    tBack=moved[slider]['travel'][1]; tUp=moved[turner]['turn'][1]
    p0=np.array(g['localAt'](slider,0.0)[0],float); pBack=np.array(g['localAt'](slider,tBack)[0],float)
    # The turn at full lift, before the slide (the DVL-10's single bone: its turn as the slide starts)
    tLift=tUp if tUp<=tBack else tBack
    if turner==slider:
        for i in range(steps):
            t=i/60
            if np.linalg.norm(np.array(g['localAt'](slider,t)[0],float)-p0)>0.005: tLift=t; break
    q0=np.array(g['localAt'](turner,0.0)[1],float); qUp=np.array(g['localAt'](turner,tLift)[1],float)
    axis,angle=axisAngle(qmul(conj(q0),qUp))
    hold=ridingHold(g,turner,'R') or holdAtFirstBack(g,turner,wp)
    # Where the hand takes it, in the turning bone's frame: the handle's knob from its mesh (the grab zone's centre; the hold's palm
    # is the hand ROOT, ~10 cm off the knob on the M700). Without a mesh: the hold's palm.
    # Which mesh: the clip's closed right fingers (the 15 finger joints' centroid at the hold, in the turner's frame) wrap the knob,
    # so of the folder's guns (the Mosin folder has the sniper's bent handle and the infantry's straight one; the clip's bones match
    # both) the one whose knob is nearest them. They agree within 1.5-3.4 cm on every rifle.
    grasp=None; J=fingerPaths(P,'R')
    if J:
        c={}; tHold=hold['t'] if hold else tBack; tp,tr,_=g['worldAt'](turner,tHold,c)
        grasp=np.mean([g['rotate'](conj(tr),g['worldAt'](j,tHold,c)[0]-tp) for j in J[1:]],axis=0)
    inCharge=charge if charge and ('/'+charge+'/') in turner else None
    candidates=[container] if inCharge else [c for c in sorted(os.listdir(ROOT+folder)) if c.endswith('_container.bundle') and c.startswith('weapon_')]
    knob=None; knobOff=None
    for candidate in candidates:
        k=meshKnob(folder,candidate,inCharge,turner.split('/')[-1],axis)
        if k is None: continue
        off=float(np.linalg.norm(grasp-np.array(k))) if grasp is not None else 0.0
        if knob is None or off<knobOff: knob,knobOff=k,off
    if knob and grasp is not None: print(f'  knob {knob}: {knobOff*100:.1f} cm from where the clip\'s fingers close')
    if knob: pass
    elif hold: knob=hold['position']
    else:
        c={}; tp,tr,_=g['worldAt'](turner,tBack,c); pp,_,_=g['worldAt'](bone(P,'/Base HumanRPalm'),tBack,c)
        knob=[round(float(x),4) for x in g['rotate'](conj(tr),pp-tp)]
    bolts[family]=dict(slider=slider.split('/')[-1],turner=turner.split('/')[-1],travel=[round(float(x),4) for x in pBack-p0],
                       distance=round(float(np.linalg.norm(pBack-p0)),4),turnAxis=[round(x,4) for x in axis],turnDegrees=round(angle,1),knob=knob,
                       hold=dict(position=hold['position'],rotation=hold['rotation'],fingers=hold['fingers']) if hold else None,
                       times=dict(lift=round(tLift,3),back=round(tBack,3)))
    print(f"{family}: slide {bolts[family]['slider']} {bolts[family]['distance']*100:.1f} cm at {tBack:.2f}s, turn {bolts[family]['turner']} {angle:.0f} deg about {[round(x,2) for x in axis]} at {tLift:.2f}s, hold {'yes' if hold else 'NO'}",flush=True)
json.dump(dict(bolts=bolts),open('bolts.json','w'),indent=1)
