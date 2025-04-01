# **SPT-VR (Single Player Tarkov VR Mod)**

**SPT-VR** brings the immersive, intense experience of Tarkov into the realm of virtual reality. Engage in intense firefights, loot dangerous environments, and survive the unforgiving world of Tarkov—all in full VR.

![Game Preview](./Assets/spt-vr-splash.gif)

---

## **Table of Contents**
1. [Setup](#setup)
2. [Controls](#controls)
3. [VR Settings](#vr-settings) 
4. [FPS/Graphics Guide](#fpsgraphics-guide)
5. [FAQ](#faq)
6. [Contributions](#contributions)
7. [Support the Mod](#support-the-mod)
8. [Development Environment Setup](#development-environment-setup)
9. [Get in contact](#get-in-contact)
---

## **Setup**

### **Prerequisites**
- **Single Player Tarkov (SPT) Installation**: Make sure you have a working copy of SPT installed.
- **SteamVR**: The mod requires SteamVR to support VR hardware.

### **Installation Steps**
1. **Ensure SPT is up to date**: Make sure you have the latest version of SPT installed on your system.
2. **Download the latest release**: On the right side of this page (if on GitHub), download the latest release from the "Releases" section.
3. **Navigate to SPT's root directory**: This should contain the `SPT.Launcher`, `SPT.Server` files, and the `BepInEx` folder.
4. **Extract the downloaded files**: Merge the folders from the release with the folders in the SPT installation.
5. **Launch SteamVR**: Ensure that SteamVR is running and your headset is connected before launching the game.
6. **Launch the game through the SPT Launcher**.

### **Uninstalling**
To uninstall the mod, go to your SPT installation and remove the BepInEx/plugins/sptvr/SPT-VR.dll file, which will stop the game from launching in VR. To reinstall it, simply drag the .dll file back into the directory.

---

## **Controls**

Use LiftIsTheWhey's YouTube video for a visual controls guide - https://www.youtube.com/watch?v=alh42lFp-Gw

### **Movement**
- **Walking:** Left joystick.
- **Look around:** Right joystick.
- **Sprint:** Click the left joystick.
- **Jump:** Push the right joystick up.
- **Vault:** Hold the right joystick up at a ledge.
- **Crouch:** Pull down on the right joystick, physically crouch, or do both.
- **Prone:** Fully crouch, release the joystick, then pull down again.
- **Reset Height and Position:** Hold down the right joystick for about half a second until you see your camera change position

### **Weapon Controls**
- **Shoot:** Right trigger.
- **Two-hand weapon:** Support the gun with the left grip when it vibrates (toggle option available).
- **Aim:** Looking down the sights automatically increases accuracy.
- **Steady aim:** Hold the left trigger to hold your breath.
- **Weapon interaction mode:** Hold the right grip when not aiming to interact with the weapon:
    - **Check Magazine**
    - **Reload**
    - **Inspect Weapon**
    - **Fix Malfunction**
    - **Toggle Tactical Devices**
    - **Change Tactical Device Mode**
    - **Toggle Firemode**
    - **Fold Stock** (not implemented yet)
- **Reload:** Press **B** or use the interaction mode.
- **Toggle Firemode:** Press **A** when two-handing or use the interaction mode.
- **Change red dot/holo mode:** Press the left grip when your hand vibrates near the sight.
- **Change optic zoom:** Pull the right joystick or rotate the left hand near the scope.
- Grenades: Select from the quick slot radial menu, then hold the right trigger to pull the pin and aim using your in-game pointer finger.

### **In-Game Interactions**
- **Swap Weapon:** 
    1. For a pistol, bring your right hand to your hip and press the right grip.
    2. For primary weapons, bring your right hand to your shoulder and press the right grip.
    3. Use the radial menu by holding the right grip at your shoulder.
- **Quick Slot Items:** Open the radial menu by bringing your left hand to your chest and holding the left grip.
- **Interacting with doors/containers/bodies/loose loot/etc:** There are two different ways to interact with the aforementioned objects:
	1. Through a menu by looking at the object, which will bring up the menu where you're looking, then using the right joystick you can navigate it and use the **A** button to select an option, or
	2. You can bring your left hand up to the object and press the left grip to perform the primary operation, or with loose loot you can hold the left grip to pick it up and bringing it over your left shoulder and releasing will place it in your inventory if there is room
- **Toggle head visor/night vision:** These can be toggled by bringing the left hand up to your head and pressing the left grip

### **Menus & Menu Interactions**
- **Select:** To interact with menu items, buttons, etc, press the **A** button while hovering over it with your laser pointer.
- **Open Inventory:** Press **X** while in a raid.
- **Open Menu:** Press **Y** while in a raid.
- **Dragging Items:** Hold the right trigger to move items.
- **Opening Item Sub-Menu:** While hovering over an item, hold down the **A** button and it will bring up a dropdown menu for said item.
- **Opening Item Display Window:** To open the items display window, double tab **A** while hovering over it.
- **Quick Equip:** While holding the left grip, pressing the **A** button on an item will automatically equip it to its respective slot.
- **Quick Transfer Item:** While holding the right grip, pressing the **A** button on an item will automatically transfer it in or out of your inventory.

### **Configuring your experience**
If you go into the Tarkov settings menu, you will see a VR tab, which allows you to modify some of the VR specific settings.

---

## VR Settings
If you look in Tarkovs settings menu you will see a new tab for VR which contains some VR specific configurations you can change to suit your play style, such as:
- Snap turn/Smooth turn settings, as well as degrees for snap turn
- Hands only / No Legs options
- Aim smoothing options
- Rotation offsets for your hands
- Graphics settings
- Weapon handling settings
- And more

---
## FPS/Graphics Guide
NOTE: Keep in mind these settings were based on my personal performance is likely very inaccurate. Additionally, messing around with some settings in raid can cause some bad visual glitches, so be aware of this before reporting any bugs
#### In game graphics guide
- Resolution likely doesn't matter too much as it's always going to render to the quality of your headset or what SteamVR is set to, but it's posssible lowering the resolution to as low as possible may net some frames.
- Anti aliasing should be off or on FXAA - No FPS difference noticed between the two, and other options cause bad visual glitches
- Resampling should be off/1x otherwise it may cause visual glitches
- DLSS and FSR don't work properly so turn them both off
- HBAO - Looks better but takes a massive hit on performance - off gets about around 10-20 fps increase
- SSR - Low drops frames by around 2-5, ultra by about 5ish. I don't personally notice any visual improvements but it seems like if you have it on, you may as well go to ultra
- Anistrophic filtering - No real FPS difference
- Sharpness at 1-1.5 I think any visual gain falls off after around 1.5+
- POST FX - Turning it off gains about 8-10 FPS in some situations, or does nothing in others

#### Additional FPS guide
- Check out the [TrueLive](https://hub.sp-tarkov.com/files/file/2454-truelive/) Donuts+Swag configuration by MaTSix which aims to follow the AI spawns as close to live Tarkov as possible, which helps reduce CPU load throughout raids and can result in a solid FPS gain.
- Thanks to yurinin for finding this: By downloading and setting up [this](https://github.com/RavenSystem/VRPerfKit_RSF) version of the VR performance toolkit, then going into the config file and setting the method to CAS, renderScale to 30 and sharpness to one, then using the SteamVR resolution settings you can scale up your resolution to much higher than normal to achieve a higher fidelity for no loss in FPS (atleast in my case)

## **FAQ**

### What controllers and headsets are supported?
The mod supports most VR headsets. The following controllers work out of the box:
- **Quest 2/3**
- **Valve Index**
- **Vive**

If your controller isn’t working, configure the control scheme through SteamVR bindings.

### Does this work with the non-SPT Escape From Tarkov?
No, using this mod with the official version of Escape From Tarkov can result in a ban.

### Does it support FIKA?
Yes, it supports FIKA. VR players will appear as non-VR players in multiplayer.

### Are other mods compatible with the VR mod?
There are several mods the testers and myself were able to confirm as working, as for other mods, anything to do with adding a new UI is likely to not work, adding new guns might work but will be missing new features, and some graphics mods may also cause issues with the VR mod, so please keep in mind before reporting any bugs, remove all mods that are not listed below.

Here is the current list of known compatible mods:
- FIKA
- Amands Graphics
- Swag + Donuts
- SAIN
- EFTApi
- Waypoints
- Declutterer
- Questing Bots
- BigBrain


### Do I need to buy this mod?
No, the mod is free, and the source code is open source.

---

## **Contributions**

A huge thank you to these primary testers who helped shape the mod:
- **groundzeroday**: Check out his work at [hexler.net](https://hexler.net/)
- **Havviks**: Thanks for making the trailer, go watch his videos on [YouTube](https://www.youtube.com/@HAVVIKS)
- **MaTSix**: Offered a lot of helpful ideas which greatly decreased the jankiness of the mod
- **LiftIsTheWhey**: Thanks for all the videos you've made, the controls guide and helping with testing so checkout [his channel](https://www.youtube.com/@LiftIsTheWhey)


---

## **Get in contact**
For reporting bugs or recommending improvements, go to either the SPT discord or Flat2VR discord servers, if you want to reach out to me personally, you can send me an email at cybensis@protonmail.com

## **Development Environment Setup**

If you wish your own changes to the mod or want to check it out for whatever purpose, follow the below steps:
1. **Clone the Repository**: This can either be done by downloading the source code through the GitHub page or by using the below command
   ```bash
   git clone https://github.com/cybensis/SPT-VR.git
2. **Open it up in your IDE:** I've only ever used Visual Studio for development so I would recommend using that. Opening the .sln file will open up Visual Studio which should already be configured to build without isssue
3. **Make your changes and build:** After making your changes, to test them simply go to the *Build* dropdown menu at the top of Visual Studio and go *Build Solution*
4. **Add the build to your SPT installation:** After building the mod, you should find the new file under *bin/Debug/TarkovVR.dll* or *bin/Release/TarkovVR.dll* depending on whether you've built a debug or release version, then in your SPT installation, replace the *BepInEx/plugins/sptvr/SPT-VR.dll* file with your new one and you should be good to go.
