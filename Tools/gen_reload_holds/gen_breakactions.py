# Break-action guns (MP-43-1C, its sawed-off, MP-18), from each gun's own clips (VRReloadController.BreakAction):
#  - open: every bone the open clip moves as the barrels drop (the barrel slot's bone turning on the hinge, the chamber and shell-port
#    bones: on the MP-43 the extractor lifting the shells, on the MP-18 riding the barrel, which isn't their parent), their local
#    poses at OPEN_KEYS evenly spaced points of the barrel's turn
#  - lever: the top lever (MP-43) / locking lever (MP-18), its local poses over its own swing in the open clip, and the gun hand's finger
#    pressing it (press: which finger, its joints, its tip in the lever bone's frame)
#  - hinge: the barrel bone's turn axis (its closed local frame, + = opening) and how far; bore: the way a shell comes out of a
#    chamber, in the barrel bone's frame (the pluck clip's shell for its first 4 cm)
#  - holds (the left hand loads in these clips): the fore-end in the left palm as the close clip's hand pushes the barrels up
#    (the palm in the barrel bone's frame, the fingers, and where the fingers close = the grab point); a new shell in the palm
#    as the load clip lines it up behind the chamber; a fired one as the pluck clip draws it out
# Weapon frame = Weapon_root_anim/weapon. Writes breakactions.json; gen_export.py merges it. Run from this folder.
import sys, os, re, json
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])

# family: folder, container, open clip, close clip, load clip, pluck clip, lever bone
GUNS={
    'mr_43':('mr43','weapon_izhmeh_mr43e-1c_12g_container.bundle','mr_43_reload_start','mr_43_reload_end','mr_43_reload_ammo_left','mr_43_reload_empty_shell_left','weapon_switch'),
    'mr_43_sawed_off':('mr43','weapon_izhmeh_mr43_sawed_off_12g_container.bundle','mr_43_sawed_off_reload_start','mr_43_sawed_off_reload_end',
                       'mr_43_sawed_off_reload_ammo_left','mr_43_sawed_off_reload_empty_shell_left','weapon_switch'),
    'mp_18':('mp18','weapon_izhmash_mp18_multi_container.bundle','mp_18_reload_start','mp_18_reload_end','mp_18_reload_in','mp_18_reload_out_throw','mp18_locking_lever'),
}
OPEN_KEYS=8
LEVER_KEYS=4
STEP=1/240
NOT_PART=re.compile(r'Human|marker|Bend_Goal|Camera|IK|LOD|aim_camera|fireport|smokeport|trigger|prop',re.I)

def r5(v): return [round(float(x),5) for x in v]

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

def relPath(wp,p): return p[len(wp)+1:]

def axisAngle(q):
    q=q if q[3]>=0 else -q; angle=2*math.degrees(math.acos(min(1.0,float(q[3])))); s=math.sqrt(max(1e-12,1-float(q[3])**2))
    return [float(x)/s for x in q[:3]],angle

def times(clip): return [i*STEP for i in range(int(float(clip.m_MuscleClip.m_StopTime)/STEP)+1)]

# The turn of a bone off its pose at t0, per sample
def turnTrack(g,path,ts):
    q0=np.array(g['localAt'](path,ts[0])[1],float)
    return [ang(q0,np.array(g['localAt'](path,t)[1],float)) for t in ts]

# The keyed window of a swing: the last sample at rest before it, the first at its most
def swingWindow(track,ts,rest=0.05):
    top=max(track); iTop=next(i for i,a in enumerate(track) if a>=top-rest)
    iRest=max((i for i in range(iTop) if track[i]<=rest),default=0)
    return iRest,iTop,top

def timeAtShare(track,ts,iRest,iTop,share):
    want=share*track[iTop]
    for i in range(iRest,iTop):
        if track[i]<=want<=track[i+1] and track[i+1]>track[i]:
            return ts[i]+(want-track[i])/(track[i+1]-track[i])*STEP
    return ts[iRest] if share<=0.5 else ts[iTop]

def boneKeys(g,paths,keyTimes,wp):
    out=[]
    for p in paths:
        keys=[]
        for t in keyTimes:
            a,q=[np.array(v,float) for v in g['localAt'](p,t)[:2]]
            keys.append(r5(list(a)+list(q/np.linalg.norm(q))))
        out.append(dict(path=relPath(wp,p),keys=keys))
    return out

def inFrame(g,frame,path,t,c):
    fp,fr,_=g['worldAt'](frame,t,c); pp,pr,_=g['worldAt'](path,t,c); fi=conj(fr)
    return g['rotate'](fi,pp-fp),qmul(fi,pr)

def holdIn(g,palm,thing,t,J,second=None):
    c={}; pos,rot=inFrame(g,palm,thing,t,c)
    hold=dict(position=r5(pos),rotation=r5(rot/np.linalg.norm(rot)),fingers=eulerList([np.array(g['localAt'](j,t)[1],float) for j in J]),t=round(float(t),3))
    if second:
        pos2,rot2=inFrame(g,palm,second,t,c)
        hold.update(position2=r5(pos2),rotation2=r5(rot2/np.linalg.norm(rot2)))
    return hold

# Two shells, as the both-barrels clips carry them (the left chamber's first): the load clip's pair lined up 5 cm out of the chambers,
# the pull clip's 2 cm out - the single holds' moments
def pairHolds(g_tree,clips,family,barrel,chambers,shellports,palm,J):
    out={}
    for key,clipName,bones,distance in (('loadPair',family+'_reload_ammo_booth',chambers,0.05),('pluckPair',family+'_reload_empty_shell_booth',shellports,0.02)):
        if clipName not in clips or len(bones)<2: continue
        g=setup(g_tree,clips[clipName]); ts=times(clips[clipName])
        rest=inFrame(g,barrel,bones[0],ts[-1] if key=='loadPair' else ts[0],{})[0]
        best=None
        for t in ts:
            s,_=inFrame(g,barrel,bones[0],t,{}); d=s-rest
            along=float(d[1]); off=float(np.linalg.norm([d[0],d[2]]))
            if off<=0.015 and (best is None or abs(along-distance)<best[0]): best=(abs(along-distance),t)
        if best: out[key]=holdIn(g,palm,bones[0],best[1],J,second=bones[1])
    return out

# A bone's meshes (MeshFilters on it and under it, from the folder's client_assets.bundle), in its frame
def boneMeshPoints(folder,container,boneLeaf):
    from UnityPy.helpers.MeshHelper import MeshHandler
    env=UnityPy.load(ROOT+folder+'/'+container,ROOT+folder+'/client_assets.bundle')
    def toParent(t,p):
        s=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition; q=np.array([r.x,r.y,r.z,r.w]); v=p*np.array([s.x,s.y,s.z])
        return v+2*np.cross(q[:3],np.cross(q[:3],v)+q[3]*v)+np.array([a.x,a.y,a.z])
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
                try: h=MeshHandler(comp.m_Mesh.read()); h.process()
                except Exception: continue
                for q in np.array(h.m_Vertices,float).reshape(-1,3):
                    for link in chain: q=toParent(link,q)
                    points.append(q)
            for ch in tr.m_Children:
                c=ch.read(); walk(c,[c]+chain)
        walk(t,[])
        if len(points)>len(best): best=points
    return np.array(best) if best else None

# The gun hand's finger on the lever at its full swing: of the right hand's fingertips (the last joint + 2.5 cm along its -x, the
# runtime's thumb-tip rule) the one nearest the lever's mesh; that finger's three joints then (local eulers) and its tip in the lever
# bone's frame (the runtime bends the finger onto it from wherever the hand holds the gun: the MP-43 clip's hand rides up 4.4 cm to
# reach the top lever, a VR hand stays on the grip)
TIP=np.array([-0.025,0.0,0.0])
# (tTop: the press's moment, else the lever's furthest turn)
def leverPress(g,P,folder,container,lever,ts,tTop=None):
    J=fingerPaths(P,'R'); wp=bone(P,'Weapon_root_anim/weapon'); palm=bone(P,'/Base HumanRPalm')
    if tTop is None:
        turn=turnTrack(g,lever,ts); tTop=ts[int(np.argmax(turn))]
    c={}
    leverAt,leverRotation,_=g['worldAt'](lever,tTop,c)
    mesh=boneMeshPoints(folder,container,lever.split('/')[-1])
    best=None
    for finger in range(5):
        last=J[3+3*finger]; jp,jr,_=g['worldAt'](last,tTop,c); tip=jp+g['rotate'](jr,TIP)
        inLever=g['rotate'](conj(leverRotation),tip-leverAt)
        off=float(np.min(np.linalg.norm(mesh-inLever,axis=1))) if mesh is not None else float(np.linalg.norm(inLever))
        if best is None or off<best[0]: best=(off,finger,inLever,tip)
    off,finger,contact,tip=best
    joints=[np.array(g['localAt'](j,tTop)[1],float) for j in J[1+3*finger:4+3*finger]]
    # Reach from the hand at the clip's start (its grip on the gun): the finger's base to the contact vs the finger's own length
    c0={}; base0,_,_=g['worldAt'](J[1+3*finger],ts[0],c0); w0,wr0,_=g['worldAt'](wp,ts[0],c0); w1,wr1,_=g['worldAt'](wp,tTop,c)
    contactInGun=g['rotate'](conj(wr1),tip-w1); baseInGun=g['rotate'](conj(wr0),base0-w0)
    length=sum(float(np.linalg.norm(np.array(P[J[k+1]][0],float))) for k in range(1+3*finger,3+3*finger))+float(np.linalg.norm(TIP))
    p0,_,_=g['worldAt'](palm,ts[0],c0); p1,_,_=g['worldAt'](palm,tTop,c)
    print(f'  lever press: {["thumb","index","middle","ring","pinky"][finger]} at {tTop:.2f}s, {off*100:.1f} cm off the lever mesh; from the grip the '
          f'contact is {np.linalg.norm(contactInGun-baseInGun)*100:.1f} cm from the finger base (finger {length*100:.1f} cm), the clip\'s palm moved '
          f'{np.linalg.norm(g["rotate"](conj(wr1),p1-w1)-g["rotate"](conj(wr0),p0-w0))*100:.1f} cm')
    # The gun hand's IK marker (what Tarkov's arm IK puts the palm on) at the start and at the press, local to its parent: the runtime
    # moves its own marker by that much, so the hand sits on the gun as the clip has it there
    marker=bone(P,'weapon/weapon_R_IK_marker')
    rest=[np.array(v,float) for v in g['localAt'](marker,ts[0])[:2]]; pressed=[np.array(v,float) for v in g['localAt'](marker,tTop)[:2]]
    print(f'    marker moved {np.linalg.norm(pressed[0]-rest[0])*100:.1f} cm, turned {ang(rest[1],pressed[1]):.1f} deg')
    return dict(finger=finger,joints=eulerList(joints),contact=r5(contact),t=round(float(tTop),3),
                markerRest=r5(list(rest[0])+list(rest[1]/np.linalg.norm(rest[1]))),markerPressed=r5(list(pressed[0])+list(pressed[1]/np.linalg.norm(pressed[1]))))

out={}
for family,(folder,container,openClip,closeClip,loadClip,pluckClip,leverBone) in GUNS.items():
    print(family,flush=True)
    tree=loadTree(ROOT+folder+'/'+container); P=tree[0]; clips=clipsOf(folder)
    wp=bone(P,'Weapon_root_anim/weapon'); barrel=bone(P,'weapon/mod_barrel'); lever=bone(P,'weapon/'+leverBone)
    palm=bone(P,'/Base HumanLPalm'); J=fingerPaths(P,'L')
    chambers=sorted(p for p in P if p.startswith(wp+'/') and re.search(r'/patron_in_weapon(_\d+)?$',p))
    shellports=sorted(p for p in P if p.startswith(wp+'/') and re.search(r'/shellport(_\d+)?$',p))

    # The open: keyed by the barrel's turn
    g=setup(tree,clips[openClip]); ts=times(clips[openClip])
    barrelTurn=turnTrack(g,barrel,ts); iRest,iTop,openDegrees=swingWindow(barrelTurn,ts)
    keyTimes=[timeAtShare(barrelTurn,ts,iRest,iTop,k/(OPEN_KEYS-1)) for k in range(OPEN_KEYS)]
    q0=np.array(g['localAt'](barrel,ts[iRest])[1],float); qOpen=np.array(g['localAt'](barrel,ts[iTop])[1],float)
    hingeAxis,hingeDegrees=axisAngle(qmul(conj(q0),qOpen))
    moved=[]
    for p in P:
        if not p.startswith(wp+'/') or p==lever or NOT_PART.search(p.split('/')[-1]): continue
        a0,r0=[np.array(v,float) for v in g['localAt'](p,ts[iRest])[:2]]; most=0.0; turned=0.0
        for i in range(iRest,iTop+1,4):
            a,r=[np.array(v,float) for v in g['localAt'](p,ts[i])[:2]]; most=max(most,float(np.linalg.norm(a-a0))); turned=max(turned,ang(r0,r))
        if most>=0.0005 or turned>=1.0: moved.append(p)
    openBones=boneKeys(g,moved,keyTimes,wp)

    # The lever: its own swing in the open clip
    leverData=None
    if lever:
        leverTurn=turnTrack(g,lever,ts); jRest,jTop,leverDegrees=swingWindow(leverTurn,ts)
        if leverDegrees>3:
            leverTimes=[timeAtShare(leverTurn,ts,jRest,jTop,k/(LEVER_KEYS-1)) for k in range(LEVER_KEYS)]
            leverData=dict(degrees=round(leverDegrees,1),bones=boneKeys(g,[lever],leverTimes,wp),press=leverPress(g,P,folder,container,lever,ts))

    # The bore: the pluck clip's shell for its first 4 cm out of its chamber, in the barrel's frame
    gp=setup(tree,clips[pluckClip]); tp=times(clips[pluckClip]); shell=shellports[0]
    starts=[inFrame(gp,barrel,shell,t,{})[0] for t in tp]
    iOut=next(i for i,s in enumerate(starts) if np.linalg.norm(s-starts[0])>=0.04)
    iMove=max(i for i in range(iOut) if np.linalg.norm(starts[i]-starts[0])<=0.002)
    bore=starts[iOut]-starts[iMove]; bore/=np.linalg.norm(bore)
    # The pluck hold half way along those 4 cm
    iMid=next(i for i in range(iMove,iOut+1) if np.linalg.norm(starts[i]-starts[iMove])>=0.02)
    pluckHold=holdIn(gp,palm,shell,tp[iMid],J)

    # The load hold: the shell lined up on the bore, 5 cm out of its chamber (the load clip's left chamber)
    gl=setup(tree,clips[loadClip]); tl=times(clips[loadClip]); chamber=chambers[0]
    best=None
    for t in tl:
        c={}; s,_=inFrame(gl,barrel,chamber,t,c)
        seat=inFrame(gl,barrel,chamber,tl[-1],{})[0]
        d=s-seat; along=float(np.dot(d,bore)); off=float(np.linalg.norm(d-along*bore))
        if off<=0.012 and 0.02<=along<=0.08 and (best is None or abs(along-0.05)<best[0]): best=(abs(along-0.05),t,along,off)
    loadHold=holdIn(gl,palm,chamber,best[1],J) if best else None

    # The fore-end: the close clip's left palm on the barrels as they start up
    gc=setup(tree,clips[closeClip]); tc=times(clips[closeClip])
    closeTurn=turnTrack(gc,barrel,tc); kStart=next(i for i,a in enumerate(closeTurn) if a>0.5)
    tHold=tc[max(kStart-1,0)]
    palmIn,_=inFrame(gc,barrel,palm,tHold,{})
    # The hand rode the barrels the whole close (else it isn't on them: the sawed-off's clip flicks them shut with the gun hand, the
    # left one comes onto the fore-end after - its hold there at the clip's end)
    kEnd=next((i for i in range(kStart,len(tc)) if abs(closeTurn[i]-max(closeTurn))<0.3),len(tc)-1)
    drift=max(float(np.linalg.norm(inFrame(gc,barrel,palm,tc[i],{})[0]-palmIn)) for i in range(kStart,kEnd+1))
    if drift>0.01: tHold=tc[-1]
    c={}; palmIn,palmQ=inFrame(gc,barrel,palm,tHold,c)
    grab=np.mean([inFrame(gc,barrel,j,tHold,c)[0] for j in J[1:]],axis=0)
    foreEnd=dict(position=r5(palmIn),rotation=r5(palmQ/np.linalg.norm(palmQ)),fingers=holdIn(gc,palm,palm,tHold,J)['fingers'],grabPoint=r5(grab),t=round(float(tHold),3))

    ports=[p for p in shellports if re.search(r'/shellport_\d+$',p)]
    pairs=pairHolds(tree,clips,family,barrel,chambers,ports,palm,J) if len(chambers)>=2 else {}
    out[family]=dict(barrel=relPath(wp,barrel),hingeAxis=r5(hingeAxis),openDegrees=round(hingeDegrees,2),bore=r5(bore),
                     chambers=[relPath(wp,p) for p in chambers],shellports=[relPath(wp,p) for p in shellports],
                     open=dict(bones=openBones,clip=openClip,times=[round(keyTimes[0],3),round(keyTimes[-1],3)]),lever=leverData,
                     foreEnd=foreEnd,loadHold=loadHold,pluckHold=pluckHold,**pairs)
    for key,hold in pairs.items():
        print(f'  {key} at {hold["t"]}s: shells {np.linalg.norm(np.array(hold["position"])-np.array(hold["position2"]))*100:.1f} cm apart in the palm')
    print(f'  open {openDegrees:.1f} deg ({hingeDegrees:.1f} about {np.round(hingeAxis,3)}) {keyTimes[0]:.3f}-{keyTimes[-1]:.3f}s, bones {[b["path"] for b in openBones]}')
    print(f'  lever {leverData["degrees"] if leverData else None}, bore {np.round(bore,3)}, chambers {len(chambers)}, shellports {len(shellports)}')
    print(f'  fore-end palm {np.round(palmIn*100,1)} cm (drift over the close {drift*100:.1f} cm), grab {np.round(grab*100,1)} at {tHold:.2f}s; '
          f'load hold at {loadHold["t"] if loadHold else None} ({best[2]*100:.1f} cm out, {best[3]*100:.1f} off the bore), pluck hold at {pluckHold["t"]}' if best else '  NO load hold')
json.dump(dict(breakActions=out),open('breakactions.json','w'),separators=(',',':'))
print('wrote breakactions.json',os.path.getsize('breakactions.json'),'bytes')
