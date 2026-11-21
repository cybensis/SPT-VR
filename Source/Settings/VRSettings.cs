using EFT;
using EFT.CameraControl;
using EFT.UI;
using EFT.UI.Settings;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using TarkovVR.Patches.Upscalers;
using TarkovVR.Patches.Visuals;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using UnityEngine.UI;

namespace TarkovVR.Source.Settings
{
    public static class VRSettings
    {
        public enum MovementMode
        {
            HeadBased = 0,
            WandBased = 1,
            JoyStickOnly = 2,
        }

        public enum SnapTurnAmount
        {
            thirty = 30,
            fourtyFive = 45,
            ninety = 90,
        }

        public enum RotationMode 
        { 
            Smooth = 0,
            Snap = 1
        }

        public enum ShadowOpt
        {
            Normal = 0,
            DisableNearShadows = 1,
            IncreaseLighting = 2
        }

        public class ModSettings
        {
            public float rotationSensitivity { get; set; }
            public float leftStickDriftSensitivity { get; set; }
            public float rightStickDriftSensitivity { get; set; }
            public MovementMode movementType { get; set; }
            public RotationMode rotationType { get; set; }
            public SnapTurnAmount snapTurnAmount { get; set; }
            public bool weaponAimSmoothing { get; set; }
            public bool leftHandedMode { get; set; }
            public bool swapSticks { get; set; }
            public bool seatedMode { get; set; }
            public float smoothingSensitivity { get; set; }
            public float scopeSmoothingSensitivity { get; set; }
            public float variableZoomSensitivity { get; set; }
            public bool scopeAimSmoothing { get; set; }
            public float aimSteadyStrength { get; set; }
            public bool enableSharpen { get; set; }
            public float rightHandVerticalAngle { get; set; }
            public float rightHandHorizontalAngle { get; set; }
            public float leftHandHorizontalAngle { get; set; }
            public float leftHandVerticalAngle { get; set; }
            public float handPosOffset { get; set; }
            public bool weaponWeight { get; set; }
            public bool weaponInertia { get; set; }
            public bool walkEffector { get; set; }
            public bool hideArms { get; set; }
            public bool hideLegs { get; set; }
            public bool desktopViewRightEye { get; set; }
            public bool desktopViewPlainMirror { get; set; }
            public bool hideoutShadowsOff { get; set; }
            public float desktopViewFieldOfView { get; set; }
            public float desktopViewSmoothing { get; set; }
            public float desktopViewVerticalFieldOfView { get; set; }
            public float desktopViewTiltDown { get; set; }
            public bool manualEating { get; set; }
            public bool disableMouseInput { get; set; }
            public bool disableCorpseContextMenu { get; set; }
            public bool disableRunAnimation { get; set; }
            public bool stepToSprint { get; set; }
            public bool disablePrismEffects { get; set; }
            public bool disablePrismFog { get; set; }
            public ShadowOpt shadowOpt { get; set; }
            public bool nativeAA { get; set; }
            public bool disableOccCulling { get; set; }
            public bool disableFrusCulling { get; set; }
            public bool useVRKeyboard { get; set; }
            public bool heldItemWeight { get; set; }
            public int opticRenderResolution { get; set; }
            public float lodBias { get; set; }
            public float cullDistance { get; set; }
            // true = start with OpenXR (default), false = the OpenVR / SteamVR plugin. No menu setting yet.
            public bool useOpenXR { get; set; }
            public bool virtualStock { get; set; }
            public bool chestSling { get; set; }
            // Guns only (the gun's grip, the foregrip, a mounted gun): a grip press takes hold, the next press lets go
            public bool gunGripToggle { get; set; }
            // Doors by hand (PhysicalDoors): turn the handle, swing the door. Off = a grip opens / closes the door.
            public bool physicalDoors { get; set; }
            // Hides the gaze menu (open / breach / unlock...) for doors moved by hand (PhysicalDoors.HidesMenu)
            public bool disableDoorContextMenu { get; set; }
            // Gun body spots placed by hand (GunSpots), per slot: the grip point x, y, z and the Weapon_root rotation x, y, z, w in the
            // body's frame. null = the default spot. Plain floats, because Newtonsoft can't write a Vector3 (its normalized property loops).
            public float[] gunSpotChest { get; set; }
            public float[] gunSpotBack { get; set; }
            public float[] gunSpotHip { get; set; }
            // The grenade slot placed by hand (GrenadeSlot): the grenade's middle x, y, z relative to the neck and its body's rotation
            // x, y, z, w, in the body frame
            public float[] grenadeSlotMiddle { get; set; }
            // Older saved format: the grenade model root's pose (~40 cm from the grenade). Converted to grenadeSlotMiddle once, then cleared.
            public float[] grenadeSlot { get; set; }
            public float armLength { get; set; }    // m, shoulder joint to wrist; 0 = never measured
            // On guns with detachable magazines B doesn't run Tarkov's reload: a press drops the magazine, held = the off hand can pull it out
            public bool manualReload { get; set; }

            // Vive Wand controller settings
            public float viveWandCrouchTrackpadThreshold { get; set; }
            public float viveWandVaultHoldTime { get; set; }
            
            public ModSettings()
            {
                rotationSensitivity = 4;
                leftStickDriftSensitivity = 0.1f;
                rightStickDriftSensitivity = 0.1f;
                movementType = MovementMode.HeadBased;
                rotationType = RotationMode.Smooth;
                snapTurnAmount = SnapTurnAmount.fourtyFive;
                weaponAimSmoothing = false;
                scopeAimSmoothing = true;
                seatedMode = false;
                leftHandedMode = false;
                swapSticks = false;
                smoothingSensitivity = 1;
                variableZoomSensitivity = 0.5f;
                scopeSmoothingSensitivity = 5;
                aimSteadyStrength = 1f;
                rightHandVerticalAngle = 50;
                rightHandHorizontalAngle = 20; 
                leftHandHorizontalAngle = 50;
                leftHandVerticalAngle = 50;
                handPosOffset = 0.0f;

                enableSharpen = true;
                weaponWeight = false;
                weaponInertia = false;
                walkEffector = false;
                hideArms = false;
                hideLegs = false;
                desktopViewRightEye = false;
                desktopViewPlainMirror = false;
                hideoutShadowsOff = false;
                desktopViewFieldOfView = 80f;
                desktopViewSmoothing = 0.15f;
                desktopViewVerticalFieldOfView = 0f;
                desktopViewTiltDown = 0f;
                manualEating = true;
                disableMouseInput = true;
                disableCorpseContextMenu = true;
                disableRunAnimation = true;
                stepToSprint = false;
                disablePrismEffects = false;
                disablePrismFog = true;               
                shadowOpt = ShadowOpt.IncreaseLighting;
                disableOccCulling = false;
                disableFrusCulling = false;
                lodBias = 1.0f;
                cullDistance = 1.0f;
                useOpenXR = true;
                useVRKeyboard = false;
                heldItemWeight = false;
                virtualStock = true;
                chestSling = true;
                gunGripToggle = false;
                physicalDoors = true;
                disableDoorContextMenu = false;
                manualReload = false;
                opticRenderResolution = 1024;
                viveWandCrouchTrackpadThreshold = 0.7f;
                viveWandVaultHoldTime = 0.3f;
            }
        }

        private static SettingsScreen settingsUi;
        public static GameObject vrSettingsObject;

        // The menu rows read back after they're built (the rest only need building: AddToggle / AddDropDown / AddSlider)
        private static SettingDropDown movementMethod;
        private static SettingDropDown rotationMethod;
        private static SettingDropDown snapTurnAmountDropDown;
        private static SettingToggle resetGunSpotsToggle;
        private static SettingDropDown shadowOptsToggle;
        private static SettingDropDown opticResolutionDropDown;
        private static SettingToggle foveatedToggle;

        private static ModSettings settings;
        private static readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\ModSettings.json");
        public static ModSettings LoadSettings()
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonConvert.DeserializeObject<ModSettings>(json);
            }
            else
            {
                return new ModSettings(); // Return default settings if file doesn't exist
            }
        }

        public static void SaveSettings()
        {
            var json = JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(SettingsFilePath, json);
        }
        static VRSettings()
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                settings = JsonConvert.DeserializeObject<ModSettings>(json);
            }
            else
            {
                settings = new ModSettings(); // Default settings if the file doesn't exist
            }
        }

        // Linearly remaps a value from one range to another. The sliders use it to display a friendly number
        // (e.g. 0..10 or -1..1); the stored setting keeps its real units.
        private static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
        {
            if (Mathf.Approximately(fromMax, fromMin))
                return toMin;
            float t = Mathf.Clamp01((value - fromMin) / (fromMax - fromMin));
            return Mathf.Lerp(toMin, toMax, t);
        }

        // Turns one of EFT's discrete SelectSlider controls into a continuous slider over [min,max]. SelectSlider.Awake()
        // forces wholeNumbers = true, an int-flooring listener and one notch per value, and our panels are cloned from an
        // inactive tab, so that Awake runs when the tab is first shown, after this method. The SmoothSlider component
        // therefore re-applies the continuous config in OnEnable (after Awake, every time the panel is shown).
        // Call after BindIndexTo (which builds and shows the control).
        private static void MakeSliderSmooth(SettingSelectSlider control, float min, float max,
                                             Func<float> getValue, Action<float> onChange, Func<float, string> format)
        {
            if (control == null)
                return;
            SelectSlider sel = control.Slider;
            UnityEngine.UI.Slider ui = (sel != null) ? sel._slider : null;
            if (ui == null)
            {
                Plugin.MyLog.LogWarning("MakeSliderSmooth: slider not found on control, leaving it discrete.");
                return;
            }

            // BindIndexTo bound this slider to the real OverallVolume (master volume) GameSetting: it sets
            // SelectSlider.action_0 = (index => OverallVolume.Value = index), applied to the audio on save. That binding must be
            // cut, or saving pushes our slider value into the master volume and mutes the game. SmoothSlider writes the actual
            // setting through the Unity slider's own onValueChanged.
            sel._onValueChanged = null;

            SmoothSlider cfg = sel.gameObject.GetComponent<SmoothSlider>();
            if (cfg == null)
                cfg = sel.gameObject.AddComponent<SmoothSlider>();
            cfg.slider = ui;
            cfg.notchContainer = sel._notchContainer;
            cfg.valueText = sel._valueText;
            cfg.min = min;
            cfg.max = max;
            cfg.getValue = getValue;
            cfg.onChange = onChange;
            cfg.format = format;
            // Apply now only if the panel is already active (SelectSlider.Awake has run). If it was cloned inactive (the usual case),
            // don't: our onChange listener must not be attached when the deferred Awake rounds the handle on first show.
            // OnEnable does the full setup (and reseeds from getValue) once the panel appears.
            if (sel.gameObject.activeInHierarchy)
                cfg.Apply();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The "VR" tab: one panel, a category picker on top and that category's rows under it (the other categories' rows hidden). A row
        // that only matters while another option is on (Row.shownWhen) is hidden while that option is off; RefreshRows re-checks after every change.

        private enum Category { Movement, Weapons, HandsAndBody, Interaction, Graphics, DesktopView }
        private static readonly string[] CATEGORY_NAMES = { "Movement", "Weapons", "Hands & Body", "Interaction", "Graphics", "Desktop View" };
        private const int EVERY_CATEGORY = -1;
        // This mod adds its own settings tab to this screen (a registered group shown by SettingsScreen.ShowScreen, which hides ours)
        private const string INTEGRATED_CONFIGURATION_MANAGER_ID = "com.sage.integratedconfigurationmanager";

        private static readonly string[] MOVEMENT_MODES = { "Head-based", "Controller-based", "Joystick only" };
        private static readonly string[] ROTATION_MODES = { "Smooth Turn", "Snap Turn" };
        private static readonly string[] SNAP_TURN_ANGLES = { "30", "45", "90" };
        private static readonly string[] SHADOW_OPTIONS = { "Normal", "Disable Near Shadows", "Distant Shadows (FPS Hit)" };
        private static readonly string[] OPTIC_RESOLUTIONS = { "256", "512", "768", "1024 (Vanilla)" };

        private sealed class Row
        {
            public GameObject gameObject;
            public int category;            // a Category, or EVERY_CATEGORY
            public Func<bool> shownWhen;    // null = always (in its category)
        }

        private static readonly List<Row> rows = new List<Row>();
        private static int shownCategory;               // kept while the game runs, not saved
        private static SettingDropDown categoryDropDown;
        private static SoundSettingsTab rowBuilder;     // the cloned tab, while its rows are built (null after)
        private static Transform rowsPanel;
        private static ScrollRect _vrSettingsScroll;

        public static void initVrSettings(SettingsScreen settingsUi)
        {
            VRSettings.settingsUi = settingsUi;
            GameObject vrSettingsButton = UnityEngine.Object.Instantiate(settingsUi._controlsButton.gameObject);
            vrSettingsButton.transform.parent = settingsUi._controlsButton.transform.parent;
            vrSettingsButton.transform.localScale = Vector3.one;
            vrSettingsButton.transform.localRotation = Quaternion.identity;
            vrSettingsButton.transform.localPosition = Vector3.zero;
            vrSettingsButton.name = "vrSettingsToggle";
            // Placed straight after the game's own tabs, whichever mod runs first, so another mod's tab added at the end of the row
            // (Integrated Configuration Manager's PLUGINS) sits next to VR
            vrSettingsButton.transform.SetSiblingIndex(settingsUi._controlsButton.transform.GetSiblingIndex() + 1);
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(INTEGRATED_CONFIGURATION_MANAGER_ID))
                Plugin.MyLog.LogInfo("[VRSettings] Integrated Configuration Manager found: its tab goes after the VR tab");

            if (vrSettingsButton.GetComponent<UIAnimatedToggleSpawner>())
            {
                UIAnimatedToggleSpawner settingsController = vrSettingsButton.GetComponent<UIAnimatedToggleSpawner>();
                settingsController.SpawnableToggle._headerLabel.text = "VR";
                settingsController._localizationSubscription = ShowVRSettings;
            }

            GameObject vrSettings = UnityEngine.Object.Instantiate(settingsUi._soundSettingsScreen.gameObject);
            vrSettings.transform.parent = settingsUi._soundSettingsScreen.transform.parent;
            vrSettings.transform.localScale = Vector3.one;
            vrSettings.transform.localRotation = Quaternion.identity;
            vrSettings.transform.localPosition = new Vector3(0, -71.5f, 0);
            vrSettings.transform.GetChild(0).localPosition = new Vector3(10, 433.5f, 0);

            SoundSettingsTab newSoundSettings = vrSettings.GetComponent<SoundSettingsTab>();
            Transform slidersPanel = newSoundSettings._slidersSection;
            for (int i = 0; i < slidersPanel.childCount; i++)
                UnityEngine.Object.Destroy(slidersPanel.GetChild(i).gameObject);
            GameObject.Destroy(newSoundSettings._togglesSection.gameObject);
            GameObject.Destroy(newSoundSettings._slidersSection.parent.FindChild("VoipSection").gameObject);

            rows.Clear();
            rowBuilder = newSoundSettings;
            rowsPanel = slidersPanel;
            try
            {
                categoryDropDown = AddDropDown(EVERY_CATEGORY, "Settings Category", CATEGORY_NAMES, CATEGORY_NAMES[shownCategory], ShowCategory,
                    tooltip: "Which of the VR settings to show");
                AddSpacer(EVERY_CATEGORY);
                AddMovementRows();
                AddWeaponRows();
                AddHandsAndBodyRows();
                AddInteractionRows();
                AddGraphicsRows();
                AddDesktopViewRows();
            }
            finally
            {
                rowBuilder = null;
            }
            RefreshRows();

            _vrSettingsScroll = SetupScrollbar(vrSettings);
            vrSettingsObject = newSoundSettings.gameObject;
            UnityEngine.Object.Destroy(newSoundSettings);
            vrSettingsObject.active = false;
        }

        private static void AddMovementRows()
        {
            int movement = (int)Category.Movement;
            movementMethod = AddDropDown(movement, "Movement Method", MOVEMENT_MODES, MOVEMENT_MODES[Mathf.Clamp((int)settings.movementType, 0, 2)],
                ChangeMovementMode, tooltip: "Which way the stick moves you: where you look, where the controller points, or the stick alone");
            rotationMethod = AddDropDown(movement, "Turning", ROTATION_MODES, ROTATION_MODES[settings.rotationType == RotationMode.Snap ? 1 : 0],
                ChangeRotationType);
            snapTurnAmountDropDown = AddDropDown(movement, "Snap Turn Angle", SNAP_TURN_ANGLES, SNAP_TURN_ANGLES[SnapTurnIndex(settings.snapTurnAmount)],
                ChangeSnapAmount, shownWhen: () => settings.rotationType == RotationMode.Snap, tooltip: "Degrees per snap");
            AddSlider(movement, "Rotation Sensitivity", 0.5f, 20f, () => settings.rotationSensitivity,
                (v) => settings.rotationSensitivity = v, (v) => Remap(v, 0.5f, 20f, 0f, 10f).ToString("0"));
            AddToggle(movement, "Swap Sticks", settings.swapSticks, SetSwapSticks,
                tooltip: "Move with the right stick, turn with the left");
            AddToggle(movement, "Disable Run Animation", settings.disableRunAnimation, SetDisableRunAnim,
                tooltip: "The gun stays in your hands while sprinting, so you can fire on the run");
            AddToggle(movement, "Step Or Lean Forward To Sprint", settings.stepToSprint, SetStepToSprint,
                tooltip: "A quick step or lean forward starts a sprint, a step back stops it");
            AddToggle(movement, "Seated Mode (Reset Your Height After)", settings.seatedMode, SetSeatedMode);

            // Vive wands only: their trackpads crouch and jump
            if (VRGlobals.vrControllerType == "vive")
            {
                AddSlider(movement, "Crouch / Prone Trackpad Threshold", 0.2f, 0.95f,
                    () => settings.viveWandCrouchTrackpadThreshold, (v) => settings.viveWandCrouchTrackpadThreshold = v,
                    (v) => Remap(v, 0.2f, 0.95f, 0f, 10f).ToString("0"));
                AddSlider(movement, "Vault / Jump Hold Time", 0.05f, 1.5f,
                    () => settings.viveWandVaultHoldTime, (v) => settings.viveWandVaultHoldTime = v, (v) => v.ToString("0.0") + "s");
            }
        }

        private static void AddWeaponRows()
        {
            int weapons = (int)Category.Weapons;
            AddToggle(weapons, "Weapon Smoothing", settings.weaponAimSmoothing, ToggleSmoothingSensitivity,
                tooltip: "Smooths out hand shake on the gun's aim");
            // smoothingSensitivity is the slerp factor (smoothingFactor): lower = heavier smoothing
            AddSlider(weapons, "Weapon Smoothing Amount (Lower = Smoother)", 0f, 50f, () => settings.smoothingSensitivity,
                (v) => settings.smoothingSensitivity = v, (v) => Remap(v, 0f, 50f, 0f, 10f).ToString("0"), shownWhen: () => settings.weaponAimSmoothing);
            AddToggle(weapons, "Aim Steadying", settings.scopeAimSmoothing, ToggleScopeSmoothingSensitivity,
                tooltip: "While aiming down sights, smooths out hand shake while your hand is nearly still but not while it moves. Stronger the more the scope zooms, and much stronger while you hold your breath (it fades as your lungs run out)");
            AddSlider(weapons, "Aim Steadying Strength", 0f, 2f, () => settings.aimSteadyStrength,
                (v) => settings.aimSteadyStrength = v, (v) => Remap(v, 0f, 2f, 0f, 10f).ToString("0"),
                shownWhen: () => settings.scopeAimSmoothing && AimSteady.enabled);
            AddSlider(weapons, "Hold Breath Sensitivity", 0f, 20f, () => settings.scopeSmoothingSensitivity,
                (v) => settings.scopeSmoothingSensitivity = v, (v) => Remap(v, 0f, 20f, 0f, 10f).ToString("0"),
                shownWhen: () => settings.scopeAimSmoothing && !AimSteady.enabled);
            AddSlider(weapons, "Variable Zoom Sensitivity", 0f, 2f, () => settings.variableZoomSensitivity,
                (v) => settings.variableZoomSensitivity = v, (v) => Remap(v, 0f, 2f, 0f, 10f).ToString("0"),
                tooltip: "How fast the stick zooms a variable scope");
            AddToggle(weapons, "Weapon Weight", settings.weaponWeight, SetWeaponWeightOn,
                tooltip: "Heavier guns swing and settle with their weight");
            AddToggle(weapons, "Virtual Gunstock (Shouldering)", settings.virtualStock, SetVirtualStockOn,
                tooltip: "Bring a stocked gun to your shoulder to steady the aim");
            AddToggle(weapons, "Chest Sling", settings.chestSling, SetChestSlingOn,
                tooltip: "Open your gun hand to let go of the gun: it stays on the foregrip hand, or hangs on your chest. Off = it stays in your hand");
            AddToggle(weapons, "Toggle Gun Grip", settings.gunGripToggle, SetGunGripToggleOn,
                tooltip: "Guns only (grip, foregrip, mounted guns): press the grip to take hold, press it again to let go, instead of holding it down. The pistol press check needs the grip held, so it's off in this mode");
            AddToggle(weapons, "Manual Reload", settings.manualReload, SetManualReloadOn,
                tooltip: "B drops the magazine instead of running the game's reload; hold B to pull it out with the other hand");
            AddToggle(weapons, "EFT Weapon Inertia", settings.weaponInertia, SetWeaponInertiaOn);
            AddToggle(weapons, "EFT Walk Bobbing", settings.walkEffector, SetWalkEffectorOn);
            // A button, not a setting: ResetGunSpots turns it back off
            resetGunSpotsToggle = AddToggle(weapons, "Reset Placed Gun Spots", false, ResetGunSpots,
                tooltip: "Puts the gun and grenade spots you placed by hand back where they start");
        }

        private static void AddHandsAndBodyRows()
        {
            int hands = (int)Category.HandsAndBody;
            AddToggle(hands, "Laser Pointer In Left Hand", settings.leftHandedMode, SetLeftHandedMode);
            // The vertical offsets pivot around 50 in CalculateRightHandPosOffset (>= 50 positive, < 50 negative): 50 stays the centre
            AddSlider(hands, "Right Hand Angle (Up / Down)", -30f, 130f, () => settings.rightHandVerticalAngle,
                (v) => settings.rightHandVerticalAngle = v, (v) => Remap(v, -30f, 130f, -1f, 1f).ToString("0.0"));
            AddSlider(hands, "Right Hand Angle (Left / Right)", -90f, 90f, () => settings.rightHandHorizontalAngle,
                (v) => settings.rightHandHorizontalAngle = v, (v) => Remap(v, -90f, 90f, -1f, 1f).ToString("0.0"));
            AddSlider(hands, "Left Hand Angle (Up / Down)", -30f, 130f, () => settings.leftHandVerticalAngle,
                (v) => settings.leftHandVerticalAngle = v, (v) => Remap(v, -30f, 130f, -1f, 1f).ToString("0.0"));
            AddSlider(hands, "Left Hand Angle (Left / Right)", -90f, 90f, () => settings.leftHandHorizontalAngle,
                (v) => settings.leftHandHorizontalAngle = v, (v) => Remap(v, -90f, 90f, -1f, 1f).ToString("0.0"));
            AddSlider(hands, "Hand Height Offset", -0.25f, 0.25f, () => settings.handPosOffset,
                (v) => settings.handPosOffset = v, (v) => Remap(v, -0.25f, 0.25f, -1f, 1f).ToString("0.0"));
            AddToggle(hands, "Hide Arms", settings.hideArms, SetHideArms, tooltip: "Shows only your hands");
            AddToggle(hands, "Hide Legs", settings.hideLegs, SetHideLegs);
        }

        private static void AddInteractionRows()
        {
            int interaction = (int)Category.Interaction;
            AddToggle(interaction, "Physical Doors", settings.physicalDoors, SetPhysicalDoorsOn,
                tooltip: "Grip a door's handle, turn it and swing the door by hand. Off = a grip opens and closes them");
            // HidesMenu needs Physical Doors on
            AddToggle(interaction, "Disable Door Context Menu", settings.disableDoorContextMenu, SetDisableDoorContextMenu,
                shownWhen: () => settings.physicalDoors, tooltip: "No look-at menu on the doors you can open by hand (keycard doors keep it)");
            AddToggle(interaction, "Disable Corpse Context Menu", settings.disableCorpseContextMenu,
                SetDisableCorpseContextMenu);
            AddToggle(interaction, "Manual Eating", settings.manualEating, SetManualEating,
                tooltip: "Open, eat and drink food with your hands");
            AddToggle(interaction, "Held Item Adds To Carry Weight", settings.heldItemWeight, SetHeldItemWeight);
            AddToggle(interaction, "Use In-Game VR Keyboard", settings.useVRKeyboard, SetUseVRKeyboard);
            AddToggle(interaction, "Disable Mouse Input", settings.disableMouseInput, SetDisableMouseInput);
        }

        private static void AddGraphicsRows()
        {
            int graphics = (int)Category.Graphics;
            opticResolutionDropDown = AddDropDown(graphics, "Scope Render Resolution", OPTIC_RESOLUTIONS, OpticResLabel(settings.opticRenderResolution),
                ChangeOpticResolution, tooltip: "Lower = faster scopes, a blurrier picture through them");
            // Render at native and use FSR/DLSS purely as anti-aliasing: needs an FSR/DLSS mode picked so the upscaler runs
            AddToggle(graphics, "Native AA (Max Quality, No Upscale)", settings.nativeAA, SetNativeAA,
                tooltip: "Full resolution, FSR/DLSS used only as anti-aliasing. Needs an FSR/DLSS mode picked in Graphics");
            shadowOptsToggle = AddDropDown(graphics, "Shadows", SHADOW_OPTIONS, SHADOW_OPTIONS[Mathf.Clamp((int)settings.shadowOpt, 0, 2)], ChangeShadowOpts);
            // The hideout's ~100 shadowed lights are most of its frame time
            AddToggle(graphics, "Disable Hideout Shadows (Saves FPS)", settings.hideoutShadowsOff, SetHideoutShadowsOff);
            // FoveatedMask's values are BepInEx config entries (so the SixBench benchmark tool can switch them), not ModSettings;
            // its debug options stay in the F12 config menu
            foveatedToggle = AddToggle(graphics, "Foveated Rendering", TarkovVR.Source.Graphics.FoveatedMask.IsEnabled,
                TarkovVR.Source.Graphics.FoveatedMask.SetEnabled, tooltip: "Less detail toward the edges of each lens, for GPU time");
            AddSlider(graphics, "Foveated Inner Radius", 0.2f, 1.2f, () => TarkovVR.Source.Graphics.FoveatedMask.InnerRadius,
                (value) => TarkovVR.Source.Graphics.FoveatedMask.InnerRadius = value, (v) => v.ToString("0.00"),
                shownWhen: () => TarkovVR.Source.Graphics.FoveatedMask.IsEnabled, tooltip: "How much of the middle of each lens keeps full detail");
            AddSlider(graphics, "LOD Bias (FPS Hit)", 0.5f, 2f, () => settings.lodBias,
                (v) => settings.lodBias = v, (v) => v.ToString("0.00"), tooltip: "Higher = full-detail models further away");
            // Scales Rendering's per-layer clutter cull table; structural layers stay uncapped. Read every frame: applies live.
            AddSlider(graphics, "Clutter Draw Distance (FPS Hit)", 0.5f, 3f, () => settings.cullDistance,
                (value) => settings.cullDistance = value, (v) => v.ToString("0.00"),
                tooltip: "How far loot, casings, bodies, bushes, rain and grass are drawn");
            AddToggle(graphics, "Disable Fog (Worse Aliasing)", settings.disablePrismFog, SetDisableFog);
        }

        private static void AddDesktopViewRows()
        {
            int desktop = (int)Category.DesktopView;
            // The plain mirror has no window camera: none of the view's own settings below apply to it
            Func<bool> steadiedView = () => !settings.desktopViewPlainMirror;
            AddToggle(desktop, "Plain Mirror (Saves CPU)", settings.desktopViewPlainMirror, SetDesktopViewPlainMirror,
                tooltip: "The headset's eye image as it is: no steadied view, field of view, tilt or smoothing");
            AddToggle(desktop, "Show The Right Eye", settings.desktopViewRightEye, SetDesktopViewRightEye,
                tooltip: "Off = the left eye");
            AddSlider(desktop, "Field Of View", 50f, 110f, () => settings.desktopViewFieldOfView,
                (v) => settings.desktopViewFieldOfView = v, (v) => v.ToString("0"), shownWhen: steadiedView, tooltip: "Degrees across the window");
            // 0 = from the window's shape; anything else squashes the picture to fit
            AddSlider(desktop, "Vertical Field Of View", 0f, 100f, () => settings.desktopViewVerticalFieldOfView,
                (v) => settings.desktopViewVerticalFieldOfView = v, (v) => v < 1f ? "Auto" : v.ToString("0"), shownWhen: steadiedView,
                tooltip: "Auto = from the window's shape; anything else squashes the picture to fit");
            AddSlider(desktop, "Tilt Down", -20f, 30f, () => settings.desktopViewTiltDown,
                (v) => settings.desktopViewTiltDown = v, (v) => v.ToString("0"), shownWhen: steadiedView,
                tooltip: "Degrees the window looks below where your head looks");
            AddSlider(desktop, "Smoothing (0 = Off)", 0f, 0.5f, () => settings.desktopViewSmoothing,
                (v) => settings.desktopViewSmoothing = v, (v) => Remap(v, 0f, 0.5f, 0f, 10f).ToString("0.0"), shownWhen: steadiedView,
                tooltip: "How long the window's view takes to catch up with a head turn");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Rows. Each control is built from a real game setting (BindTo / BindIndexTo create the control). Its change action is replaced, so
        // nothing is ever written to that setting (for a slider, MakeSliderSmooth cuts the binding).

        private static SettingToggle AddToggle(int category, string label, bool value, Action<bool> onChange, Func<bool> shownWhen = null,
                                               string tooltip = null)
        {
            SettingToggle control = rowBuilder.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, rowsPanel);
            control.BindTo(settingsUi._soundSettingsScreen._soundSettings.MusicOnRaidEnd);
            control.Toggle._onValueChanged = (on) =>
            {
                onChange(on);
                RefreshRows();
            };
            control.Text.localizationKey = label;
            control.Toggle.UpdateValue(value);
            AddRow(control, category, shownWhen, tooltip);
            return control;
        }

        private static SettingDropDown AddDropDown(int category, string label, string[] options, string current, Action<int> onChange,
                                                   Func<bool> shownWhen = null, string tooltip = null)
        {
            SettingDropDown control = rowBuilder.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, rowsPanel);
            control.BindTo(settingsUi._soundSettingsScreen._soundSettings.VoipDevice, new ReadOnlyCollection<string>(options),
                (option) => option == "Settings/UnavailablePressType" ? option.Localized() : option);
            control.Text.localizationKey = label;
            control.DropDown.SetLabelText(current);
            control.DropDown._onValueChanged._onChange = (index) =>
            {
                onChange(index);
                RefreshRows();
            };
            AddRow(control, category, shownWhen, tooltip);
            return control;
        }

        private static SettingSelectSlider AddSlider(int category, string label, float min, float max, Func<float> getValue, Action<float> onChange,
                                                     Func<float, string> format, Func<bool> shownWhen = null, string tooltip = null)
        {
            SettingSelectSlider control = rowBuilder.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, rowsPanel);
            control.BindIndexTo(settingsUi._soundSettingsScreen._soundSettings.OverallVolume, settingsUi._soundSettingsScreen._variants, (x) => x.ToString());
            control.Text.localizationKey = label;
            MakeSliderSmooth(control, min, max, getValue, onChange, format);
            AddRow(control, category, shownWhen, tooltip);
            return control;
        }

        // An empty row: a slider with its parts hidden
        private static void AddSpacer(int category)
        {
            SettingSelectSlider control = rowBuilder.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, rowsPanel);
            control.BindIndexTo(settingsUi._soundSettingsScreen._soundSettings.OverallVolume, settingsUi._soundSettingsScreen._variants, (x) => x.ToString());
            // It is bound to the master volume, which a save applies, so the binding is cut
            control.Slider._onValueChanged = null;
            control.Slider.gameObject.SetActive(false);
            control.Text.gameObject.SetActive(false);
            AddRow(control, category, null, null);
        }

        private static void AddRow(SettingControl control, int category, Func<bool> shownWhen, string tooltip)
        {
            rows.Add(new Row { gameObject = control.gameObject, category = category, shownWhen = shownWhen });
            if (string.IsNullOrEmpty(tooltip))
                return;
            try
            {
                control.GetOrCreateTooltip().SetMessageText(tooltip, rawText: true);
            }
            catch (Exception e)
            {
                Plugin.MyLog.LogWarning($"[VRSettings] no tooltip on '{control.Text.localizationKey}': {e.Message}");
            }
        }

        // Each row shown or hidden for the picked category and its own condition
        private static void RefreshRows()
        {
            if (rowBuilder != null)
                return;
            foreach (Row row in rows)
            {
                if (row.gameObject == null)
                    continue;
                bool shown = (row.category == EVERY_CATEGORY || row.category == shownCategory) && (row.shownWhen == null || row.shownWhen());
                if (row.gameObject.activeSelf != shown)
                    row.gameObject.SetActive(shown);
            }
            if (_vrSettingsScroll != null && _vrSettingsScroll.content != null && _vrSettingsScroll.gameObject.activeInHierarchy)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_vrSettingsScroll.content);
        }

        private static void ShowCategory(int index)
        {
            shownCategory = Mathf.Clamp(index, 0, CATEGORY_NAMES.Length - 1);
            if (categoryDropDown != null)
                categoryDropDown.DropDown.SetLabelText(CATEGORY_NAMES[shownCategory]);
            RefreshRows();
            if (_vrSettingsScroll != null)
                _vrSettingsScroll.verticalNormalizedPosition = 1f;
        }

        private static int SnapTurnIndex(SnapTurnAmount amount)
        {
            return amount == SnapTurnAmount.thirty ? 0 : amount == SnapTurnAmount.ninety ? 2 : 1;
        }

        private static ScrollRect SetupScrollbar(GameObject vrSettings)
        {
            const float scrollbarWidth = 10f;
            const float reservedVertical = 200f;  // canvas space taken by the header and tabs above the panel

            SoundSettingsTab tab = vrSettings.GetComponent<SoundSettingsTab>();
            RectTransform contentRT = tab._slidersSection.GetComponent<RectTransform>();
            RectTransform panelRT = contentRT.parent.GetComponent<RectTransform>();

            // --- Panel sizing ---
            // Panel has non-stretch Y anchors (1..1), so sizeDelta.y is the real height.
            // Canvas is the only ancestor with a sane rect, so size against it directly.
            var canvasRT = panelRT.GetComponentInParent<Canvas>().GetComponent<RectTransform>();
            float viewportHeight = Mathf.Max(canvasRT.rect.height - reservedVertical, 400f);
            panelRT.sizeDelta = new Vector2(panelRT.sizeDelta.x, viewportHeight);

            // Kill any ContentSizeFitter on the panel so a layout rebuild can't grow it back to fit children.
            var panelCsf = panelRT.GetComponent<ContentSizeFitter>();
            if (panelCsf != null) UnityEngine.Object.Destroy(panelCsf);

            // --- Content (slidersPanel) ---
            // Stays a direct child of panel. Top-stretch anchored, right-inset to leave
            // a strip for the scrollbar so it doesn't overlap controls.
            contentRT.anchorMin = new Vector2(0f, 1f);
            contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.anchoredPosition = Vector2.zero;
            contentRT.offsetMin = new Vector2(0f, contentRT.offsetMin.y);
            contentRT.offsetMax = new Vector2(0f, 0f);

            var vlg = contentRT.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
                vlg.padding = new RectOffset(vlg.padding.left, vlg.padding.right, 40, 40);

            var csf = contentRT.GetComponent<ContentSizeFitter>()
                      ?? contentRT.gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // --- Clip content to panel ---
            if (panelRT.GetComponent<RectMask2D>() == null)
                panelRT.gameObject.AddComponent<RectMask2D>();

            LayoutRebuilder.ForceRebuildLayoutImmediate(panelRT);

            // --- Scrollbar ---
            // Scrollbar lives outside the panel so RectMask2D doesn't clip it.
            // Anchor-match to panel so it tracks position and size automatically.
            GameObject scrollbarGO = new GameObject("VRScrollbar", typeof(RectTransform));
            scrollbarGO.transform.SetParent(panelRT.parent, false);
            RectTransform scrollbarRT = scrollbarGO.GetComponent<RectTransform>();

            scrollbarRT.anchorMin = panelRT.anchorMin;
            scrollbarRT.anchorMax = panelRT.anchorMax;
            scrollbarRT.pivot = new Vector2(0f, panelRT.pivot.y);

            // Right edge of panel in its parent's local space:
            // pivot position + offset from pivot to right edge.
            float panelRightX = panelRT.anchoredPosition.x + (1f - panelRT.pivot.x) * panelRT.rect.width;
            scrollbarRT.anchoredPosition = new Vector2(panelRightX, panelRT.anchoredPosition.y);
            scrollbarRT.sizeDelta = new Vector2(scrollbarWidth, panelRT.sizeDelta.y);

            var scrollbarBg = scrollbarGO.AddComponent<UnityEngine.UI.Image>();
            scrollbarBg.color = new Color(0.1f, 0.1f, 0.1f, 0.7f);

            var scrollbar = scrollbarGO.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            GameObject handleArea = new GameObject("Sliding Area", typeof(RectTransform));
            handleArea.transform.SetParent(scrollbarGO.transform, false);
            var haRT = handleArea.GetComponent<RectTransform>();
            haRT.anchorMin = Vector2.zero;
            haRT.anchorMax = Vector2.one;
            haRT.offsetMin = new Vector2(2f, 10f);
            haRT.offsetMax = new Vector2(-2f, -10f);

            GameObject handle = new GameObject("Handle", typeof(RectTransform));
            handle.transform.SetParent(handleArea.transform, false);
            var hRT = handle.GetComponent<RectTransform>();
            hRT.anchorMin = Vector2.zero;
            hRT.anchorMax = Vector2.one;
            hRT.sizeDelta = Vector2.zero;

            var handleImg = handle.AddComponent<UnityEngine.UI.Image>();
            handleImg.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            scrollbar.handleRect = hRT;
            scrollbar.targetGraphic = handleImg;

            // --- ScrollRect ---
            // Panel acts as its own viewport. ScrollRectNoDrag responds only to scroll
            // events from VRUIInteracter (no auto-scroll-to-focused-child).
            var scrollRect = panelRT.GetComponent<ScrollRectNoDrag>()
                             ?? panelRT.gameObject.AddComponent<ScrollRectNoDrag>();
            scrollRect.content = contentRT;
            scrollRect.viewport = panelRT;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.vertical = true;
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;
            scrollRect.inertia = false;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            return scrollRect;
        }

        private static void ChangeMovementMode(int mode)
        {
            settings.movementType = (MovementMode)Mathf.Clamp(mode, 0, 2);
            movementMethod.DropDown.SetLabelText(MOVEMENT_MODES[(int)settings.movementType]);
        }

        public static void ShowVRSettings()
        {
            if (settingsUi == null || !vrSettingsObject)
                return;
            settingsUi._currentTab.gameObject.active = false;
            vrSettingsObject.active = true;
            // Its value is a BepInEx config entry, which the F12 menu can change while this menu is closed
            if (foveatedToggle != null)
                foveatedToggle.Toggle.UpdateValue(TarkovVR.Source.Graphics.FoveatedMask.IsEnabled, sendCallback: false);
            RefreshRows();
            if (_vrSettingsScroll != null)
                _vrSettingsScroll.verticalNormalizedPosition = 1f;
        }

        public static void CloseVRSettings()
        {
            if (vrSettingsObject)
                vrSettingsObject.active = false;
        }

        public static float GetRotationSensitivity() {
            return settings.rotationSensitivity;
        }
        public static float GetLeftStickSensitivity()
        {
            return settings.leftStickDriftSensitivity;
        }
        public static float GetRightStickSensitivity()
        {
            return settings.rightStickDriftSensitivity;
        }

        public static float GetSmoothingSensitivity()
        {
            return settings.smoothingSensitivity;
        }

        public static float GetScopeSensitivity()
        {
            return settings.scopeSmoothingSensitivity;
        }
        public static float GetAimSteadyStrength()
        {
            return settings.scopeAimSmoothing ? Mathf.Max(settings.aimSteadyStrength, 0f) : 0f;
        }
        public static float GetVariableZoomSensitivity()
        {
            return settings.variableZoomSensitivity;
        }
        private static void ToggleSmoothingSensitivity(bool turnOn)
        {
            settings.weaponAimSmoothing = turnOn;
        }
        public static bool SmoothWeaponAim()
        {
            return settings.weaponAimSmoothing;
        }

        private static void SetWeaponWeightOn(bool turnOn)
        {
            settings.weaponWeight = turnOn;
        }
        public static bool GetWeaponWeightOn()
        {
            return settings.weaponWeight;
        }

        private static void SetVirtualStockOn(bool turnOn)
        {
            settings.virtualStock = turnOn;
        }
        public static bool GetVirtualStockOn()
        {
            return settings.virtualStock;
        }

        private static void SetChestSlingOn(bool turnOn)
        {
            settings.chestSling = turnOn;
        }
        public static bool GetChestSlingOn()
        {
            return settings.chestSling;
        }

        private static void SetGunGripToggleOn(bool turnOn)
        {
            settings.gunGripToggle = turnOn;
        }
        public static bool GetGunGripToggle()
        {
            return settings != null && settings.gunGripToggle;
        }

        private static void SetPhysicalDoorsOn(bool turnOn)
        {
            settings.physicalDoors = turnOn;
        }
        public static bool GetPhysicalDoorsOn()
        {
            return settings.physicalDoors;
        }
        private static void SetDisableDoorContextMenu(bool turnOn)
        {
            settings.disableDoorContextMenu = turnOn;
        }
        public static bool GetDisableDoorContextMenu()
        {
            return settings.disableDoorContextMenu;
        }

        // A gun spot placed by hand (GunSpots; index 0 the chest sling, 1 the shoulder, 2 the hip): its grip point and Weapon_root
        // rotation in the body's frame. Saved straight away (placed in a raid, not in this menu).
        public static bool GetGunSpot(int index, out Vector3 grip, out Quaternion rotation)
        {
            float[] data = index == 0 ? settings.gunSpotChest : index == 1 ? settings.gunSpotBack : index == 2 ? settings.gunSpotHip : null;
            if (data == null || data.Length != 7)
            {
                grip = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            grip = new Vector3(data[0], data[1], data[2]);
            rotation = new Quaternion(data[3], data[4], data[5], data[6]);
            return true;
        }

        public static void SetGunSpot(int index, Vector3 grip, Quaternion rotation)
        {
            rotation = Quaternion.Normalize(rotation);
            float[] data = { grip.x, grip.y, grip.z, rotation.x, rotation.y, rotation.z, rotation.w };
            if (index == 0)
                settings.gunSpotChest = data;
            else if (index == 1)
                settings.gunSpotBack = data;
            else if (index == 2)
                settings.gunSpotHip = data;
            else
                return;
            SaveSettings();
        }

        // The grenade slot placed by hand (GrenadeSlot): the grenade's middle relative to the neck and its body's rotation, in the body
        // frame. Saved straight away.
        public static bool GetGrenadeSlot(out Vector3 middle, out Quaternion rotation)
        {
            return ReadPose(settings != null ? settings.grenadeSlotMiddle : null, out middle, out rotation);
        }

        public static void SetGrenadeSlot(Vector3 middle, Quaternion rotation)
        {
            rotation = Quaternion.Normalize(rotation);
            settings.grenadeSlotMiddle = new[] { middle.x, middle.y, middle.z, rotation.x, rotation.y, rotation.z, rotation.w };
            settings.grenadeSlot = null;
            SaveSettings();
        }

        // The older saved placement (grenadeSlot): the pose of the model's root, which sits ~40 cm from the grenade. GrenadeSlot converts
        // it to the middle-and-body pose once it has a model to measure.
        public static bool GetOldGrenadeSlot(out Vector3 rootPosition, out Quaternion rootRotation)
        {
            return ReadPose(settings != null ? settings.grenadeSlot : null, out rootPosition, out rootRotation);
        }

        private static bool ReadPose(float[] data, out Vector3 position, out Quaternion rotation)
        {
            if (data == null || data.Length != 7)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = new Vector3(data[0], data[1], data[2]);
            rotation = new Quaternion(data[3], data[4], data[5], data[6]);
            return true;
        }

        private static void ResetGunSpots(bool turnOn)
        {
            if (!turnOn)
                return;
            settings.gunSpotChest = null;
            settings.gunSpotBack = null;
            settings.gunSpotHip = null;
            settings.grenadeSlot = null;
            settings.grenadeSlotMiddle = null;
            SaveSettings();
            Plugin.MyLog.LogInfo("[GunSpots] placed gun spots reset to the defaults");
            // Switch the toggle back off without calling this again: it acts as a button, not a setting
            if (resetGunSpotsToggle != null)
                resetGunSpotsToggle.Toggle.UpdateValue(false, sendCallback: false);
        }

        private static void SetManualReloadOn(bool turnOn)
        {
            settings.manualReload = turnOn;
        }
        public static bool GetManualReloadOn()
        {
            return settings.manualReload;
        }

        private static void SetWeaponInertiaOn(bool turnOn)
        {
            settings.weaponInertia = turnOn;
        }
        public static bool GetWeaponInertiaOn()
        {
            return settings.weaponInertia;
        }

        private static void SetWalkEffectorOn(bool turnOn)
        {
            settings.walkEffector = turnOn;
        }
        public static bool GetWalkEffectorOn()
        {
            return settings.walkEffector;
        }

        public static bool SmoothScopeAim()
        {
            return settings.scopeAimSmoothing;
        }
        private static void ToggleScopeSmoothingSensitivity(bool turnOn)
        {
            settings.scopeAimSmoothing = turnOn;
        }
        public static MovementMode GetMovementMode()
        {
            return settings.movementType;
        }

        public static float GetPrimaryHandVertOffset()
        {
            if (GetLeftHandedMode())
                return settings.leftHandVerticalAngle;
            else
                return settings.rightHandVerticalAngle;
        }
        public static float GetSecondaryHandVertOffset()
        {
            if (GetLeftHandedMode())
                return settings.rightHandVerticalAngle;
            else
                return settings.leftHandVerticalAngle;
        }

        public static float GetPrimaryHandHorOffset()
        {
            if (settings.leftHandedMode)
                return settings.leftHandHorizontalAngle;
            else
                return settings.rightHandHorizontalAngle;
        }
        public static float GetSecondaryHandHorOffset()
        {
            if (settings.leftHandedMode)
                return settings.rightHandHorizontalAngle;
            else
                return settings.leftHandHorizontalAngle;
        }
        public static float GetLodBias()
        {
            return settings.lodBias;
        }
        public static bool GetUseOpenXR()
        {
            return settings.useOpenXR;
        }
        public static float GetCullDistance()
        {
            return Mathf.Clamp(settings.cullDistance, 0.5f, 3f);
        }

        public static float GetRightHandVerticalOffset()
        {
            return settings.rightHandVerticalAngle;
        }
        public static float GetArmLength()
        {
            return settings.armLength;
        }

        public static void SetArmLength(float armLength)
        {
            settings.armLength = armLength;
            SaveSettings();
        }

        public static float GetHandPosOffset()
        {
            return settings.handPosOffset;
        }

        private static void SetSeatedMode(bool turnOn)
        {        
            settings.seatedMode = turnOn;
        }
        public static bool GetSeatedMode()
        {
            return settings.seatedMode;
        }


        public static float GetRightHandHorizontalOffset()
        {
            return settings.rightHandHorizontalAngle;
        }

        public static float GetLeftHandHorizontalOffset()
        {
            return settings.leftHandHorizontalAngle;
        }

        public static bool GetSharpenOn()
        {
            return settings.enableSharpen;
        }

        // The "Laser Pointer In Left Hand" toggle (stored as leftHandedMode, so saved settings keep their value) puts the laser and the
        // menu controls on the left controller. The gun goes into the left hand through the hand swap (ChestHang), not through this.
        private static void SetLeftHandedMode(bool toggle)
        {
            settings.leftHandedMode = toggle;
            if (VRGlobals.menuVRManager)
                VRGlobals.menuVRManager.SetLaserHand(toggle);
        }

        // Always false: the mirrored left-handed mode is not used, so everything that reads this (the arms' mirror, the gun hand's
        // controller, the sticks, the holsters, the wrist panels) plays right-handed. The stored setting only picks the laser's hand
        // (GetLaserInLeftHand).
        public static bool GetLeftHandedMode()
        {
            return false;
        }

        public static bool GetLaserInLeftHand()
        {
            return settings.leftHandedMode;
        }

        // Swaps what the right and left sticks do (VRInput), for left-handed play. The laser's hand is a separate setting.
        private static void SetSwapSticks(bool toggle)
        {
            settings.swapSticks = toggle;
        }

        public static bool GetSwapSticks()
        {
            return settings != null && settings.swapSticks;
        }

        public static bool GetHideArms()
        {
            return settings.hideArms;
        }

        public static bool GetDesktopViewRightEye()
        {
            return settings != null && settings.desktopViewRightEye;
        }
        public static bool GetDesktopViewPlainMirror()
        {
            return settings != null && settings.desktopViewPlainMirror;
        }
        public static bool GetHideoutShadowsOff()
        {
            return settings != null && settings.hideoutShadowsOff;
        }
        private static void SetHideoutShadowsOff(bool off)
        {
            settings.hideoutShadowsOff = off;
        }

        // Degrees across the desktop window (a settings file without this field reads 0, which gives the default)
        public static float GetDesktopViewFieldOfView()
        {
            return settings == null || settings.desktopViewFieldOfView < 1f ? 80f : Mathf.Clamp(settings.desktopViewFieldOfView, 30f, 150f);
        }

        // Degrees up-down in the desktop window, 0 = from the window's shape
        public static float GetDesktopViewVerticalFieldOfView()
        {
            return settings == null || settings.desktopViewVerticalFieldOfView < 1f ? 0f : Mathf.Clamp(settings.desktopViewVerticalFieldOfView, 1f, 150f);
        }

        // Degrees the desktop window's view is aimed below where the head looks
        public static float GetDesktopViewTiltDown()
        {
            return settings == null ? 0f : Mathf.Clamp(settings.desktopViewTiltDown, -45f, 45f);
        }

        // Seconds the desktop window's view takes to catch up with a head turn
        public static float GetDesktopViewSmoothing()
        {
            return settings == null ? 0.15f : Mathf.Clamp(settings.desktopViewSmoothing, 0f, 1f);
        }
        private static void SetDesktopViewRightEye(bool rightEye)
        {
            settings.desktopViewRightEye = rightEye;
        }
        private static void SetDesktopViewPlainMirror(bool plain)
        {
            settings.desktopViewPlainMirror = plain;
        }
        private static void SetHideArms(bool turnOff)
        {
            settings.hideArms = turnOff;
            if (VRGlobals.origArmsModel && VRGlobals.handsOnlyModel) {
                if (turnOff)
                {
                    VRGlobals.origArmsModel.transform.parent.gameObject.active = false;
                    VRGlobals.handsOnlyModel.transform.parent.gameObject.active = true;
                }
                else {
                    VRGlobals.origArmsModel.transform.parent.gameObject.active = true;
                    VRGlobals.handsOnlyModel.transform.parent.gameObject.active = false;
                }
            }
        }

        public static bool GetHideLegs()
        {
            return settings.hideLegs;
        }
        private static void SetHideLegs(bool turnOff)
        {
            settings.hideLegs = turnOff;
            if (VRGlobals.legsModel)
                VRGlobals.legsModel.transform.parent.gameObject.active = !turnOff;
        }
        private static void SetDisableMouseInput(bool turnOn)
        {
            settings.disableMouseInput = turnOn;
        }
        public static bool GetDisableMouseInput()
        {
            return settings.disableMouseInput;
        }
        private static void SetDisableCorpseContextMenu(bool turnOn)
        {
            settings.disableCorpseContextMenu = turnOn;
        }
        public static bool GetDisableCorpseContextMenu()
        {
            return settings.disableCorpseContextMenu;
        }
        private static void SetManualEating(bool turnOn)
        {
            settings.manualEating = turnOn;
        }
        public static bool GetManualEating()
        {
            return settings.manualEating;
        }

        private static void SetNativeAA(bool on)
        {
            settings.nativeAA = on;
            UpscalerQualityPatches.ReapplyForNativeAA();   // apply live (no need to re-pick the FSR quality)
        }
        public static bool GetNativeAA()
        {
            return settings.nativeAA;
        }

        public static bool GetDisableRunAnim()
        {
            return settings.disableRunAnimation;
        }

        private static void SetDisableRunAnim(bool turnOn)
        {
            settings.disableRunAnimation = turnOn;
        }

        // A quick step or lean forward starts a sprint, a step back ends it (StepSprint)
        public static bool GetStepToSprint()
        {
            return settings != null && settings.stepToSprint;
        }

        private static void SetStepToSprint(bool turnOn)
        {
            settings.stepToSprint = turnOn;
        }
        public static bool GetDisablePrismEffects()
        {
            return settings.disablePrismEffects;
        }

        public static bool GetDisableFog()
        {
            return settings.disablePrismFog;
        }

        private static void SetDisableFog(bool turnOff)
        {
            settings.disablePrismFog = turnOff;
        }

        public static bool GetOccCulling()
        {
            return settings.disableOccCulling;
        }

        public static bool GetFrusCulling()
        {
            return settings.disableFrusCulling;
        }

        private static void ChangeShadowOpts(int mode)
        {
            settings.shadowOpt = (ShadowOpt)Mathf.Clamp(mode, 0, 2);
            shadowOptsToggle.DropDown.SetLabelText(SHADOW_OPTIONS[(int)settings.shadowOpt]);

            if (!VisualPatches.distantShadow)
                return;
            
            if (GetShadowOpts() == VRSettings.ShadowOpt.IncreaseLighting)
            {
                VisualPatches.distantShadow.EnableMultiviewTiles = false;
                VisualPatches.distantShadow.PreComputeMask = true;
                QualitySettings.shadowDistance = 25f;
                VisualPatches.distantShadow.CurrentMaskResolution = DistantShadow.ResolutionState.FULL;
            }
            else if (VRSettings.GetShadowOpts() == VRSettings.ShadowOpt.DisableNearShadows)
            {
                VisualPatches.distantShadow.EnableMultiviewTiles = true;
                VisualPatches.distantShadow.PreComputeMask = false;
            }
            else
            {
                VisualPatches.distantShadow.EnableMultiviewTiles = true;
                VisualPatches.distantShadow.PreComputeMask = true;
            }
            VisualPatches.distantShadow.DisableFeature();
        }
        public static ShadowOpt GetShadowOpts()
        {
            return settings.shadowOpt;
        }

        // ---- Scope (optic) render resolution ----
        public static int GetOpticRenderResolution()
        {
            return settings.opticRenderResolution;
        }

        private static string OpticResLabel(int res)
        {
            return res >= 1024 ? "1024 (Vanilla)" : res.ToString();
        }

        private static void ChangeOpticResolution(int index)
        {
            int[] opts = { 256, 512, 768, 1024 };
            int res = (index >= 0 && index < opts.Length) ? opts[index] : 512;  // (OPTIC_RESOLUTIONS' order)
            settings.opticRenderResolution = res;
            if (opticResolutionDropDown != null)
                opticResolutionDropDown.DropDown.SetLabelText(OpticResLabel(res));

            // Live re-apply if a scope camera already exists (otherwise it takes effect on next scope setup).
            // SetResolution gets intercepted by WeaponPatches.ScaleOpticResolution which reads this setting.
            try
            {
                if (CameraManager.Exist && CameraManager.Instance.OpticCameraManager != null && CameraManager.Instance.OpticCameraManager.Camera != null)
                    CameraManager.Instance.OpticCameraManager.SetResolution(res);
            }
            catch { }
        }


        private static void ChangeRotationType(int mode)
        {
            settings.rotationType = mode == 1 ? RotationMode.Snap : RotationMode.Smooth;
            rotationMethod.DropDown.SetLabelText(ROTATION_MODES[(int)settings.rotationType]);
        }
        public static RotationMode GetRotationType()
        {
            return settings.rotationType;
        }

        private static void ChangeSnapAmount(int mode)
        {
            settings.snapTurnAmount = mode == 0 ? SnapTurnAmount.thirty : mode == 2 ? SnapTurnAmount.ninety : SnapTurnAmount.fourtyFive;
            snapTurnAmountDropDown.DropDown.SetLabelText(SNAP_TURN_ANGLES[SnapTurnIndex(settings.snapTurnAmount)]);
        }
        public static SnapTurnAmount GetSnapTurnAmount()
        {
            return settings.snapTurnAmount;
        }
        public static bool GetUseVRKeyboard()
        {
            return settings.useVRKeyboard;
        }
        private static void SetUseVRKeyboard(bool turnOn)
        {
            settings.useVRKeyboard = turnOn;
        }

        public static bool GetHeldItemWeight()
        {
            return settings.heldItemWeight;
        }
        private static void SetHeldItemWeight(bool turnOn)
        {
            settings.heldItemWeight = turnOn;
        }
        // ---- Vive Crouch Threshold ----
        public static float GetCrouchThreshold()
        {
            return settings.viveWandCrouchTrackpadThreshold;
        }

        // ---- Vive Vault Hold Time ----
        public static float GetVaultHoldTime()
        {
            return settings.viveWandVaultHoldTime;
        }
    }
}
