# Belt-fed machine guns (VRReloadController.Belt), from each gun's own clips: the feed cover and the sight mount's latch as hinges, the
# belt hanging out of a box that's on the gun but not yet laid in the feed tray, and the RPD's charging handle.
#  - cover / latch: the bone the gun's open clip turns most, its turn (axis in its own closed frame, degrees) from the first frame to the
#    most open one. The latch's button: the bone that slides as it's pressed, and how far (its parent's frame). Latch = the swinging scope
#    mount Tarkov flips aside before opening the cover when a scope is on it (the PKP's, the RPDN's; pk_sight_open / rpdn_sight_open).
#  - belt: the box's belt_link chain (box root frame) at the moment its box insert clip has the box seated and the belt end not yet taken:
#    every link's pose, and the last link still in the box (the ones before it don't move between that and the belt laid in the tray).
#  - hands/holds: which hand works each part in the clips (cover, latch, belt end, box, handle) and its pose on it (partHold).
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
    # Every other box bone the idle clip keys, its local pose there (name, position, quaternion, scale): the spent links hanging off the
    # last one (empty_link_*), the fake belt, the lid. Where the animator doesn't pose a box put on by hand, the runtime holds them so
    # (his PKM: "the empty belt is coming out the top" - the spent links left in the model's own rest shape)
    keyed=sorted({o[0] for o in gi['owner'] if o[0] and o[0].startswith(boxRoot+'/') and not re.match(r'^belt_link_\d+$',o[0].split('/')[-1])})
    names=[path.split('/')[-1] for path in keyed]; poses=[]
    for path in keyed:
        pos,rot,scale,_=gi['localAt'](path,0.0)
        poses.append(r4(list(pos)+list(rot)+list(scale)))
    idleLocals=dict(names=names,poses=poses)
    print(f'  idle-keyed box bones besides the links: {len(names)} ({", ".join(sorted({re.sub(r"_[0-9]+$","_*",n) for n in names}))}), '
          f'duplicate names: {sorted({n for n in names if names.count(n)>1})}',flush=True)
    # Laid too: Tarkov's own belt in the tray (its idle clip), every link in the box frame - where a belt the hand lays goes
    return dict(_seated=seated,idleLocals=idleLocals,_hangT=hangT,_end=links[-1],box=boxRoot.split('/')[-1],boxIds=boxIds,prefix='belt_link_',count=len(links),anchor=anchor,spacing=round(spacing,5),
                hanging=[r4(list(p)+list(q)) for p,q in hanging],laid=[r4(list(p)+list(q)) for p,q in laid])

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

# The hands on the parts (his "why isn't the hand locking into the animation position", and "the right hand pulls the charging handle on
# the RPD, left hand is the mag release": which hand does each part is the clips', every gun its own). Per frame (60 a second), where the
# part carries a palm's point (that point fixed in the part, weapon frame) against where that palm goes: a frame the part moves that point
# 5 cm/s or more counts, the palm rides it where the two agree within 35 %. The part's hand: the one riding it most; neither (a cover that
# springs open, a latch flicked aside) - the one that leaves the gun. Its pose at the middle of its longest ride, or with none at the first
# frame the part moves (the press). palmInPart: the palm in the part's frame (a hinge, the belt's end, a handle - the runtime's handle
# holds); else the part in the palm's frame (the box's slot bone - the runtime's magazine holds, inBoneFrame).
STEP=1/60
def partHold(tree,clip,partPath,palmInPart=True,start=0.0,end=None,label=''):
    P=tree[0]; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime); end=stop if end is None else min(end,stop)
    times=[t for t in (min(i*STEP,stop) for i in range(int(stop/STEP)+1)) if start-1e-6<=t<=end+1e-6]
    weapon=bone(P,'Weapon_root_anim/weapon'); palms={s:bone(P,f'/Base Human{s}Palm') for s in 'LR'}
    rows=[]
    for t in times:
        c={}; wp,wr,_=g['worldAt'](weapon,t,c)
        def inW(pose): return g['rotate'](conj(wr),pose[0]-wp),qmul(conj(wr),pose[1])
        r=dict(t=t,part=inW(g['worldAt'](partPath,t,c)[:2]),world={'part':g['worldAt'](partPath,t,c)[:2]})
        for s in 'LR':
            r[s]=inW(g['worldAt'](palms[s],t,c)[:2]); r['world'][s]=g['worldAt'](palms[s],t,c)[:2]
        rows.append(r)
    per={}
    for s in 'LR':
        moving=0; riding=[]; first=None
        for i in range(len(rows)-1):
            A,B=rows[i],rows[i+1]; (pa,ra),(pb,rb)=A['part'],B['part']; ha,hb=A[s][0],B[s][0]
            carried=pb+g['rotate'](rb,g['rotate'](conj(ra),ha-pa))
            ride=np.linalg.norm(carried-ha)
            if ride/STEP<0.05: continue
            moving+=1; first=i if first is None else first
            if np.linalg.norm(hb-carried)<0.35*ride: riding.append(i)
        runs=[]
        for i in riding:
            if runs and i-runs[-1][-1]<=2: runs[-1].append(i)
            else: runs.append([i])
        leaves=max(float(np.linalg.norm(r[s][0]-rows[0][s][0])) for r in rows)
        per[s]=dict(moving=moving,riding=len(riding),best=max(runs,key=len) if runs else [],first=first,leaves=leaves)
    side=max('LR',key=lambda s:(per[s]['riding'],per[s]['leaves'])) if max(per[s]['riding'] for s in 'LR')>=3 else max('LR',key=lambda s:per[s]['leaves'])
    d=per[side]
    if len(d['best'])>=3: i=d['best'][len(d['best'])//2]; how=f"rides {d['riding']}/{d['moving']}, middle of t{rows[d['best'][0]]['t']:.2f}-{rows[d['best'][-1]+1]['t']:.2f}"
    elif d['first'] is not None: i=d['first']; how=f"no ride ({d['riding']}/{d['moving']}), as it starts to move"
    else:
        print(f'  {label}: the part never moves in {clip.m_Name}',flush=True); return None
    t=rows[i]['t']; (pp,pr),(hp,hr)=rows[i]['world']['part'],rows[i]['world'][side]
    if palmInPart: pos,rot=g['rotate'](conj(pr),hp-pp),qmul(conj(pr),hr)
    else: pos,rot=g['rotate'](conj(hr),pp-hp),qmul(conj(hr),pr)
    J=fingerPaths(P,side); qs=[np.array(g['localAt'](j,t)[1],float) for j in J]
    c={}; tips=[g['worldAt'](J[k],t,c)[0] for k in (3,6,9,12,15)]
    tip=min(float(np.linalg.norm(q-pp)) for q in tips)
    other='L' if side=='R' else 'R'
    print(f"  {label}: {side} hand ({how}; the {other}: rides {per[other]['riding']}/{per[other]['moving']}, leaves the gun {per[other]['leaves']*100:.0f} cm), t{t:.2f}, "
          f"palm {np.linalg.norm(hp-pp)*100:.0f} cm / nearest tip {tip*100:.0f} cm from the part's origin",flush=True)
    return dict(hand=side,position=r4(pos),rotation=toUnityEuler(rot),fingers=eulerList(qs))

# The belt's end turns and bends under the fingers as it's fed (no ride in its own frame): the hand that takes it (the one leaving the gun
# most), at the frame its nearest fingertip is closest to the end link - the pinch
def pinchHold(tree,clip,partPath,start,end,label=''):
    P=tree[0]; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime); end=min(end,stop); weapon=bone(P,'Weapon_root_anim/weapon')
    times=[start+i*STEP for i in range(int((end-start)/STEP)+1)]
    best=None; leaves={}
    for s in 'LR':
        palm=bone(P,f'/Base Human{s}Palm'); J=fingerPaths(P,s); first=None; most=0.0; near=None
        for t in times:
            c={}; wp,wr,_=g['worldAt'](weapon,t,c); pp,pr,_=g['worldAt'](partPath,t,c); hp,hr,_=g['worldAt'](palm,t,c)
            inGun=g['rotate'](conj(wr),hp-wp); first=inGun if first is None else first; most=max(most,float(np.linalg.norm(inGun-first)))
            tip=min(float(np.linalg.norm(g['worldAt'](J[k],t,c)[0]-pp)) for k in (3,6,9,12,15))
            if near is None or tip<near[0]: near=(tip,t,(pp,pr),(hp,hr),J)
        leaves[s]=most
        if best is None or most>best[0]: best=(most,s,near)
    _,side,(tip,t,(pp,pr),(hp,hr),J)=best
    qs=[np.array(g['localAt'](j,t)[1],float) for j in J]
    other='L' if side=='R' else 'R'
    print(f"  {label}: {side} hand (leaves the gun {leaves[side]*100:.0f} cm, the {other} {leaves[other]*100:.0f}), pinch at t{t:.2f}: nearest tip {tip*100:.1f} cm, palm {np.linalg.norm(hp-pp)*100:.0f} cm",flush=True)
    return dict(hand=side,position=r4(g['rotate'](conj(pr),hp-pp)),rotation=toUnityEuler(qmul(conj(pr),hr)),fingers=eulerList(qs))

# The belt's end from the box seated until it lies in the tray (Tarkov's lay): the clip's end pose within 5 mm in the weapon's frame
def layWindow(tree,insertClip,linkPath,hangT):
    P=tree[0]; g=setup(tree,insertClip); stop=float(insertClip.m_MuscleClip.m_StopTime); weapon=bone(P,'Weapon_root_anim/weapon')
    def endAt(t):
        c={}; wp,wr,_=g['worldAt'](weapon,t,c); return g['rotate'](conj(wr),g['worldAt'](linkPath,t,c)[0]-wp)
    final=endAt(stop)
    for i in range(int((stop-hangT)/STEP)+1):
        t=hangT+i*STEP
        if np.linalg.norm(endAt(t)-final)<0.005: return t
    return stop

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

# The box rocking on and off the gun like an AK magazine, but across it (his PKM: "it should just rock in similar to how an AK mag does
# but from left to right of the gun instead of front to back"; "start in this position where it's hooking into the mag slot then as you
# push up/tilt, it pushes the other side up into position"). Tarkov's pk_mag_out rolls the box about the barrel; as a path to follow it was
# "too exact and hard to get right". A hinge on the box body's top edge that stays hooked as it rolls out (the one the roll lifts), along
# the barrel (the weapon frame's y: the stock sits at y -23 cm, the barrel at -52), BOX_ROCK degrees open. The body: its biggest box
# collider (the mesh data isn't in the box's own bundle). Slot frame = the seated box root's (ModPlacer identity on these boxes).
BOX_ROCK={'pk':25.0}
def boxBody(box):
    env=UnityPy.load(MAGAZINES+box); T={o.path_id:o.read() for o in env.objects if o.type.name=='Transform'}
    def toParent(t,p):
        sc=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition
        q=np.array([r.x,r.y,r.z,r.w]); v=p*np.array([sc.x,sc.y,sc.z])
        return v+2*np.cross(q[:3],np.cross(q[:3],v)+q[3]*v)+np.array([a.x,a.y,a.z])
    best=None
    for o in env.objects:
        if o.type.name!='BoxCollider': continue
        d=o.read(); go=d.m_GameObject.read()
        tr=None
        for c in go.m_Components:
            pid=c.component.path_id if hasattr(c,'component') else c.path_id
            if pid in T: tr=T[pid]
        if tr is None: continue
        chain=[]; t=tr
        while t is not None:
            chain.append(t); f=t.m_Father.path_id; t=T.get(f) if f else None
        centre=np.array([d.m_Center.x,d.m_Center.y,d.m_Center.z]); half=np.array([d.m_Size.x,d.m_Size.y,d.m_Size.z])/2
        corners=[]
        for sx in (-1,1):
            for sy in (-1,1):
                for sz in (-1,1):
                    q=centre+half*np.array([sx,sy,sz])
                    for link in chain[:-1]: q=toParent(link,q)
                    corners.append(q)
        corners=np.array(corners); volume=float(np.prod(corners.max(0)-corners.min(0)))
        # the closest-detail one (LOD0): LOD1's also wraps the mount, 6 cm past the box's end
        lod=0 if 'lod0' in go.m_Name.lower() else 1
        if best is None or (lod,-volume)<(best[4],-best[0]): best=(volume,corners.min(0),corners.max(0),go.m_Name,lod)
    return best
def boxRock(family,tree,boxRoot,outClip,seat,angle):
    body=boxBody(GUNS[family][3])
    if body is None: print('  box rock: no body collider',flush=True); return None
    _,lo,hi,name,_=body
    axis=np.array([0.0,-1.0,0.0])
    # the out clip's roll sense about the barrel: which top end it lifts (that end stays hooked)
    g=setup(tree,outClip); stop=float(outClip.m_MuscleClip.m_StopTime); weapon=bone(tree[0],'Weapon_root_anim/weapon'); sp,sr=seat; roll=0.0
    for i in range(int(stop*60)+1):
        t=min(i/60,stop); c={}; wp,wr,_=g['worldAt'](weapon,t,c); bp,br,_=g['worldAt'](boxRoot,t,c)
        rel=qmul(conj(sr),qmul(conj(wr),br)); rel=rel if rel[3]>=0 else -rel
        if np.linalg.norm(g['rotate'](conj(sr),g['rotate'](conj(wr),bp-wp)-sp))>0.05:
            roll=float(np.dot(rel[:3],axis)); break
    turned=np.array(list(np.sin(np.radians(10))*axis)+[np.cos(np.radians(10))])*(1 if roll>=0 else -1)
    def rotate(q,v): return g['rotate'](q,v)
    rightEnd=np.array([hi[0],(lo[1]+hi[1])/2,hi[2]]); leftEnd=np.array([lo[0],(lo[1]+hi[1])/2,hi[2]])
    lifts=rotate(turned,rightEnd)[2]>rightEnd[2]
    pivot=rightEnd if lifts else leftEnd
    if roll<0: axis=-axis
    print(f'  box rock: body {name} {np.round(lo*100,1)}..{np.round(hi*100,1)} cm, the clip rolls {"+" if roll>=0 else "-"} about the barrel, hooked on the {"right" if lifts else "left"} top edge {np.round(pivot*100,1)} cm, {angle} deg',flush=True)
    return dict(axis=r4(axis),angle=angle,pivot=r4(pivot))

# The left hand's places on the gun: where its clips put the left IK marker and hand marker (and that hand's fingers) at rest and aiming.
# The PKM's aiming clip moves the left hand from the handguard to the box (his: "ADSing seems to trigger an animation change where your
# off hand moves to the box"); the runtime lets the hand take whichever is nearer and keeps it there. Weapon bone frame.
AIM_IDLE={'pk':'pk_idle_ironsight'}
def supportSpot(tree,clip,name):
    P=tree[0]; g=setup(tree,clip); c={}
    wp,wr,_=g['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,c)
    spot=dict(name=name)
    for key,leaf in (('ik','/weapon_L_IK_marker'),('hand','/weapon_L_hand_marker')):
        mp,mr,_=g['worldAt'](bone(P,leaf),0.0,c); spot[key]=r4(list(g['rotate'](conj(wr),mp-wp))+list(qmul(conj(wr),mr)))
    spot['fingers']=eulerList([np.array(g['localAt'](j,0.0)[1],float) for j in fingerPaths(P,'L')])
    return spot

# The clip each gun's charging handle is read off for whose hand it is (the PKM's hold itself comes from gen_holds)
CHARGE_CLIPS={'pk':'pk_charge','rpd_reload':'rpd_reload_start_arm_0','m60_v1':'m60_v_1_charge'}
def holdOnly(h): return {k:v for k,v in h.items() if k in ('hand','position','rotation','fingers','inBoneFrame')} if h else None

out={}; handles={}
for family,(folder,container,latchContainer,box,idleName,coverName,latchName,insertName,chargeName,mates,outName,knobName) in GUNS.items():
    clips=clipsOf(folder)
    tree,boxRoot=treeWithBox(folder+'/'+container,box)
    entry={}
    entry['cover'],coverAt=hinge(tree,clips[coverName],COVER_BONES)
    latchTree,_=treeWithBox(folder+'/'+latchContainer,box)
    latch,latchAt=hinge(latchTree,clips[latchName],LATCH_BONES) if latchName else (None,None)
    if latch:
        latch['button'],latch['buttonTravel']=button(latchTree,clips[latchName])
    entry['latch']=latch
    entry['belt']=belt(tree,boxRoot,clips[idleName],clips[insertName])
    entry['wellAxis']=boxAxis(tree,boxRoot,clips[outName])
    gIdle=setup(tree,clips[idleName]); cIdle={}; wpI,wrI,_=gIdle['worldAt'](bone(tree[0],'Weapon_root_anim/weapon'),0.0,cIdle); bpI,brI,_=gIdle['worldAt'](boxRoot,0.0,cIdle)
    seatPose=(gIdle['rotate'](conj(wrI),bpI-wpI),qmul(conj(wrI),brI))
    if family in BOX_ROCK:
        entry['boxRock']=boxRock(family,tree,boxRoot,clips[outName],seatPose,BOX_ROCK[family])
    print(f'{family}: cover {entry["cover"]}, latch {latch}',flush=True)
    # The hands on each part, and which hand that is
    holds={}
    if coverAt: holds['cover']=partHold(tree,clips[coverName],coverAt[0],label=f'cover ({coverName})')
    if latchAt: holds['latch']=partHold(latchTree,clips[latchName],latchAt[0],label=f'latch ({latchName})')
    seated=None
    if entry['belt']:
        b=entry['belt']; seated,hangT,endPath=b.pop('_seated'),b.pop('_hangT'),b.pop('_end')
        layT=layWindow(tree,clips[insertName],endPath,hangT)
        holds['belt']=pinchHold(tree,clips[insertName],endPath,hangT,layT,label=f'belt end ({insertName} t{hangT:.2f}-{layT:.2f})')
    # The box: its root, which rides its slot bone (the PKM's and RPD's clips key only that, the root at its seat on it = the slot bone's
    # pose, the runtime's inBoneFrame) or has curves of its own (the M60's: the model's own pose, not the bone's)
    rootKeyed={o[0] for o in setup(tree,clips[outName])['owner'] if o[0]==boxRoot}|{o[0] for o in setup(tree,clips[insertName])['owner'] if o[0]==boxRoot}
    holds['boxPull']=partHold(tree,clips[outName],boxRoot,palmInPart=False,label=f'box out ({outName})')
    holds['boxInsert']=partHold(tree,clips[insertName],boxRoot,palmInPart=False,end=seated,label=f'box in ({insertName})')
    for k in ('boxPull','boxInsert'):
        if holds[k]: holds[k]['inBoneFrame']=not rootKeyed
    print(f'  box root keyed by its clips: {bool(rootKeyed)}',flush=True)
    chargeHold=partHold(tree,clips[CHARGE_CLIPS[family]],bone(tree[0],'/weapon_charge'),label=f'charging handle ({CHARGE_CLIPS[family]})')
    hands={k:h['hand'] for k,h in holds.items() if h}
    if holds['boxPull'] and holds['boxInsert'] and holds['boxPull']['hand']!=holds['boxInsert']['hand']:
        print(f"  !! box out by {holds['boxPull']['hand']}, in by {holds['boxInsert']['hand']}",flush=True)
    hands['box']=(holds['boxInsert'] or holds['boxPull'] or {}).get('hand')
    hands.pop('boxPull',None); hands.pop('boxInsert',None)
    if chargeHold: hands['charge']=chargeHold['hand']
    entry['hands']=hands
    if family in AIM_IDLE:
        spots=[supportSpot(tree,clips[idleName],'foregrip'),supportSpot(tree,clips[AIM_IDLE[family]],'box')]
        spots[1]['onBox']=True      # only with a box on the gun (his: "you shouldn't be able to hold on the ammo box spot if it's not in the gun")
        apart=np.linalg.norm(np.array(spots[0]['ik'][:3])-np.array(spots[1]['ik'][:3]))
        print(f"  support spots (the left IK marker): foregrip {np.round(np.array(spots[0]['ik'][:3])*100,1)} cm, box {np.round(np.array(spots[1]['ik'][:3])*100,1)} cm (weapon frame), {apart*100:.1f} cm apart",flush=True)
        entry['supportSpots']=spots
    entry['holds']={k:holdOnly(h) for k,h in holds.items() if h}
    print(f'  hands: {hands}',flush=True)
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
        # The clip's hand on the handle where ridingHold's strict still-run test finds none (the RPD's: the knob folds out under the hand
        # first, then a short pull - headset: "right hand pulls the charging handle on the RPD")
        if chargeHold and not (holdR if chargeHold['hand']=='R' else holdL):
            filled=dict(position=chargeHold['position'],rotation=chargeHold['rotation'],fingers=chargeHold['fingers'],travel=travel)
            if chargeHold['hand']=='R': holdR=filled
            else: holdL=filled
            print(f"  handle: the {chargeHold['hand']} hand's hold from its ride",flush=True)
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
