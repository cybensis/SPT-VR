# Ring pulls from the grenades' own pin-pull clips (the grenade plan's round 3): for every grenade model with a pin and a ring, the
# clip that pulls it (<prefix>_idle_to_out / *_pin_pull_out), sampled with the reload generator's forward kinematics, gives
#  - the pin's pull axis and how far it slides on it before it's free (in the pin's PARENT frame: what holds it on the grenade)
#  - the clip's left hand where the pin starts moving (the finger in the ring): palm pose + index tip in the RING's frame
#  - that moment's left finger pose (local eulers, palm children Digit11..Digit53, as the reload tables)
# A bundle holds several roots that share bone paths, so every root is its own tree; the clip goes with the root it animates most.
# Output: out/GrenadeRings.json and a summary. usage: python gen_grenades.py
import os, re, json, math, zlib
import numpy as np, UnityPy
import gen_holds as holds

ROOT = holds.ROOT
FOLDERS = ['f1', 'm67', 'rgd5', 'rgn', 'zarya', 'm18', 'm7920', 'v40', 'chattabka_vog17', 'chattabka_vog25', 'rdg2']
STEP = 1 / 120


def load_roots(path):
    env = UnityPy.load(path)
    transforms = {o.path_id: o.read() for o in env.objects if o.type.name == 'Transform'}
    names = {}

    def name(t):
        key = t.m_GameObject.path_id
        if key not in names:
            names[key] = t.m_GameObject.read().m_Name
        return names[key]

    roots = {}
    for top in transforms.values():
        if top.m_Father.path_id != 0:
            continue
        prefab, parent = {}, {}

        def walk(t, prefix):
            for child in t.m_Children:
                node = transforms.get(child.path_id)
                if node is None:
                    continue
                p = name(node) if prefix == '' else prefix + '/' + name(node)
                a, b, s = node.m_LocalPosition, node.m_LocalRotation, node.m_LocalScale
                prefab[p] = (np.array([a.x, a.y, a.z]), np.array([b.x, b.y, b.z, b.w]), np.array([s.x, s.y, s.z]))
                parent[p] = prefix
                walk(node, p)

        walk(top, '')
        roots[name(top)] = (prefab, parent, {zlib.crc32(p.encode()) & 0xffffffff: p for p in prefab})
    return roots


def pin_and_ring(prefab):
    pin = ring = None
    for p in prefab:
        last = p.split('/')[-1]
        if '/Weapon_root_anim/weapon/' not in '/' + p and not p.startswith('Weapon_root/Weapon_root_anim/weapon/'):
            continue
        if re.fullmatch(r'(weapon_)?(grenade_)?pin(_[a-z0-9]+)?', last) and not re.search(r'pin_[lr]$', last):
            if pin is None or len(p) < len(pin):
                pin = p
    if pin is None:
        return None, None
    for p in prefab:
        last = p.split('/')[-1]
        if p.startswith(pin + '/') and p.count('/') == pin.count('/') + 1 and 'ring' in last and 'LOD' not in last:
            ring = p
    return pin, ring


def tip(g, path, t, cache):
    joint = g['worldAt'](path, t, cache)[0]
    previous = g['worldAt'](g['parentOf'][path], t, cache)[0]
    return joint + (joint - previous) * 0.8


def in_frame(g, frame_pos, frame_rot, pos, rot=None):
    c = holds.conj(frame_rot)
    p = g['rotate'](c, pos - frame_pos)
    return (p, holds.qmul(c, rot)) if rot is not None else p


def analyse(folder, container, root_name, tree, clip):
    g = holds.setup(tree, clip)
    P = g['prefabLocal']
    pin, ring = pin_and_ring(P)
    palm = holds.bone(P, '/Base HumanLPalm')
    index = holds.bone(P, '/Base HumanLDigit23')
    hook = holds.bone(P, '/Base HumanLDigit22')
    marker = holds.bone(P, '/weapon_L_IK_marker')
    fingers = holds.fingerPaths(P, 'L')
    weapon_root = 'Weapon_root' if 'Weapon_root' in P else None
    if not (pin and ring and palm and weapon_root):
        return None
    pin_parent = g['parentOf'][pin]
    stop = float(clip.m_MuscleClip.m_StopTime)
    samples = []
    for i in range(int(stop / STEP) + 1):
        t = i * STEP
        cache = {}
        rp, rr, _ = g['worldAt'](weapon_root, t, cache)
        pp, pr, _ = g['worldAt'](pin_parent, t, cache)
        pinp, pinr, _ = g['worldAt'](pin, t, cache)
        ringp, ringr, _ = g['worldAt'](ring, t, cache)
        palmp, palmr, _ = g['worldAt'](palm, t, cache)
        markerPose = g['worldAt'](marker, t, cache)[:2] if marker else None
        samples.append(dict(t=t, pin_in_parent=in_frame(g, pp, pr, pinp), ring=(ringp, ringr), palm=(palmp, palmr),
                            index=tip(g, index, t, cache) if index else None, pin_world=pinp,
                            hook=g['worldAt'](hook, t, cache)[0] if hook else None, marker=markerPose,
                            pin_parent=(pp, pr)))
    rest = samples[0]['pin_in_parent']
    start = next((s for s in samples if np.linalg.norm(s['pin_in_parent'] - rest) > 0.002), None)
    if start is None:
        return dict(folder=folder, container=container, root=root_name, clip=clip.m_Name, pin=pin, ring=ring, moved=False)
    axis_sample = next((s for s in samples if s['t'] >= start['t'] and np.linalg.norm(s['pin_in_parent'] - rest) > 0.015), samples[-1])
    axis = axis_sample['pin_in_parent'] - rest
    axis = axis / max(np.linalg.norm(axis), 1e-9)
    # How far the pin runs along that line before it leaves it (the prongs out of the fuse)
    free = 0.0
    for s in samples:
        if s['t'] < start['t']:
            continue
        d = s['pin_in_parent'] - rest
        along = float(np.dot(d, axis))
        lateral = float(np.linalg.norm(d - axis * along))
        if lateral > 0.004:
            break
        free = max(free, along)
    # The hand on the ring a moment before it moves (the finger hooked, not yet pulling)
    grab = max((s for s in samples if s['t'] <= start['t']), key=lambda s: s['t'])
    ringp, ringr = grab['ring']
    palm_in_ring, palm_rot_in_ring = in_frame(g, ringp, ringr, grab['palm'][0], grab['palm'][1])
    index_in_ring = in_frame(g, ringp, ringr, grab['index']) if grab['index'] is not None else None
    finger_eulers = None
    if fingers:
        finger_eulers = [holds.toUnityEuler(np.array(g['localAt'](j, grab['t'])[1], float)) for j in fingers[1:]]
    pin_rest_local = P[pin]
    # The arm's IK target (where your hand's latch goes) and the hooking joint, in the ring's frame at rest: the latch rides the ring
    marker_in_ring = marker_rot_in_ring = hook_in_ring = None
    if grab['marker'] is not None:
        marker_in_ring, marker_rot_in_ring = in_frame(g, ringp, ringr, grab['marker'][0], grab['marker'][1])
    if grab['hook'] is not None:
        hook_in_ring = in_frame(g, ringp, ringr, grab['hook'])
    # The ring's own rest pose in the pin parent's frame (where the latch is measured from before the pull)
    ring_in_parent = in_frame(g, grab['pin_parent'][0], grab['pin_parent'][1], ringp)
    return dict(
        folder=folder, container=container, root=root_name, clip=clip.m_Name, moved=True,
        pin=pin.split('/')[-1], ring=ring.split('/')[-1], pinParent=pin_parent.split('/')[-1],
        clipLength=round(stop, 3), pullStart=round(start['t'], 3),
        pullAxis=[round(float(v), 4) for v in axis], freeDistance=round(free, 4),
        pinRestLocalPosition=[round(float(v), 5) for v in pin_rest_local[0]],
        palmInRing=[round(float(v), 4) for v in palm_in_ring], palmRotationInRing=[round(float(v), 4) for v in palm_rot_in_ring],
        palmEulerInRing=holds.toUnityEuler(palm_rot_in_ring),
        indexTipInRing=[round(float(v), 4) for v in index_in_ring] if index_in_ring is not None else None,
        indexTipToRingCm=round(float(np.linalg.norm(index_in_ring)) * 100, 1) if index_in_ring is not None else None,
        markerInRing=[round(float(v), 4) for v in marker_in_ring] if marker_in_ring is not None else None,
        markerRotationInRing=[round(float(v), 4) for v in marker_rot_in_ring] if marker_rot_in_ring is not None else None,
        hookInRing=[round(float(v), 4) for v in hook_in_ring] if hook_in_ring is not None else None,
        ringInPinParent=[round(float(v), 4) for v in ring_in_parent],
        fingers=finger_eulers)


def vec(values):
    return 'new Vector3(' + ', '.join(f'{float(v):.4f}f' for v in values) + ')'


def quat(values):
    return 'new Quaternion(' + ', '.join(f'{float(v):.4f}f' for v in values) + ')'


def write_csharp(results, path):
    lines = [
        '// GENERATED by Tools/gen_reload_holds/gen_grenades.py from the game\'s own grenade models and their pin-pull clips - do not edit,',
        '// run the script again. One entry per pin bone (its name is unique per grenade model).',
        'using System.Collections.Generic;',
        'using UnityEngine;',
        '',
        'namespace TarkovVR.Source.Weapons',
        '{',
        '    internal static partial class GrenadeRings',
        '    {',
        '        private static readonly Dictionary<string, RingData> generated = new Dictionary<string, RingData>',
        '        {',
    ]
    for r in results:
        fingers = ', '.join(vec(f) for f in ([[0, 0, 0]] + (r['fingers'] or [])))
        lines.append(f'            // {r["container"]}: {r["clip"]}, pull from {r["pullStart"]} s')
        lines.append(f'            ["{r["pin"]}"] = new RingData("{r["ring"]}", {vec(r["pullAxis"])}, {vec(r["markerInRing"] or [0, 0, 0])}, '
                     f'{quat(r["markerRotationInRing"] or [0, 0, 0, 1])}, {vec(r["hookInRing"] or [0, 0, 0])},')
        lines.append(f'                new[] {{ {fingers} }}),')
    lines += ['        };', '    }', '}', '']
    open(path, 'w', newline='\r\n').write('\n'.join(lines))
    print('wrote', path, len(results), 'grenades')


def run():
    results, report = [], []
    for folder in FOLDERS:
        d = ROOT + folder + '/'
        clips = {}
        for o in UnityPy.load(d + 'client_assets.bundle').objects:
            if o.type.name == 'AnimationClip':
                try:
                    c = o.read()
                    clips[c.m_Name] = c
                except Exception:
                    pass
        # The current models (<name>_model[_simple].generated, with the pin-pull / throw up-down / tripwire clips) - the game's
        # 0.16 set; the older <name>.generated ones pull the pin inside their throw start and have no tripwire clips
        pulls = [n for n in clips if n.endswith('pin_pull_out')]
        for container in sorted(c for c in os.listdir(d) if c.endswith('_container.bundle')):
            roots = load_roots(d + container)
            best = None
            for clip_name in pulls:
                hashes = [b.path for b in clips[clip_name].m_ClipBindingConstant.genericBindings]
                for root_name, tree in roots.items():
                    if '_model' not in root_name:
                        continue
                    pin, ring = pin_and_ring(tree[0])
                    if not pin or not ring:
                        continue
                    matched = sum(h in tree[2] for h in hashes)
                    if best is None or matched > best[0]:
                        best = (matched, clip_name, root_name)
            if best is None:
                report.append(f'{folder}/{container}: no ring (or no pin-pull clip): {sorted(pulls)}')
                continue
            matched, clip_name, root_name = best
            result = analyse(folder, container, root_name, roots[root_name], clips[clip_name])
            if result is None:
                report.append(f'{folder}/{container}: {clip_name} on {root_name}: missing bones')
                continue
            result['bindingsMatched'] = matched
            results.append(result)
    os.makedirs('out', exist_ok=True)
    json.dump(results, open('out/GrenadeRings.json', 'w'), indent=1)
    write_csharp([r for r in results if r.get('moved')], '../../Source/Weapons/GrenadeRings.Generated.cs')
    for r in results:
        if not r.get('moved'):
            print(f"{r['container']}: {r['clip']} on {r['root']}: the pin doesn't move in this clip")
            continue
        print(f"{r['container']}: clip {r['clip']} ({r['clipLength']} s) on {r['root']}, pin {r['pin']} under {r['pinParent']}, ring {r['ring']}: "
              f"pull from {r['pullStart']} s along {r['pullAxis']}, free after {r['freeDistance'] * 100:.1f} cm; index tip {r['indexTipToRingCm']} cm off the ring; "
              f"palm in ring {r['palmInRing']} {r['palmEulerInRing']}")
    for line in report:
        print(line)


if __name__ == '__main__':
    run()
