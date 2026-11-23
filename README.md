# SPT-VR

**SPT-VR** puts Single Player Tarkov in virtual reality. You play with your hands: you hold the gun, reload it by hand, open doors by their handles, pull grenade pins, take guns off your body, loot with your hands and eat by bringing food to your mouth.

[![SPT-VR gameplay](https://media2.giphy.com/media/v1.Y2lkPTc5MGI3NjExZDQxZHN4NTU4ZTd2bHA4czRmbjlsemNhNjBkNHY4aWoxdTR5aHlsbyZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9Zw/vybnEvzrYKyDTXAITB/giphy.gif)](https://media2.giphy.com/media/v1.Y2lkPTc5MGI3NjExZDQxZHN4NTU4ZTd2bHA4czRmbjlsemNhNjBkNHY4aWoxdTR5aHlsbyZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9Zw/vybnEvzrYKyDTXAITB/giphy.gif)

## Contents

1. [Features](#features)
2. [Requirements](#requirements)
3. [Installing](#installing)
4. [Controls](#controls)
5. [Settings](#settings)
6. [Performance](#performance)
7. [FAQ](#faq)
8. [Building from source](#building-from-source)
9. [Credits and contact](#credits-and-contact)

## Features

- **Reloading by hand** for every kind of gun: magazines, charging handles, bolt-actions, pump and semi-auto shotguns, break-actions, revolvers, belt-fed machine guns and grenade launchers. Jams are cleared by hand too.
- **Guns on your body.** Let go of your gun and it hangs across your chest. Your second gun sits behind your shoulder and your pistol on your hip, ready to grab. You can move each spot by hand.
- **Physical guns.** Your gun bumps into walls instead of passing through them and breaks windows. Weapon weight, aim steadying and shouldering a stocked gun all change how it handles.
- **Grenades** sit on your chest. Pull the ring with your other hand and throw.
- **Doors by hand.** Turn the handle and swing the door, unlock it with your key, or breach it with your gun.
- **Looting with your hands.** Point your palm at items to grab them, put them away over your shoulder, reach into containers and drag bodies.
- **Eating and drinking by hand.** Open the can, bottle or wrapper and bring it to your mouth.
- **Mounted guns** are operated by their handles.
- **Your body follows you.** Your upper body turns with your head, you crouch and lean with your own body, and on Valve Index your fingers are tracked.
- **Either hand can hold a gun.**
- **FIKA co-op.** Other players see your real head, arms and hands (see the [FAQ](#faq)).
- **Performance options** for VR: foveated rendering, scope render resolution, upscaler support and more.

## Requirements

- **SPT 4.1** (built and tested on SPT 4.1.6, Tarkov 0.16.9).
- **A PC VR headset running through SteamVR.** The mod starts VR through OpenXR and falls back to OpenVR on its own if OpenXR can't start.
- **A high-end PC.** This is a very heavy mod and will not run well on mid-range hardware. Recommended minimums:

| | Minimum |
|---|---|
| GPU (Nvidia) | RTX 30 series or newer with 16 GB of VRAM or more |
| GPU (AMD) | RX 7000 series (RDNA 3 / 4) or newer with 16 GB of VRAM or more |
| CPU | An AMD X3D chip (5800X3D, 7800X3D, 9800X3D or similar) |
| RAM | 32 GB with a separate headless FIKA PC, more if you host yourself |
| Drive | SSD, ideally NVMe |

You may get by with a little less, but this is what it takes for a playable experience.

## Installing

1. Make sure SPT 4.1 is installed and up to date.
2. Download the latest SPT-VR release zip.
3. Open the zip and copy its **BepInEx** folder into your SPT folder (the one with `EscapeFromTarkov.exe` and `SPT.Launcher`), merging it with the BepInEx folder already there.
4. **Playing with FIKA:** also copy the **BepInEx** folder from inside `FIKASync (NEEDED ONLY FOR FIKA)` into your SPT folder. Every computer in the group needs it, including a headless client. Players in your group who don't play in VR install only this folder, not step 3's. Without FIKA, leave this folder out.
5. Start SteamVR with your headset connected.
6. Start the game through the SPT Launcher.

Everything installs inside the BepInEx folder. None of the game's own files are replaced. On start, the mod adds a few VR files to `EscapeFromTarkov_Data`, which Unity needs to find there.

**Uninstalling:** delete `BepInEx/plugins/sptvr` and `BepInEx/patchers/SPT-VR-Preloader.dll`. To remove every trace, also delete `UnityOpenXR` and `XRSDKOpenVR` from `EscapeFromTarkov_Data/UnitySubsystems`, and `EscapeFromTarkov_Data/StreamingAssets/SteamVR`.

## Controls

The full controls guide comes with the mod. Open **`BepInEx/plugins/sptvr/SPT-VR-Controls.html`** in your SPT folder with any web browser. It is also in this repository at [Docs/SPT-VR-Controls.html](Docs/SPT-VR-Controls.html): download it and open it in a browser.

It shows what every button does in each situation (on the move, holding a gun, hands free, grenades, menus), the places on your body you reach for, how the hand interactions work (doors, keys, loot, bodies, mounted guns, eating) and the settings that change the controls.

## Settings

In the game's settings there is a **VR** tab. A dropdown at the top picks the category:

- **Movement:** how the stick moves you, smooth or snap turning, swapping the sticks, step-to-sprint, seated mode.
- **Weapons:** weapon weight, aim steadying, shouldering, the chest sling, toggling the gun grip, manual reloading, resetting your gun spots.
- **Hands & Body:** which hand points the laser in menus, hand angles, hiding your arms or legs.
- **Interaction:** physical doors, context menus on doors and bodies, manual eating, the in-game VR keyboard.
- **Graphics:** scope render resolution, shadows, foveated rendering, LOD bias, clutter draw distance, fog.
- **Desktop View:** what the game window on your monitor shows.

Many settings explain themselves when you point the laser at them.

## Performance

What has worked best on an RX 9070 XT, Ryzen 5 7600X, 32 GB of RAM and an NVMe drive:

1. Install [VRAM Cleaner](https://hub.sp-tarkov.com/files/file/2876-vram-cleaner/).
2. In the game's graphics settings, set everything to the lowest or off except textures, shadows, anisotropic filtering and LOD / visibility, which you can set to taste. Anti-aliasing must be off or FXAA, nothing else works. With 10 GB of VRAM or less, set textures to medium or low.
3. Of the checkboxes at the bottom of the graphics settings, turn on grass shadows, high quality color and Streets low quality textures, and leave the rest off (volumetric lighting is your call).
4. **Use an upscaler:** Nvidia DLSS or AMD FSR 3.
5. In SteamVR's settings, set Tarkov's resolution between 100% and 150%. This costs the most performance of anything, so lower is faster, and it works together with the upscaler.
6. Try the options in **VR › Graphics**: foveated rendering, a lower scope render resolution, Disable Hideout Shadows, and less LOD bias and clutter draw distance. **VR › Desktop View › Plain Mirror** saves CPU time too.
7. Run FIKA with a headless client, ideally on a second PC on your network with at least 32 GB of RAM and a decent CPU. Running headless on the same PC only helps with at least 64 GB of RAM.
8. Install a bot spawn mod such as MOAR or ABPS. The AI is the biggest performance cost in SPT, and these mods manage spawns much better.

## FAQ

**Which headsets and controllers are supported?**
Any headset that supports OpenXR and can work with SteamVR. (Let me know if one doesn't work)

**Does it work with FIKA?**
Yes. Install `SPT-VR-FikaSync.dll` on every computer in the group (including headless), VR or not (see [Installing](#installing)). Other players then see your real head, arm and hand movements, and things you do by hand such as reloads and doors.

**Does it work with other mods?**
SPT-VR has built-in support for FIKA, Dynamic Maps, Amands's Graphics, Weapon Customizer, FuckThisCulling, ORBIT and Integrated Configuration Manager. A lot of other mods just work. Mods that add their own new UI often don't, guns from mods may be missing some by-hand features, and some graphics mods cause problems. Add mods one at a time, and remove the rest before reporting a bug.

**VR doesn't start, or starts in the wrong mode.**
Make sure SteamVR is running before you start the game, and check `BepInEx/LogOutput.log` for lines from SPT-VR. If OpenXR can't start, the mod falls back to OpenVR. To always use OpenVR, set `"useOpenXR": false` in `BepInEx/plugins/sptvr/ModSettings.json`.

## Building from source

1. Clone this repository and open `TarkovVR.sln` (Visual Studio or Rider), or build from the command line.
2. The VR assemblies are in the `libs` folder. The game's and the supported mods' are not in the repository, so copy them in from your SPT install. The code reads private members, so publicize them first, for example with [BepInEx.AssemblyPublicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer):
   - into `libs/Managed`, from `EscapeFromTarkov_Data/Managed`: `Assembly-CSharp`, `Assembly-CSharp-firstpass`, `AnimationSystem.Types`, `Cinemachine`, `Comfort`, `Comfort.Unity`, `CommonExtensions`, `ItemComponent.Types`, `Newtonsoft.Json`, `Sirenix.Serialization`, `Unity.Postprocessing.Runtime`, `Unity.TextMeshPro` and `UnityEngine.UI`;
   - into `libs/ModSupport`, from each mod's plugin folder: `Fika.Core`, `AmandsGraphics`, `DynamicMaps`, `KmyTarkovConfiguration`, `ORBIT` (saved as `Orbit.dll`), `FuckThisCulling` and `Tyfon.WeaponCustomizer`.
3. Build with `dotnet build -c Release`. The build lands in `bin/Release/netstandard2.1/SPT-VR.dll`. Copy it over `BepInEx/plugins/sptvr/SPT-VR.dll` in your SPT folder.
4. The other projects: `FikaSync` (the co-op module, `SPT-VR-FikaSync.dll`), `Preloader` (`SPT-VR-Preloader.dll`, goes in `BepInEx/patchers`) and `DevTools` (a developer settings plugin, not shipped to players).

## Credits and contact

SPT-VR was created by **cybensis**. Since 1.0.6 (4/28/2025) it has been developed by **MaTSix**.

Thank you to the testers who helped and provided bug reports and feedback.

**Join the Discord: <https://discord.gg/U8B8h3s6SN>**
