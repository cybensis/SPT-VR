# One ReloadHolds.json for players with and without a content mod: the game's export as it is, plus what the mod's export has that it
# doesn't (its guns' sets, magazine types and halves the game's lack). A base gun's data stays the game's: under the mod its set can be
# another (the backport's MCX animator lists mcx_ clips first, the game's mcx_4_), and its export fills from there.
# python merge_exports.py <game export> <mod export> <out>
import json, sys

game, mod = json.load(open(sys.argv[1])), json.load(open(sys.argv[2]))
added = []
for key, value in mod.items():
    if key not in game:
        game[key] = value; added.append((key, '*'))
    elif isinstance(value, dict) and isinstance(game[key], dict):
        for name, entry in value.items():
            if name not in game[key]:
                game[key][name] = entry; added.append((key, name))
            elif key == 'grips':
                for half, hold in entry.items():
                    if half not in game[key][name]:
                        game[key][name][half] = hold; added.append((key, f'{name}:{half}'))
    elif isinstance(value, list) and isinstance(game[key], list):
        for name in value:
            if name not in game[key]:
                game[key].append(name); added.append((key, name))
json.dump(game, open(sys.argv[3], 'w'), separators=(',', ':'))
for key in sorted({k for k, _ in added}):
    print(key, sorted(n for k, n in added if k == key))
