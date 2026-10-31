import sys
import struct
import zlib
import math
import bisect
import numpy as np
import UnityPy

# Forward kinematics of a Mecanim clip at given times: every transform's world pose from the clip's curves (pos/euler/
# scale) and the prefab's local pose for anything the clip doesn't animate. Root = identity.
# usage: clip_pose.py <tree bundle> <clip bundle> <exact clip name> <time> [<time> ...]
treeBundle, clipBundle, clipName = sys.argv[1], sys.argv[2], sys.argv[3]
times = [float(value) for value in sys.argv[4:]]

def name(transform):
    return transform.m_GameObject.read().m_Name

prefabLocal = {}    # path -> (pos, quat xyzw, scale)
parentOf = {}
children = {}
treeEnvironment = UnityPy.load(treeBundle)
transforms = {o.path_id: o.read() for o in treeEnvironment.objects if o.type.name == "Transform"}
def walk(transform, prefix):
    for child in transform.m_Children:
        try:
            childTransform = child.read()
        except Exception:
            continue
        path = name(childTransform) if prefix == "" else prefix + "/" + name(childTransform)
        p, r, s = childTransform.m_LocalPosition, childTransform.m_LocalRotation, childTransform.m_LocalScale
        prefabLocal[path] = (np.array([p.x, p.y, p.z]), np.array([r.x, r.y, r.z, r.w]), np.array([s.x, s.y, s.z]))
        parentOf[path] = prefix
        children.setdefault(prefix, []).append(path)
        walk(childTransform, path)
roots = [t for t in transforms.values() if t.m_Father.path_id == 0]
for root in roots:
    walk(root, "")
hashToPath = {zlib.crc32(path.encode("utf-8")) & 0xffffffff: path for path in prefabLocal}

def curveCount(binding):
    if binding.typeID == 4 and binding.attribute == 2:
        return 4
    if binding.typeID == 4 and binding.attribute in (1, 3, 4):
        return 3
    return 1

def readStreamed(words):
    raw = struct.pack("<%dI" % len(words), *words)
    position = 0
    while position < len(raw):
        time, = struct.unpack_from("<f", raw, position); position += 4
        keyCount, = struct.unpack_from("<I", raw, position); position += 4
        keys = []
        for _ in range(keyCount):
            index, = struct.unpack_from("<I", raw, position); position += 4
            coefficients = struct.unpack_from("<4f", raw, position); position += 16
            keys.append((index, coefficients))
        yield time, keys

clip = None
for objectReader in UnityPy.load(clipBundle).objects:
    if objectReader.type.name == "AnimationClip":
        candidate = objectReader.read()
        if candidate.m_Name == clipName:
            clip = candidate
            break
owner = []
for binding in clip.m_ClipBindingConstant.genericBindings:
    path = hashToPath.get(binding.path)
    for component in range(curveCount(binding)):
        owner.append((path, binding.attribute, component, binding.typeID))
muscle = clip.m_MuscleClip.m_Clip
muscle = getattr(muscle, 'data', muscle)
streamed, dense, constant = muscle.m_StreamedClip, muscle.m_DenseClip, muscle.m_ConstantClip
series = {}
for time, keys in readStreamed(list(streamed.data)):
    if not math.isfinite(time) or abs(time) > 1e6:
        continue
    for index, coefficients in keys:
        series.setdefault(index, []).append((time, coefficients[3]))
for frame in range(dense.m_FrameCount):
    time = dense.m_BeginTime + frame / dense.m_SampleRate
    for curve in range(dense.m_CurveCount):
        series.setdefault(streamed.curveCount + curve, []).append((time, dense.m_SampleArray[frame * dense.m_CurveCount + curve]))
for curve, value in enumerate(constant.data):
    series.setdefault(streamed.curveCount + dense.m_CurveCount + curve, []).append((0.0, value))

def valueAt(curve, time):
    points = series.get(curve)
    if not points:
        return None
    keyTimes = [p[0] for p in points]
    index = bisect.bisect_right(keyTimes, time)
    if index == 0:
        return points[0][1]
    if index >= len(points):
        return points[-1][1]
    (t0, v0), (t1, v1) = points[index - 1], points[index]
    return v0 if t1 <= t0 else v0 + (v1 - v0) * (time - t0) / (t1 - t0)

def quatFromEuler(x, y, z):
    # Unity Quaternion.Euler: z, then x, then y (extrinsic) = qy * qx * qz
    def axis(a, degrees):
        h = math.radians(degrees) / 2
        v = np.zeros(3); v[a] = math.sin(h)
        return np.array([v[0], v[1], v[2], math.cos(h)])
    return quatMul(quatMul(axis(1, y), axis(0, x)), axis(2, z))

def quatMul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return np.array([aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz])

def rotate(q, v):
    qv = np.array([v[0], v[1], v[2], 0.0])
    conj = np.array([-q[0], -q[1], -q[2], q[3]])
    return quatMul(quatMul(q, qv), conj)[:3]

def localAt(path, time):
    pos, rot, scale = [c.copy() for c in prefabLocal[path]]
    euler = [None, None, None]
    for curve, (owned, attribute, component, typeID) in enumerate(owner):
        if owned != path or typeID != 4:
            continue
        value = valueAt(curve, time)
        if value is None:
            continue
        if attribute == 1: pos[component] = value
        elif attribute == 3: scale[component] = value
        elif attribute == 4: euler[component] = value
    if all(e is not None for e in euler):
        rot = quatFromEuler(*euler)
    return pos, rot, scale, euler

def worldAt(path, time, cache):
    if path in cache:
        return cache[path]
    if path == "":
        result = (np.zeros(3), np.array([0.0, 0.0, 0.0, 1.0]), np.ones(3))
    else:
        parentPos, parentRot, parentScale = worldAt(parentOf[path], time, cache)
        pos, rot, scale, _ = localAt(path, time)
        result = (parentPos + rotate(parentRot, parentScale * pos), quatMul(parentRot, rot), parentScale * scale)
    cache[path] = result
    return result

def find(suffix):
    matches = [p for p in prefabLocal if p.endswith(suffix)]
    return matches[0] if matches else None

release = find("/weapon_release")
weapon = find("Weapon_root_anim/weapon")
palm = find("/Base HumanRPalm")
fingerTips = {finger: find(f"/Base HumanRDigit{finger}3") for finger in range(1, 6)}
def tipOf(path, cache):
    # past the last joint by the middle joint's length
    joint = worldAt(path, time, cache)[0]
    previous = worldAt(parentOf[path], time, cache)[0]
    return joint + (joint - previous) * 0.8
print(f"=== {clipName}: release {release}")
for time in times:
    cache = {}
    releasePos = worldAt(release, time, cache)[0]
    weaponPos, weaponRot, _ = worldAt(weapon, time, cache)
    conj = np.array([-weaponRot[0], -weaponRot[1], -weaponRot[2], weaponRot[3]])
    palmPos, palmRot, _ = worldAt(palm, time, cache)
    palmInWeapon = rotate(conj, palmPos - weaponPos)
    palmRotInWeapon = quatMul(conj, palmRot)
    distances = " ".join(f"d{finger}={np.linalg.norm(tipOf(path, cache) - releasePos)*100:.1f}cm" for finger, path in fingerTips.items())
    print(f" t={time:.2f} palm in weapon {np.round(palmInWeapon*100, 2)} cm rot {np.round(palmRotInWeapon, 3)} | tip to release: {distances}")
    for finger in (2,):
        for joint in (1, 2, 3):
            path = find(f"/Base HumanRDigit{finger}{joint}")
            print(f"    Digit{finger}{joint} euler {np.round(localAt(path, time)[3], 2)}")
