# Which clips animate each grenade model's pin (diagnostic for gen_grenades.py): every root with a pin + ring, every clip in the
# folder that binds the pin's path, and how far the pin travels in it
import os, re, sys, zlib
import numpy as np, UnityPy
import gen_holds as holds
from gen_grenades import load_roots, pin_and_ring, ROOT, FOLDERS

for folder in (sys.argv[1:] or FOLDERS):
    d = ROOT + folder + '/'
    clips = {}
    for o in UnityPy.load(d + 'client_assets.bundle').objects:
        if o.type.name == 'AnimationClip':
            try:
                c = o.read(); clips[c.m_Name] = c
            except Exception:
                pass
    for container in sorted(c for c in os.listdir(d) if c.endswith('_container.bundle')):
        for root_name, tree in load_roots(d + container).items():
            pin, ring = pin_and_ring(tree[0])
            if not pin:
                continue
            pin_hash = zlib.crc32(pin.encode()) & 0xffffffff
            ring_hash = zlib.crc32(ring.encode()) & 0xffffffff if ring else None
            hits = []
            for name, clip in clips.items():
                bound = [b.path for b in clip.m_ClipBindingConstant.genericBindings]
                if pin_hash not in bound and ring_hash not in bound:
                    continue
                g = holds.setup(tree, clip)
                stop = float(clip.m_MuscleClip.m_StopTime)
                parent = g['parentOf'][pin]
                rest = None; travel = 0.0; first = None
                for i in range(int(stop * 60) + 1):
                    t = i / 60; cache = {}
                    pp, pr, _ = g['worldAt'](parent, t, cache)
                    p = g['rotate'](holds.conj(pr), g['worldAt'](pin, t, cache)[0] - pp)
                    if rest is None:
                        rest = p
                    dist = float(np.linalg.norm(p - rest))
                    if first is None and dist > 0.002:
                        first = t
                    travel = max(travel, dist)
                hits.append(f'{name} ({stop:.2f}s, travel {travel * 100:.1f} cm{"" if first is None else f" from {first:.2f}s"})')
            print(f'{container} / {root_name}: pin {pin} ->', '; '.join(sorted(hits)) if hits else 'NO clip binds it')
