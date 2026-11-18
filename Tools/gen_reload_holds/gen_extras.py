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
# A content mod's containers' own clips (gen_census with CONTAINER_CLIPS): only sets the game's clips don't have
for folder,v in census.items():
    for c in v.get('containerClips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',magClipName(folder,c))
        if m: famFolder.setdefault(m.group(1).lower(),folder)
from game_paths import BUNDLES as GAME_BUNDLES
ITEM_BUNDLES=GAME_BUNDLES+'assets/content/items/mods/'

def loadFolder(folder):
    d=ROOT+folder+'/'; clips=folderClips(folder)
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
# the receiver, so the grab zone is where the clip's fingers close on it, HANDLE_GRAB_AT_HOLD). AVT-40 / SVT-40 (2026-11-07, both rigs one
# animation set avt40): the carrier itself, 10 cm straight back in avt40_charge_* (left hand over the top) and rechamber (right hand),
# its knob part of the carrier mesh (HANDLE_KNOB_SIDE). Left out: the RPD (no clip moves its handle
# bones), the M60 (no rack clip), the VSK-94 (no clips of its own), the AGS-30 and NSV (mounted).
# gen_holds' own left/right hold search on that bone, merged into its handles by gen_export; handleBones tells the runtime the bone.
HANDLE_BONE_RIGS={'hk_mp7':'weapon_charghing_handle','dsa_sa58':'weapon_charghing_handle','hk_ump':'weapon_charge','tdi_kriss_vector':'weapon_charge',
                  'zid_pkm':'weapon_charge','kel_tec_rfb':'weapon_charge','iwi_uzi_9x19':'weapon_charge','sword_int_mk_18':'weapon_charge','r11_rsass':'weapon_charghing_000',
                  'tochmash_sr2m':'weapon_charging_handle','mps_aa_12':'weapon_charge','dt_mdr':'weapon_charge_000','springfield_m1a':'weapon_bolthandle',
                  'kbp_9a91':'weapon_bolt_charge','ckib_ash_12':'weapon_charging_handle','hk_g36':'weapon_charge_handle',
                  'zmz_pp-9':'weapon_bolt','zis_ppsh41':'weapon_bolt','iwi_uzi_pro':'weapon_bolt','vepr_hunter_vpo-101':'weapon_bolt',
                  'toz_avt_40':'weapon_bolt_carrier','toz_svt_40':'weapon_bolt_carrier',
                  'nb_val_mod4':'weapon_charge','norinco_qbz191':'weapon_bolt_carrier',
                  # mod_charge, but every one of its racks is a two-frame yank gen_holds' riding test can't read: the fast-yank rule below
                  # (the left thumb and index stay 2 cm off the handle's knob through the pull)
                  'howa_type20':'mod_charge'}
# (the SR-2M's knob bone rides weapon_bolt_carrier, 9.9 cm back with it: moving the knob alone left the carrier and bolt behind)
HANDLE_CARRIERS={'kbp_9a91':'weapon_bolt','ckib_ash_12':'weapon_bolt','hk_g36':'weapon_bolt_carrier','tochmash_sr2m':'weapon_bolt_carrier'}
# (the 9A-91's and ASh-12's knob bones carry a mesh named bolt_rotation, not a handle's: their zone where the clip's fingers close too)
HANDLE_GRAB_AT_HOLD=('zmz_pp-9','zis_ppsh41','iwi_uzi_pro','vepr_hunter_vpo-101','kbp_9a91','ckib_ash_12')
# Ambidextrous: a knob on each side (the MDR's: a rod running back into the gun, a folding paddle each side at its front, on bones of
# their own). The zone on each paddle, the box centre of its mesh in the handle bone's frame, left first. Was the clip's palm bone
# (HANDLE_GRAB_AT_HOLD), but Tarkov's MDR racks underhand: that's the wrist 10 cm below and 6 cm left of the knob (headset 2026-11-07,
# a picture: the zone far off to the side, "should be on charging handle in middle of gun").
HANDLE_KNOB_PAIRS={'dt_mdr':('weapon_charge_002','weapon_charge_012')}
# Over the top, for a palm above the handle (his ask: "the m4 pose for grabbing the handle if your palm is above the handle"; every
# MDR clip racks underhand, the left hand under the left paddle): the M4's hold (m4a1_reload_charge_0, the index curled over the T's
# left wing, thumb behind it) moved onto the left paddle. The wing in the M4's mod_charge frame, from the Colt handle's mesh (its bar
# runs forward, mounted turned 90 deg about x): box centre and outer edge. Outer edges matched across (the hand comes from outside),
# centres along and up the gun.
OVER_TOP_FROM_M4=('dt_mdr',)
AR_WING_CENTRE=np.array([-0.0226,-0.0098,0.0038]); AR_WING_OUTER_X=-0.0272
def meshPartBox(folder,container,frameLeaf,partLeaf):
    from UnityPy.helpers.MeshHelper import MeshHandler
    env=UnityPy.load(ROOT+folder+'/'+container,ROOT+folder+'/client_assets.bundle')
    def toParent(t,p):
        s=t.m_LocalScale; r=t.m_LocalRotation; a=t.m_LocalPosition; q=np.array([r.x,r.y,r.z,r.w]); v=p*np.array([s.x,s.y,s.z])
        return v+2*np.cross(q[:3],np.cross(q[:3],v)+q[3]*v)+np.array([a.x,a.y,a.z])
    # The part's mesh vertices in the frame bone's frame; the rig holds it more than once: the copy with the most vertices (LOD0)
    best=[]
    for o in env.objects:
        if o.type.name!='Transform': continue
        t=o.read()
        if t.m_GameObject.read().m_Name!=frameLeaf: continue
        points=[]
        def walk(tr,chain,inPart):
            inPart=inPart or tr.m_GameObject.read().m_Name==partLeaf
            if inPart:
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
                c=ch.read(); walk(c,[c]+chain,inPart)
        walk(t,[],False)
        if len(points)>len(best): best=points
    if not best: return None
    v=np.array(best); return v.min(0),v.max(0)
# A knob sticking out of the carrier where the clip's palm bone is far off it (AVT/SVT: the left hand reaches over the top, its palm
# 15 cm from the knob; the fingertips on it): the grab zone on the knob itself, the centre of the bone's mesh vertices within 1 cm of
# its furthest point to that side, in the bone's frame. (axis, sign): the AVT/SVT knob is the carrier's +x, (4.5,-4.6,-1.5) cm.
# The RFB's forward handle is a rod running back into the gun with the knob out its -x (left) side, (-4.8,-2.9,0.8) cm: its furthest
# point back (the runtime's default zone) is 14 cm behind the knob on the gun's centre line.
# The backport's QBZ-191: its handle is the carrier's knob out the +x (right) side, (4.1,-4.0,-0.1) cm; its back end is 16 cm behind it.
HANDLE_KNOB_SIDE={'toz_avt_40':(0,1),'toz_svt_40':(0,1),'kel_tec_rfb':(0,-1),'dsa_sa58':(0,-1),'norinco_qbz191':(0,1)}
# The gun hand's hold from Tarkov's own right-hand rack where ridingHold turns it down: the AVT/SVT's charge_0/1 and rechamber rack with
# the RIGHT hand (index 2-3 cm off the knob all the way back; charge_2 is the left-hand one), 10 cm in 4 frames with the palm pivoting
# ~2 cm about the fingers - over the 1.5 cm limit, so it fell to jam_hardslide (8.1 cm, a stuck slide). Headset 2026-11-07, a picture of
# the SVT's rack: that pose. Read at full pull (fullBackTime: the middle of the frames within 2 mm of all the way back).
# The SR-2M's: its chambering clip (sr2m_load_one, the handle held back while the round goes in), the right hand on the knob; its racks
# are all the left's.
GUN_HAND_HOLD_CLIP={'toz_avt_40':'avt40_charge_1','toz_svt_40':'avt40_charge_1','tochmash_sr2m':'sr2m_load_one'}
# A knob the clips' hand never quite reaches (the SR-2M's, 2.5 cm long: the hand 2-8 cm off it in every clip, so the hold floated beside
# it): the hold moved onto it, the pose as it is. The off hand's (its clip has the knob between the thumb and index): their tips' midpoint
# on the knob's middle. The gun hand's (every finger hooked in front of the knob, 3-5 cm ahead): the index tip on the knob's face.
PINCH_ON_KNOB=('tochmash_sr2m',)
HOOK_ON_KNOB=('tochmash_sr2m',)
# A bone's meshes in its own frame, with the container's dependency bundles (Windows.json) where the meshes live there
def boneMeshPoints(folder,container,boneLeaf):
    from UnityPy.helpers.MeshHelper import MeshHandler
    key='assets/content/weapons/'+folder+'/'+container
    manifest=json.load(open(GAME_BUNDLES+'Windows.json',encoding='utf-8'))
    deps=[x for x in manifest.get(key,{}).get('Dependencies',[]) if x.endswith('.bundle') and not re.search('audio|animations|textures|physics|hands',x) and os.path.exists(GAME_BUNDLES+x)]
    env=UnityPy.load(ROOT+folder+'/'+container,*[GAME_BUNDLES+x for x in deps])
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
            name=tr.m_GameObject.read().m_Name
            for c in tr.m_GameObject.read().m_Components:
                ptr=c.component if hasattr(c,'component') else c
                try: comp=ptr.read()
                except Exception: continue
                if type(comp).__name__!='MeshFilter' or ('LOD' in name and 'LOD0' not in name): continue
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
    return np.array(best) if best else None
# The hold's palm moved onto the knob (its mesh points, handle frame), from the fingertips at the hold's moment in its clip: pinch = the
# thumb and index tips' midpoint on the knob's middle, hook = the index tip on the knob's nearest point
def holdOntoKnob(hold,g,P,charge,side,knob,mode):
    J=fingerPaths(P,side); c={}; t=hold['t']
    cp,cr,_=g['worldAt'](charge,t,c); tips=[]
    for finger in (0,1):
        last,_,_=g['worldAt'](J[1+finger*3+2],t,c); before,_,_=g['worldAt'](J[1+finger*3+1],t,c)
        tips.append(g['rotate'](conj(cr),last+(last-before)*0.8-cp))
    if mode=='pinch':
        shift=knob.mean(0)-(tips[0]+tips[1])/2
    else:
        shift=knob[np.linalg.norm(knob-tips[1],axis=1).argmin()]-tips[1]
    hold['position']=[round(float(x),4) for x in np.array(hold['position'])+shift]
    return shift
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
    for clip in v.get('clips',[])+v.get('containerClips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',magClipName(folder,clip))
        if matching and m:
            family=m.group(1).lower()
            HANDLE_BONE_GUNS[family]=(folder,)+next(((c,b,k) for c,b,k in matching if family in c),matching[0])
extraHandles={}; extraGunHandHandles={}; handleBones={}; handleCarriers={}; handleGrabPoints={}; extraMovesWithBolt=[]; namedTrees={}
handleKnobs={}; handleOverTop={}; handleKnobFolds={}
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
    if rig in PINCH_ON_KNOB and family in extraHandles and 't' in extraHandles[family]:
        knobPoints=boneMeshPoints(folder,container,boneName)
        if knobPoints is not None:
            shift=holdOntoKnob(extraHandles[family],setup(tree,clips[extraHandles[family]['clip']]),P,charge,'L',knobPoints,'pinch')
            print(f'{family} off-hand hold moved {np.round(shift*100,1)} cm: the knob between the thumb and index',flush=True)
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
        if rig in HOOK_ON_KNOB:
            knobPoints=boneMeshPoints(folder,container,boneName)
            if knobPoints is not None:
                shift=holdOntoKnob(h,setup(tree,clips[h['clip']]),P,charge,'R',knobPoints,'hook')
                print(f'{family} gun-hand hold moved {np.round(shift*100,1)} cm: the index on the knob',flush=True)
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
    # Where the clip's fingers close on the handle (their joints' centroid with it all the way back, in the handle bone's frame), on the
    # nearest point of the handle's own meshes. Not the hold's palm: that's the hand root, 10-18 cm off the handle when the clip racks
    # from below (the VPO-101's AK handle: 18 cm under the gun, out of reach)
    if rig in HANDLE_GRAB_AT_HOLD and grabHold:
        held=setup(tree,clips[grabHold['clip']]); heldAt,_=fullBackTime(held,charge,wp); c={}
        handleAt,handleRotation,_=held['worldAt'](charge,heldAt,c)
        side='L' if grabHold is extraHandles.get(family) else 'R'
        closing=np.mean([held['rotate'](conj(handleRotation),held['worldAt'](j,heldAt,c)[0]-handleAt) for j in fingerPaths(P,side)[1:]],axis=0)
        knobPoints=boneMeshPoints(folder,container,boneName)
        point=closing if knobPoints is None else knobPoints[np.argmin(np.linalg.norm(knobPoints-closing,axis=1))]
        handleGrabPoints[family]=[round(float(x),4) for x in point]
        print(f'{family} grab point {np.round(point*100,1)} cm (fingers close {np.linalg.norm(point-closing)*100:.1f} cm off it; '
              f'the hold\'s palm {np.linalg.norm(np.array(grabHold["position"])-point)*100:.1f} cm)',flush=True)
    if rig in HANDLE_KNOB_SIDE:
        knob=meshKnobPoint(folder,container,boneName,*HANDLE_KNOB_SIDE[rig])
        if knob: handleGrabPoints[family]=knob
    if rig in HANDLE_KNOB_PAIRS:
        boxes=[meshPartBox(folder,container,boneName,part) for part in HANDLE_KNOB_PAIRS[rig]]
        if all(boxes):
            handleKnobs[family]=[[round(float(x),4) for x in (lo+hi)/2] for lo,hi in boxes]
            handleGrabPoints[family]=handleKnobs[family][0]
            m4=R['handles'].get('m4a1')
            if rig in OVER_TOP_FROM_M4 and m4 and family in extraHandles:
                lo,hi=boxes[0]; shift=(lo+hi)/2-AR_WING_CENTRE; shift[0]=lo[0]-AR_WING_OUTER_X
                handleOverTop[family]=dict(position=[round(float(x),4) for x in np.array(m4['position'])+shift],rotation=m4['rotation'],
                                           fingers=m4['fingers'],travel=extraHandles[family]['travel'])
            print(f'{family} knobs {handleKnobs[family]}, over the top {handleOverTop.get(family,{}).get("position")}',flush=True)
            # Each paddle folds out about its pin as the hand takes it (his ask: "make the paddles fold out when you grab them"). The
            # clips only ever fold the left one (45 deg, the hand under it): its turn at full pull, in its own frame; the right one's is
            # that turn mirrored across the gun's centre plane (the handle frame's x: the paddles sit at +-x), back in its own frame.
            hold=extraHandles.get(family)
            if hold:
                g=setup(tree,clips[hold['clip']]); t,_=fullBackTime(g,charge,wp)
                left,right=(bone(P,'/'+part) for part in HANDLE_KNOB_PAIRS[rig])
                foldLocal=qmul(conj(np.array(g['localAt'](left,0.0)[1],float)),np.array(g['localAt'](left,t)[1],float))
                c={}; _,chargeRot,_=g['worldAt'](charge,0.0,c); _,leftRot,_=g['worldAt'](left,0.0,c); _,rightRot,_=g['worldAt'](right,0.0,c)
                leftInCharge=qmul(conj(chargeRot),leftRot); rightInCharge=qmul(conj(chargeRot),rightRot)
                foldCharge=qmul(qmul(leftInCharge,foldLocal),conj(leftInCharge))
                mirrored=np.array([foldCharge[0],-foldCharge[1],-foldCharge[2],foldCharge[3]])
                foldRight=qmul(qmul(conj(rightInCharge),mirrored),rightInCharge)
                def axisAngle(q):
                    q=q if q[3]>=0 else -q; s=np.linalg.norm(q[:3])
                    return [round(float(x),4) for x in (q[:3]/s if s>1e-9 else np.array([1.0,0,0]))],round(2*math.degrees(math.atan2(s,q[3])),1)
                handleKnobFolds[family]=[dict(bone=name,axis=axisAngle(q)[0],angle=axisAngle(q)[1]) for name,q in
                                         ((HANDLE_KNOB_PAIRS[rig][0],foldLocal),(HANDLE_KNOB_PAIRS[rig][1],foldRight))]
                print(f'{family} paddle folds {handleKnobFolds[family]} (clip {hold["clip"]} t {t:.2f})',flush=True)
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
# Press checks (his ask 2026-11-07: thumb on A/B/stick + finger on the trigger with the grip let go = the gun hand pinches the back
# of the slide, the trigger draws it back far enough to see the round). Tarkov's own chamber check (<prefix>_check_chamber): the
# gun hand stays on the grip, the OTHER hand pinches the slide over the top and it pops back ~2 cm (1911: 2.04) and holds. Read
# at the middle of that hold: the hand that moved most off the gun (L in every one checked), its palm in the slide bone's frame,
# its fingers, and how far back the slide is. The runtime mirrors a left hand onto the gun hand. The Glock 17's folder has no
# check clip: the 18C's (same slide, same rig layout).
PRESS_CHECK_FROM={'glock':'glock18c'}
pressChecks={}
for family,slideHold in sorted(R['slides'].items()):
    source=PRESS_CHECK_FROM.get(family,family)
    folder=famFolder.get(source)
    if folder is None: continue
    clipNames=census.get(folder,{}).get('clips',[])
    clipName=next((n for n in clipNames if n.lower()==source+'_check_chamber'),None)
    if clipName is None: continue
    tree=None
    for c in sorted(os.listdir(ROOT+folder)):
        if not (c.endswith('_container.bundle') and c.startswith('weapon_')): continue
        candidate=loadTree(ROOT+folder+'/'+c)
        if bone(candidate[0],'/'+slideHold['bone']) and bone(candidate[0],'/Base HumanLPalm') and bone(candidate[0],'/Base HumanRPalm'):
            tree=candidate; break
    if tree is None: continue
    clip=None
    for o in UnityPy.load(ROOT+folder+'/client_assets.bundle').objects:
        if o.type.name!='AnimationClip': continue
        try: candidateClip=o.read()
        except Exception: continue
        if candidateClip.m_Name==clipName:
            clip=candidateClip; break
    if clip is None: continue
    P=tree[0]; slide=bone(P,'/'+slideHold['bone']); wp=bone(P,'Weapon_root_anim/weapon'); g=setup(tree,clip)
    stop=float(clip.m_MuscleClip.m_StopTime); rows=[]
    for i in range(int(stop*30)+1):
        t=i/30; c={}
        sp,_,_=g['worldAt'](slide,t,c); wpp,wr,_=g['worldAt'](wp,t,c)
        rows.append((t,g['rotate'](conj(wr),sp-wpp)))
    rest=rows[0][1]; backs=[(t,float(p[1]-rest[1])) for t,p in rows]; top=max(b for _,b in backs)
    if top<0.005: continue
    plateau=[t for t,b in backs if b>=top-0.002]; t=plateau[len(plateau)//2]
    def palmInGun(side,time):
        c={}; pp,_,_=g['worldAt'](bone(P,f'/Base Human{side}Palm'),time,c); wpp,wr,_=g['worldAt'](wp,time,c); return g['rotate'](conj(wr),pp-wpp)
    side=max('LR',key=lambda s:float(np.linalg.norm(palmInGun(s,t)-palmInGun(s,0.0))))
    c={}; sp,sr,_=g['worldAt'](slide,t,c); pp,pr,_=g['worldAt'](bone(P,f'/Base Human{side}Palm'),t,c)
    pressChecks[family]=dict(bone=slideHold['bone'],hand=side,position=[round(float(x),4) for x in g['rotate'](conj(sr),pp-sp)],
        rotation=toUnityEuler(qmul(conj(sr),pr)),fingers=eulerList([np.array(g['localAt'](j,t)[1],float) for j in fingerPaths(P,side)]),
        travel=round(top,4),clip=clipName,t=round(t,3))
    print(f'{family} press check: {side} hand, slide back {top*100:.2f} cm, palm {pressChecks[family]["position"]} ({clipName} t {t:.2f})',flush=True)
LOW_ROCK_GUNS=('ak50',)
rockIns={}
for family in LOW_ROCK_GUNS:
    tree,clips=loadFolder(famFolder[family]); mag=bone(tree[0],'/mod_magazine')
    rocks=rockIn(tree,clips,family,mag,minAngle=5) if mag else None
    if rocks:
        rockIns[family]=dict(axis=rocks['axis'],angle=rocks['angle'],thumb=rocks['thumb'])
        print(f'{family} rock-in:',{k:v for k,v in rocks.items() if k!='thumb'},flush=True)
json.dump(dict(boltBones=boltBones,sideHandles=sideHandles,rockIn=rockIns,handles=extraHandles,gunHandHandles=extraGunHandHandles,
               handleBones=handleBones,handleCarriers=handleCarriers,handleGrabPoints=handleGrabPoints,handleMovesWithBolt=extraMovesWithBolt,
               handleKnobs=handleKnobs,handleOverTop=handleOverTop,handleKnobFolds=handleKnobFolds,pressChecks=pressChecks),open('extras.json','w'))
