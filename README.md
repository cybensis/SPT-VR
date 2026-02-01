 SPT-VR (Single Player Tarkov VR Mod) 
======================================

**SPT-VR** brings the immersive, intense experience of Tarkov into the realm of virtual reality. Engage in intense firefights, loot dangerous environments, and survive the unforgiving world of Tarkov—all in full VR.

[![giphy.gif](https://media2.giphy.com/media/v1.Y2lkPTc5MGI3NjExZDQxZHN4NTU4ZTd2bHA4czRmbjlsemNhNjBkNHY4aWoxdTR5aHlsbyZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9Zw/vybnEvzrYKyDTXAITB/giphy.gif)](https://media2.giphy.com/media/v1.Y2lkPTc5MGI3NjExZDQxZHN4NTU4ZTd2bHA4czRmbjlsemNhNjBkNHY4aWoxdTR5aHlsbyZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9Zw/vybnEvzrYKyDTXAITB/giphy.gif)

 Table of Contents 
-------------------

1. [Setup](#setup)
2. [Controls](#controls)
3. [FPS/Graphics Guide](#fpsgraphics-guide)
4. [FAQ](#faq)
5. [Contributions](#contributions)
6. [Development Environment Setup](#development-environment-setup)
7. [Get in contact](#get-in-contact)
 
 Setup 
-------

###  Prerequisites 

- **Single Player Tarkov (SPT) Installation**: Make sure you have a working copy of SPT installed.
- **SteamVR**: The mod requires SteamVR to support VR hardware.
 
###  Installation Steps 

1. **Ensure SPT is up to date**: Make sure you have the latest version of SPT installed on your system.
2. **Download the latest release**: On the right side of this page (if on GitHub), download the latest release from the "Releases" section.
3. **Navigate to SPT's root directory**: This should contain the SPT.Launcher, SPT.Server files, and the BepInEx folder.
4. **Extract the downloaded files**: Merge the folders from the release with the folders in the SPT installation.
5. **Launch SteamVR**: Ensure that SteamVR is running and your headset is connected before launching the game.
6. **Launch the game through the SPT Launcher**.
 
###  Uninstalling 

To uninstall the mod, go to your SPT installation and remove the BepInEx/plugins/sptvr/SPT-VR.dll file, which will stop the game from launching in VR. To reinstall it, simply drag the .dll file back into the directory.

 Controls 
----------

###  Movement 

- **Walking:** Left joystick.
- **Look around:** Right joystick.
- **Sprint:** Click the right joystick.
- **Jump:** Push the right joystick up.
- **Vault:** Hold the right joystick up at a ledge.
- **Crouch:** Pull down on the right joystick, physically crouch, or do both.
- **Prone:** Fully crouch, release the joystick, then pull down again.
 
###  Weapon Controls 

- **Fire:** Right trigger.
- **Two-hand weapon:** Support the gun with the left grip when it vibrates (toggle option available).
- **Aim:** Looking down the sights automatically increases accuracy.
- **Steady aim:** Hold the left trigger to hold your breath.
- **Weapon interaction mode:** Hold the right grip when not aiming to interact with the weapon: 
    - Check Magazine
    - Reload
    - Inspect Weapon
    - Fix Malfunction
    - Toggle Firemode
    - Fold Stock (not implemented yet)
- **Reload:** Press **B** or use the interaction mode.
- **Toggle Firemode:** Press **A** or use the interaction mode when two-handing.
- **Change red dot/holo mode:** Press the left grip when your hand vibrates near the sight.
- **Change optic zoom:** Pull the right joystick or rotate the left hand near the scope.
- **Grenades:** Select from the quick slot radial menu, then hold the right trigger to pull the pin, trigger will act as grip, do throwing motion and let go of trigger to throw. Press left trigger while pin is pulled to reset pin and cancel throw
- **Toggle Tactical Device:** While holding secondary grip, press B on secondary controller, long press B to toggle other modes
- **Under Barrel Grenade Launcher:** While holding gun with other hand, hold secondary controller trigger and press A
 
###  In-Game Interactions 

- **Swap Weapon:**
    1. For a pistol, bring your right hand to your hip and press the right grip.
    2. For primary weapons, bring your right hand to your shoulder and press the right grip.
    3. Use the radial menu by holding the right grip at your shoulder.
- **Quick Slot Items:** Open the radial menu by putting your hand in backpack (over non-dominant shoulder) and press and hold grip, bring into view while keeping grip held, select item with thumbstick and let go of grip
- **Interacting with doors/containers/bodies/loose loot/etc:**
    1. Through a menu by looking at the object, which will bring up the menu where you're looking, then using the right joystick you can navigate it and use the **A** button to select an option.
    2. Bring your left hand up to the object and press the left grip to perform the primary operation, or with loose loot you can hold the left grip to pick it up and bring it over your left shoulder and release to place it in your inventory if there is room.
- **Toggle head visor/night vision:** These can be toggled by bringing the left hand up to your head and pressing the left grip.
- **Toggle Head light:** Left hand by head and press trigger instead of grip
- **Drop Backpack:** Put left hand in backpack collider (over non-dominant shoulder) and press trigger
 
###  Menus & Menu Interactions 

- **Select:** To interact with menu items, buttons, etc, press the **A** button while hovering over it with your laser pointer.
- **Open Inventory:** Press **X** while in a raid.
- **Open Menu:** Press **Y** while in a raid.
- **Dragging Items:** Hold the right trigger to move items.
- **Opening Item Sub-Menu:** While hovering over an item, hold down the **A** button to bring up a dropdown menu for that item.
- **Opening Item Display Window:** Double tap **A** while hovering over an item to open the item display window.
- **Quick Equip:** While holding the left grip, pressing the **A** button on an item will automatically equip it to its respective slot.
- **Quick Transfer Item:** While holding the right grip, pressing the **A** button on an item will automatically transfer it in or out of your inventory.
 
###  Configuring your experience 

If you go into the Tarkov settings menu, you will see a VR tab, which allows you to modify some of the VR-specific settings.

Performance Guide!

For reference my specs -

AMD 9070 XT

32GB of ram

Ryzen 5 7600x

NVME SSD

1. Install VRAMCleaner - [VRAM Cleaner](https://hub.sp-tarkov.com/files/file/2876-vram-cleaner/)
2. Set all graphics settings to lowest/off except textures, shadows, anisotropic, and LOD/visibility (set those to whatever you want) AA should always be set to off or FXAA, nothing else works (set texture to medium or low if you have 10GB VRAM or less)
3. Check boxes on graphics settings at bottom, Grass shadows, high quality color, and streets low quality textures. Everything else unchecked, volumetric lighting is your preference. Another checkbox I suggest using if you are 10GB or less VRAM is Mip Streaming. This free's up A LOT of VRAM and can help a lot with performance on lower VRAM cards
4. ***Use an upscaler - Nvidia DLSS - AMD FSR3***
5. In SteamVR settings, go into your tarkov video settings and set the resolution in there to 100-150%. This is one of the things that hurts performance the most, the lower the better. You need a lot of VRAM if you want to run this at a high resolution (keep in mind if you're using an upscaler, this setting plays together with that)
6. Learn how to use a headless Fika client, I don't recommend running headless through your own PC but it can help performance, just make sure you have AT LEAST 64GB of ram. In reality, you should run a headless client on a separate PC on your network that has at least 32GB of ram and a decent CPU
7. Install a bot spawner mod such as MOAR or ABPS, these are also big helps to performance because the main killer of performance in SPT is the AI. These mods are much better at managing spawns.
8. Last thing I'd suggest which may not apply for many people, I disable my 4k monitor whenever I play which seems to help a bit.

**IMPORTANT**: This mod is a *very* heavy mod, it will not run on a low-end or mid-grade PC. This requires a very high-end PC. 

Minimum GPU Nvidia - Any 30 series+ with 16GB+ of VRAM 

Minimum GPU AMD - Any RX 7000+ RDNA3/4 with 16GB+ of VRAM.

Minimum CPU - Really I can't recommend anything other than the AMD X3D chips like the 5800x3d, 9800x3d, etc

Minimum RAM: 32GB if you have a secondary headless PC, if you're self hosting you should really have more than 32GB

Minimum drive - SSD or NVMe SSD, preferably NVMe but this isn't a big deal


You may be able to get away with slightly lower specs but this is what I recommend to have an actual playable experience.

 FAQ 
-----

###  What controllers and headsets are supported? 

The mod supports most VR headsets. The following controllers work out of the box:

- Quest 2/3
- Valve Index
- Vive
 
If your controller isn’t working, configure the control scheme through SteamVR bindings.

###  Does this work with the non-SPT Escape From Tarkov? 

No, using this mod with the official version of Escape From Tarkov can result in a ban.

###  Does it support FIKA? 

Yes, it supports FIKA. VR players will appear as non-VR players in multiplayer.

###  Are other mods compatible with the VR mod? 

Several mods have been confirmed to work. However, mods that add a new UI may not work, new guns might be missing features, and some graphics mods may cause issues. Please remove all incompatible mods before reporting any bugs.

Current list of known compatible mods:

- FIKA
- Hollywood Graphics/FX
- SAIN
- Waypoints
- Declutterer
- BigBrain
- Epic's AIO modded scopes (still needs some work)
- A lot of others, my best suggestion is to try one at a time, most UI mods that *add* totally new UI elements will not work properly
 
###  Do I need to buy this mod? 

No, the mod is free, and the source code is open-source.

 Contributions 
---------------

A huge thank you to these primary testers who helped shape the mod:

- **groundzeroday**: Check out his work at [hexler.net](https://hexler.net/)
- **Havviks**: Thanks for making the trailer, go watch his videos on [YouTube](https://www.youtube.com/@HAVVIKS)
- **MaTSix**: Offered a lot of helpful ideas which greatly decreased the jankiness of the mod

 Get in contact 
----------------

**Join the discord - <https://discord.gg/U8B8h3s6SN>**

If you want to reach out to me personally, you can send an email to <a>cybensis@protonmail.com</a>

 Development Environment Setup 
-------------------------------

If you wish to make your own changes to the mod or want to check it out for any purpose, follow the steps below:

1. **Clone the Repository**: This can either be done by downloading the source code from the GitHub page or using the command below: git clone https://github.com/cybensis/SPT-VR.git
2. **Open it in your IDE**: I've only ever used Visual Studio for development, so I would recommend using that. Opening the .sln file will open Visual Studio, which should already be configured to build without issue.
3. **Make your changes and build**: After making your changes, to test them simply go to the *Build* dropdown menu at the top of Visual Studio and select *Build Solution*.
4. **Add the build to your SPT installation**: After building the mod, you should find the new file under *bin/Debug/TarkovVR.dll* or *bin/Release/TarkovVR.dll* depending on whether you've built a debug or release version. Then, in your SPT installation, replace the BepInEx/plugins/sptvr/SPT-VR.dll file with your new one and you should be good to go.