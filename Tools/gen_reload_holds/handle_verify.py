# Do the generated handle holds put the fingers on the handle? Fingertips against the handle model, per animation set.
import sys,json,re,os
sys.path.insert(0,'.')
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])
gen=json.load(open('gen_all.json')); H=gen['result']['handles']
items=json.load(open('F:/SPT4.1/SPT_Runtime/SPT_Data/database/templates/items.json',encoding='utf-8')); guns=json.load(open('guns.json')); census=json.load(open('clip_census.json'))
famFolder={}
for folder,v in census.items():
    for c in v.get('clips',[]):
        m=re.match(r'(.+?)_mag_in(?:_\d+)?$',c)
        if m: famFolder[m.group(1).lower()]=folder
BUNDLES='F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/'
def handleMesh(folder):
    for g in guns:
        if g['folder']!=folder: continue
        for s in items[g['id']]['_props'].get('Slots',[]):
            if s['_name']=='mod_charge':
                for i in s['_props']['filters'][0]['Filter']:
                    if i not in items: continue
                    try:
                        env=UnityPy.load(BUNDLES+items[i]['_props']['Prefab']['path'])
                        for o in env.objects:
                            if o.type.name=='Mesh':
                                a=o.read().m_LocalAABB
                                return items[i]['_name'],np.array([a.m_Center.x,a.m_Center.y,a.m_Center.z]),np.array([a.m_Extent.x,a.m_Extent.y,a.m_Extent.z])
                    except Exception: pass
    return None,None,None
for family,h in sorted(H.items()):
    folder=famFolder[family]; d=ROOT+folder+'/'
    clips={}
    for o in UnityPy.load(d+'client_assets.bundle').objects:
        if o.type.name=='AnimationClip':
            try:
                c=o.read(); clips[c.m_Name]=c
            except Exception: pass
    tree=None
    for c in os.listdir(d):
        if c.endswith('_container.bundle') and c.startswith('weapon_'):
            t=loadTree(d+c)
            if bone(t[0],'/mod_charge') and bone(t[0],'/Base HumanLPalm'):
                tree=t; break
    g=setup(tree,clips[h['clip']]); P=tree[0]; ch=bone(P,'/mod_charge')
    # the moment the hold is read: handle all the way back
    stop=float(clips[h['clip']].m_MuscleClip.m_StopTime); wp=bone(P,'Weapon_root_anim/weapon'); rows=[]; rest=None
    for i in range(int(stop*30)+1):
        t=i/30; c={}; cp,_,_=g['worldAt'](ch,t,c); wpp,wr,_=g['worldAt'](wp,t,c); ig=g['rotate'](conj(wr),cp-wpp)
        if rest is None: rest=ig
        rows.append((t,float(np.linalg.norm(ig-rest))))
    top=max(r[1] for r in rows); back=[r[0] for r in rows if r[1]>=top-0.001]; t=back[len(back)//2]
    c={}; cp,cr,_=g['worldAt'](ch,t,c)
    name,center,extent=handleMesh(folder)
    dist={}
    for label,fi in (('thumb',1),('index',2),('middle',3)):
        a=bone(P,f'/Base HumanLDigit{fi}3'); jp,_,_=g['worldAt'](a,t,c); pa,_,_=g['worldAt'](g['parentOf'][a],t,c)
        tip=g['rotate'](conj(cr),jp+(jp-pa)*0.8-cp)
        if center is not None:
            off=np.maximum(np.abs(tip-center)-extent,0); dist[label]=round(float(np.linalg.norm(off))*100,1)
        else: dist[label]=None
    print(f"{family:12s} {h['clip']:30s} left hand moved {h['leftHandMoved']*100:4.1f} cm, handle back {h['travel']*100:4.1f} cm | fingertip to handle model cm {dist} ({name})")
print('no left hand clip:',[r for r in gen['report'] if len(r)>2 and r[1]=='charge'])
