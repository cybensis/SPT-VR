# Each gun's animator clips, in the order its controller lists them (Tools/gen_reload_holds; run before gen_export, which reads
# gun_families.json): the runtime picks a gun's animation set from that order (VRReloadController.AnimationFamily), and the clip a
# magazine type plays can come from another set (the AK-102's controller: ak101_mag_in_0, ak100_mag_in_1..5, ak103_mag_in_6).
# The controller is the WeaponPrefab's _originalAnimatorController: in the gun's container, or another gun's (matched by path id); an
# AnimatorOverrideController is its base's clips with its overrides. Clips sit in the folders' client_assets bundles, or in the gun's own
# container (a content mod's guns), named by CAB + path id.
import json, os, re, sys
import UnityPy

from clip_names import magClipName
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

# A state that plays one clip with no blend: 'states' = [(clip index, the RelType conditions of each transition into it)], so
# magazineTypes can tell by the clip's name which way it goes. The VAL's RELOAD OUT plays val_mag_out_0 for every magazine (its
# bundle also holds a val_mag_out_1 nothing plays); the PM's RELOAD OUT / RELOAD OUT DRUM are picked by RelTypeOld < 1 / > 0.
CONDITION_GREATER, CONDITION_LESS, CONDITION_EQUALS, CONDITION_NOT_EQUAL = 3, 4, 6, 7

def magazineBlends(controllerObject):
    def data(value):
        return value.get('data', value) if isinstance(value, dict) else value
    tree = controllerObject.read_typetree()
    strings = dict(tree['m_TOS'])
    result = {}
    singleStates = []
    for stateMachine in tree['m_Controller']['m_StateMachineArray']:
        machine = data(stateMachine)
        machineStates = [data(state) for state in machine['m_StateConstantArray']]
        conditionsInto = {}
        transitions = [data(transition) for state in machineStates for transition in state.get('m_TransitionConstantArray', [])]
        transitions += [data(transition) for transition in machine.get('m_AnyStateTransitionConstantArray', [])]
        for transition in transitions:
            typeConditions = [(strings.get(data(condition)['m_EventID']), data(condition)['m_ConditionMode'], data(condition)['m_EventThreshold'])
                              for condition in transition.get('m_ConditionConstantArray', [])]
            conditionsInto.setdefault(transition.get('m_DestinationState'), []).append(
                [condition for condition in typeConditions if condition[0] in MAGAZINE_TYPE_PARAMETERS])
        for stateIndex, state in enumerate(machineStates):
            leaves = [data(node) for blendTree in state['m_BlendTreeConstantArray'] for node in data(blendTree)['m_NodeArray']]
            if len(leaves) == 1 and not leaves[0].get('m_ChildIndices'):
                singleStates.append((leaves[0].get('m_ClipID', -1), conditionsInto.get(stateIndex, [])))
        for state in machine['m_StateConstantArray']:
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
    result['states'] = singleStates
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

HIGHEST_MAGAZINE_TYPE = 15   # VRReloadController.HIGHEST_MAGAZINE_TYPE

def conditionsPass(conditions, direction, magazineType):
    for parameter, mode, threshold in conditions:
        if MAGAZINE_TYPE_PARAMETERS[parameter] != direction:
            continue
        if (mode == CONDITION_GREATER and not magazineType > threshold) or (mode == CONDITION_LESS and not magazineType < threshold) \
                or (mode == CONDITION_EQUALS and magazineType != threshold) or (mode == CONDITION_NOT_EQUAL and magazineType == threshold):
            return False
    return True

# A way the blends don't cover: the single-clip states that play a magazine clip of it, a type to each that one of its ways in lets
# through ('*' = every type: one clip whatever the magazine). gen_export keeps the types the gun's magazines have.
def stateTypes(blends, clips):
    if not blends or 'error' in blends or clips is None:
        return None
    types = {}
    for direction in MAGAZINE_TYPE_PARAMETERS.values():
        if blends.get(direction):
            continue
        played = []
        for clipIndex, conditionSets in blends.get('states', []):
            clip = clips[clipIndex] if 0 <= clipIndex < len(clips) else None
            if clip and re.match(r'.+?_mag_' + direction + r'(?:_\d+)?$', magClipName('', clip)):
                played.append((clip, conditionSets))
        if not played:
            continue
        if len({clip for clip, _ in played}) == 1:
            types[direction] = {'*': [played[0][0]]}
            continue
        for magazineType in range(HIGHEST_MAGAZINE_TYPE + 1):
            for clip, conditionSets in played:
                if any(conditionsPass(conditions, direction, magazineType) for conditions in conditionSets):
                    entry = types.setdefault(direction, {}).setdefault(str(magazineType), [])
                    if clip not in entry:
                        entry.append(clip)
    return types or None

def magazineTypes(blends, clips):
    if not blends or 'error' in blends or clips is None:
        return None
    types = {}
    for direction, pairs in blends.items():
        if direction == 'states':
            continue
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
    blends = resolveBlends(pathId) if pathId is not None else None
    result[gun['name']] = dict(controller=name, clips=named, magazineTypes=magazineTypes(blends, named), stateTypes=stateTypes(blends, named))
    print(gun['name'], name, None if clips is None else len(clips), flush=True)
json.dump(result, open('gun_families.json', 'w'))
print('done', sum(1 for entry in result.values() if entry['clips']), 'of', len(result), 'guns with their clips')
