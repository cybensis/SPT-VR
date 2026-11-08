# Tube shotguns, from each gun's own clips (VRReloadController.Shotgun): shells loaded one at a time by hand, through the loading port
# underneath into the tube and through the ejection port into the chamber, and the pump worked by the support hand.
#  - pump: every bone the pump stroke moves (the pump itself, the bolt, the left-hand IK marker the support hand follows, a lock, the
#    feeder), their local poses at PUMP_KEYS evenly spaced points of the stroke, from the gun's after-shot clip (fire_0, else its
#    rechamber). Semi-autos have none.
#  - tube: the shell (Tarkov carries it on the patron_in_weapon bone in its reload clips) and the LEFT palm in the weapon bone's frame
#    over the last part of the reload loop's push: from where the shell comes onto the gun's centre plane under the loading port to
#    where it stops in the tube. PATH_KEYS keys evenly spaced along the shell's path, the fingers at each.
#  - port: the same over the first-shell clip's drop through the ejection port: from PORT_REACH before where it lies when the action
#    closes, to there; the close then carries it into the chamber (close: the shell by how far the action still is from closed).
#  - hold: the shell in the left palm where the tube path starts (the hand carries it from the rig that way).
# Weapon frame = the clip's Weapon_root_anim/weapon bone (+z up, -y toward the muzzle). Writes shotguns.json; gen_export.py merges it.
# Run from this folder.
import sys, os, re, json
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])

# family: rig folder, container, clip folder, pump clip (None = semi-auto), tube clip, port clip, the action bone that closes in the
# port clip (the pump on a pump gun, the bolt on a semi-auto)
GUNS={
    'mr133':('mr133','weapon_izhmeh_mr133_12g_container.bundle','mr133','mr133_fire_0','mr133_reload_loop','mr133_reload_first_catch_0','mod_handguard'),
    'm870':('m870','weapon_remington_model_870_12g_container.bundle','m870','m870_fire_0','m870_ammo_in_loop','m870_ammo_in_0','mod_handguard'),
    'm590':('m590','weapon_mossberg_590a1_12g_container.bundle','m590','m590_fire_0','m590_ammo_in_loop','m590_ammo_in_0','mod_handguard'),
    'ks23':('ks23','weapon_toz_ks23m_23x75_container.bundle','ks23','ks23_rechamber_0','ks23_reload_loop_0','ks23_ammo_in_chambering_0','mod_handguard'),
    'm3s90':('benelli','weapon_benelli_m3_s90_12g_container.bundle','m3s90','m3s90_pa_fire_0','m3s90_ammo_in_loop','m3s90_pa_ammo_in_first_0','mod_handguard'),
    'mr153':('mr153','weapon_izhmeh_mr153_12g_container.bundle','mr153',None,'mr153_reload_loop','mr153_reload_first_catch_0','weapon_slide_0'),
}
PUMP_KEYS=8
PATH_KEYS=10
PORT_REACH=0.07          # m of the drop the port path covers
CENTRE=0.012             # m off the gun's centre plane the shell counts as under the loading port
NOT_PUMP=re.compile(r'Human|Camera|aim_camera|fireport|patron|shell|Bend_Goal|LOD|_R_|mod_magazine|mod_stock|mod_barrel|mod_sight|mod_mount|mod_muzzle|mod_scope|mod_tactical|mod_pistol|mod_foregrip|Collarbone|vest|trigger|hammer',re.I)
STEP=1/120

def r4(v): return [round(float(x),5) for x in v]

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

def pumpStroke(tree,clip,pumpBone):
    P=tree[0]; g=setup(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime); wp=bone(P,'Weapon_root_anim/weapon'); pu=bone(P,'/'+pumpBone)
    n=int(stop/STEP)+1; ts=[i*STEP for i in range(n)]
    p0=np.array(g['localAt'](pu,0.0)[0],float)
    disp=[float(np.linalg.norm(np.array(g['localAt'](pu,t)[0],float)-p0)) for t in ts]
    top=max(disp); iBack=next(i for i,d in enumerate(disp) if d>=top-0.0003)
    iRest=max((i for i in range(iBack) if disp[i]<=0.0003),default=0)
    tRest,tBack=ts[iRest],ts[iBack]
    def timeAt(f):
        want=f*top
        for i in range(iRest,iBack):
            if disp[i]<=want<=disp[i+1] and disp[i+1]>disp[i]: return ts[i]+(want-disp[i])/(disp[i+1]-disp[i])*STEP
        return tRest if f<=0.5 else tBack
    keyTimes=[timeAt(k/(PUMP_KEYS-1)) for k in range(PUMP_KEYS)]
    bones=[]
    for p in P:
        if not p.startswith(wp+'/') or NOT_PUMP.search(p.split('/')[-1]): continue
        a0,q0=[np.array(v,float) for v in g['localAt'](p,tRest)[:2]]
        moved=0.0; turned=0.0
        for i in range(iRest,iBack+1,4):
            a,q=[np.array(v,float) for v in g['localAt'](p,ts[i])[:2]]
            moved=max(moved,float(np.linalg.norm(a-a0))); turned=max(turned,ang(q0,q))
        if moved<0.0005 and turned<1.0: continue
        keys=[]
        for t in keyTimes:
            a,q=[np.array(v,float) for v in g['localAt'](p,t)[:2]]
            keys.append(r4(list(a)+list(q/np.linalg.norm(q))))
        bones.append(dict(path=relPath(wp,p),keys=keys,moved=round(moved,4),turned=round(turned,1)))
    travelInWeapon=np.array(g['localAt'](pu,tBack)[0],float)-p0
    print(f'  pump {clip.m_Name}: {top*100:.1f} cm back at {tBack:.2f}s from {tRest:.2f}s, bones {[(b["path"],b["moved"],b["turned"]) for b in bones]}')
    return dict(travel=round(top,4),direction=r4(travelInWeapon/top),bones=bones,clip=clip.m_Name,times=[round(tRest,3),round(tBack,3)]),p0,travelInWeapon

def sampler(tree,clip):
    P=tree[0]; g=setup(tree,clip); wp=bone(P,'Weapon_root_anim/weapon'); sh=bone(P,'/patron_in_weapon'); lp=bone(P,'/Base HumanLPalm'); J=fingerPaths(P,'L')
    def at(t):
        c={}
        wpp,wr,_=g['worldAt'](wp,t,c); sp,sr,_=g['worldAt'](sh,t,c); pp,pr,_=g['worldAt'](lp,t,c)
        wi=conj(wr)
        return dict(t=t,shell=g['rotate'](wi,sp-wpp),shellQ=qmul(wi,sr),palm=g['rotate'](wi,pp-wpp),palmQ=qmul(wi,pr),
                    inPalm=g['rotate'](conj(pr),sp-pp),inPalmQ=qmul(conj(pr),sr),fingers=[np.array(g['localAt'](j,t)[1],float) for j in J])
    return g,at

def pathKeys(at,t0,t1):
    # Evenly spaced along the shell's path (arc length), everything evaluated at that exact clip time
    ts=np.linspace(t0,t1,int((t1-t0)/STEP)+2); pts=[at(t)['shell'] for t in ts]
    arc=[0.0]
    for a,b in zip(pts,pts[1:]): arc.append(arc[-1]+float(np.linalg.norm(b-a)))
    total=arc[-1]; keys=[]
    for k in range(PATH_KEYS):
        want=total*k/(PATH_KEYS-1); i=next((i for i in range(len(arc)-1) if arc[i]<=want<=arc[i+1]),len(arc)-2)
        t=ts[i]+((want-arc[i])/(arc[i+1]-arc[i]) if arc[i+1]>arc[i] else 0)*(ts[i+1]-ts[i])
        s=at(t)
        keys.append(dict(s=round(k/(PATH_KEYS-1),4),t=round(float(t),3),shell=r4(list(s['shell'])+list(s['shellQ']/np.linalg.norm(s['shellQ']))),
                         palm=r4(list(s['palm'])+list(s['palmQ']/np.linalg.norm(s['palmQ']))),fingers=eulerList(s['fingers'])))
    return keys,total

def tubePath(tree,clip):
    g,at=sampler(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime)
    S=[at(i*STEP) for i in range(int(stop/STEP)+1)]
    win=int(0.1/STEP); iEnd=None
    for i in range(int(0.1/STEP),len(S)-win):
        s=S[i]
        if np.linalg.norm(s['shell']-s['palm'])>0.22 or abs(s['shell'][0])>CENTRE: continue
        if max(float(np.linalg.norm(S[j]['shell']-s['shell'])) for j in range(i,i+win))<0.002: iEnd=i; break
    iStart=iEnd
    while iStart>0 and abs(S[iStart-1]['shell'][0])<=CENTRE: iStart-=1
    t0,t1=S[iStart]['t'],S[iEnd]['t']
    keys,length=pathKeys(at,t0,t1); s0=at(t0)
    hold=dict(position=r4(s0['inPalm']),rotation=r4(s0['inPalmQ']/np.linalg.norm(s0['inPalmQ'])),fingers=eulerList(s0['fingers']))
    print(f'  tube {clip.m_Name}: {t0:.2f}-{t1:.2f}s, {length*100:.1f} cm from {np.round(s0["shell"]*100,1)} to {np.round(S[iEnd]["shell"]*100,1)}, shell {np.linalg.norm(s0["inPalm"])*100:.1f} cm off the palm')
    return dict(keys=keys,duration=round(t1-t0,3),length=round(length,4),clip=clip.m_Name),hold

def portPath(tree,clip,actionBone,closedLocal,travelInWeapon):
    P=tree[0]; g,at=sampler(tree,clip); stop=float(clip.m_MuscleClip.m_StopTime); ab=bone(P,'/'+actionBone)
    ts=[i*STEP for i in range(int(stop/STEP)+1)]
    local=[np.array(g['localAt'](ab,t)[0],float) for t in ts]
    if closedLocal is None:
        # A semi-auto's bolt: closed is where it ends, open as far as it is from there at most
        closedLocal=local[-1]; top=max(float(np.linalg.norm(v-closedLocal)) for v in local)
        open_=[float(np.linalg.norm(v-closedLocal))/top for v in local]
    else:
        d=travelInWeapon/np.linalg.norm(travelInWeapon)
        open_=[float(np.dot(v-closedLocal,d))/float(np.linalg.norm(travelInWeapon)) for v in local]
    iClose=max(i for i in range(len(ts)) if open_[i]>=max(open_)-0.005 and any(o<=0.05 for o in open_[i:]))
    iShut=next(i for i in range(iClose,len(ts)) if open_[i]<=0.02)
    S=[at(t) for t in ts[:iShut+1]]
    drop=S[iClose]['shell']; iEnd=iClose
    while iEnd>0 and np.linalg.norm(S[iEnd-1]['shell']-drop)<=0.002: iEnd-=1
    iStart=iEnd
    while iStart>0 and np.linalg.norm(S[iStart]['shell']-drop)<PORT_REACH: iStart-=1
    t0,t1=ts[iStart],ts[iEnd]
    keys,length=pathKeys(at,t0,t1)
    # The close: the shell by how far the action is still open (1 at the drop, 0 shut)
    close=[]
    for k in range(PATH_KEYS):
        want=1-k/(PATH_KEYS-1); i=next((i for i in range(iClose,iShut) if open_[i]>=want>=open_[i+1]),iShut)
        s=S[i]; close.append(r4([want]+list(s['shell'])+list(s['shellQ']/np.linalg.norm(s['shellQ']))))
    moved=float(np.linalg.norm(S[iShut]['shell']-drop))
    print(f'  port {clip.m_Name}: {t0:.2f}-{t1:.2f}s, {length*100:.1f} cm to {np.round(drop*100,1)}, action shuts {ts[iClose]:.2f}-{ts[iShut]:.2f}s carrying the shell {moved*100:.1f} cm')
    return dict(keys=keys,duration=round(t1-t0,3),length=round(length,4),close=close if moved>0.005 else None,clip=clip.m_Name)

out={}
for family,(folder,container,clipFolder,pumpClip,tubeClip,portClip,actionBone) in GUNS.items():
    print(family,flush=True)
    tree=loadTree(ROOT+folder+'/'+container); clips=clipsOf(clipFolder)
    pump=None; closedLocal=None; travelInWeapon=None
    if pumpClip:
        pump,closedLocal,travelInWeapon=pumpStroke(tree,clips[pumpClip],'mod_handguard')
    tube,hold=tubePath(tree,clips[tubeClip])
    port=portPath(tree,clips[portClip],actionBone,closedLocal,travelInWeapon)
    out[family]=dict(pump=pump,tube=tube,port=port,hold=hold)

# Semi-autos' bolt handles (VRReloadController's charging handle on these guns): the bone the hand pulls back, its travel (back =
# +y in the gun frame, fullBackTime), the gun hand's hold on it (ridingHold 'R': only the right hand racks in their clips; the
# steadiest of the rack clips), and on the MR-153 / MP-155, whose knob is part of the carrier's mesh, the knob's middle (that
# mesh's vertices furthest to the right, +x). The M3's handle is its mod_charge slot's own item: the runtime finds it.
SEMI_HANDLES={
    'mr153':('mr153','weapon_izhmeh_mr153_12g_container.bundle','mr153','weapon_slide_0',['mr153_reload_first_empty_0','mr153_out_to_idle_arm_0'],
             [('mp155','weapon_kalashnikov_mp155_12g_container.bundle')]),
    'm3s90':('benelli','weapon_benelli_m3_s90_12g_container.bundle','m3s90','mod_charge',['m3s90_sa_ammo_in_first_no_catch','m3s90_sa_out_to_idle_charge_0'],None),
}
def meshKnobPoint(folder,container,boneLeaf,axis,sign,shell=0.01):
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
                try:
                    h=MeshHandler(comp.m_Mesh.read()); h.process()
                except Exception: continue
                for q in np.array(h.m_Vertices,float).reshape(-1,3):
                    for link in chain: q=toParent(link,q)
                    points.append(q)
            for ch in tr.m_Children:
                c=ch.read(); walk(c,[c]+chain)
        walk(t,[])
        if len(points)>len(best): best=points
    if not best: return None
    v=np.array(best); side=v[:,axis]*sign
    return [round(float(x),4) for x in v[side>=side.max()-shell].mean(0)]
def fullBackTime(g,path,wp):
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rest=None; rows=[]
    for i in range(int(stop*30)+1):
        t=i/30; c={}; q,_,_=g['worldAt'](path,t,c); wpp,wr,_=g['worldAt'](wp,t,c); inGun=g['rotate'](conj(wr),q-wpp)
        if rest is None: rest=inGun
        rows.append((t,float(inGun[1]-rest[1])))
    # The FIRST frame all the way back: these clips' hand leaves the handle once the bolt locks back (the plateau's middle had it off
    # fetching a shell)
    top=max(r[1] for r in rows); plateau=[r[0] for r in rows if r[1]>=top-0.002]
    return plateau[0],top
for family,(folder,container,clipFolder,boneLeaf,rackClips,mates) in SEMI_HANDLES.items():
    tree=loadTree(ROOT+folder+'/'+container); P=tree[0]; clips=clipsOf(clipFolder)
    charge=bone(P,'/'+boneLeaf); wp=bone(P,'Weapon_root_anim/weapon'); best=None
    for n in rackClips:
        g=setup(tree,clips[n]); h=ridingHold(g,charge,'R')
        if h and (best is None or h['drift']<best['drift']): h['clip']=n; best=h
    g=setup(tree,clips[rackClips[0]]); tBack,top=fullBackTime(g,charge,wp)
    if best is None:
        # A yank the hand doesn't ride: the right palm on it at full pull
        palm=bone(P,'/Base HumanRPalm'); c={}; cp,cr,_=g['worldAt'](charge,tBack,c); pp,pr,_=g['worldAt'](palm,tBack,c)
        best=dict(position=[round(float(x),4) for x in g['rotate'](conj(cr),pp-cp)],rotation=toUnityEuler(qmul(conj(cr),pr)),
                  fingers=eulerList([np.array(g['localAt'](j,tBack)[1],float) for j in fingerPaths(P,'R')]),travel=round(float(top),3),clip=rackClips[0]+' (full pull)')
    # The closed right fingers at full pull, in the handle bone's frame: where the clip's hand has the knob
    c={}; cp,cr,_=g['worldAt'](charge,tBack,c)
    grasp=np.mean([g['rotate'](conj(cr),g['worldAt'](j,tBack,c)[0]-cp) for j in fingerPaths(P,'R')[1:]],axis=0)
    knob=meshKnobPoint(folder,container,boneLeaf,0,1) if mates is not None else None
    handle=dict(bone=boneLeaf,carrier=boneLeaf if mates is not None else None,travel=round(float(top),4),grabPoint=knob,
                gunHandHold=dict(position=best['position'],rotation=best['rotation'],fingers=best['fingers'],travel=round(float(top),3)))
    out[family]['handle']=handle
    print(f'{family} handle {boneLeaf}: {top*100:.1f} cm back at {tBack:.2f}s, right hold from {best["clip"]}, knob {knob}, fingers at {np.round(grasp,4)}'
          + (f' ({np.linalg.norm(np.array(knob)-grasp)*100:.1f} cm off the knob)' if knob else ''),flush=True)
    for mateFolder,mateContainer in (mates or []):
        other=meshKnobPoint(mateFolder,mateContainer,boneLeaf,0,1)
        print(f'  {mateFolder} knob {other}' + (f' ({np.linalg.norm(np.array(other)-np.array(knob))*100:.1f} cm from the {family} one)' if other and knob else ''),flush=True)
json.dump(dict(shotguns=out),open('shotguns.json','w'),separators=(',',':'))
print('wrote shotguns.json',os.path.getsize('shotguns.json'),'bytes')
