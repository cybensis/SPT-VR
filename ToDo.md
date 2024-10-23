
- Look into fixing DLSS for VR
- Look at how the raycast works for filing cabinets
- Apply offset rotation setting to left hand as well - Causing issues so im leaving it for now
- Fix inv bug with holding trigger just at the drag item threshold
- Fix issue with haviing steam overlay open when loading into raid hiding inv
- BeforeGBuffer WindowsCull_FPS Camera command buffers rendering window light in one eye
- Weapon mesh highlighting not working with thermals on
- Maybe add a little icon/circle for you're looking at an interactablee so you know where the raycast hits
- Play some raids to see if I can get the squiggly text bug
- Make under barrel grenade launchers work






- Prevent arms from doing the jump and sprint animations - TRIED DUNNO HOW

- Add motion sickness support, that being snap turn with different degrees, and the ability to turn off some of the visual effects and maybe vignetting
- See if you can make it so that when guns bump into walls, instead of automatically moving the gun around, make it smack into stuff and push against them - Turning off the player component stops this from happpening, fika has some observerOverlap property that seems to have something to do with it in the player comp
- Fix bug where eatign when two handed makes the left hand not work
- LEFT HANDED MODE - Invert the weapon to -1,1,1 scale, then for the HumanRibcage->Base HumanL/RCollarbone, invert these with -1,1,1 scale and add 180 degrees to the upper arms for the weapon
- Need to fix tagilla again - SEEMS TO STILL BE WORKING? I tried going in places above and below him with no issue
- Improve the prone head and arms positions
- Action UI not appearing in hideout
- Fix the double vision when you're in pain
- fix FakeCharacterGI reflections
- OKP-7 red dot works perfectly, no parallax, look into it later
- Issues with windows in distance not appearing in one eye
- Add support for "UI Fixes"
- Fix the submenus appearing in your face for a split second on UI Fixes 
- Try and fix rotating your head really quickly where the body kind of spazzes out - MAYBE FIXED?
- Issue with player model spazzing out, I think its caused by the body to head matching code sitting in a sweetspot where it doesn't finish matching and gets stuck - MAYBE NOT OCCURIING ANYMORE
- Inventory item submenu wont open when in the hideout overview inventory screen
- Grenades are just randomly spawning on me, not like thrown grenades but literal items I can just pick up - Only seems to happen if you use grenades 

- Cant drag maps around or select others in the menu
- Issue with underbarrel grenade launchers hijacking the fire mode swithc highlighting
- Fix eating food and med'ing so  you can move your right hand
- Add vibration for shooting
- Scope with red dot on top prioritizes red dot over scope
- Issuee with black line of shadows appearing at a certain distance from the player, mostly visible indoors on interchange. Still there after disabling all camera components, increasing near clip plane increases distance from player the line appears, and I tried deleting the command buffers up to AfterForwardOpaqueBuffer
- Work on matching colliders with physical body more
- Aiming with a scope on lighthouse, probably other maps, makes grass despawn
- UI was bugging out when I received at item from a trader and I think pressed B to go back which overlayed the preset menu on top of other menus and the Escape from tarkov logo on the main menu was black, also going presets then clicking the hideout button, maybe menu button too will keep the presets menu there
- Issue with there being a radius of light around you, then everything beyond that radius has a shadow or is just darker. - Disabling the DistantShadows GBuffer made it go away, so its something to do with near shadows?
- Add an option for using the right joystick for the left handed radial menu
- Fix TOD Scattering only rendering in one eye, probably just a matrix change - I THINK THE ISSUE IS WHEN LOOKING IN CERTAIN DIRECTIONS, THE FOG CAN BE DARKER IN ONE EYE
- Make it so the right hand laser is available all the way up until the round is loading
- Issue on RESERVE in the undergroup area under one of the big barracks some of the walls aren't loading in on that doorway that leads to thee hallway and the stairs up to thee building
- Fix the right arm bend goal after pulling pin
- Make it so some components on our gun dont get lit up by our flashlights
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
- FIKA