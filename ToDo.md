
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



- Fixed jittering when moving and looking around physically, I'm not noticing much at all
- Added a toggle for hands only mode that removes the arms in the VR settings tab
- Added a toggle to hide the legs in the VR settings tab


- Improved VR settings menu layout
- Added snap turn option
- Adding graphics setting for different distant shadow methods
- Fixed issue with walls being see through and models not appearing at a distance, most noticeable indoors on interchange, looks better but tanks performance a bit
- Fixed bug with swapping weapons causing the camera to be stuck in place for a brief moment

- Fixed hide legs setting causing errors
- Fixed issue with laser pointer not appearing after coming out of hideout
- Possibly actually really fixed jittering this time
- Fixed issue with hands jittering when no weapon is equipped or left hand jittering when not attached to gun
- Fixed medding/eating disconnecting camera from body

- Improved jittering
- Hands no longer shift around when entering two handed mode
- Improved right hand positioning for all weapons
- Fixed the issue with medding/eating/etc causing the gun to point down

- Prevent arms from doing the jump and sprint animations - TRIED DUNNO HOW

- Fix the right hand laser position already
- https://escapefromtarkov.fandom.com/wiki/KMZ_1P59_3-10x_riflescope this scope also not working apparently
- Fix the VR keyboard not working with open composite
- As a BEAR the watch is a separate mesh from the arms and shows up when using hands only mode
- Remove unused settings
- Use the CC_FastVignette for motiion sickness - Need to fix it so it renders in both eyes
- Disable CC_Wiggle and UnityStandardAssets.ImageEffects.MotionBlur for motion sickness
- See if you can make it so that when guns bump into walls, instead of automatically moving the gun around, make it smack into stuff and push against them - The TurnAwayEffector.Proces() function handles the positioning and rotation for collisions
- Fix vulcan MG 3.5x night vision scope
- Fix the inventory menu not appearing in front of the player when moving around playspace
- Fix the issue with the left hand radial menu not blocking walking
- There's apparently issues with the lighting getting randomly darker on interchange around the OLI escalaters - Looked into but couldn't replicate anything
- Make it so that the left hand supporting collider is always prioritized over the scope to prevent the hand going to the scope instead of supporting
- Can't change firemode on AUG
- reloading PKM bugs out position, 
- Add motion sickness support, that being snap turn with different degrees, and the ability to turn off some of the visual effects and maybe vignetting
- LEFT HANDED MODE - Invert the weapon to -1,1,1 scale, then for the HumanRibcage->Base HumanL/RCollarbone, invert these with -1,1,1 scale and add 180 degrees to the upper arms for the weapon
- Need to fix tagilla again - SEEMS TO STILL BE WORKING? I tried going in places above and below him with no issue
- Improve the prone head and arms positions
- Action UI not appearing in hideout
- Fix the double vision when you're in pain
- fix FakeCharacterGI reflections
- Issues with windows in distance not appearing in one eye
- Add support for "UI Fixes"
- Fix the submenus appearing in your face for a split second on UI Fixes 
- Try and fix rotating your head really quickly where the body kind of spazzes out - MAYBE FIXED?
- Issue with player model spazzing out, I think its caused by the body to head matching code sitting in a sweetspot where it doesn't finish matching and gets stuck - MAYBE NOT OCCURIING ANYMORE
- Inventory item submenu wont open when in the hideout overview inventory screen
- Grenades are just randomly spawning on me, not like thrown grenades but literal items I can just pick up - Only seems to happen if you use grenades 

- Fix bug where entering hideout then immediately going back hides laser
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