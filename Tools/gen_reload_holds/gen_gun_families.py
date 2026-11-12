# Each gun's animator clips, in the order its controller lists them (Tools/gen_reload_holds; run before gen_export, which reads
# gun_families.json): the runtime picks a gun's animation set from that order (VRReloadController.AnimationFamily), and the clip a
# magazine type plays can come from another set (the AK-102's controller: ak101_mag_in_0, ak100_mag_in_1..5, ak103_mag_in_6).
# The controller is the WeaponPrefab's _originalAnimatorController: in the gun's container, or another gun's (matched by path id); an
# AnimatorOverrideController is its base's clips with its overrides. Clips sit in the folders' client_assets bundles (named by CAB + path id).
import json, os, sys
import UnityPy

BUNDLES = 'F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/'
guns = json.load(open('guns.json'))
controllers = {}
controllerOfGun = {}
readingFile = {}

def clipReference(pointer):
    assetsFile = readingFile['file']
    cab = assetsFile.name.lower() if pointer.m_FileID == 0 else assetsFile.externals[pointer.m_FileID - 1].path.split('/')[-1].lower()
    return (cab, pointer.m_PathID)

def readController(controllerObject):
    controller = controllerObject.read()
    if controllerObject.type.name == 'AnimatorController':
        return dict(name=controller.m_Name, clips=[clipReference(clip) for clip in controller.m_AnimationClips])
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

clipNames = {}
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
    result[gun['name']] = dict(controller=name, clips=None if clips is None else [clipNames.get(clip) for clip in clips])
    print(gun['name'], name, None if clips is None else len(clips), flush=True)
json.dump(result, open('gun_families.json', 'w'))
print('done', sum(1 for entry in result.values() if entry['clips']), 'of', len(result), 'guns with their clips')
