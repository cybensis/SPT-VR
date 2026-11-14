# The jolt Tarkov's clips give the gun when a part hits its stop: a magazine seated, a bolt or slide slammed home (the charging handle
# let go, the bolt catch hit), a bolt-action's bolt pushed home, a pump closed. For GunJolt: the gun's pose in its own frame from that
# moment, less the straight line to `window` s later (the clip's slow move back to the aim), at 60 Hz.
# Gun = Weapon_root_anim/weapon in Weapon_root's frame (its -y = forward along the bore, +z = up). Position in m, rotation as a rotation
# vector in degrees, both in the gun's frame at the stop.
# python gen_jolts.py <out json> [folders...]
import sys, os, re, math, json, time
import numpy as np
import gen_holds as G
from clip_names import magClipName
from game_paths import BUNDLES

WINDOW = 0.35           # s after the stop the slow motion is measured to
MIN_WINDOW = 0.1        # s: a clip that ends sooner after the stop has no jolt to give
STEP = 1 / 60
MOVE_AWAY = 0.01        # m off its final spot = the part is on its way
ARRIVED = 0.001         # m: home

# The part that hits its stop, per moment: the first of these the clip moves along the bore by MOVE_AWAY or more
PARTS = dict(magazine=['mod_magazine'], slam=['weapon_bolt', 'weapon_slide', 'mod_charge'], catch=['weapon_bolt', 'weapon_slide'],
             boltClose=['weapon_bolt'], pump=['mod_handguard', 'weapon_slide'])
# The clips each moment comes from, the first a family has
def clipCandidates(family, kind):
    if kind == 'slam':
        return [f'{family}_reload_charge_0', f'{family}_reload_charge', f'{family}_charge_end_0', f'{family}_reload_charge_end', f'{family}_reload_charge_1',
                f'{family}_reload_charge_2']
    if kind == 'catch':
        return [f'{family}_reload_catch_0', f'{family}_reload_catch', f'{family}_catch_end_0', f'{family}_reload_catch_end', f'{family}_reload_off_catch',
                f'{family}_reload_end_catch', f'{family}_reload_catch_1']
    if kind == 'boltClose':
        return [f'{family}_bolt_close_0', f'{family}_bolt_close', f'{family}_bolt_close_1', f'{family}_bolt_close_2']
    if kind == 'pump':
        return [f'{family}_fire_0', f'{family}_fire']
    return []

# The pump shotguns' families: gen_shotguns' output, else the mod's own copy of the export
def shotgunFamilies():
    if os.path.exists('shotguns.json'):
        return set(json.load(open('shotguns.json')).get('shotguns', {}))
    # (the game folder: three up from StreamingAssets/Windows)
    exported = os.path.join(BUNDLES, '..', '..', '..', 'BepInEx', 'plugins', 'sptvr', 'Assets', 'ReloadHolds.json')
    for path in ('out/ReloadHolds.json', os.path.normpath(exported)):
        if os.path.exists(path):
            return {f for f, g in json.load(open(path)).get('shotguns', {}).items() if g.get('pump')}
    return set()
SHOTGUNS = shotgunFamilies()

def slerp(a, b, t):
    if np.dot(a, b) < 0: b = -b
    th = math.acos(min(1.0, float(np.dot(a, b))))
    if th < 1e-6: return a
    return (math.sin((1 - t) * th) * a + math.sin(t * th) * b) / math.sin(th)

def rotationVector(q):
    q = q if q[3] >= 0 else -q
    angle = 2 * math.acos(min(1.0, float(q[3])))
    s = math.sqrt(max(1e-12, 1 - float(q[3]) ** 2))
    return math.degrees(angle) * q[:3] / s

def jolt(tree, clip, partNames):
    g = G.setup(tree, clip)
    P = g['prefabLocal']
    weapon = G.bone(P, 'Weapon_root_anim/weapon')
    top = [p for p in P if p.endswith('Weapon_root')]
    if weapon is None or not top:
        return None
    top = top[0]
    stop = float(clip.m_MuscleClip.m_StopTime)
    times = [i * STEP for i in range(int(stop / STEP) + 1)]
    def gunPose(t, c):
        wp, wr, _ = g['worldAt'](weapon, t, c); rp, rr, _ = g['worldAt'](top, t, c)
        return g['rotate'](G.conj(rr), wp - rp), G.qmul(G.conj(rr), wr), wp, wr
    parts = [G.bone(P, '/' + name) for name in partNames]
    # A bolt / slide / pump under another name (the Glock's slide): what the clip moves furthest along the bore, not a hand's marker
    if partNames != PARTS['magazine']:
        animated = sorted(set(o[0] for o in g['owner'] if o[3] == 4 and o[0] and o[0].startswith(weapon + '/')))
        skip = ('marker', 'bend_goal', 'ik', 'human', 'patron', 'shell', 'magazine', 'camera')
        def boreTravel(path):
            ys = []
            for t in times[::4]:
                c = {}
                _, _, wp, wr = gunPose(t, c)
                ys.append(g['rotate'](G.conj(wr), g['worldAt'](path, t, c)[0] - wp)[1])
            return max(ys) - min(ys)
        others = [p for p in animated if not any(k in p.split('/')[-1].lower() for k in skip) and p not in parts]
        parts += sorted(others, key=lambda p: -boreTravel(p))[:1]
    for part in parts:
        if part is None or not part.startswith(weapon + '/'):
            continue
        name = part.split('/')[-1]
        track = []
        for t in times:
            c = {}
            _, _, wp, wr = gunPose(t, c)
            track.append(g['rotate'](G.conj(wr), g['worldAt'](part, t, c)[0] - wp))
        track = np.array(track)
        final = track[-1]
        # along the bore for the bolts, slides and pumps (a magazine goes in from below: all of it)
        travel = np.abs(track[:, 1] - final[1]) if name != 'mod_magazine' else np.linalg.norm(track - final, axis=1)
        away = [i for i in range(len(times)) if travel[i] > MOVE_AWAY]
        if not away:
            continue
        arrive = next((i for i in range(away[-1], len(times)) if np.linalg.norm(track[i] - final) < ARRIVED), None)
        if arrive is None:
            continue
        t0 = times[arrive]
        end = min(stop, t0 + WINDOW)
        if end - t0 < MIN_WINDOW:
            continue
        frames = int(round((end - t0) / STEP))
        p0, q0, _, _ = gunPose(t0, {})
        p1, q1, _, _ = gunPose(t0 + frames * STEP, {})
        positions, rotations = [], []
        for i in range(frames + 1):
            f = i / frames
            p, q, _, _ = gunPose(t0 + i * STEP, {})
            trendP = p0 + (p1 - p0) * f
            trendQ = slerp(q0, q1, f)
            positions.append([round(float(v), 5) for v in g['rotate'](G.conj(q0), p - trendP)])
            rotations.append([round(float(v), 3) for v in rotationVector(G.qmul(G.conj(trendQ), q))])
        peak = max(float(np.linalg.norm(v)) for v in positions)
        peakTurn = max(float(np.linalg.norm(v)) for v in rotations)
        return dict(clip=clip.m_Name, part=name, at=round(t0, 3), dt=round(STEP, 6), peak=round(peak, 4), peakTurn=round(peakTurn, 2),
                    position=positions, rotation=rotations)
    return None

def bestTree(trees, clip, needs):
    hashes = [b.path for b in clip.m_ClipBindingConstant.genericBindings]
    best = None
    for name, tree in trees.items():
        # (a magazine's model must have it; a bolt, slide or pump may go by another name: jolt() finds it)
        if G.bone(tree[0], 'Weapon_root_anim/weapon') is None or (needs == PARTS['magazine'] and not any(G.bone(tree[0], '/' + n) for n in needs)):
            continue
        score = sum(h in tree[2] for h in hashes)
        if best is None or score > best[0]:
            best = (score, name)
    return trees[best[1]] if best else None

def run(folders):
    result, report = {}, []
    for folder in folders:
        d = G.ROOT + folder + '/'
        if not os.path.isdir(d):
            continue
        try:
            clips = G.folderClips(folder)
        except Exception as e:
            report.append((folder, 'ERR clips', repr(e)[:80])); continue
        families = set()
        for n in clips:
            m = re.match(r'(.+?)_(?:mag_in|bolt_close)(?:_\d+)?$', magClipName(folder, n))
            if m: families.add(m.group(1))
            m = re.match(r'(.+?)_fire(?:_\d+)?$', n)
            if m and m.group(1).lower() in SHOTGUNS: families.add(m.group(1))
        if not families:
            continue
        trees = {}
        for c in os.listdir(d):
            if c.endswith('_container.bundle') and c.startswith('weapon_'):
                try: trees[c] = G.loadTree(d + c)
                except Exception as e: report.append((folder, c, 'ERR tree', repr(e)[:80]))
        if not trees:
            continue
        started = time.time()
        folderResult = {}
        for family in sorted(families):
            key = family.lower()
            events = {}
            # every magazine type's own clip
            for n in sorted(clips):
                m = re.match(re.escape(family) + r'_mag_in(?:_(\d+))?$', n)
                if not m: continue
                tree = bestTree(trees, clips[n], PARTS['magazine'])
                if tree is None: continue
                try: j = jolt(tree, clips[n], PARTS['magazine'])
                except Exception as e: report.append((key, n, 'ERR', repr(e)[:80])); continue
                if j: events[f'magazine|{int(m.group(1) or 0)}'] = j
            for kind in ('slam', 'catch', 'boltClose', 'pump'):
                if kind == 'pump' and key not in SHOTGUNS:
                    continue
                for n in clipCandidates(family, kind):
                    if n not in clips: continue
                    tree = bestTree(trees, clips[n], PARTS[kind])
                    if tree is None: continue
                    try: j = jolt(tree, clips[n], PARTS[kind])
                    except Exception as e: report.append((key, n, 'ERR', repr(e)[:80])); continue
                    if j:
                        events[kind] = j
                        break
            if events:
                folderResult[key] = events
        # Guns in one folder share a rig: what one family has no clip for, another's
        for key, events in folderResult.items():
            for kind in ('slam', 'catch', 'boltClose', 'pump'):
                if kind not in events:
                    mate = next((e[kind] for k, e in sorted(folderResult.items()) if kind in e), None)
                    if mate and (kind != 'pump' or key in SHOTGUNS):
                        events[kind] = dict(mate, borrowed=True)
        result.update(folderResult)
        print(f'{folder}: ' + ', '.join(f'{k} [' + ' '.join(f'{e}:{v["peak"]*100:.1f}cm/{v["peakTurn"]:.1f}deg' for e, v in sorted(ev.items())) + ']'
                                       for k, ev in sorted(folderResult.items())) + f' ({time.time() - started:.0f}s)', flush=True)
    return result, report

if __name__ == '__main__':
    folders = sys.argv[2:] or sorted(os.listdir(G.ROOT))
    result, report = run(folders)
    os.makedirs('out', exist_ok=True)
    json.dump(result, open(sys.argv[1], 'w'), separators=(',', ':'))
    print('DONE families', len(result), 'problems', len(report), flush=True)
    for r in report:
        print('  ', r)
