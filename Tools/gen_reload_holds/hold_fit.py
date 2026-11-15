# How far the off hand's finger joints are from a magazine's mesh at the moment gen_holds takes a clip's hold: checks a hold against
# the magazine it's used for. A joint 0.6-1.7 cm off the surface is on it (a finger's radius); 3+ cm, the hand isn't shaped for that
# magazine (the SA58's drum clip on its 30 round: 3-8 cm; its own _0 clip: under 1.7).
# python hold_fit.py <weapons folder> <clip set> <in_N|out_N>=<magazine bundle name> ...
#   e.g. python hold_fit.py sa58 sa58 in_0=mag_fal_ds_arms_fal_sa58_762x51_30 out_2=mag_fal_x_products_x_fal_762x51_50
# Which clip a gun plays for a magazine type: gun_families.json magazineTypes (the animator's own), not the clip's number.
import os, sys
import numpy as np, UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
import gen_holds as G
from game_paths import BUNDLES

MAGAZINES = BUNDLES + 'assets/content/items/mods/magazines/'
FINGERS = ['thumb', 'index', 'middle', 'ring', 'pinky']

def rotate(q, v): return G.qmul(G.qmul(q, np.array([v[0], v[1], v[2], 0.0])), G.conj(q))[:3]

# The LOD0 mesh in the item view's frame, and the view's pose on its slot bone: its ModPlacer, else Euler(90, 0, 0) (SlotView.InsertItem)
def magazineMesh(bundle):
    environment = UnityPy.load(MAGAZINES + bundle + '.bundle')
    transforms = {o.path_id: o.read() for o in environment.objects if o.type.name == 'Transform'}
    seatPosition, seatRotation = np.zeros(3), G.axisq(90, [1, 0, 0])
    for o in environment.objects:
        if o.type.name != 'MonoBehaviour':
            continue
        try:
            tree = o.read_typetree()
        except Exception:
            continue
        if 'ModPosition' in tree and 'ModRotation' in tree:
            p, r = tree['ModPosition'], tree['ModRotation']
            seatPosition = np.array([p['x'], p['y'], p['z']])
            seatRotation = G.qmul(G.qmul(G.axisq(r['y'], [0, 1, 0]), G.axisq(r['x'], [1, 0, 0])), G.axisq(r['z'], [0, 0, 1]))
    best = None
    for o in environment.objects:
        if o.type.name != 'MeshFilter':
            continue
        meshFilter = o.read(); name = meshFilter.m_GameObject.read().m_Name
        if best is not None and 'LOD0' not in name:
            continue
        handler = MeshHandler(meshFilter.m_Mesh.read()); handler.process()
        vertices = np.array(handler.m_Vertices, float).reshape(-1, 3)
        triangles = np.array([i for sub in handler.get_triangles() for t in sub for i in t], int).reshape(-1, 3)
        link = next(t for t in transforms.values() if t.m_GameObject.path_id == meshFilter.m_GameObject.path_id)
        while link.m_Father.path_id != 0:
            s, r, a = link.m_LocalScale, link.m_LocalRotation, link.m_LocalPosition
            q = np.array([r.x, r.y, r.z, r.w])
            vertices = np.array([rotate(q, v * np.array([s.x, s.y, s.z])) for v in vertices]) + np.array([a.x, a.y, a.z])
            link = transforms[link.m_Father.path_id]
        best = (name, vertices, triangles)
        if 'LOD0' in name:
            break
    return best, seatPosition, seatRotation

def distanceToMesh(point, a, b, c):
    ab, ac, ap = b - a, c - a, point - a
    normal = np.cross(ab, ac)
    d1, d2 = (ab * ap).sum(1), (ac * ap).sum(1)
    bp, cp = point - b, point - c
    d3, d4, d5, d6 = (ab * bp).sum(1), (ac * bp).sum(1), (ab * cp).sum(1), (ac * cp).sum(1)
    inside = (d3 * d6 - d5 * d4 >= 0) & (d5 * d2 - d1 * d6 >= 0) & (d1 * d4 - d3 * d2 >= 0)
    plane = np.abs((ap * normal).sum(1)) / np.sqrt((normal * normal).sum(1) + 1e-12)
    def segment(p0, p1):
        d = p1 - p0; t = np.clip(((point - p0) * d).sum(1) / ((d * d).sum(1) + 1e-12), 0, 1)
        return np.linalg.norm(p0 + d * t[:, None] - point, axis=1)
    edge = np.minimum(np.minimum(segment(a, b), segment(b, c)), segment(c, a))
    return float(np.where(inside, plane, edge).min())

folder, prefix = sys.argv[1], sys.argv[2]
clips = G.folderClips(folder)
directory = G.ROOT + folder + '/'
trees = {c: G.loadTree(directory + c) for c in os.listdir(directory) if c.endswith('_container.bundle') and c.startswith('weapon_')}
probe = next(n for n in clips if n.startswith(prefix + '_mag_in'))
hashes = [b.path for b in clips[probe].m_ClipBindingConstant.genericBindings]
tree = trees[max((c for c in trees if G.bone(trees[c][0], '/mod_magazine')), key=lambda c: sum(h in trees[c][2] for h in hashes))]
P = tree[0]; J = G.fingerPaths(P); magazineBone = G.bone(P, '/mod_magazine')
for pair in sys.argv[3:]:
    suffix, bundle = pair.split('=')
    clipName = f'{prefix}_mag_{suffix}'; pull = suffix.startswith('out')
    if clipName not in clips:
        print(clipName, 'no such clip'); continue
    (meshName, vertices, triangles), seatPosition, seatRotation = magazineMesh(bundle)
    g = G.setup(tree, clips[clipName])
    hold = G.magHold(g, J, magazineBone, pull, prefix)
    if hold is None:
        print(clipName, 'no hold'); continue
    cache = {}
    bonePosition, boneRotation, _ = g['worldAt'](magazineBone, hold['time'], cache)
    viewRotation = G.qmul(boneRotation, seatRotation); viewPosition = bonePosition + rotate(boneRotation, seatPosition)
    world = np.array([rotate(viewRotation, v) for v in vertices]) + viewPosition
    a, b, c = world[triangles[:, 0]], world[triangles[:, 1]], world[triangles[:, 2]]
    size = vertices.max(0) - vertices.min(0)
    print(f'\n{clipName} {"pull" if pull else "insert"} at {hold["time"]:.2f} s on {meshName} ({np.round(size * 100, 1)} cm)')
    for finger in range(5):
        joints = [g['worldAt'](j, hold['time'], cache)[0] for j in J[1 + finger * 3:4 + finger * 3]]
        print(f'  {FINGERS[finger]:6s} ' + '  '.join(f'{label} {distanceToMesh(p, a, b, c) * 100:4.1f}' for label, p in zip(('base', 'mid', 'end'), joints)) + ' cm')
