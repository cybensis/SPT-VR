# Belt-fed machine guns (VRReloadController.Belt), from each gun's own clips: the feed cover and the sight mount's latch as hinges, the
# belt hanging out of a box that's on the gun but not yet laid in the feed tray, and the RPD's charging handle.
#  - cover / latch: the bone the gun's open clip turns most, its turn (axis in its own closed frame, degrees) from the first frame to the
#    most open one. The latch's button: the bone that slides as it's pressed, and how far (its parent's frame). Latch = the swinging scope
#    mount Tarkov flips aside before opening the cover when a scope is on it (the PKP's, the RPDN's; pk_sight_open / rpdn_sight_open).
#  - belt: the box's belt_link chain (box root frame) at the moment its box insert clip has the box seated and the belt end not yet taken:
#    every link's pose, and the last link still in the box (the ones before it don't move between that and the belt laid in the tray).
#  - handle (RPD, M60): weapon_charge slides back; the RPD's knob weapon_charge_handle folds out as it's taken, the M60's weapon_charging_handle
#    doesn't (its grab point then); the bolt carrier the charge clip moves with it (the M60's comes back with the handle).
# Writes lmgs.json; gen_export.py merges it ("lmgs", the handles into the handle sections). Run from this folder.
import sys, os, re, json, zlib
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])

# Rotations keyed as quaternion curves (attribute 2) too: clip_pose reads euler curves only, and the M60's clips turn its cover with
# quaternions (missed, its cover read as not moving - his screenshot showed it up). Patched into the clip's own globals, so worldAt uses it.
_eulerOnlySetup=setup
def setup(tree,clip):
    g=_eulerOnlySetup(tree,clip)
    base,owner,valueAt=g['localAt'],g['owner'],g['valueAt']
    quaternionCurves={}
    for curve,(path,attribute,component,typeID) in enumerate(owner):
        if path is not None and typeID==4 and attribute==2: quaternionCurves.setdefault(path,[None]*4)[component]=curve
    def localAt(path,time):
        pos,rot,scale,euler=base(path,time)
        curves=quaternionCurves.get(path)
        if curves and None not in curves:
            q=[valueAt(c,time) for c in curves]
            if None not in q:
                q=np.array(q,float); n=np.linalg.norm(q)
                if n>1e-9: rot=q/n
        return pos,rot,scale,euler
    g['localAt']=localAt
    return g

MAGAZINES='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/items/mods/magazines/'
ITEMS=json.load(open('F:/SPT4.1/SPT_Runtime/SPT_Data/database/templates/items.json',encoding='utf-8'))
# family: rig folder, container (cover + belt), container with the latch, box, idle clip, cover clip, latch clip (None: no latch), box insert
# clip, charge clip (None: the handle's data comes from elsewhere), other families sharing it, box-out clip, the handle's knob
GUNS={
    'pk':('pkm','weapon_zid_pkm_762x54r_container.bundle','weapon_zid_pkp_762x54r_container.bundle','mag_pkm_zid_pk_std_762x54r_100.bundle',
          'pk_idle_armed','pk_open_cover','pk_sight_open','pk_mag_in',None,[],'pk_mag_out',None),
    'rpd_reload':('rpd','weapon_zid_rpd_762x39_container.bundle','weapon_zid_rpd_762x39_container.bundle','mag_rpd_zid_rpd_762x39_100.bundle',
                  'rpd_idle_armed','rpd_reload_open_cover','rpdn_sight_open','rpd_reload_mag_in','rpd_reload_start_arm_0',['rpdn_reload'],'rpd_reload_mag_out',
                  'weapon_charge_handle'),
    # Every M60 is the m60_v1 rig with the m60_v1 clips and the box from items/ (the m60 folder's set binds a box without its 'belt' node);
    # 'm60' too in case a controller plays that set
    'm60_v1':('m60_v1','weapon_usord_m60e4_v1_762x51_container.bundle','weapon_usord_m60e4_v1_762x51_container.bundle','mag_m60_capco_m60e3_assault_box_762x51_100.bundle',
              'm60_v1_idle_armed','m60_v1_reload_open_cover',None,'m60_v1_mag_in','m60_v_1_charge',['m60'],'m60_v1_mag_out','weapon_charging_handle'),
}
COVER_BONES=re.compile(r'^(mod_dustcover|mod_reciever)$')
LATCH_BONES=re.compile(r'^weapon_mount_axis$')
BUTTON_BONES=re.compile(r'^weapon_mount_button$')

def r4(v): return [round(float(x),5) for x in v]

def axisAngle(q):
    q=q if q[3]>=0 else -q; s=np.linalg.norm(q[:3])
    return [round(float(x),4) for x in (q[:3]/s if s>1e-9 else np.array([1.0,0,0]))],round(2*math.degrees(math.atan2(s,q[3])),1)

clipsByFolder={}
def clipsOf(folder):
    if folder not in clipsByFolder:
        clips={}
        for o in UnityPy.load(ROOT+folder+'/client_assets.bundle').objects:
            if o.type.name=='AnimationClip':
                try: c=o.read(); clips[c.m_Name]=c
                except Exception: pass
        clipsByFolder[folder]=clips
    return clipsByFolder[folder]

# The weapon's tree with the box under its mod_magazine as Tarkov's animator binds it (mod_magazine/<box root>/belt_root/...)
def treeWithBox(container,box):
    prefab,parent,_=loadTree(ROOT+container)
    boxPrefab,boxParent,_=loadTree(MAGAZINES+box)
    env=UnityPy.load(MAGAZINES+box)
    rootName=[t.m_GameObject.read().m_Name for t in (o.read() for o in env.objects if o.type.name=='Transform') if t.m_Father.path_id==0][0]
    slot=[p for p in prefab if p.endswith('mod_magazine')][0]
    boxRoot=slot+'/'+rootName
    prefab[boxRoot]=(np.zeros(3),np.array([0.,0.,0.,1.]),np.ones(3)); parent[boxRoot]=slot
    for p,pose in boxPrefab.items():
        prefab[boxRoot+'/'+p]=pose; parent[boxRoot+'/'+p]=boxRoot if boxParent[p]=='' else boxRoot+'/'+boxParent[p]
    return (prefab,parent,{zlib.crc32(p.encode())&0xffffffff:p for p in prefab}),boxRoot

def hinge(tree,clip,pattern):
    P=tree[0]; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime)
    paths=[p for p in P if pattern.match(p.split('/')[-1])]
    best=None
    for path in paths:
        r0=np.array(g['localAt'](path,0.0)[1],float)
        for i in range(int(stop*30)+1):
            t=min(i/30,stop); r=np.array(g['localAt'](path,t)[1],float); a=ang(r0,r)
            if best is None or a>best[0]: best=(a,path,t,qmul(conj(r0),r))
    if best is None or best[0]<5: return None,None
    axis,angle=axisAngle(best[3])
    return dict(bone=best[1].split('/')[-1],axis=axis,angle=angle),(best[1],best[2])

def button(tree,clip):
    P=tree[0]; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime)
    for path in [p for p in P if BUTTON_BONES.match(p.split('/')[-1])]:
        p0=np.array(g['localAt'](path,0.0)[0],float); far=None
        for i in range(int(stop*30)+1):
            p=np.array(g['localAt'](path,min(i/30,stop))[0],float)-p0
            if far is None or np.linalg.norm(p)>np.linalg.norm(far): far=p
        if far is not None and np.linalg.norm(far)>0.003: return path.split('/')[-1],r4(far)
    return None,None

def belt(tree,boxRoot,idleClip,insertClip):
    P=tree[0]
    links=sorted([p for p in P if p.startswith(boxRoot+'/') and re.match(r'^belt_link_\d+$',p.split('/')[-1])],key=lambda p:p.split('/')[-1])
    gi=setup(tree,idleClip); g=setup(tree,insertClip); stop=float(insertClip.m_MuscleClip.m_StopTime)
    def inBox(gg,t):
        c={}; bp,br,_=gg['worldAt'](boxRoot,t,c)
        return [(gg['rotate'](conj(br),gg['worldAt'](l,t,c)[0]-bp),qmul(conj(br),gg['worldAt'](l,t,c)[1])) for l in links],(bp,br)
    laid,(idleBox,idleBoxRot)=inBox(gi,0.0)
    seated=None
    for i in range(int(stop*30)+1):
        t=min(i/30,stop); c={}; bp,br,_=g['worldAt'](boxRoot,t,c); wp,wr,_=g['worldAt'](bone(P,'Weapon_root_anim/weapon'),t,c)
        ci={}; ip,ir,_=gi['worldAt'](boxRoot,0.0,ci); iwp,iwr,_=gi['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,ci)
        here=g['rotate'](conj(wr),bp-wp); idle=gi['rotate'](conj(iwr),ip-iwp)
        if np.linalg.norm(here-idle)<0.003 and ang(qmul(conj(wr),br),qmul(conj(iwr),ir))<2:
            seated=t; break
    if seated is None: return None
    # The belt end as the box seats, then the first frame it starts toward the tray: just before that
    hangT=seated; tip0,_=inBox(g,seated)
    for i in range(int((stop-seated)*30)+1):
        t=seated+i/30; tip,_=inBox(g,t)
        if np.linalg.norm(tip[-1][0]-tip0[-1][0])>0.01: break
        hangT=t
    hanging,_=inBox(g,hangT)
    # In the box: everything before the hanging end. The end is the run of links at the far end that move further and further between
    # hanging and laid (each at least 3 mm more than the one before it); the rest is the box's own coil, which may shift as a whole (the
    # RPD's steps a link along its curve, the M60's settles 3-6 cm) or not at all (the PKM's)
    moved=[float(np.linalg.norm(lp-hp)) for (lp,_),(hp,_) in zip(laid,hanging)]
    anchor=len(moved)-1
    while anchor>1 and moved[anchor]>moved[anchor-1]+0.003:
        anchor-=1
    anchor+=1       # 0-based index of the last link the end grows from = 1-based number of the last link in the box
    spacing=float(np.mean([np.linalg.norm(hanging[k+1][0]-hanging[k][0]) for k in range(len(hanging)-1)]))
    print(f'  belt: {len(links)} links, box seated t{seated:.2f}, hanging t{hangT:.2f}, last link in the box #{anchor}, spacing {spacing*100:.2f} cm, '
          f'end laid {np.round(laid[-1][0]*100,1)} hanging {np.round(hanging[-1][0]*100,1)} cm (box frame)',flush=True)
    # The box it's for (its root's name, and its item templates - the items whose prefab is this bundle): another box on the same gun (the
    # PKTM's 250, the M60's LBT pouch) has its own shape
    boxFile=boxRoot.split('/')[-1]+'.bundle'
    boxIds=sorted(i for i,it in ITEMS.items() if it.get('_props',{}).get('Prefab',{}).get('path','').endswith('/'+boxFile))
    print(f'  box templates {boxIds}',flush=True)
    return dict(box=boxRoot.split('/')[-1],boxIds=boxIds,prefix='belt_link_',count=len(links),anchor=anchor,spacing=round(spacing,5),
                hanging=[r4(list(p)+list(q)) for p,q in hanging])

# Which way the box leaves the gun, in the mod_magazine bone's frame (the runtime's magazine track, wellAxes): the box root's first 1-6 cm
# off its seat in the box-out clip, averaged; how much it turns over that, printed (a box that swings off only roughly follows a line)
def boxAxis(tree,boxRoot,outClip):
    P,parentOf=tree[0],tree[1]; slot=parentOf[boxRoot]; g=setup(tree,outClip); stop=float(outClip.m_MuscleClip.m_StopTime)
    # Against the weapon bone: these clips move the mod_magazine bone itself, the box rides it. Seated slot frame at the end.
    weapon=bone(P,'Weapon_root_anim/weapon'); rows=[]
    c={}; sp0,sr0,_=g['worldAt'](slot,0.0,c); wp0,wr0,_=g['worldAt'](weapon,0.0,c); slotInWeapon=qmul(conj(wr0),sr0)
    for i in range(int(stop*120)+1):
        t=min(i/120,stop); c={}; bp,br,_=g['worldAt'](boxRoot,t,c); wp,wr,_=g['worldAt'](weapon,t,c)
        rows.append((t,g['rotate'](conj(wr),bp-wp),qmul(conj(wr),br)))
    seat=rows[0]; directions=[]; turn=0.0; times=[]
    # the box only leaves late in the clip (the cover and the belt first): from the last frame still seated
    start=max(i for i,r in enumerate(rows) if np.linalg.norm(r[1]-seat[1])<0.002)
    for t,pos,rot in rows[start:]:
        off=pos-seat[1]; d=float(np.linalg.norm(off))
        if d>0.06: break
        if d>=0.01: directions.append(off/d); turn=max(turn,ang(rot,seat[2])); times.append(t)
    if len(directions)<2:
        print(f'  box leaves: only {len(directions)} samples in 1-6 cm',flush=True); return None
    mean=np.mean(directions,axis=0); mean=g['rotate'](conj(slotInWeapon),mean/np.linalg.norm(mean))
    print(f'  box leaves along {np.round(mean,3)} (mod_magazine frame), t{times[0]:.2f}-{times[-1]:.2f}, turning {turn:.0f} deg over it',flush=True)
    return [round(float(x),4) for x in mean]

def meshPoint(folder,container,boneLeaf):
    # The middle of a bone's meshes (its whole subtree), in its PARENT's frame (prefab poses): the RPD's folding knob in weapon_charge's
    from UnityPy.helpers.MeshHelper import MeshHandler
    env=UnityPy.load(ROOT+folder+'/'+container,ROOT+folder+'/client_assets.bundle')
    def toParent(t,p):
        s=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition; q=np.array([r.x,r.y,r.z,r.w]); v=p*np.array([s.x,s.y,s.z])
        return v+2*np.cross(q[:3],np.cross(q[:3],v)+q[3]*v)+np.array([a.x,a.y,a.z])
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
                try: h=MeshHandler(comp.m_Mesh.read()); h.process()
                except Exception: continue
                for q in np.array(h.m_Vertices,float).reshape(-1,3):
                    for link in chain: q=toParent(link,q)
                    points.append(q)
            for ch in tr.m_Children:
                c=ch.read(); walk(c,[c]+chain)
        walk(t,[])
        if points: return r4(toParent(t,np.mean(points,axis=0)))
    return None

out={}; handles={}
for family,(folder,container,latchContainer,box,idleName,coverName,latchName,insertName,chargeName,mates,outName,knobName) in GUNS.items():
    clips=clipsOf(folder)
    tree,boxRoot=treeWithBox(folder+'/'+container,box)
    entry={}
    entry['cover'],_=hinge(tree,clips[coverName],COVER_BONES)
    latchTree,_=treeWithBox(folder+'/'+latchContainer,box)
    latch,_=hinge(latchTree,clips[latchName],LATCH_BONES) if latchName else (None,None)
    if latch:
        latch['button'],latch['buttonTravel']=button(latchTree,clips[latchName])
    entry['latch']=latch
    entry['belt']=belt(tree,boxRoot,clips[idleName],clips[insertName])
    entry['wellAxis']=boxAxis(tree,boxRoot,clips[outName])
    print(f'{family}: cover {entry["cover"]}, latch {latch}',flush=True)
    for f in [family]+mates: out[f]=entry
    if chargeName:
        g=setup(tree,clips[chargeName]); P=tree[0]; charge=bone(P,'/weapon_charge')
        fold,_=hinge(tree,clips[chargeName],re.compile('^'+re.escape(knobName)+'$'))
        knob=meshPoint(folder,container,knobName)
        holdR=ridingHold(g,charge,'R'); holdL=ridingHold(g,charge,'L')
        stop=float(clips[chargeName].m_MuscleClip.m_StopTime)
        def travelOf(path):
            p0=np.array(g['localAt'](path,0.0)[0],float)
            return max(float(np.linalg.norm(np.array(g['localAt'](path,min(i/30,stop))[0],float)-p0)) for i in range(int(stop*30)+1))
        travel=round(travelOf(charge),4)
        # The bolt carrier the clip brings back with the handle, by its share of the handle's travel (StartMovedBolts)
        carrier=bone(P,'/weapon_bolt_carrier'); carrierTravel=travelOf(carrier) if carrier else 0.0
        boltBones=[dict(bone='weapon_bolt_carrier',ratio=round(carrierTravel/travel,3))] if carrier and carrierTravel>=0.015 and travel>0 else None
        print(f'  handle: fold {fold}, knob {knob}, right hold {bool(holdR)}, left hold {bool(holdL)}, travel {travel}, carrier {carrierTravel*100:.1f} cm',flush=True)
        for f in [family]+mates:
            handles[f]=dict(bone='weapon_charge',travel=travel,
                            knobs=[knob] if knob and fold else None,folds=[fold] if fold else None,grabPoint=knob if knob and not fold else None,
                            boltBones=boltBones,
                            gunHand={k:v for k,v in holdR.items() if k in ('position','rotation','fingers','travel')} if holdR else None,
                            offHand={k:v for k,v in holdL.items() if k in ('position','rotation','fingers','travel')} if holdL else None)
json.dump(dict(lmgs=out,lmgHandles=handles),open('lmgs.json','w'),separators=(',',':'))
print('wrote lmgs.json',os.path.getsize('lmgs.json'),'bytes')
