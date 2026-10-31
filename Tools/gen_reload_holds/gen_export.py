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
