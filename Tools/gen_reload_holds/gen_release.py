# Which hand and finger presses each gun's magazine release in its own mag_out clip (Tools/gen_reload_holds; run after gen_holds,
# before gen_export, which reads release.json as releasePress), with the hands placed as the game places them:
# the gun hand on weapon_R_IK_marker (Tarkov IKs that palm there; the clips' own right arm is meaningless), the off hand as animated
# (its weight curve hands it to the clip during a reload; checked against weapon_L_IK_marker as palmOffL).
import sys, json, os, re
sys.path.insert(0, '.')
exec(open('gen_holds.py', encoding='utf-8').read().split("if __name__=='__main__':")[0])
census = json.load(open('clip_census.json'))
FINGERS = ('thumb', 'index', 'middle', 'ring', 'pinky')

def isReleasePart(name):
    return (re.search('release|mag_catch', name, re.I) is not None) and not re.search('bolt|slide', name, re.I) and 'LOD' not in name

def pickTree(folder, family, clips, prefix):
    d = ROOT + folder + '/'
    probe = next((n for n in clips if n.startswith(prefix + '_mag_in')), None)
    hashes = [b.path for b in clips[probe].m_ClipBindingConstant.genericBindings] if probe else []
    best = None
    for c in sorted(os.listdir(d)):
        if not (c.endswith('_container.bundle') and c.startswith('weapon_')):
            continue
        try:
            tree = loadTree(d + c)
        except Exception:
            continue
        P = tree[0]
        if not (bone(P, '/mod_magazine') and bone(P, '/weapon_R_IK_marker') and fingerPaths(P, 'L') and fingerPaths(P, 'R')):
            continue
        n = sum(h in tree[2] for h in hashes)
        if best is None or n > best[0]:
            best = (n, c, tree)
    return best

# The gun hand where the clip itself puts it, when that's on the gun (a pistol's reload animates the gun hand): within this of its marker
FK_RIGHT_WITHIN = 0.03
def rightPalmOff(g, P, t, cache):
    palm, _, _ = g['worldAt'](bone(P, '/Base HumanRPalm'), t, cache); marker, _, _ = g['worldAt'](bone(P, '/weapon_R_IK_marker'), t, cache)
    return float(np.linalg.norm(palm - marker))

def tipsAt(g, P, t, cache):
    out = {}
    marker, markerRot, _ = g['worldAt'](bone(P, '/weapon_R_IK_marker'), t, cache)
    rightAsAnimated = rightPalmOff(g, P, t, cache) <= FK_RIGHT_WITHIN
    for side in 'LR':
        J = fingerPaths(P, side)
        palmPos, palmRot, _ = g['worldAt'](J[0], t, cache)
        for f in range(5):
            last, _, _ = g['worldAt'](J[1 + f * 3 + 2], t, cache)
            before, _, _ = g['worldAt'](J[1 + f * 3 + 1], t, cache)
            tip = last + (last - before) * 0.8
            if side == 'R' and not rightAsAnimated:
                tip = marker + g['rotate'](markerRot, g['rotate'](conj(palmRot), tip - palmPos))
            out[(side, f)] = tip
    return out

FILTER = set(sys.argv[1:])
results = {}
done = set()
for folder, v in sorted(census.items()):
    if not os.path.isdir(ROOT + folder):
        continue
    names = v.get('clips', [])
    prefixes = sorted({re.match(r'(.+?)_mag_in(?:_\d+)?$', n).group(1) for n in names if re.match(r'(.+?)_mag_in(?:_\d+)?$', n)})
    if not prefixes:
        continue
    clips = {}
    try:
        for o in UnityPy.load(ROOT + folder + '/client_assets.bundle').objects:
            if o.type.name == 'AnimationClip':
                try:
                    c = o.read(); clips[c.m_Name] = c
                except Exception:
                    pass
    except Exception as e:
        print(folder, 'ERR clips', repr(e)[:80], flush=True); continue
    for prefix in prefixes:
        family = prefix.lower()
        if family in done or (FILTER and family not in FILTER):
            continue
        done.add(family)
        try:
            picked = pickTree(folder, family, clips, prefix)
            if picked is None:
                results[family] = dict(note='no usable rig'); print(family, 'no usable rig', flush=True); continue
            _, container, tree = picked
            P = tree[0]
            parts = [p for p in P if isReleasePart(p.split('/')[-1]) and '/mod_magazine/' not in p + '/' and not p.endswith('/mod_magazine')]
            outs = sorted(n for n in clips if re.match(re.escape(prefix) + r'_mag_out(_\d+)?$', n))
            found = None
            for clipName in outs:
                g = setup(tree, clips[clipName]); stop = float(clips[clipName].m_MuscleClip.m_StopTime)
                for part in parts:
                    rest = None; peak = 0.0; peakT = None
                    for i in range(int(stop * 30) + 1):
                        t = i / 30; lp, lq = g['localAt'](part, t)[:2]; lp = np.array(lp, float); lq = np.array(lq, float)
                        if rest is None: rest = (lp, lq)
                        move = float(np.linalg.norm(lp - rest[0])) * 1000 + ang(lq, rest[1])
                        if move > peak: peak, peakT = move, t
                    if peakT is not None and peak >= 0.5 and (found is None or peak > found[3]):
                        # onset: the last moment before the peak with the part under a tenth of its travel
                        onset = 0.0
                        for i in range(int(peakT * 30), -1, -1):
                            lp, lq = g['localAt'](part, i / 30)[:2]
                            if float(np.linalg.norm(np.array(lp, float) - rest[0])) * 1000 + ang(np.array(lq, float), rest[1]) < 0.1 * peak:
                                onset = i / 30; break
                        found = (clipName, part, peakT, peak, g, onset)
                if found:
                    break
            if not found:
                results[family] = dict(container=container, parts=[p.split('/')[-1] for p in parts], note='no mag_out clip moves a release part')
                print(family, 'no release press', [p.split('/')[-1] for p in parts], flush=True); continue
            clipName, part, t, peak, g, onset = found
            c = {}
            partAt, _, _ = g['worldAt'](part, t, c)
            tips = tipsAt(g, P, t, c)
            c0 = {}; partOn, _, _ = g['worldAt'](part, onset, c0); tipsOn = tipsAt(g, P, onset, c0)
            approach = {k: float(np.linalg.norm(tipsOn[k] - partOn)) - float(np.linalg.norm(tips[k] - partAt)) for k in tips}
            ranked = sorted(((float(np.linalg.norm(tip - partAt)), side, f) for (side, f), tip in tips.items()))
            Lpalm, _, _ = g['worldAt'](bone(P, '/Base HumanLPalm'), t, c); Lmk, _, _ = g['worldAt'](bone(P, '/weapon_L_IK_marker'), t, c)
            best = ranked[0]
            J = fingerPaths(P, best[1])
            joints = eulerList([np.array(g['localAt'](J[1 + best[2] * 3 + k], t)[1], float) for k in range(3)])
            results[family] = dict(container=container, part=part.split('/')[-1], clip=clipName, t=round(t, 3),
                                   hand=best[1], finger=best[2], distance=round(best[0], 3),
                                   next=[(s, FINGERS[f], round(dd, 3)) for dd, s, f in ranked[1:4]],
                                   palmOffL=round(float(np.linalg.norm(Lpalm - Lmk)), 3), palmOffR=round(rightPalmOff(g, P, t, {}), 3), joints=joints)
            print(f'{family:14s} {part.split("/")[-1]:24s} {clipName:22s} t={t:.2f}: {best[1]} {FINGERS[best[2]]} {best[0]*100:.1f} cm | next ' +
                  ', '.join(f'{s} {FINGERS[f]} {dd*100:.1f}' for dd, s, f in ranked[1:4]) + f' | palmOff L {results[family]["palmOffL"]*100:.1f} R {results[family]["palmOffR"]*100:.1f}' +
                  f' | onset {onset:.2f} approach ' + ', '.join(f'{s} {FINGERS[f]} {approach[(s,f)]*100:+.1f}' for dd, s, f in ranked[:4]), flush=True)
            results[family]['fingers'] = [dict(hand=s, finger=f, distance=round(dd, 4), approach=round(approach[(s, f)], 4),
                                               joints=eulerList([np.array(g['localAt'](fingerPaths(P, s)[1 + f * 3 + k], t)[1], float) for k in range(3)]))
                                          for dd, s, f in ranked]
        except Exception as e:
            results[family] = dict(note='ERR ' + repr(e)[:120]); print(family, 'ERR', repr(e)[:120], flush=True)
json.dump(results, open('release_audit.json' if not FILTER else 'release_audit_part.json', 'w'), indent=1)

# Who presses it: a finger within NEAR that moves with the release as it goes in (one resting by it doesn't: an AK's trigger finger
# sits 1.6 cm from the paddle the off thumb pushes); else the nearest within REACH (a pistol's thumb, 3-5 cm off the button's bone);
# else nobody listed (the runtime keeps the gun hand's index). The off hand only where its animation is on the gun (palmOffL).
NEAR, REACH, MOVES, LEFT_ON_GUN = 0.03, 0.05, 0.001, 0.05
press = {}
for family, r in sorted(results.items()):
    fingers = [f for f in r.get('fingers', []) if f['hand'] == 'R' or r['palmOffL'] <= LEFT_ON_GUN]
    moving = [f for f in fingers if f['distance'] <= NEAR and f['approach'] >= MOVES]
    reach = [f for f in fingers if f['distance'] <= REACH]
    chosen = min(moving, key=lambda f: f['distance']) if moving else (min(reach, key=lambda f: f['distance']) if reach else None)
    if chosen:
        press[family] = dict(hand='gun' if chosen['hand'] == 'R' else 'off', finger=chosen['finger'], joints=chosen['joints'],
                             distance=chosen['distance'], clip=r['clip'], part=r['part'])
        print(f"{family:14s} -> {press[family]['hand']} {FINGERS[chosen['finger']]} ({chosen['distance']*100:.1f} cm, {'moves with it' if moving else 'nearest'})", flush=True)
    else:
        print(f'{family:14s} -> nobody', flush=True)
if not FILTER:
    json.dump(press, open('release.json', 'w'), indent=1)
print('done', len(results), 'pressed', len(press))
