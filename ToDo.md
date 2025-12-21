NEW FEATURES
- Added a left hand mode
- Fixed some issues with not being able to join a FIKA server as a client and not being able to leave the server
- Arms no longer perform most of the sprint and jump animation
- Added an extra slider for scope smoothing

THINGS TO DO IN NEW UPDATES
- Might need to change the left hand world interaction laser
- Find a way to disable FSR permanently as it causes errors
- When swapping from left to right hand/vice versa  in the hideout overview it doesnt swap the laser
- Looking up on factory messes with lighting
- Selecting a weapon then putting it away in the hideout makes it so you cant reequip one in left handed mode
- Also need to change the position of the mag ammo count

GETTING WEAPON IN LEFT HANDED MODE TO NOT BE INVERTED - Don't think its possible, the animations just don't work
- Need to look into the visual effects when walking into barbed wire or getting hurt as it completely blacked my screen and caused the highlighting mesh to stick to my screen again, might just be a setting though, if it is make sure it is disabled permanently
- Try setting the scale of the base of the weapon itself to -1,1,1, it flips the hands around but this just might need some rotating around
-	Looked into it and if I set the hand bone scale to -1 -1 1 it orients correctly but is slightly off from the gun, to change its position it would involve changing the right hand IK marker but even updating it in update and lateUpdate doesn't work as it seems the aniimator on the weapon supercedes it no matter what so figure something out with that maybe or just checkout eft.player.method22 for the hand setting position 
	- __instance._markers[0].localPosition += new UnityEngine.Vector3(0.11f,0.03f,0.01f);
	- __instance._markers[0].localEulerAngles += new UnityEngine.Vector3(-30,80,-40);
	- __instance._markers[1].localPosition += new UnityEngine.Vector3(-0.065f,0,0);
- Refactor the blockCrouch and blockJump stuff, shouldn't need them at all, just block joystick when menus are open and in the jump input handler just check for if player is crouched


- The reticle size of the EOTECH/Scope combo changes when you swap between the scope zoom sizes
- Turning around while planting a device or something bugs out
- Fix the VR keyboard not working with open composite
- As a BEAR the watch is a separate mesh from the arms and shows up when using hands only mode
- Remove unused settings
- Use the CC_FastVignette for motiion sickness - Need to fix it so it renders in both eyes
- Disable CC_Wiggle and UnityStandardAssets.ImageEffects.MotionBlur for motion sickness
- See if you can make it so that when guns bump into walls, instead of automatically moving the gun around, make it smack into stuff and push against them - The TurnAwayEffector.Proces() function handles the positioning and rotation for collisions
- Fix vulcan MG 3.5x night vision scope
- There's apparently issues with the lighting getting randomly darker on interchange around the OLI escalaters - Looked into but couldn't replicate anything
- Make it so that the left hand supporting collider is always prioritized over the scope to prevent the hand going to the scope instead of supporting
- reloading PKM bugs out position, 
- Add motion sickness support, that being snap turn with different degrees, and the ability to turn off some of the visual effects and maybe vignetting
- LEFT HANDED MODE - Invert the weapon to -1,1,1 scale, then for the HumanRibcage->Base HumanL/RCollarbone, invert these with -1,1,1 scale and add 180 degrees to the upper arms for the weapon
- Need to fix tagilla again - SEEMS TO STILL BE WORKING? I tried going in places above and below him with no issue
- Action UI not appearing in hideout
- Fix the double vision when you're in pain
- fix FakeCharacterGI reflections
- Issues with windows in distance not appearing in one eye
- Add support for "UI Fixes"
- Fix the submenus appearing in your face for a split second on UI Fixes 
- Inventory item submenu wont open when in the hideout overview inventory screen

- Fix bug where entering hideout then immediately going back hides laser
- Cant drag maps around or select others in the menu
- Issue with underbarrel grenade launchers hijacking the fire mode swithc highlighting
- Fix eating food and med'ing so  you can move your right hand
- Add vibration for shooting
- Issuee with black line of s appearing at a certain distance from the player, mostly visible indoors on interchange. Still there after disabling all camera components, increasing near clip plane increases distance from player the line appears, and I tried deleting the command buffers up to AfterForwardOpaqueBuffer
- Work on matching colliders with physical body more
- UI was bugging out when I received at item from a trader and I think pressed B to go back which overlayed the preset menu on top of other menus and the Escape from tarkov logo on the main menu was black, also going presets then clicking the hideout button, maybe menu button too will keep the presets menu there
- Issue with there being a radius of light around you, then everything beyond that radius has a shadow or is just darker. - Disabling the DistantShadows GBuffer made it go away, so its something to do with near shadows?
- Add an option for using the right joystick for the left handed radial menu
- Make it so the right hand laser is available all the way up until the round is loading
- Make it so some components on our gun dont get lit up by our flashlights
- Maybe lower some of the flashbang effects as well
- Add a motion sickness toggle for a lot of the effects
- Maybe try and fix the  motion blur for thermal and the main menu, and the other visual stuff on the menu
- BeforeGBuffer WindowsCull_FPS Camera command buffers rendering window light in one eye
- Weapon mesh highlighting not working with thermals on
- Maybe add a little icon/circle for you're looking at an interactablee so you know where the raycast hits

Known Compatible Mods List
- Amands Graphics
- Swag + Donuts
- EFTApi
- Waypoints
- Declutterer
- Questing Bots
- BigBrain
- FIKA


