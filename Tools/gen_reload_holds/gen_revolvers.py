# Revolvers (Chiappa Rhino 50DS / 200DS, RSh-12, MTs-255-12), from each gun's own clips (VRReloadController.BreakAction's revolver side):
#  - crane: the bone the cylinder swings out on, keyed by its own turn in the open clip like a break-action's barrel (hinge axis in its
#    closed local frame, + opening). The cylinder's own spin in that clip is its indexing: left to the animator, as are the round bones.
#  - release: the cylinder release under the gun hand's thumb, its turn (Rhino) or slide (MTs-255) over the open clip, keyed; the
#    pressing finger as gen_breakactions' leverPress
#  - ejector: every bone the eject-all clip moves as the extractor goes out (the MTs-255's lever too), keyed by the extractor's travel;
#    that travel (local, its parent = the cylinder); the left palm on it as it starts (in the extractor bone's frame) and the fingertip
#    touching it then (in the cylinder bone's frame)
#  - holds (the left hand works all of it in these clips): the palm on the cylinder as the close clip's hand pushes it home (crane
#    frame, + where the fingers close); a new round in the palm lined up behind its chamber; a fired case drawn part way out
# Weapon frame = Weapon_root_anim/weapon. Writes revolvers.json; gen_export.py merges it. Run from this folder.
import sys, os, re, json
exec(open('gen_breakactions.py',encoding='utf-8').read().split('\nout={}')[0])

# family: folder, container, crane, cylinder, extractor, release, open clip, close clip, eject-all clip, pluck clip, load clip
REVOLVERS={
    'rhino':('rhino','weapon_chiappa_rhino_50ds_9x33r_container.bundle','weapon_crane','weapon_crane/mod_magazine','weapon_crane/mod_magazine/weapon_extractor',
             'weapon_mag_release','rhino_reload_start_empty_1','rhino_reload_end_1','rhino_reload_start_all_1','rhino_shell_eject_1','rhino_shell_insert_1'),
    'rsh12':('rsh12','weapon_kbp_rsh_12_127x55_container.bundle','weapon_slide/weapon_cylinder_axis','weapon_slide/weapon_cylinder_axis/mod_magazine',
             'weapon_slide/weapon_cylinder_axis/mod_magazine/weapon_extractor','weapon_slide/weapon_mag_release',
             'rsh12_reload_start_0','rsh12_reload_end_0_0','rsh12_reload_start_all_0','rsh12_shell_out_0','rsh12_ammo_in_0'),
    'mc255':('mc255','weapon_ckib_mc_255_12g_container.bundle','weapon_cylinder_crane','weapon_cylinder_crane/mod_magazine',
             'weapon_cylinder_crane/mod_magazine/weapon_cylinder_extractor','weapon_cylinder_release',
             'mc255_reload_start','mc255_reload_end','mc255_reload_start_all','mc255_shell_extract_0','mc255_shell_insert_0'),
}
EJECT_KEYS=6
ROUND_BONE=re.compile(r'/(patron_in_weapon|shellport)(_\d+)?$')

# A bone's travel off its pose at t0 (m), per sample
def travelTrack(g,path,ts):
    a0=np.array(g['localAt'](path,ts[0])[0],float)
    return [float(np.linalg.norm(np.array(g['localAt'](path,t)[0],float)-a0)) for t in ts]

def movedIn(g,P,wp,ts,i0,i1,skip):
    moved=[]
    for p in P:
        if not p.startswith(wp+'/') or NOT_PART.search(p.split('/')[-1]) or ROUND_BONE.search(p) or any(p==s or p.startswith(s+'/') for s in skip): continue
        a0,r0=[np.array(v,float) for v in g['localAt'](p,ts[i0])[:2]]; most=0.0; turned=0.0
        for i in range(i0,i1+1,4):
            a,r=[np.array(v,float) for v in g['localAt'](p,ts[i])[:2]]; most=max(most,float(np.linalg.norm(a-a0))); turned=max(turned,ang(r0,r))
        if most>=0.0005 or turned>=1.0: moved.append(p)
    return moved

out={}
for family,(folder,container,craneLeaf,cylinderLeaf,extractorLeaf,releaseLeaf,openClip,closeClip,ejectClip,pluckClip,loadClip) in REVOLVERS.items():
    print(family,flush=True)
    tree=loadTree(ROOT+folder+'/'+container); P=tree[0]; clips=clipsOf(folder)
    wp=bone(P,'Weapon_root_anim/weapon'); W=lambda leaf: bone(P,'weapon/'+leaf)
    crane,cylinder,extractor,release=W(craneLeaf),W(cylinderLeaf),W(extractorLeaf),W(releaseLeaf)
    palm=bone(P,'/Base HumanLPalm'); J=fingerPaths(P,'L')
    def numbered(name): return sorted((p for p in P if p.startswith(cylinder+'/') and re.search(r'/'+name+r'_\d+$',p)),key=lambda p:int(p.rsplit('_',1)[1]))
    chambers=numbered('patron_in_weapon'); shellports=numbered('shellport')

    # The open: the crane, keyed by its turn
    g=setup(tree,clips[openClip]); ts=times(clips[openClip])
    craneTurn=turnTrack(g,crane,ts); iRest,iTop,openDegrees=swingWindow(craneTurn,ts)
    keyTimes=[timeAtShare(craneTurn,ts,iRest,iTop,k/(OPEN_KEYS-1)) for k in range(OPEN_KEYS)]
    q0=np.array(g['localAt'](crane,ts[iRest])[1],float); qOpen=np.array(g['localAt'](crane,ts[iTop])[1],float)
    hingeAxis,hingeDegrees=axisAngle(qmul(conj(q0),qOpen))
    openBones=boneKeys(g,[crane]+movedIn(g,P,wp,ts,iRest,iTop,[crane,cylinder,release]),keyTimes,wp)

    # The release: its turn, or its slide (mm), over the open clip
    turn=turnTrack(g,release,ts); slide=[d*1000 for d in travelTrack(g,release,ts)]
    track,unit=(turn,'deg') if max(turn)>=3 else (slide,'mm')
    releaseData=None
    if max(track)>=0.5:
        jRest,jTop,most=swingWindow(track,ts)
        releaseTimes=[timeAtShare(track,ts,jRest,jTop,k/(LEVER_KEYS-1)) for k in range(LEVER_KEYS)]
        releaseData=dict(degrees=round(most,1),bones=boneKeys(g,[release],releaseTimes,wp),press=leverPress(g,P,folder,container,release,ts,tTop=ts[jTop]))
    print(f'  release {most if releaseData else max(track):.1f} {unit}{"" if releaseData else " - too little, none"}')

    # The ejector: keyed by the extractor's travel in the eject-all clip
    ge=setup(tree,clips[ejectClip]); te=times(clips[ejectClip])
    travel=travelTrack(ge,extractor,te); eRest,eTop,ejectTravel=swingWindow(travel,te,rest=0.0003)
    ejectTimes=[timeAtShare(travel,te,eRest,eTop,k/(EJECT_KEYS-1)) for k in range(EJECT_KEYS)]
    # (with the bones riding the extractor's push besides it: the MTs-255's lever, under the crane; not the crane or cylinder, which
    # may still be swinging out)
    others=[p for p in movedIn(ge,P,wp,te,eRest,eTop,[extractor,release]) if p!=crane and p!=cylinder and not cylinder.startswith(p+'/')]
    ejectBones=boneKeys(ge,[extractor]+others,ejectTimes,wp)
    a0=np.array(ge['localAt'](extractor,te[eRest])[0],float); a1=np.array(ge['localAt'](extractor,te[eTop])[0],float)
    ejectVector=a1-a0
    # The left hand on it as it starts out (10% of its travel): the fingertip nearest any of the ejector's meshes (the MTs-255's is on
    # its lever) = the press bone, which the hand rides; the palm in its frame, where the fingers close, and that fingertip's way over the
    # push in the crane's frame (the hand's push is measured along it)
    tStart=timeAtShare(travel,te,eRest,eTop,0.1)
    ejectPaths=[extractor]+others
    meshes={p:boneMeshPoints(folder,container,p.split('/')[-1]) for p in ejectPaths}
    c={}
    best=None
    for p in ejectPaths:
        bp,br,_=ge['worldAt'](p,tStart,c)
        for finger in range(5):
            jp,jr,_=ge['worldAt'](J[3+3*finger],tStart,c); tip=jp+ge['rotate'](jr,TIP)
            inBone=ge['rotate'](conj(br),tip-bp)
            off=float(np.min(np.linalg.norm(meshes[p]-inBone,axis=1))) if meshes[p] is not None else float(np.linalg.norm(inBone))
            if best is None or off<best[0]: best=(off,finger,p,inBone)
    off,finger,pressBone,tipInBone=best
    # (the palm in the press bone's frame, as the cylinder hold: holdIn is the other way round, a held thing in the palm's frame)
    palmOnPress,palmOnPressQ=inFrame(ge,pressBone,palm,tStart,c)
    ejectHold=dict(position=r5(palmOnPress),rotation=r5(palmOnPressQ/np.linalg.norm(palmOnPressQ)),fingers=holdIn(ge,palm,palm,tStart,J)['fingers'],t=round(float(tStart),3))
    grabPoint=np.mean([inFrame(ge,pressBone,j,tStart,c)[0] for j in J[1:]],axis=0)
    def tipInCrane(t):
        cc={}; bp,br,_=ge['worldAt'](pressBone,t,cc); kp,kr,_=ge['worldAt'](crane,t,cc)
        return ge['rotate'](conj(kr),bp+ge['rotate'](br,tipInBone)-kp)
    contactStart,contactEnd=tipInCrane(te[eRest]),tipInCrane(te[eTop])
    palm0=inFrame(ge,pressBone,palm,tStart,{})[0]; drift=max(float(np.linalg.norm(inFrame(ge,pressBone,palm,te[i],{})[0]-palm0)) for i in range(eRest,eTop+1))
    print(f'  ejector {ejectTravel*100:.2f} cm along {np.round(ejectVector/np.linalg.norm(ejectVector),3)} ({te[eRest]:.2f}-{te[eTop]:.2f}s), bones {[b["path"] for b in ejectBones]}; '
          f'{["thumb","index","middle","ring","pinky"][finger]} on {pressBone.split("/")[-1]}, {off*100:.1f} cm off its mesh at {tStart:.2f}s, the push moves it '
          f'{np.linalg.norm(contactEnd-contactStart)*100:.1f} cm, the palm drifts {drift*100:.1f} cm on it')
    ejector=dict(bones=ejectBones,bone=relPath(wp,extractor),travel=r5(ejectVector),pressBone=relPath(wp,pressBone),
                 hold=dict(ejectHold,grabPoint=r5(grabPoint)),contact=r5(contactStart),contactEnd=r5(contactEnd))

    # The bore: the pluck clip's case for its first 1.5 cm out of its chamber, in the cylinder's frame
    gp=setup(tree,clips[pluckClip]); tp=times(clips[pluckClip]); shell=shellports[0]
    starts=[inFrame(gp,cylinder,shell,t,{})[0] for t in tp]
    iOut=next(i for i,s in enumerate(starts) if np.linalg.norm(s-starts[0])>=0.015)
    iMove=max(i for i in range(iOut) if np.linalg.norm(starts[i]-starts[0])<=0.002)
    bore=starts[iOut]-starts[iMove]; bore/=np.linalg.norm(bore)
    iMid=next(i for i in range(iMove,iOut+1) if np.linalg.norm(starts[i]-starts[iMove])>=0.01)
    pluckHold=holdIn(gp,palm,shell,tp[iMid],J)
    c={}; _,cylRot,_=gp['worldAt'](cylinder,tp[iMove],c); _,boneRot,_=gp['worldAt'](chambers[0],tp[iMove],c)
    boneUp=gp['rotate'](qmul(conj(cylRot),boneRot),np.array([0,1.0,0]))
    print(f'  bore {np.round(bore,3)} in the cylinder, {math.degrees(math.acos(min(1,float(np.dot(bore,boneUp))))):.1f} deg off the chamber bone\'s +y; chambers {len(chambers)}, shell ports {len(shellports)}')

    # The load hold: the round lined up 2-8 cm behind its chamber, nearest 4 cm
    gl=setup(tree,clips[loadClip]); tl=times(clips[loadClip]); chamber=chambers[0]
    seat=inFrame(gl,cylinder,chamber,tl[-1],{})[0]
    bestLoad=None
    for t in tl:
        d=inFrame(gl,cylinder,chamber,t,{})[0]-seat; along=float(np.dot(d,bore)); offBore=float(np.linalg.norm(d-along*bore))
        if offBore<=0.012 and 0.02<=along<=0.08 and (bestLoad is None or abs(along-0.04)<bestLoad[0]): bestLoad=(abs(along-0.04),t,along,offBore)
    loadHold=holdIn(gl,palm,chamber,bestLoad[1],J) if bestLoad else None
    # The carry: the round in the fingers as the clip's hand brings it over, 15 cm or more from its chamber, where it sits stillest in
    # the palm (the hold in the hand before it's lined up; the load hold is the push behind it)
    def inPalm(t): return inFrame(gl,palm,chamber,t,{})
    def tipOff(t):
        c={}; rp,_,_=gl['worldAt'](chamber,t,c)
        return min(float(np.linalg.norm(gl['worldAt'](J[3+3*f],t,c)[0]+gl['rotate'](gl['worldAt'](J[3+3*f],t,c)[1],TIP)-rp)) for f in range(5))
    carry=None
    for i in range(1,len(tl)-1,4):
        t=tl[i]; d=inFrame(gl,cylinder,chamber,t,{})[0]-seat
        if np.linalg.norm(d)<0.15 or tipOff(t)>0.025: continue
        (p0,q0),(p1,q1)=inPalm(tl[max(i-8,0)]),inPalm(tl[min(i+8,len(tl)-1)])
        still=float(np.linalg.norm(p1-p0))*100+ang(q0,q1)/5
        if carry is None or still<carry[0]: carry=(still,t)
    carryHold=holdIn(gl,palm,chamber,carry[1],J) if carry else None
    if carryHold and loadHold:
        print(f'  carry hold at {carry[1]:.2f}s, {np.linalg.norm(np.array(carryHold["position"])-np.array(loadHold["position"]))*100:.1f} cm / '
              f'{ang(np.array(carryHold["rotation"]),np.array(loadHold["rotation"])):.0f} deg off the load hold')

    # The cylinder in the left palm as the close clip pushes it home
    gc=setup(tree,clips[closeClip]); tc=times(clips[closeClip])
    closeTurn=turnTrack(gc,crane,tc); kStart=next(i for i,a in enumerate(closeTurn) if a>0.5)
    tHold=tc[max(kStart-1,0)]
    palmIn,_=inFrame(gc,crane,palm,tHold,{})
    kEnd=next((i for i in range(kStart,len(tc)) if abs(closeTurn[i]-max(closeTurn))<0.3),len(tc)-1)
    drift=max(float(np.linalg.norm(inFrame(gc,crane,palm,tc[i],{})[0]-palmIn)) for i in range(kStart,kEnd+1))
    c={}; palmIn,palmQ=inFrame(gc,crane,palm,tHold,c)
    grab=np.mean([inFrame(gc,crane,j,tHold,c)[0] for j in J[1:]],axis=0)
    cylinderHold=dict(position=r5(palmIn),rotation=r5(palmQ/np.linalg.norm(palmQ)),fingers=holdIn(gc,palm,palm,tHold,J)['fingers'],grabPoint=r5(grab),t=round(float(tHold),3))

    out[family]=dict(revolver=True,barrel=relPath(wp,crane),cylinder=relPath(wp,cylinder),hingeAxis=r5(hingeAxis),openDegrees=round(hingeDegrees,2),bore=r5(bore),
                     chambers=[relPath(wp,p) for p in chambers],shellports=[relPath(wp,p) for p in shellports],
                     open=dict(bones=openBones,clip=openClip,times=[round(keyTimes[0],3),round(keyTimes[-1],3)]),lever=releaseData,
                     foreEnd=cylinderHold,loadHold=loadHold,carryHold=carryHold,pluckHold=pluckHold,ejector=ejector)
    c={}; fp,_,_=g['worldAt'](bone(P,'/fireport'),ts[0],c); wpp,wpr,_=g['worldAt'](wp,ts[0],c); cyp,_,_=g['worldAt'](cylinder,ts[0],c); kp,kr,_=g['worldAt'](crane,ts[0],c)
    print(f'  weapon frame: fireport {np.round(g["rotate"](conj(wpr),fp-wpp)*100,1)} cm, cylinder {np.round(g["rotate"](conj(wpr),cyp-wpp)*100,1)}, crane {np.round(g["rotate"](conj(wpr),kp-wpp)*100,1)}, '
          f'crane rotation in it {np.round(qmul(conj(wpr),kr),3)}')
    print(f'  crane {openDegrees:.1f} deg ({hingeDegrees:.1f} about {np.round(hingeAxis,3)}) {keyTimes[0]:.3f}-{keyTimes[-1]:.3f}s, open bones {[b["path"] for b in openBones]}')
    print(f'  cylinder hold palm {np.round(palmIn*100,1)} cm (drift over the close {drift*100:.1f} cm), grab {np.round(grab*100,1)} at {tHold:.2f}s; '
          f'load hold {"at %.2fs, %.1f cm out, %.1f off the bore" % (bestLoad[1],bestLoad[2]*100,bestLoad[3]*100) if bestLoad else "NONE"}, pluck hold at {pluckHold["t"]}')
json.dump(dict(revolvers=out),open('revolvers.json','w'),separators=(',',':'))
print('wrote revolvers.json',os.path.getsize('revolvers.json'),'bytes')
