# Each gun's animator clips, in the order its controller lists them (Tools/gen_reload_holds; run before gen_export, which reads
# gun_families.json): the runtime picks a gun's animation set from that order (VRReloadController.AnimationFamily), and the clip a
# magazine type plays can come from another set (the AK-102's controller: ak101_mag_in_0, ak100_mag_in_1..5, ak103_mag_in_6).
# The controller is the WeaponPrefab's _originalAnimatorController: in the gun's container, or another gun's (matched by path id); an
# AnimatorOverrideController is its base's clips with its overrides. Clips sit in the folders' client_assets bundles, or in the gun's own
# container (a content mod's guns), named by CAB + path id.
import json, os, sys
import UnityPy

from game_paths import BUNDLES
guns = json.load(open('guns.json'))
controllers = {}
controllerOfGun = {}
readingFile = {}
clipNames = {}

def clipReference(pointer):
    assetsFile = readingFile['file']
    cab = assetsFile.name.lower() if pointer.m_FileID == 0 else assetsFile.externals[pointer.m_FileID - 1].path.split('/')[-1].lower()
    return (cab, pointer.m_PathID)

# The clip each magazine type plays: the controller's blend trees on RelTypeNew (the magazine going in) / RelTypeOld (coming out), threshold
# = magAnimationIndex. Not the clip's number: the SA58 plays sa58_mag_in_0 for types 0 and 2 and _2 for its drum (3), the G28 _1 / _2 for 0 / 1.
# Per parameter, type -> the controller clip index (a child blend node: its first leaf); every state's that blends on it (the drop-the-
# magazine one too: gen_export keeps the magazine clips).
MAGAZINE_TYPE_PARAMETERS = {'RelTypeNew': 'in', 'RelTypeOld': 'out'}

def magazineBlends(controllerObject):
    def data(value):
        return value.get('data', value) if isinstance(value, dict) else value
    tree = controllerObject.read_typetree()
    strings = dict(tree['m_TOS'])
    result = {}
    for stateMachine in tree['m_Controller']['m_StateMachineArray']:
        for state in data(stateMachine)['m_StateConstantArray']:
            for blendTree in data(state)['m_BlendTreeConstantArray']:
                nodes = [data(node) for node in data(blendTree)['m_NodeArray']]
                def firstLeaf(index, depth=0):
                    node = nodes[index]
                    if not node.get('m_ChildIndices') or depth > 8:
                        return node.get('m_ClipID', -1)
                    return firstLeaf(node['m_ChildIndices'][0], depth + 1)
                for node in nodes:
                    direction = MAGAZINE_TYPE_PARAMETERS.get(strings.get(node.get('m_BlendEventID')))
                    children = node.get('m_ChildIndices') or []
                    if direction is None or not children:
                        continue
                    thresholds = data(node.get('m_Blend1dData', {})).get('m_ChildThresholdArray', [])
                    for child, threshold in zip(children, thresholds):
                        result.setdefault(direction, []).append((int(round(threshold)), firstLeaf(child)))
    return result

def readController(controllerObject):
    controller = controllerObject.read()
    if controllerObject.type.name == 'AnimatorController':
        try:
            blends = magazineBlends(controllerObject)
        except Exception as error:
            blends = dict(error=repr(error)[:100])
        return dict(name=controller.m_Name, clips=[clipReference(clip) for clip in controller.m_AnimationClips], blends=blends)
    overrides = {}
    for pair in controller.m_Clips:
        overrides[clipReference(pair.m_OriginalClip)] = clipReference(pair.m_OverrideClip)
    baseClips = None
    if controller.m_Controller.m_FileID == 0 and controller.m_Controller.m_PathID != 0:
        try:
            baseClips = [clipReference(clip) for clip in controller.m_Controller.read().m_AnimationClips]
        except Exception:
            baseClips = None
    return dict(name=controller.m_Name, base=controller.m_Controller.m_PathID, baseClips=baseClips, overrides=overrides)

for gun in guns:
    try:
        environment = UnityPy.load(BUNDLES + gun['path'])
    except Exception as error:
        print('ERR', gun['name'], repr(error)[:100], flush=True)
        continue
    for assetObject in environment.objects:
        readingFile['file'] = assetObject.assets_file
        if assetObject.type.name == 'AnimationClip':
            try:
                clipNames[(assetObject.assets_file.name.lower(), assetObject.path_id)] = assetObject.peek_name()
            except Exception:
                pass
        if assetObject.type.name in ('AnimatorController', 'AnimatorOverrideController') and assetObject.path_id not in controllers:
            try:
                controllers[assetObject.path_id] = readController(assetObject)
            except Exception as error:
                controllers[assetObject.path_id] = dict(error=repr(error)[:100])
    for assetObject in environment.objects:
        if assetObject.type.name != 'MonoBehaviour':
            continue
        try:
            tree = assetObject.read_typetree()
        except Exception:
            continue
        if '_originalAnimatorController' in tree:
            controllerOfGun[gun['name']] = tree['_originalAnimatorController']['m_PathID']
            break

def resolve(pathId, depth=0):
    entry = controllers.get(pathId)
    if entry is None or depth > 4:
        return None, None
    if 'clips' in entry:
        return entry['name'], entry['clips']
    baseClips = entry.get('baseClips')
    if baseClips is None:
        _, baseClips = resolve(entry.get('base'), depth + 1)
    if baseClips is None:
        return entry.get('name'), None
    return entry['name'], [entry['overrides'].get(clip, clip) for clip in baseClips]

# An override controller keeps its base's blend trees (its clips are the base's indices, overridden)
def resolveBlends(pathId, depth=0):
    entry = controllers.get(pathId)
    if entry is None or depth > 4:
        return None
    if 'clips' in entry:
        return entry.get('blends')
    return resolveBlends(entry.get('base'), depth + 1)

def magazineTypes(blends, clips):
    if not blends or 'error' in blends or clips is None:
        return None
    types = {}
    for direction, pairs in blends.items():
        for magazineType, clipIndex in pairs:
            if 0 <= clipIndex < len(clips) and clips[clipIndex]:
                played = types.setdefault(direction, {}).setdefault(str(magazineType), [])
                if clips[clipIndex] not in played:
                    played.append(clips[clipIndex])
    return types or None

for folder, _, files in os.walk(BUNDLES + 'assets/content/weapons'):
    for fileName in files:
        if not fileName.endswith('.bundle') or '_container' in fileName:
            continue
        try:
            bundleEnvironment = UnityPy.load(os.path.join(folder, fileName))
        except Exception:
            continue
        for assetObject in bundleEnvironment.objects:
            if assetObject.type.name == 'AnimationClip':
                try:
                    clipNames[(assetObject.assets_file.name.lower(), assetObject.path_id)] = assetObject.peek_name()
                except Exception:
                    pass

result = {}
for gun in guns:
    pathId = controllerOfGun.get(gun['name'])
    name, clips = resolve(pathId) if pathId is not None else (None, None)
    named = None if clips is None else [clipNames.get(clip) for clip in clips]
    result[gun['name']] = dict(controller=name, clips=named, magazineTypes=magazineTypes(resolveBlends(pathId), named) if pathId is not None else None)
    print(gun['name'], name, None if clips is None else len(clips), flush=True)
json.dump(result, open('gun_families.json', 'w'))
print('done', sum(1 for entry in result.values() if entry['clips']), 'of', len(result), 'guns with their clips')
