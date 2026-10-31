# Generates reload hand holds from the game's clips. Hand frame = weapon_L_IK_marker (your hand takes its place); magazines
# are stored in their BONE's frame (the model's offset comes from its ModPlacer at runtime). Mecanim euler = Rz*Ry*Rx.
import sys, os, re, math, json, zlib, time
import numpy as np, UnityPy
SRC=open('clip_pose.py',encoding='utf-8').read().split('release = find(')[0]
SRC=re.sub(r'treeBundle, clipBundle, clipName = .*?\n','',SRC,count=1)
SRC=re.sub(r'times = \[.*?\n','',SRC,count=1)
SRC=re.sub(r'prefabLocal = \{\}.*?hashToPath = [^\n]*\n','prefabLocal, parentOf, hashToPath = PRELOADED_TREE\n',SRC,count=1,flags=re.S)
SRC=re.sub(r'clip = None\r?\nfor objectReader.*?break\r?\n','clip = PRELOADED_CLIP\n',SRC,count=1,flags=re.S)
assert 'PRELOADED_TREE' in SRC and 'PRELOADED_CLIP' in SRC

def conj(q): return np.array([-q[0],-q[1],-q[2],q[3]])
def qmul(a,b):
    x1,y1,z1,w1=a;x2,y2,z2,w2=b
    return np.array([w1*x2+x1*w2+y1*z2-z1*y2,w1*y2-x1*z2+y1*w2+z1*x2,w1*z2+x1*y2-y1*x2+z1*w2,w1*w2-x1*x2-y1*y2-z1*z2])
def axisq(deg,ax):
    h=math.radians(deg)/2; return np.array([ax[0]*math.sin(h),ax[1]*math.sin(h),ax[2]*math.sin(h),math.cos(h)])
def clipEuler(x,y,z): return qmul(qmul(axisq(z,[0,0,1]),axisq(y,[0,1,0])),axisq(x,[1,0,0]))
def toUnityEuler(q):
    x,y,z,w=q
    m=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],[2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],[2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
    return [round(float(v),2) for v in np.degrees([math.asin(np.clip(-m[1,2],-1,1)),math.atan2(m[0,2],m[2,2]),math.atan2(m[1,0],m[1,1])])%360]
def ang(a,b): return math.degrees(2*math.acos(min(1,abs(float(np.dot(a,b))))))
def angleOf(q): return math.degrees(2*math.acos(min(1,abs(float(q[3])))))

ROOT='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/weapons/'

def loadTree(path):
    env=UnityPy.load(path); trs={o.path_id:o.read() for o in env.objects if o.type.name=="Transform"}
    goName={}; prefab,parent={},{}
    def nm(t):
        k=t.m_GameObject.path_id
        if k not in goName: goName[k]=t.m_GameObject.read().m_Name
        return goName[k]
    def walk(t,prefix):
        for ch in t.m_Children:
            c=trs.get(ch.path_id)
            if c is None: continue
            p=nm(c) if prefix=="" else prefix+"/"+nm(c)
            a,b,s=c.m_LocalPosition,c.m_LocalRotation,c.m_LocalScale
            prefab[p]=(np.array([a.x,a.y,a.z]),np.array([b.x,b.y,b.z,b.w]),np.array([s.x,s.y,s.z]))
            parent[p]=prefix; walk(c,p)
    for t in trs.values():
        if t.m_Father.path_id==0: walk(t,"")
    return prefab,parent,{zlib.crc32(p.encode())&0xffffffff:p for p in prefab}

def setup(tree,clip):
    g={'PRELOADED_TREE':tree,'PRELOADED_CLIP':clip}
    exec(compile(SRC,'clip_pose','exec'),g)
    g['quatFromEuler']=clipEuler
    return g

def bone(P,s):
    m=[p for p in P if p.endswith(s)]
    return m[0] if m else None

def fingerPaths(P,side='L'):
    palm=bone(P,f'/Base Human{side}Palm')
    if palm is None: return None
    out=[palm]
    for f in range(1,6):
        a=f'{palm}/Base Human{side}Digit{f}1'; b=f'{a}/Base Human{side}Digit{f}2'; c=f'{b}/Base Human{side}Digit{f}3'
        if a not in P or b not in P or c not in P: return None
        out+=[a,b,c]
    return out

def sample(g,target,J,step=1/30):
    P=g['prefabLocal']; mk,wp,palm=bone(P,'/weapon_L_IK_marker'),bone(P,'Weapon_root_anim/weapon'),bone(P,'/Base HumanLPalm')
    stop=float(g['clip'].m_MuscleClip.m_StopTime); S=[]
    for i in range(int(stop/step)+1):
        t=i*step; c={}
        mp,mr,_=g['worldAt'](target,t,c); kp,kr,_=g['worldAt'](mk,t,c); wpp,wr,_=g['worldAt'](wp,t,c); pp,_,_=g['worldAt'](palm,t,c)
        qs=[np.array(g['localAt'](j,t)[1],float) for j in J]
        S.append(dict(t=t,rel=g['rotate'](conj(kr),mp-kp),relq=qmul(conj(kr),mr),inGun=g['rotate'](conj(wr),mp-wpp),palmOff=float(np.linalg.norm(pp-kp)),qs=qs,curl=sum(angleOf(q) for q in qs[4:])))
    return S

def stillRun(S,ok):
    best=None;start=None
    for i,s in enumerate(S):
        good=ok(s) and (start is None or (np.linalg.norm(s['rel']-S[i-1]['rel'])<=0.01 and ang(s['relq'],S[i-1]['relq'])<=6))
        if good:
            if start is None: start=i
        else:
            if start is not None and (best is None or i-start>best[1]-best[0]): best=(start,i)
            start=i if ok(s) else None
    if start is not None and (best is None or len(S)-start>best[1]-best[0]): best=(start,len(S))
    return S[best[0]:best[1]] if best and best[1]-best[0]>=3 else None

def avgQ(run):
    first=run[0]['relq']; v=np.zeros(4)
    for r in run: v+=r['relq'] if np.dot(r['relq'],first)>=0 else -r['relq']
    return v/np.linalg.norm(v)

def eulerList(qs): return [x for q in qs for x in toUnityEuler(q)]

# The tightest moment isn't the grip on these guns (headset, SCAR: the fingers don't hold the magazine), the carrying part of the clip is
CARRYING_GRIP_GUNS=('scar_16','scar_17')

# A pistol's insert: the last frame of the hold is the hand already opening to let the magazine go (headset, Glock: "he doesn't seem to be
# holding it", fingertips 1-2.5 cm off it), so the grip is the middle of the longest stretch where the fingers keep still
PISTOLS=('aps','deagle','glock','glock18c','m1911a1','m9','mp443','p226r','pb','pl15','pm','sr1mp','tt','usp')
def steadyFingers(run):
    best=(0,0); start=0
    for end in range(len(run)):
        while abs(run[end]['curl']-run[start]['curl'])>run[start]['curl']*0.1: start+=1
        if end-start>best[1]-best[0]: best=(start,end)
    return run[(best[0]+best[1])//2]['qs']

def magHold(g,J,mag,pull,family=None):
    S=sample(g,mag,J)
    seat=S[0]['inGun'] if pull else S[-1]['inGun']
    run=stillRun(S,lambda s:np.linalg.norm(s['inGun']-seat)>=0.03 and np.linalg.norm(s['rel'])<=0.35)
    if not run: return None
    pos=np.mean([r['rel'] for r in run],axis=0)
    qs=list(run[-1]['qs'])
    if not pull and family in PISTOLS: qs=list(steadyFingers(run))
    if pull:
        # The grip: the most closed moment in this hold before the hand opens to let go of the magazine (late in the clip it
        # closes again, empty, with the bone still at the hold). The thumb is that moment's too. A drum keeps the hand far more open than
        # that while it carries it: past the magazine's farthest point the clip is bringing the hand back, closing into a fist with the
        # bone still riding along, so if that's what it found, the grip is the tightest moment before there.
        tightest=None; carrying=None
        farthest=max(range(len(S)),key=lambda i:np.linalg.norm(S[i]['inGun']-seat))
        for index,f in enumerate(S):
            if np.linalg.norm(f['rel']-pos)>0.03: continue
            if tightest is not None and f['curl']<tightest['curl']*0.75: break
            if tightest is None or f['curl']>tightest['curl']: tightest=f
            if index<=farthest: carrying=tightest
        if carrying is not None and (carrying['curl']<tightest['curl']*0.75 or family in CARRYING_GRIP_GUNS): tightest=carrying
        if tightest is not None: qs=list(tightest['qs'])
    return dict(position=[round(float(x),4) for x in pos],rotation=toUnityEuler(avgQ(run)),fingers=eulerList(qs),frames=len(run),palmOff=round(float(np.mean([r['palmOff'] for r in run])),3))

def handleHold(g,J,charge):
    S=sample(g,charge,J)
    rest=S[0]['inGun']; moved=[np.linalg.norm(s['inGun']-rest) for s in S]; top=max(moved)
    if top<0.02: return None
    run=[s for s,m in zip(S,moved) if m>=top-0.001]
    if len(run)<2: return None
    mid=run[len(run)//2]
    P=g['prefabLocal']; mk=bone(P,'/weapon_L_IK_marker'); c={}
    cp,cr,_=g['worldAt'](charge,mid['t'],c); kp,kr,_=g['worldAt'](mk,mid['t'],c)
    pos=g['rotate'](conj(cr),kp-cp); rot=qmul(conj(cr),kr)
    return dict(position=[round(float(x),4) for x in pos],rotation=toUnityEuler(rot),fingers=eulerList(mid['qs']),travel=round(float(top),3),palmOff=round(mid['palmOff'],3))

def leftHandTravel(g,charge):
    P=g['prefabLocal']; mk,wp=bone(P,'/weapon_L_IK_marker'),bone(P,'Weapon_root_anim/weapon')
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rows=[]; rest=None
    for i in range(int(stop*30)+1):
        t=i/30; c={}
        cp,_,_=g['worldAt'](charge,t,c); kp,_,_=g['worldAt'](mk,t,c); wpp,wr,_=g['worldAt'](wp,t,c)
        ig=g['rotate'](conj(wr),cp-wpp)
        if rest is None: rest=ig
        rows.append((float(np.linalg.norm(ig-rest)),g['rotate'](conj(wr),kp-wpp)))
    top=max(r[0] for r in rows); firstBack=next(i for i,r in enumerate(rows) if r[0]>=top-0.002)
    lastRest=max((i for i in range(firstBack) if rows[i][0]<=0.002),default=0)
    return float(np.linalg.norm(rows[firstBack][1]-rows[lastRest][1]))

# Clips that may show a hand working the handle, best first: a rack, then the clips that rack on the way to something else.
# Guns name their rack clips differently, and two of these are misspelt in the game's files.
RACK_CLIP_RANKS=[r'reload_charge(_\d+)?$',r'charge(_inv)?(_\d+)?$',r'rech[ae]mber(_\d+)?$',r'misfire(_\d+)?$',r'disch\w*$',r'(jam|ammo_)']
def rackClipRank(suffix):
    if re.search('check|shell|catch|bolt_open',suffix): return None
    for rank,pattern in enumerate(RACK_CLIP_RANKS):
        if re.match(pattern,suffix): return rank
    return None

# A palm's pose in the handle bone's frame, from a clip where it rides the handle: it has to travel in the gun's frame, and over the
# back half of the pull move as one with the handle. Either alone isn't enough: a fast pull has only a frame or two in that back half,
# which a hand still on the grip passes, and a hand also moves in clips where it only reaches over or gets out of the way.
def ridingHold(g,charge,side):
    P=g['prefabLocal']; palm,wp=bone(P,f'/Base Human{side}Palm'),bone(P,'Weapon_root_anim/weapon'); J=fingerPaths(P,side)
    if palm is None or J is None: return None
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rows=[]; rest=None
    for i in range(int(stop*30)+1):
        t=i/30; c={}
        cp,cr,_=g['worldAt'](charge,t,c); pp,pr,_=g['worldAt'](palm,t,c); wpp,wr,_=g['worldAt'](wp,t,c)
        inGun=g['rotate'](conj(wr),cp-wpp)
        if rest is None: rest=inGun
        rows.append(dict(t=t,back=float(np.linalg.norm(inGun-rest)),pos=g['rotate'](conj(cr),pp-cp),rot=qmul(conj(cr),pr),palmInGun=g['rotate'](conj(wr),pp-wpp)))
    top=max(r['back'] for r in rows)
    if top<0.03: return None
    firstBack=next(i for i,r in enumerate(rows) if r['back']>=top-0.002)
    lastRest=max((i for i in range(firstBack) if rows[i]['back']<=0.002),default=0)
    riding=[r for r in rows[lastRest:firstBack+1] if r['back']>=top*0.5]
    if len(riding)<2: return None
    end=riding[-1]
    drift=max(float(np.linalg.norm(r['pos']-end['pos'])) for r in riding); turn=max(ang(r['rot'],end['rot']) for r in riding)
    moved=float(np.linalg.norm(end['palmInGun']-rows[lastRest]['palmInGun']))
    if drift>0.015 or turn>20 or moved<top*0.5: return None
    qs=[np.array(g['localAt'](j,end['t'])[1],float) for j in J]
    return dict(position=[round(float(x),4) for x in end['pos']],rotation=toUnityEuler(end['rot']),fingers=eulerList(qs),travel=round(float(top),3),drift=round(drift,3),turn=round(float(turn),1),t=end['t'],moved=round(moved,3),palmOff=0.0)

def magazinePath(g,mag):
    P=g['prefabLocal']; wp=bone(P,'Weapon_root_anim/weapon'); stop=float(g['clip'].m_MuscleClip.m_StopTime); rows=[]
    for i in range(int(stop*30)+1):
        t=i/30; c={}
        mp,mq,_=g['worldAt'](mag,t,c); wpp,wr,_=g['worldAt'](wp,t,c)
        rows.append(dict(t=t,pos=g['rotate'](conj(wr),mp-wpp),rot=qmul(conj(wr),mq)))
    return rows

# A magazine that rocks out of its well (AK-type guns): both clips turn it about the well bone by a good 20 degrees with that bone
# hardly moving, where a straight magazine's turn is under 10. Bone-local axis and angle, and the off thumb's joints with the catch
# furthest in (on these guns that thumb is what presses it, about 1.5 cm from it in the clip).
def rockIn(tree,clips,prefix,mag):
    P=tree[0]
    outName=next((n for n in (f'{prefix}_mag_out_0',f'{prefix}_mag_out') if n in clips),None); inName=next((n for n in (f'{prefix}_mag_in_0',f'{prefix}_mag_in') if n in clips),None)
    if not outName or not inName: return None
    gOut=setup(tree,clips[outName]); rowsOut=magazinePath(gOut,mag); seat=rowsOut[0]
    near=[r for r in rowsOut if np.linalg.norm(r['pos']-seat['pos'])<=0.012]
    tilted=max(near,key=lambda r:ang(r['rot'],seat['rot'])); angle=ang(tilted['rot'],seat['rot'])
    rowsIn=magazinePath(setup(tree,clips[inName]),mag); seatIn=rowsIn[-1]
    hook=max((ang(r['rot'],seatIn['rot']) for r in rowsIn if np.linalg.norm(r['pos']-seatIn['pos'])<=0.012),default=0.0)
    if angle<15 or hook<12: return None
    delta=qmul(conj(seat['rot']),tilted['rot'])
    if delta[3]<0: delta=-delta
    axis=delta[:3]/np.linalg.norm(delta[:3])
    result=dict(axis=[round(float(x),4) for x in axis],angle=round(float(angle),1),hook=round(float(hook),1),thumb=None,thumbToCatch=None)
    result['thumb'],result['thumbToCatch']=thumbOnCatch(gOut)
    return result

# The off thumb's three joints with the catch furthest in, and that thumb's tip from the catch
def thumbOnCatch(g):
    P=g['prefabLocal']; J=fingerPaths(P,'L')
    catchParts=[p for p in P if re.search('release|mag_catch',p.split('/')[-1],re.I) and not re.search('bolt|slide|lod|stock|launcher',p.split('/')[-1],re.I)]
    if not catchParts or not J: return None,None
    part=catchParts[0]; stop=float(g['clip'].m_MuscleClip.m_StopTime); rest=None; peak=0.0; peakTime=None
    for i in range(int(stop*30)+1):
        t=i/30; local=g['localAt'](part,t); position=np.array(local[0],float); rotation=np.array(local[1],float)
        if rest is None: rest=(position,rotation)
        move=float(np.linalg.norm(position-rest[0]))*1000+ang(rotation,rest[1])
        if move>peak: peak=move; peakTime=t
    if peakTime is None or peak<0.5: return None,None
    c={}; catchAt,_,_=g['worldAt'](part,peakTime,c)
    last,_,_=g['worldAt'](J[3],peakTime,c); before,_,_=g['worldAt'](J[2],peakTime,c)
    return eulerList([np.array(g['localAt'](j,peakTime)[1],float) for j in J[1:4]]),round(float(np.linalg.norm(last+(last-before)*0.8-catchAt)),3)

# The other way to hold an AK's handle. Each hand's clips on these guns only show it one way (the left over the top, the right from
# below), so the other comes from a clip that shows it, the same handle on another rig, moved over through the gun's frame.
THUMB_CATCH_GUNS=('mp5','mp5k')
OTHER_GRIP_GUNS=('ak100','ak74n','ak74m','akm','aks74n','aks74u','rpk16','saiga12k_10')
LEFT_UNDER_HAND=('pp-19-01','pp-19-01_charge_2')
RIGHT_OVER_HAND=('ak74n','ak74n_reload_charge_0')

# A palm against the handle in the gun's frame, with the handle all the way back
def handOnHandle(g,charge,side):
    P=g['prefabLocal']; palm,wp=bone(P,f'/Base Human{side}Palm'),bone(P,'Weapon_root_anim/weapon'); J=fingerPaths(P,side)
    stop=float(g['clip'].m_MuscleClip.m_StopTime); rest=None; rows=[]
    for i in range(int(stop*30)+1):
        t=i/30; c={}
        cp,_,_=g['worldAt'](charge,t,c); wpp,wr,_=g['worldAt'](wp,t,c); inGun=g['rotate'](conj(wr),cp-wpp)
        if rest is None: rest=inGun
        rows.append((t,float(np.linalg.norm(inGun-rest))))
    top=max(r[1] for r in rows); t=next(r[0] for r in rows if r[1]>=top-0.002)
    c={}; cp,_,_=g['worldAt'](charge,t,c); pp,pr,_=g['worldAt'](palm,t,c); wpp,wr,_=g['worldAt'](wp,t,c)
    return dict(position=g['rotate'](conj(wr),pp-cp),rotation=qmul(conj(wr),pr),fingers=eulerList([np.array(g['localAt'](j,t)[1],float) for j in J]))

def holdOnBone(hand,boneInGun):
    inverse=conj(boneInGun); axis=inverse[:3]; position=hand['position']
    onBone=position+2*np.cross(axis,np.cross(axis,position)+inverse[3]*position)
    return dict(position=[round(float(x),4) for x in onBone],rotation=toUnityEuler(qmul(conj(boneInGun),hand['rotation'])),fingers=hand['fingers'])

# A pistol has no charging handle: the left hand works its slide the same way. The slide is a bone of the rig, the first of these the
# clip moves with the hand riding it, and the hold is on that bone like a handle's.
SLIDE_BONES=('weapon_slide','mod_reciever','slide')
# Other ways to take a slide, from the pistols whose clips show them: palm on top, and up from below at the side. gen_export moves them onto
# each pistol by the back and top of its slide, since the slide bone sits somewhere else on each.
SLIDE_OTHER_GRIPS=(('over','p226r','p226r_discharge'),('below','aps','aps_misfire_1'))
SLIDE_CLIP_RANKS=[r'reload_charge(_\d+)?$',r'(reload_)?charge_(to_)?end(_\d+)?$|reload_end_charge$',r'rechamber(_\d+)?$',r'misfire(_\d+)?$',r'discharge$',r'jam_(slide|soft|hard)']
def slideHold(tree,clips,prefix,names):
    P=tree[0]; best=None
    for n in sorted(names):
        suffix=n[len(prefix)+1:]
        rank=next((r for r,pattern in enumerate(SLIDE_CLIP_RANKS) if re.match(pattern,suffix)),None)
        if rank is None or re.search('idle|shell|inspect|check',suffix): continue
        g=setup(tree,clips[n])
        for boneName in SLIDE_BONES:
            path=bone(P,'/'+boneName)
            if path is None: continue
            h=ridingHold(g,path,'L')
            if h is None: continue
            if best is None or (rank,h['drift'])<best[0]:
                h['bone']=boneName; h['clip']=n; best=((rank,h['drift']),h)
            break
    return best[1] if best else None

# Which way a magazine leaves its well, in the well bone's frame, from the clip: a pistol's goes down the raked grip, which the box of its
# mesh can't say (the mesh is modelled upright around it). Only where the clip moves it in a straight line, unturned.
def wellAxis(g,mag):
    rows=magazinePath(g,mag); seat=rows[0]; directions=[]
    for r in rows:
        off=r['pos']-seat['pos']; distance=float(np.linalg.norm(off))
        if distance>0.09: break
        if distance>=0.01 and ang(r['rot'],seat['rot'])<=5: directions.append(off/distance)
    if len(directions)<2: return None
    mean=np.mean(directions,axis=0); mean=mean/np.linalg.norm(mean)
    spread=max(math.degrees(math.acos(min(1.0,float(np.dot(d,mean))))) for d in directions)
    if spread>8: return None
    return [round(float(x),4) for x in g['rotate'](conj(seat['rot']),mean)]

def palmHold(g,J,old,new):
    So=sample(g,old,J); Sn=sample(g,new,J)
    seat=So[0]['inGun']
    runOld=stillRun(So,lambda s:np.linalg.norm(s['inGun']-seat)>=0.03 and np.linalg.norm(s['rel'])<=0.40)
    if not runOld: return None
    t0,t1=runOld[0]['t'],runOld[-1]['t']
    runNew=stillRun([s for s in Sn if t0-1e-6<=s['t']<=t1+1e-6],lambda s:np.linalg.norm(s['rel'])<=0.40)
    if not runNew: return None
    thumb,thumbToCatch=thumbOnCatch(g)
    return dict(thumb=thumb,thumbToCatch=thumbToCatch,newMagazine=dict(position=[round(float(x),4) for x in np.mean([r['rel'] for r in runNew],axis=0)],rotation=toUnityEuler(avgQ(runNew))),
        oldMagazine=dict(position=[round(float(x),4) for x in np.mean([r['rel'] for r in runOld],axis=0)],rotation=toUnityEuler(avgQ(runOld))),
        fingers=eulerList(runNew[len(runNew)//2]['qs']))

def run(folders,outFile=None):
    result=dict(grips={},handles={},gunHandHandles={},handleAlternates={},gunHandHandleAlternates={},handleMovesWithBolt=[],handleNotches={},thumbCatches={},wellAxes={},slides={},slideBoneInGun={},slideGrips={},rockIn={},palms={}); report=[]
    boneInGun={}; otherGrips={}
    for folder in folders:
        d=ROOT+folder+'/'
        if not os.path.isfile(d+'client_assets.bundle'): continue
        clips={}
        try:
            for o in UnityPy.load(d+'client_assets.bundle').objects:
                if o.type.name=="AnimationClip":
                    try:
                        c=o.read(); clips[c.m_Name]=c
                    except Exception: pass
        except Exception as e:
            report.append((folder,'ERR load',repr(e)[:80])); continue
        prefixes=sorted(set(m.group(1) for m in (re.match(r'(.+?)_mag_in(?:_\d+)?$',n) for n in clips) if m))
        if not prefixes: continue
        trees={}
        for c in os.listdir(d):
            if c.endswith('_container.bundle') and c.startswith('weapon_'):
                try: trees[c]=loadTree(d+c)
                except Exception as e: report.append((folder,c,'ERR tree',repr(e)[:80]))
        for prefix in prefixes:
            family=prefix.lower(); names=[n for n in clips if n.startswith(prefix+'_')]
            probe=next(n for n in names if '_mag_in' in n)
            hashes=[b.path for b in clips[probe].m_ClipBindingConstant.genericBindings]
            best=None
            for c,tree in trees.items():
                n=sum(h in tree[2] for h in hashes)
                if (best is None or n>best[0]) and bone(tree[0],'/mod_magazine') and bone(tree[0],'/weapon_L_IK_marker') and bone(tree[0],'Weapon_root_anim/weapon') and fingerPaths(tree[0]): best=(n,c)
            if best is None:
                report.append((family,'no usable model')); continue
            tree=trees[best[1]]; P=tree[0]; J=fingerPaths(P); mag=bone(P,'/mod_magazine'); t0=time.time(); got=[]
            for n in names:
                m=re.match(re.escape(prefix)+r'_mag_(in|out)(?:_(\d+))?$',n)
                if not m: continue
                pull=m.group(1)=='out'; typ=int(m.group(2) or 0)
                try: h=magHold(setup(tree,clips[n]),J,mag,pull,family)
                except Exception as e:
                    report.append((family,n,'ERR',repr(e)[:80])); continue
                if h is None:
                    report.append((family,n,'no still hold')); continue
                result['grips'].setdefault(f'{family}|{typ}',{})['pull' if pull else 'insert']=h; got.append(n)
            charge=bone(P,'/mod_charge')
            if charge:
                # Our handle grab is the left hand. Many guns rack with the right in their low mastery clips, with the left
                # still on the handguard: only a clip where the left hand travels with the handle says anything.
                best=None
                for n in sorted(names):
                    rank=rackClipRank(n[len(prefix)+1:])
                    if rank is None: continue
                    try:
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
                    except Exception as e:
                        report.append((family,n,'ERR',repr(e)[:80])); continue
                    if best is None or (rank,-moved)<best[0]:
                        h['clip']=n; h['leftHandMoved']=round(moved,3); best=((rank,-moved),h)
                if best: result['handles'][family]=best[1]
                else: report.append((family,'charge','no clip racks with the left hand'))
                # The gun hand's grab, once it's off the grip: the first kind of clip that shows it, the steadiest of those
                best=None
                for n in sorted(names):
                    rank=rackClipRank(n[len(prefix)+1:])
                    if rank is None: continue
                    try: h=ridingHold(setup(tree,clips[n]),charge,'R')
                    except Exception as e:
                        report.append((family,n,'ERR',repr(e)[:80])); continue
                    if h and (best is None or (rank,h['drift'])<best[0]):
                        h['clip']=n; best=((rank,h['drift']),h)
                if best: result['gunHandHandles'][family]=best[1]
                else: report.append((family,'charge','no clip racks with the right hand'))
                g=setup(tree,clips[next(n for n in names if '_mag_in' in n)]); c={}
                _,chargeRotation,_=g['worldAt'](charge,0.0,c); _,gunRotation,_=g['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,c)
                boneInGun[family]=qmul(conj(gunRotation),chargeRotation)
                # Some guns' held bolt pose moves the handle bone itself (an MP5's handle locks back on its own)
                handleAtRest,_,_=g['worldAt'](charge,0.0,c); gunAtRest,_,_=g['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,c)
                handleAtRest=g['rotate'](conj(gunRotation),handleAtRest-gunAtRest)
                for n in sorted(x for x in names if re.search(r'(catch_on|on_catch)$',x)):
                    held=setup(tree,clips[n]); c2={}
                    handleHeld,_,_=held['worldAt'](charge,0.0,c2); gunHeld,gunHeldRotation,_=held['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,c2)
                    if np.linalg.norm(held['rotate'](conj(gunHeldRotation),handleHeld-gunHeld)-handleAtRest)>=0.02:
                        result['handleMovesWithBolt'].append(family)
                        # Turned as well: it locks in a notch
                        turn=qmul(conj(np.array(g['localAt'](charge,0.0)[1],float)),np.array(held['localAt'](charge,0.0)[1],float))
                        if turn[3]<0: turn=-turn
                        turnAngle=2*math.degrees(math.acos(min(1.0,float(turn[3]))))
                        if turnAngle>=15: result['handleNotches'][family]=dict(axis=[round(float(x),4) for x in turn[:3]/np.linalg.norm(turn[:3])],angle=round(turnAngle,1))
                        break
                for (sourceFamily,sourceClip),side,key in ((LEFT_UNDER_HAND,'L','left'),(RIGHT_OVER_HAND,'R','right')):
                    if family==sourceFamily: otherGrips[key]=handOnHandle(setup(tree,clips[sourceClip]),charge,side)
            outClip=next((n for n in (f'{prefix}_mag_out_0',f'{prefix}_mag_out') if n in clips),None)
            if outClip:
                try:
                    axis=wellAxis(setup(tree,clips[outClip]),mag)
                    if axis: result['wellAxes'][family]=axis
                except Exception as e:
                    report.append((family,'wellAxis','ERR',repr(e)[:80]))
            if not charge:
                try:
                    slide=slideHold(tree,clips,prefix,names)
                    if slide:
                        result['slides'][family]=slide
                        slidePath=bone(P,'/'+slide['bone']); g=setup(tree,clips[next(n for n in names if '_mag_in' in n)]); c={}
                        _,slideRotation,_=g['worldAt'](slidePath,0.0,c); _,gunRotation,_=g['worldAt'](bone(P,'Weapon_root_anim/weapon'),0.0,c)
                        result['slideBoneInGun'][family]=[float(x) for x in qmul(conj(gunRotation),slideRotation)]
                        for key,sourceFamily,sourceClip in SLIDE_OTHER_GRIPS:
                            if family==sourceFamily and sourceClip in clips:
                                hand=handOnHandle(setup(tree,clips[sourceClip]),slidePath,'L')
                                result['slideGrips'][key]=dict(family=family,clip=sourceClip,position=[float(x) for x in hand['position']],rotation=[float(x) for x in hand['rotation']],fingers=hand['fingers'])
                except Exception as e:
                    report.append((family,'slide','ERR',repr(e)[:80]))
            # Their off thumb works the catch too, but the magazine comes straight out
            if family in THUMB_CATCH_GUNS:
                thumb,_=thumbOnCatch(setup(tree,clips[next(n for n in (f'{prefix}_mag_out_0',f'{prefix}_mag_out') if n in clips)]))
                if thumb: result['thumbCatches'][family]=thumb
            try:
                rocks=rockIn(tree,clips,prefix,mag)
                if rocks: result['rockIn'][family]=rocks
            except Exception as e:
                report.append((family,'rockIn','ERR',repr(e)[:80]))
            new=bone(P,'/mod_magazine_new')
            # AK-type guns name it mag_admin
            for n in sorted(x for x in names if re.match(re.escape(prefix)+r'_(reload|mag)_admin(_\d+)?$',x)):
                if new:
                    try: h=palmHold(setup(tree,clips[n]),J,mag,new)
                    except Exception as e:
                        report.append((family,n,'ERR',repr(e)[:80])); continue
                    if h:
                        result['palms'][family]=h; break
            print(f'{folder}/{family}: {len(got)} mag clips, handle {family in result["handles"]}, palm {family in result["palms"]}, {time.time()-t0:.0f}s',flush=True)
            if outFile: json.dump(dict(result=result,report=report),open(outFile,'w'))
    for family in OTHER_GRIP_GUNS:
        if family not in boneInGun: continue
        if 'left' in otherGrips: result['handleAlternates'][family]=[holdOnBone(otherGrips['left'],boneInGun[family])]
        if 'right' in otherGrips: result['gunHandHandleAlternates'][family]=[holdOnBone(otherGrips['right'],boneInGun[family])]
    return result,report

if __name__=='__main__':
    folders=sys.argv[2:] or sorted(os.listdir(ROOT))
    r,rep=run(folders,sys.argv[1])
    json.dump(dict(result=r,report=rep),open(sys.argv[1],'w'))
    print('DONE grips',len(r['grips']),'handles',len(r['handles']),'palms',len(r['palms']),'problems',len(rep),flush=True)
