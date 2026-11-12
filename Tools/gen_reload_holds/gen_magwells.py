# How deep each set's magwell is, from its own mag-in clips (Tools/gen_reload_holds; run before gen_export, which reads magwells.json as
# magwellDepths): the clip brings the magazine in turned and off to the side, lines it up and pushes it straight up the well; where that
# last straight push starts, below the seat, is the magwell's mouth (SR-2M 12.6 cm, Uzi 12.3, MP9 11.5, M4 6.9). The deepest over a set's
# clips. A clip that starts with the magazine nearly in says nothing deep (0): the runtime keeps its own track there. Not pistols (their
# own catch reach) - the runtime ignores it on rock-in guns too.
import sys, os, json
exec(open('gen_holds.py', encoding='utf-8').read().split("if __name__=='__main__':")[0])
OFF_AXIS = 0.006       # m off the well's axis still counted as the straight push
OFF_TURN = 6.0         # deg off the seated turn
census = json.load(open('clip_census.json'))
pistolFolders = {gun['folder'] for gun in json.load(open('guns.json')) if gun['cls'] == 'pistol'}
FILTER = set(sys.argv[1:])
results = {}
for folder in sorted(census):
    folderPath = ROOT + folder + '/'
    if not os.path.isdir(folderPath) or folder in pistolFolders or (FILTER and folder not in FILTER):
        continue
    try:
        clips = folderClips(folder)
    except Exception:
        continue
    prefixes = sorted({re.match(r'(.+?)_mag_in(?:_\d+)?$', n).group(1) for n in clips if re.match(r'(.+?)_mag_in(?:_\d+)?$', n)})
    if not prefixes:
        continue
    trees = [loadTree(folderPath + c) for c in sorted(os.listdir(folderPath)) if c.endswith('_container.bundle') and c.startswith('weapon_')]
    trees = [tree for tree in trees if bone(tree[0], '/mod_magazine')]
    for prefix in prefixes:
        names = sorted(n for n in clips if re.match(re.escape(prefix) + r'_mag_in(_\d+)?$', n))
        bindings = [b.path for b in clips[names[0]].m_ClipBindingConstant.genericBindings]
        if not trees:
            continue
        tree = max(trees, key=lambda t: sum(h in t[2] for h in bindings))
        magazineBone = bone(tree[0], '/mod_magazine')
        deepest = (0.0, None)
        for name in names:
            try:
                rows = magazinePath(setup(tree, clips[name]), magazineBone)
            except Exception:
                continue
            seat = rows[-1]
            outOfWell = qmul(qmul(seat['rot'], np.array([0, 0, -1.0, 0])), conj(seat['rot']))[:3]
            depth = 0.0
            for row in reversed(rows):
                offset = row['pos'] - seat['pos']
                along = float(np.dot(offset, outOfWell))
                if float(np.linalg.norm(offset - outOfWell * along)) > OFF_AXIS or ang(row['rot'], seat['rot']) > OFF_TURN:
                    break
                depth = max(depth, along)
            if depth > deepest[0]:
                deepest = (depth, name)
        results[prefix.lower()] = round(deepest[0], 4)
        print(f'{prefix.lower():14s} {deepest[0]*100:5.1f} cm ({deepest[1]})', flush=True)
json.dump(results, open('magwells.json' if not FILTER else 'magwells_part.json', 'w'), indent=1)
print('done', len(results))
