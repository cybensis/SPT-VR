
- Look into fixing DLSS for VR
- Look at how the raycast works for filing cabinets
- Try and dial in the two handed weapon handling
- Apply offset rotation setting to left hand as well - Causing issues so im leaving it for now
- Fix inv bug with holding trigger just at the drag item threshold
- Fix issue with haviing steam overlay open when loading into raid hiding inv
- BeforeGBuffer WindowsCull_FPS Camera command buffers rendering window light in one eye
- Make grenades throwable
- Make melee weapons usable
- Check the unheard edition background works
- Weapon mesh highlighting not working with thermals on
- Maybe add a little icon/circle for you're looking at an interactablee so you know where the raycast hits
- Play some raids to see if I can get the squiggly text bug
- Add weight to items by getting the weight of said item and applying a smoothing factor to the hand movement and keeping it always rotated down
- Make under barrel grenade launchers work

- Made some small position adjustments to the hand position when you change the right hand vertical rotation offset slider - FIXED - NOT AFTER CHANGIN CODE


- Pointing left hand down will bug out right hand selection for UI - CANT REPLICATE
- Fix dragging heals onto body not being accurate - CANT REPLICATE
- Prevent arms from doing the jump and sprint animations - TRIED DUNNO HOW
- Red dots don't move around like holos, look into this for holos - WASNT TRUE AND CANT FIGURE OUT HOW TO CENTRE THEM
- My HK is bugging the fuck out when walking on lightouse - Seems to happen with long weapons, maybe specifically weapons where the hands are far apart? - DOESN'T SEEM TO HAPPEN MUCH ANYMORE

- For like one frame when you release two handing the gun appears at a weird angle
- Need to look at the melee weapons and grenades positions
- Undo some of the body/head matching 
- Pull the gun in a bit so its easier to aim it close to your face
- Fix maps not working
- Add support for "UI Fixes"
- Add vibration for shooting
- Killa sledgehammer not connecting
- Fix the submenus appearing in your face for a split second on UI Fixes 
- Try and fix rotating your head really quickly where the body kind of spazzes out
- Issue with player model spazzing out, I think its caused by the body to head matching code sitting in a sweetspot where it doesn't finish matching and gets stuck
- Lower the blur from being shot at
- Inventory item submenu wont open when in the hideout overview inventory screen
- Add physical crouching - make it append to the stick crouching as well
- Grenades are just randomly spawning on me, not like thrown grenades but literal items I can just pick up - Only seems to happen if you use grenades
- Mutant doesn't have highlighting

- Work on matching colliders with physical body more
- People are having trouble with getting the left hand extraction UI to show
- Aiming with a scope on lighthouse, probably other maps, makes grass despawn
- UI was bugging out when I received at item from a trader and I think pressed B to go back which overlayed the preset menu on top of other menus and the Escape from tarkov logo on the main menu was black
- Issue with there being a radius of light around you, then everything beyond that radius has a shadow or is just darker. - Disabling the DistantShadows GBuffer made it go away, so its something to do with near shadows?
- Add an option for using the right joystick for the left handed radial menu
- Fix TOD Scattering only rendering in one eye, probably just a matrix change - I THINK THE ISSUE IS WHEN LOOKING IN CERTAIN DIRECTIONS, THE FOG CAN BE DARKER IN ONE EYE
- Make it so the right hand laser is available all the way up until the round is loading
- Issue on RESERVE in the undergroup area under one of the big barracks some of the walls aren't loading in on that doorway that leads to thee hallway and the stairs up to thee building
- Fix the right arm bend goal after pulling pin
- Make it so some components on our gun dont get lit up by our flashlights
- Hide the back holstered gun
- Add underhand/low grenade throws by holding left grip when throwing
- Maybe lower some of the flashbang effects as well
- Add a motion sickness toggle for a lot of the effects
- Remove all the different UI components that are visible around the character
- Maybe try and fix the  motion blur for thermal and the main menu, and the other visual stuff on the menu
- When loading into reserve or probs other maps too, looking up you can see the underside of the map
- Add removing backpack by holding left grip on shoulder for 1 second
- Add removing rig by holding left grip on chest for 1 second?

Known Compatible Mods List
- Amands Graphics
- Swag + Donuts
- EFTApi
- Waypoints
- Declutterer
- Questing Bots
- BigBrain