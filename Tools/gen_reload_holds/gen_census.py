# The generators' gun list (guns.json: every gun's template, class, reload mode, prefab folder) and each weapon folder's clip names
# (clip_census.json: its client_assets bundle's AnimationClips; with CONTAINER_CLIPS its containers' own as containerClips), from
# game_paths' bundles and items
import json, os
import UnityPy
from game_paths import ITEMS, WEAPONS, CONTAINER_CLIPS

items = json.load(open(ITEMS, encoding='utf-8'))
guns = []
for itemId, item in items.items():
    props = item.get('_props') or {}
    path = (props.get('Prefab') or {}).get('path', '')
    if 'weapClass' not in props or not path.startswith('assets/content/weapons/'):
        continue
    guns.append(dict(id=itemId, name=item['_name'], cls=props.get('weapClass'), mode=props.get('ReloadMode'), folder=path.split('/')[3], path=path))
json.dump(guns, open('guns.json', 'w'))

def clipsIn(bundle):
    clips = []
    try:
        for assetObject in UnityPy.load(bundle).objects:
            if assetObject.type.name == 'AnimationClip':
                clips.append(assetObject.peek_name())
    except Exception as error:
        print(bundle, 'ERR', repr(error)[:100])
    return clips

census = {}
for folder in sorted(os.listdir(WEAPONS)):
    folderPath = WEAPONS + folder + '/'
    if not os.path.isdir(folderPath):
        continue
    entry = dict(clips=clipsIn(folderPath + 'client_assets.bundle') if os.path.isfile(folderPath + 'client_assets.bundle') else [])
    if CONTAINER_CLIPS:
        entry['containerClips'] = [clip for container in sorted(os.listdir(folderPath)) if container.endswith('_container.bundle')
                                   and container.startswith('weapon_') for clip in clipsIn(folderPath + container)]
    if entry['clips'] or entry.get('containerClips'):
        census[folder] = entry
json.dump(census, open('clip_census.json', 'w'))
print('guns', len(guns), 'folders', len(census))
