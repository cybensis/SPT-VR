import re

# A magazine clip's name the usual way (gun_mag_in_0, the number = the magazine's magAnimationIndex). Every generator step that reads
# clip names goes through magClipName / renameMagClips; a renamed clip takes the place of one already called that.
#  - fiveseven_magin_0 / _magout_0: the Five-seveN's spelling
#  - gun_reload_in_0 / _out_0: the magazine clips of the guns listed. The MPX's own mpx_mag_in_1..4 never move the magazine (the game
#    plays its reload ones). Other guns' reload_in clips are grenade launchers, shells, or beside their own mag_in.
#  - named for the magazine: the G36's own (no suffix) is its type 7, its STANAG ones by size; the PP-19-01's drum is its type 1
#  - a content mod's re-exported clips: wrapped as <root>_<root>_<name>_<layer> (the backport's Armature_Armature_m4a1_mag_in_0_1_Base
#    Layer), a type's clip with a take number after it (m4a1_mag_in_0_1)
RELOAD_NAMED_MAGAZINE_CLIPS=('sr2m','scar_x17','mpx','ak308')
NAMED_MAGAZINE_TYPES={'g36':{'':7,'stanag30':0,'stanag20':2,'stanag40':3,'stanag60wide':4,'stanag60drum':5,'stanag10':6},
                      'pp-19-01':{'':0,'drum':1}}
def unwrapClipName(name):
    m=re.match(r'^([A-Za-z0-9]+)_\1_(.+)_[A-Za-z ]+ Layer$',name)
    return m.group(2) if m else name

def magClipName(folder,name):
    name=unwrapClipName(name)
    m=re.match(r'(.+?)_mag_(in|out)_(\d+)_\d+$',name)
    if m: return f'{m.group(1)}_mag_{m.group(2)}_{m.group(3)}'
    m=re.match(r'(.+?)_mag(in|out)(_\d+)?$',name)
    if m: return f'{m.group(1)}_mag_{m.group(2)}{m.group(3) or ""}'
    m=re.match(r'(.+?)_reload_(in|out)(_\d+)?$',name)
    if m and m.group(1) in RELOAD_NAMED_MAGAZINE_CLIPS: return f'{m.group(1)}_mag_{m.group(2)}{m.group(3) or ""}'
    m=re.match(r'(.+?)_mag_(in|out)(?:_([a-z]\w*))?$',name)
    if m and m.group(1) in NAMED_MAGAZINE_TYPES and (m.group(3) or '') in NAMED_MAGAZINE_TYPES[m.group(1)]:
        return f'{m.group(1)}_mag_{m.group(2)}_{NAMED_MAGAZINE_TYPES[m.group(1)][m.group(3) or ""]}'
    return name

def renameMagClips(folder,clips):
    for name in list(clips):
        renamed=magClipName(folder,name)
        if renamed!=name: clips[renamed]=clips.pop(name)
