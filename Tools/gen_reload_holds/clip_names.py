import re

# A magazine clip's name the usual way (gun_mag_in_0): the Five-seveN spells them fiveseven_magin_0 / _magout_0, and the SR-2M
# sr2m_reload_in_0 / _out_0 - only the SR-2M: other guns' reload_in clips are grenade launchers, shells, or beside their own mag_in.
# Every generator step that reads clip names goes through this.
RELOAD_NAMED_MAGAZINE_CLIPS=('sr2m',)
def magClipName(folder,name):
    m=re.match(r'(.+?)_mag(in|out)(_\d+)?$',name)
    if m: return f'{m.group(1)}_mag_{m.group(2)}{m.group(3) or ""}'
    m=re.match(r'(.+?)_reload_(in|out)(_\d+)?$',name)
    if m and folder in RELOAD_NAMED_MAGAZINE_CLIPS: return f'{m.group(1)}_mag_{m.group(2)}{m.group(3) or ""}'
    return name
