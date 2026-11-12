import os

# The game's bundles (StreamingAssets/Windows/) and item database. SPTVR_BUNDLES / SPTVR_ITEMS point a run at another set: a content mod's
# bundles laid over the game's, its items merged in (build_overlay.py).
BUNDLES = os.environ.get('SPTVR_BUNDLES', 'F:/SPT4.1/EscapeFromTarkov_Data/StreamingAssets/Windows/')
ITEMS = os.environ.get('SPTVR_ITEMS', 'F:/SPT4.1/SPT_Runtime/SPT_Data/database/templates/items.json')
WEAPONS = BUNDLES + 'assets/content/weapons/'
# Also read the clips guns carry in their own containers: a content mod's guns' all of them; the game's a compass set, and the RSASS
# its drum's (rsass_mag_in_5). Reading every container is slow.
CONTAINER_CLIPS = os.environ.get('SPTVR_CONTAINER_CLIPS') == '1'
