# A content mod's guns for the generators: the game's weapon and item bundles with the mod's laid over them (hardlinks: nothing copied,
# the out folder must be on the game's drive; the generators only read them), and the game's item database with the mod's items merged
# in the way WTT's CustomItems load them: a clone of itemTplToClone with overrideProperties on top, and addtoModSlots putting the new
# item in every slot named in modSlot that takes the item it was cloned from.
# python build_overlay.py <game folder> <mod folder> <out folder>; then SPTVR_BUNDLES=<out>/bundles/ SPTVR_ITEMS=<out>/items.json
import copy, json, os, shutil, sys

game, mod, out = sys.argv[1:4]
gameBundles = os.path.join(game, 'EscapeFromTarkov_Data', 'StreamingAssets', 'Windows')
modBundles = os.path.join(mod, 'bundles')
outBundles = os.path.join(out, 'bundles')
if os.path.isdir(outBundles):
    shutil.rmtree(outBundles)

def linkTree(source, relative):
    count = 0
    for folder, _, files in os.walk(os.path.join(source, relative)):
        for fileName in files:
            sourcePath = os.path.join(folder, fileName)
            target = os.path.join(outBundles, os.path.relpath(sourcePath, source))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            if os.path.exists(target):
                os.remove(target)
            os.link(sourcePath, target)
            count += 1
    return count

for relative in ('assets/content/weapons', 'assets/content/items'):
    print('game', relative, linkTree(gameBundles, relative))
for relative in ('assets/content/weapons', 'assets/content/items'):
    if os.path.isdir(os.path.join(modBundles, relative)):
        print('mod', relative, linkTree(modBundles, relative))

# The bundle manifest (which bundles each one needs): the game's, with the mod's (its bundles.json) over it
manifest = json.load(open(os.path.join(gameBundles, 'Windows.json'), encoding='utf-8'))
modManifest = os.path.join(mod, 'bundles.json')
if os.path.isfile(modManifest):
    for entry in json.load(open(modManifest, encoding='utf-8-sig')).get('manifest', []):
        manifest[entry['key']] = dict(FileName=entry['key'], Dependencies=entry.get('dependencyKeys', []))
json.dump(manifest, open(os.path.join(outBundles, 'Windows.json'), 'w', encoding='utf-8'))

items = json.load(open(os.path.join(game, 'SPT_Runtime', 'SPT_Data', 'database', 'templates', 'items.json'), encoding='utf-8'))
added = []
slotAdditions = []
for folder, _, files in os.walk(os.path.join(mod, 'db', 'CustomItems')):
    for fileName in sorted(files):
        if not fileName.endswith('.json'):
            continue
        for itemId, entry in json.load(open(os.path.join(folder, fileName), encoding='utf-8-sig')).items():
            if not isinstance(entry, dict) or entry.get('itemTplToClone') not in items:
                continue
            item = copy.deepcopy(items[entry['itemTplToClone']])
            item['_id'] = itemId
            if entry.get('parentId'):
                item['_parent'] = entry['parentId']
            item['_props'].update(entry.get('overrideProperties') or {})
            item['_name'] = item['_props'].get('Name', item.get('_name'))
            items[itemId] = item
            added.append(itemId)
            if entry.get('addtoModSlots'):
                slotAdditions.append((itemId, entry['itemTplToClone'], set(entry.get('modSlot') or [])))
for itemId, clonedFrom, slotNames in slotAdditions:
    for item in items.values():
        for slot in (item.get('_props') or {}).get('Slots') or []:
            if slotNames and slot.get('_name') not in slotNames:
                continue
            for slotFilter in slot.get('_props', {}).get('filters', []):
                if clonedFrom in slotFilter.get('Filter', []) and itemId not in slotFilter['Filter']:
                    slotFilter['Filter'].append(itemId)
json.dump(items, open(os.path.join(out, 'items.json'), 'w', encoding='utf-8'))
print('items added', len(added), 'slot additions', len(slotAdditions))
