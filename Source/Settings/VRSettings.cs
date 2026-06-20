using EFT.UI;
using EFT.UI.Ragfair;
using EFT.UI.Settings;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using TarkovVR;
using TarkovVR.Patches.Visuals;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using static HBAO_Core;

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

        // Vive Wand controller scales
        private static readonly float[] ViveWandVaultTimeValues = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f];
        private static readonly float[] ViveWandCrouchThresholdValues = [0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f];
        
        public class ModSettings
        {
            public float rotationSensitivity { get; set; }
            public float leftStickDriftSensitivity { get; set; }
            public float rightStickDriftSensitivity { get; set; }
            public MovementMode movementType { get; set; }
            public RotationMode rotationType { get; set; }
            public SnapTurnAmount snapTurnAmount { get; set; }
            public bool weaponAimSmoothing { get; set; }
            public bool snapToGun { get; set; }
            public bool supportGunHoldToggle { get; set; }
            public bool leftHandedMode { get; set; }
            public bool seatedMode { get; set; }
            public float smoothingSensitivity { get; set; }
            public float scopeSmoothingSensitivity { get; set; }
            public float variableZoomSensitivity { get; set; }
            public bool scopeAimSmoothing { get; set; }
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
            public bool manualEating { get; set; }
            public bool disableMouseInput { get; set; }
            public bool disableRunAnimation { get; set; }
            public bool disablePrismEffects { get; set; }
            public bool disablePrismFog { get; set; }
            public ShadowOpt shadowOpt { get; set; }
            public bool disableOccCulling { get; set; }
            public bool disableFrusCulling { get; set; }
            public bool useVRKeyboard { get; set; }
            public bool heldItemWeight { get; set; }
            public int opticRenderResolution { get; set; }
            public float lodBias { get; set; }

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
                snapToGun = true;
                supportGunHoldToggle = false;
                smoothingSensitivity = 1;
                variableZoomSensitivity = 0.5f;
                scopeSmoothingSensitivity = 5;
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
                manualEating = false;
                disableMouseInput = true;
                disableRunAnimation = true;
                disablePrismEffects = false;
                disablePrismFog = true;               
                shadowOpt = ShadowOpt.IncreaseLighting;
                disableOccCulling = false;
                disableFrusCulling = false;
                lodBias = 1.0f;
                useVRKeyboard = false;
                heldItemWeight = false;
                opticRenderResolution = 512;
                viveWandCrouchTrackpadThreshold = 0.7f;
                viveWandVaultHoldTime = 0.3f;
            }
            // Add more settings as needed
        }

        private static SettingsScreen settingsUi;
        public static GameObject vrSettingsObject;
        public static GameObject vrGraphicsObject;

        //private static SettingSelectSlider leftStickDriftSlider;
        //private static SettingSelectSlider rightStickDriftSlider;
        // Movement Settings
        private static SettingSelectSlider sensitivitySlider;
        private static SettingDropDown movementMethod;
        private static SettingDropDown rotationMethod;
        private static SettingDropDown snapTurnAmountDropDown;
        // Weapon handling settings
        private static SettingToggle leftHandedMode;
        private static SettingToggle aimSmoothingToggle;
        private static SettingToggle snapToGunToggle;
        private static SettingToggle supportGunHoldToggle;
        private static SettingToggle weaponWeightToggle;
        private static SettingToggle weaponInertiaToggle;
        private static SettingToggle walkEffectorToggle;
        private static SettingSelectSlider aimSmoothingSlider;
        private static SettingSelectSlider scopeSmoothingSlider;
        private static SettingToggle scopeSmoothingToggle;
        private static SettingSelectSlider rightHandVerticalAngleSlider;
        private static SettingSelectSlider rightHandHorizontalAngleSlider;
        private static SettingSelectSlider leftHandHorizontalAngleSlider;
        private static SettingSelectSlider leftHandVerticalAngleSlider;
        private static SettingSelectSlider handPosOffsetSlider;
        private static SettingSelectSlider variableZoomSensitivitySlider;

        // Vive controller settings
        private static SettingSelectSlider viveWandCrouchThresholdSlider;
        private static SettingSelectSlider viveWandVaultHoldTimeSlider;

        // Graphics Settings
        private static SettingToggle sharpenToggle;
        private static SettingToggle occCullingToggle;
        private static SettingToggle frusCullingToggle;
        private static SettingDropDown shadowOptsToggle;
        private static SettingToggle disablePrismEffectsToggle;
        private static SettingToggle disableFogToggle;
        private static SettingDropDown opticResolutionDropDown;
        private static SettingSelectSlider lodBiasSlider;

        // Other settings
        private static SettingToggle hideArmsToggle;
        private static SettingToggle hideLegsToggle;
        private static SettingToggle manualEatingToggle;
        private static SettingToggle disableMouseInput;
        private static SettingToggle disableRunAnimationToggle;
        private static SettingToggle seatedModeToggle;
        private static SettingToggle useVRKeyboardToggle;
        private static SettingToggle heldItemWeightToggle;

        private static SettingSelectSlider emptySpacingSlider;


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
                settings = new ModSettings(); // Return default settings if file doesn't exist
            }
        }

        // Linearly remaps a value from one range to another. Used by the sliders to DISPLAY a clean,
        // friendly number (e.g. 0..10 or -1..1) decoupled from the messy underlying value range -
        // the stored setting keeps its real units; only the label is scaled.
        private static float Remap(float v, float fromMin, float fromMax, float toMin, float toMax)
        {
            if (Mathf.Approximately(fromMax, fromMin))
                return toMin;
            float t = Mathf.Clamp01((v - fromMin) / (fromMax - fromMin));
            return Mathf.Lerp(toMin, toMax, t);
        }

        // Turns one of EFT's discrete SelectSlider controls ("jumping to each line") into a smooth,
        // continuous slider over a real [min,max] range. EFT's SelectSlider.Awake() forces
        // wholeNumbers = true + an int-flooring listener + one notch per value, and - because our VR
        // panels are cloned from an INACTIVE tab - that Awake runs only when the VR tab is first
        // shown, i.e. AFTER this method. So we can't just reconfigure the slider once here; instead we
        // attach a SmoothSlider component that re-applies the continuous config in OnEnable (which
        // runs after Awake, every time the panel is shown). See SmoothSlider.cs.
        // Call AFTER BindIndexTo (which builds + shows the control).
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

            // CRITICAL: BindIndexTo binds this slider to the REAL OverallVolume (master volume)
            // GameSetting - it sets SelectSlider.action_0 = (index => OverallVolume.Value = index),
            // and that value gets applied to the audio on save. We don't drive OverallVolume; our
            // SmoothSlider writes the actual setting via the Unity slider's own onValueChanged. So
            // sever action_0 here - otherwise saving pushes our slider value into the master volume
            // and mutes the game. (The old code masked this by overwriting action_0 with its own
            // ChangeX callback; the SmoothSlider rewrite dropped that, hence the muted-audio bug.)
            sel.action_0 = null;

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
            // Only Apply now if the panel is ALREADY active (then SelectSlider.Awake has already run,
            // so its wholeNumbers=true rounding happened before our listener exists - no write-back).
            // The usual case is the panel being cloned INACTIVE: we deliberately DON'T Apply here, so
            // our onChange listener isn't attached when the deferred Awake rounds the handle on first
            // show. OnEnable does the full setup (and reseeds from getValue) once the panel appears.
            if (sel.gameObject.activeInHierarchy)
                cfg.Apply();
        }

        public static void initVrSettings(SettingsScreen settingsUi)
        {
            VRSettings.settingsUi = settingsUi;
            GameObject vrSettingsButton = UnityEngine.Object.Instantiate(settingsUi._controlsButton.gameObject);
            vrSettingsButton.transform.parent = settingsUi._controlsButton.transform.parent;
            vrSettingsButton.transform.localScale = Vector3.one;
            vrSettingsButton.transform.localRotation = Quaternion.identity;
            vrSettingsButton.transform.localPosition = Vector3.zero;
            vrSettingsButton.name = "vrSettingsToggle";

            if (vrSettingsButton.GetComponent<UIAnimatedToggleSpawner>())
            {
                UIAnimatedToggleSpawner settingsController = vrSettingsButton.GetComponent<UIAnimatedToggleSpawner>();
                settingsController.SpawnableToggle._headerLabel.text = "VR";
                settingsController.action_0 = ShowVRSettings;
            }

            // Second custom tab: "VR Graphics" (sits next to "VR")
            GameObject vrGraphicsButton = UnityEngine.Object.Instantiate(settingsUi._controlsButton.gameObject);
            vrGraphicsButton.transform.parent = settingsUi._controlsButton.transform.parent;
            vrGraphicsButton.transform.localScale = Vector3.one;
            vrGraphicsButton.transform.localRotation = Quaternion.identity;
            vrGraphicsButton.transform.localPosition = Vector3.zero;
            vrGraphicsButton.name = "vrGraphicsToggle";

            if (vrGraphicsButton.GetComponent<UIAnimatedToggleSpawner>())
            {
                UIAnimatedToggleSpawner graphicsController = vrGraphicsButton.GetComponent<UIAnimatedToggleSpawner>();
                graphicsController.SpawnableToggle._headerLabel.text = "VR More";
                graphicsController.action_0 = ShowVRGraphicsSettings;
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
            {
                UnityEngine.Object.Destroy(slidersPanel.GetChild(i).gameObject);
            }
            GameObject.Destroy(newSoundSettings._togglesSection.gameObject);
            GameObject.Destroy(newSoundSettings._slidersSection.parent.FindChild("VoipSection").gameObject);
            ReadOnlyCollection<string> movementMethods = new ReadOnlyCollection<string>(new List<string> { "Head-based movement", "Wand-based movement", "JoyStick only movement" });
            movementMethod = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, slidersPanel);
            movementMethod.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, movementMethods, (x) => !(x == "Head-based movement") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            movementMethod.Text.localizationKey = "Movement method: ";
            if (settings.movementType == MovementMode.HeadBased)
                movementMethod.DropDown.SetLabelText("Head-based movement");
            else if (settings.movementType == MovementMode.WandBased)
                movementMethod.DropDown.SetLabelText("Wand-based movement");
            else
                movementMethod.DropDown.SetLabelText("JoyStick only movement");

            movementMethod.DropDown.gclass1626_0.Action_0 = ChangeMovementMode;

            ReadOnlyCollection<string> rotationsModes = new ReadOnlyCollection<string>(new List<string> { "Smooth Turn", "Snap Turn" });
            rotationMethod = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, slidersPanel);
            rotationMethod.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, rotationsModes, (x) => !(x == "Smooth Turn") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            rotationMethod.Text.localizationKey = "Rotation method: ";
            if (settings.rotationType == RotationMode.Smooth)
                rotationMethod.DropDown.SetLabelText("Smooth Turn");
            else
                rotationMethod.DropDown.SetLabelText("Snap Turn");

            rotationMethod.DropDown.gclass1626_0.Action_0 = ChangeRotationType;

            

            ReadOnlyCollection<string> snapTurnMethods = new ReadOnlyCollection<string>(new List<string> { "30", "45", "90" });
            snapTurnAmountDropDown = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, slidersPanel);
            snapTurnAmountDropDown.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, snapTurnMethods, (x) => !(x == "45") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            snapTurnAmountDropDown.Text.localizationKey = "Snap turn Amount: ";
            if (settings.snapTurnAmount == SnapTurnAmount.thirty)
                snapTurnAmountDropDown.DropDown.SetLabelText("30");
            else if (settings.snapTurnAmount == SnapTurnAmount.fourtyFive)
                snapTurnAmountDropDown.DropDown.SetLabelText("45");
            else
                snapTurnAmountDropDown.DropDown.SetLabelText("90");


            snapTurnAmountDropDown.DropDown.gclass1626_0.Action_0 = ChangeSnapAmount;


            sensitivitySlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            sensitivitySlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            sensitivitySlider.Text.localizationKey = "Rotation Sensitivity:";
            MakeSliderSmooth(sensitivitySlider, 0.5f, 20f, () => settings.rotationSensitivity,
                (v) => settings.rotationSensitivity = v, (v) => Remap(v, 0.5f, 20f, 0f, 10f).ToString("0"));
            //leftStickDriftSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            //leftStickDriftSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            //leftStickDriftSlider.Slider.action_0 = ChangeLeftStickDriftSensitivity;
            //leftStickDriftSlider.Text.localizationKey = "Left Stick Drift Sensitivity: ";
            //leftStickDriftSlider.Slider.UpdateValue((int) settings.leftStickDriftSensitivity * 10);
            //leftStickDriftSlider.transform.localPosition = new Vector3(0, -70, 0);

            //rightStickDriftSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            //rightStickDriftSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            //rightStickDriftSlider.Slider.action_0 = ChangeRightStickDriftSensitivity;
            //rightStickDriftSlider.Text.localizationKey = "Right Stick Drift Sensitivity: ";
            //rightStickDriftSlider.Slider.UpdateValue((int) settings.rightStickDriftSensitivity * 10);
            //rightStickDriftSlider.transform.localPosition = new Vector3(0, -120, 0);


            
            emptySpacingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            emptySpacingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            emptySpacingSlider.Slider.gameObject.SetActive(false);
            emptySpacingSlider.Text.gameObject.SetActive(false);
            

            seatedModeToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            seatedModeToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            seatedModeToggle.Toggle.action_0 = SetSeatedMode;
            seatedModeToggle.Text.localizationKey = "Seated Mode - Reset your height after setting";
            seatedModeToggle.Toggle.UpdateValue(settings.seatedMode);

            leftHandedMode = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            leftHandedMode.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            leftHandedMode.Toggle.action_0 = SetLeftHandedMode;
            leftHandedMode.Text.localizationKey = "Left Handed Mode (VERY experimental) ";
            leftHandedMode.Toggle.UpdateValue(settings.leftHandedMode);

            snapToGunToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            snapToGunToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            snapToGunToggle.Toggle.action_0 = SetSnapToGun;
            snapToGunToggle.Text.localizationKey = "Turn On Left Hand Snap To Weapon ";
            snapToGunToggle.Toggle.UpdateValue(settings.snapToGun);

            supportGunHoldToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            supportGunHoldToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            supportGunHoldToggle.Toggle.action_0 = SetSupportGunHoldToggle;
            supportGunHoldToggle.Text.localizationKey = "Toggle Hold Grip For Two Handing ";
            supportGunHoldToggle.Toggle.UpdateValue(settings.supportGunHoldToggle);

            scopeSmoothingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            scopeSmoothingToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            scopeSmoothingToggle.Toggle.action_0 = ToggleScopeSmoothingSensitivity;
            scopeSmoothingToggle.Text.localizationKey = "Turn On Hold Breath Sensitivity ";
            scopeSmoothingToggle.Toggle.UpdateValue(settings.scopeAimSmoothing);

            aimSmoothingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            aimSmoothingToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            aimSmoothingToggle.Toggle.action_0 = ToggleSmoothingSensitivity;
            aimSmoothingToggle.Text.localizationKey = "Turn On Weapon Smoothing ";
            aimSmoothingToggle.Toggle.UpdateValue(settings.weaponAimSmoothing);

            weaponWeightToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            weaponWeightToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            weaponWeightToggle.Toggle.action_0 = SetWeaponWeightOn;
            weaponWeightToggle.Text.localizationKey = "Turn On Weapon Weight";
            weaponWeightToggle.Toggle.UpdateValue(settings.weaponWeight);

            weaponInertiaToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            weaponInertiaToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            weaponInertiaToggle.Toggle.action_0 = SetWeaponInertiaOn;
            weaponInertiaToggle.Text.localizationKey = "Turn On EFT Weapon Inertia";
            weaponInertiaToggle.Toggle.UpdateValue(settings.weaponInertia);

            walkEffectorToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            walkEffectorToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            walkEffectorToggle.Toggle.action_0 = SetWalkEffectorOn;
            walkEffectorToggle.Text.localizationKey = "Turn On EFT Weapon Walk Bobbing";
            walkEffectorToggle.Toggle.UpdateValue(settings.walkEffector);

            heldItemWeightToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            heldItemWeightToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            heldItemWeightToggle.Toggle.action_0 = SetHeldItemWeight;
            heldItemWeightToggle.Text.localizationKey = "Turn On Held Item Adds To Carry Weight";
            heldItemWeightToggle.Toggle.UpdateValue(settings.heldItemWeight);

            aimSmoothingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            aimSmoothingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            // smoothingSensitivity is the slerp factor used as `smoothingFactor`: lower = heavier
            // smoothing, 50 = the special "no smoothing" value. Drive it directly so the bar spans
            // the whole 0..50 range continuously (wider than the old ~0..22 it could reach).
            aimSmoothingSlider.Text.localizationKey = "Weapon Smoothing (lower = smoother):";
            MakeSliderSmooth(aimSmoothingSlider, 0f, 50f, () => settings.smoothingSensitivity,
                (v) => settings.smoothingSensitivity = v, (v) => Remap(v, 0f, 50f, 0f, 10f).ToString("0"));

            scopeSmoothingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            scopeSmoothingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            scopeSmoothingSlider.Text.localizationKey = "Hold Breath Sensitivity:";
            MakeSliderSmooth(scopeSmoothingSlider, 0f, 20f, () => settings.scopeSmoothingSensitivity,
                (v) => settings.scopeSmoothingSensitivity = v, (v) => Remap(v, 0f, 20f, 0f, 10f).ToString("0"));

            
            variableZoomSensitivitySlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            variableZoomSensitivitySlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            variableZoomSensitivitySlider.Text.localizationKey = "Variable Zoom Sensitivity:";
            MakeSliderSmooth(variableZoomSensitivitySlider, 0f, 2f, () => settings.variableZoomSensitivity,
                (v) => settings.variableZoomSensitivity = v, (v) => Remap(v, 0f, 2f, 0f, 10f).ToString("0"));
            

            rightHandVerticalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            rightHandVerticalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            // Vertical hand offsets pivot around 50 in CalculateRightHandPosOffset (>=50 positive,
            // <50 negative), so keep 50 as the neutral centre and widen symmetrically.
            rightHandVerticalAngleSlider.Text.localizationKey = "Right hand vertical rot offset:";
            MakeSliderSmooth(rightHandVerticalAngleSlider, -30f, 130f, () => settings.rightHandVerticalAngle,
                (v) => settings.rightHandVerticalAngle = v, (v) => Remap(v, -30f, 130f, -1f, 1f).ToString("0.0"));


            rightHandHorizontalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            rightHandHorizontalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            rightHandHorizontalAngleSlider.Text.localizationKey = "Right hand horizontal rot offset:";
            MakeSliderSmooth(rightHandHorizontalAngleSlider, -90f, 90f, () => settings.rightHandHorizontalAngle,
                (v) => settings.rightHandHorizontalAngle = v, (v) => Remap(v, -90f, 90f, -1f, 1f).ToString("0.0"));

            leftHandVerticalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            leftHandVerticalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            leftHandVerticalAngleSlider.Text.localizationKey = "Left hand vertical rot offset:";
            MakeSliderSmooth(leftHandVerticalAngleSlider, -30f, 130f, () => settings.leftHandVerticalAngle,
                (v) => settings.leftHandVerticalAngle = v, (v) => Remap(v, -30f, 130f, -1f, 1f).ToString("0.0"));

            leftHandHorizontalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            leftHandHorizontalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            leftHandHorizontalAngleSlider.Text.localizationKey = "Left hand horizontal rot offset:";
            MakeSliderSmooth(leftHandHorizontalAngleSlider, -90f, 90f, () => settings.leftHandHorizontalAngle,
                (v) => settings.leftHandHorizontalAngle = v, (v) => Remap(v, -90f, 90f, -1f, 1f).ToString("0.0"));

            handPosOffsetSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            handPosOffsetSlider.BindIndexTo(
                settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume,
                settingsUi._soundSettingsScreen.readOnlyCollection_0,
                (sliderIndex) =>
                {
                    float normalizedValue = sliderIndex / 100f; // Convert to 0-1 range
                    float displayValue = (normalizedValue * 0.20f) - 0.10f; // Map to -0.10 to 0.10
                    return displayValue.ToString("F2"); // Format to 2 decimal places
                }
            );
            handPosOffsetSlider.Text.localizationKey = "Hand position offset (up/down):";
            MakeSliderSmooth(handPosOffsetSlider, -0.25f, 0.25f, () => settings.handPosOffset,
                (v) => settings.handPosOffset = v, (v) => Remap(v, -0.25f, 0.25f, -1f, 1f).ToString("0.0"));

            emptySpacingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            emptySpacingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            emptySpacingSlider.Slider.gameObject.SetActive(false);
            emptySpacingSlider.Text.gameObject.SetActive(false);

            disableRunAnimationToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            disableRunAnimationToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disableRunAnimationToggle.Toggle.action_0 = SetDisableRunAnim;
            disableRunAnimationToggle.Text.localizationKey = "Disable Run Animation";
            disableRunAnimationToggle.Toggle.UpdateValue(settings.disableRunAnimation);
            

            /*
            sharpenToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            sharpenToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            sharpenToggle.Toggle.action_0 = SetSharpen;
            sharpenToggle.Text.localizationKey = "Enable Sharpen ";
            sharpenToggle.Toggle.UpdateValue(settings.enableSharpen);
            */
            useVRKeyboardToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            useVRKeyboardToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            useVRKeyboardToggle.Toggle.action_0 = SetUseVRKeyboard;
            useVRKeyboardToggle.Text.localizationKey = "Use In-Game VR Keyboard";
            useVRKeyboardToggle.Toggle.UpdateValue(settings.useVRKeyboard);

            // Vive Wand-specific settings — only shown when Vive controllers are detected
            if (VRGlobals.vrControllerType == "vive")
            {
                viveWandCrouchThresholdSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
                viveWandCrouchThresholdSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, new ReadOnlyCollection<float>(ViveWandCrouchThresholdValues),
                    (x) => x.ToString("F1"));
                viveWandCrouchThresholdSlider.Text.localizationKey = "Crouch/Prone Trackpad Threshold (Vive):";
                MakeSliderSmooth(viveWandCrouchThresholdSlider, 0.2f, 0.95f, () => settings.viveWandCrouchTrackpadThreshold,
                    (v) => settings.viveWandCrouchTrackpadThreshold = v, (v) => Remap(v, 0.2f, 0.95f, 0f, 10f).ToString("0"));

                viveWandVaultHoldTimeSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
                viveWandVaultHoldTimeSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, new ReadOnlyCollection<float>(ViveWandVaultTimeValues),
                    (x) => x.ToString("F1") + "s");
                viveWandVaultHoldTimeSlider.Text.localizationKey = "Vault/Jump Hold Time (Vive):";
                MakeSliderSmooth(viveWandVaultHoldTimeSlider, 0.05f, 1.5f, () => settings.viveWandVaultHoldTime,
                    (v) => settings.viveWandVaultHoldTime = v, (v) => v.ToString("0.0") + "s");
            }

            /*
            occCullingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            occCullingToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            occCullingToggle.Toggle.action_0 = SetOccCulling;
            occCullingToggle.Text.localizationKey = "Disable Occlusion Culling ";
            occCullingToggle.Toggle.UpdateValue(settings.disableOccCulling);

            frusCullingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            frusCullingToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            frusCullingToggle.Toggle.action_0 = SetFrusCulling;
            frusCullingToggle.Text.localizationKey = "Disable Frustum Culling ";
            frusCullingToggle.Toggle.UpdateValue(settings.disableFrusCulling);
            */
            _vrSettingsScroll = SetupScrollbar(vrSettings);
            vrSettingsObject = newSoundSettings.gameObject;
            UnityEngine.Object.Destroy(newSoundSettings);
            vrSettingsObject.active = false;

            // ============================ VR More tab (graphics/interactions ============================
            GameObject vrGraphics = UnityEngine.Object.Instantiate(settingsUi._soundSettingsScreen.gameObject);
            vrGraphics.transform.parent = settingsUi._soundSettingsScreen.transform.parent;
            vrGraphics.transform.localScale = Vector3.one;
            vrGraphics.transform.localRotation = Quaternion.identity;
            vrGraphics.transform.localPosition = new Vector3(0, -71.5f, 0);
            vrGraphics.transform.GetChild(0).localPosition = new Vector3(10, 433.5f, 0);

            SoundSettingsTab gSoundSettings = vrGraphics.GetComponent<SoundSettingsTab>();
            Transform gPanel = gSoundSettings._slidersSection;
            for (int i = 0; i < gPanel.childCount; i++)
                UnityEngine.Object.Destroy(gPanel.GetChild(i).gameObject);
            GameObject.Destroy(gSoundSettings._togglesSection.gameObject);
            GameObject.Destroy(gSoundSettings._slidersSection.parent.FindChild("VoipSection").gameObject);

            // Scope render resolution
            ReadOnlyCollection<string> opticResOpts = new ReadOnlyCollection<string>(new List<string> { "256", "512", "768", "1024 (Vanilla)" });
            opticResolutionDropDown = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, gPanel);
            opticResolutionDropDown.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, opticResOpts, (x) => !(x == "512") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            opticResolutionDropDown.Text.localizationKey = "Scope Render Resolution: ";
            opticResolutionDropDown.DropDown.SetLabelText(OpticResLabel(settings.opticRenderResolution));
            opticResolutionDropDown.DropDown.gclass1626_0.Action_0 = ChangeOpticResolution;

            // Shadows
            ReadOnlyCollection<string> gShadowOpts = new ReadOnlyCollection<string>(new List<string> { "Normal", "Disable Near Shadows", "Distant Shadows (FPS hit)" });
            shadowOptsToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, gPanel);
            shadowOptsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, gShadowOpts, (x) => !(x == "Normal") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            shadowOptsToggle.Text.localizationKey = "Shadows Settings: ";
            if (settings.shadowOpt == ShadowOpt.Normal)
                shadowOptsToggle.DropDown.SetLabelText("Normal");
            else if (settings.shadowOpt == ShadowOpt.DisableNearShadows)
                shadowOptsToggle.DropDown.SetLabelText("Disable Near Shadows");
            else
                shadowOptsToggle.DropDown.SetLabelText("Distant Shadows (FPS hit)");
            shadowOptsToggle.DropDown.gclass1626_0.Action_0 = ChangeShadowOpts;

            lodBiasSlider = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, gPanel);
            lodBiasSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            lodBiasSlider.Text.localizationKey = "LOD Bias Factor (FPS Hit):";
            MakeSliderSmooth(lodBiasSlider, 0.5f, 2f, () => settings.lodBias,
                (v) => settings.lodBias = v, (v) => v.ToString("0.00"));

            emptySpacingSlider = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, gPanel);
            emptySpacingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            emptySpacingSlider.Slider.gameObject.SetActive(false);
            emptySpacingSlider.Text.gameObject.SetActive(false);

            // Prism effects
            /*
            disablePrismEffectsToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            disablePrismEffectsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disablePrismEffectsToggle.Toggle.action_0 = SetDisablePrismEffects;
            disablePrismEffectsToggle.Text.localizationKey = "Disable Prism Effects";
            disablePrismEffectsToggle.Toggle.UpdateValue(settings.disablePrismEffects);
            */
            // Fog
            disableFogToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            disableFogToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disableFogToggle.Toggle.action_0 = SetDisableFog;
            disableFogToggle.Text.localizationKey = "Disable Fog - Worse aliasing if enabled";
            disableFogToggle.Toggle.UpdateValue(settings.disablePrismFog);

            // Hide arms
            hideArmsToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            hideArmsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            hideArmsToggle.Toggle.action_0 = SetHideArms;
            hideArmsToggle.Text.localizationKey = "Hide Arms";
            hideArmsToggle.Toggle.UpdateValue(settings.hideArms);

            // Hide legs
            hideLegsToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            hideLegsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            hideLegsToggle.Toggle.action_0 = SetHideLegs;
            hideLegsToggle.Text.localizationKey = "Hide Legs";
            hideLegsToggle.Toggle.UpdateValue(settings.hideLegs);

            // Manual eating
            manualEatingToggle = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            manualEatingToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            manualEatingToggle.Toggle.action_0 = SetManualEating;
            manualEatingToggle.Text.localizationKey = "Manual Eating";
            manualEatingToggle.Toggle.UpdateValue(settings.manualEating);

            // Disable mouse input
            disableMouseInput = gSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, gPanel);
            disableMouseInput.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disableMouseInput.Toggle.action_0 = SetDisableMouseInput;
            disableMouseInput.Text.localizationKey = "Disable Mouse Input";
            disableMouseInput.Toggle.UpdateValue(settings.disableMouseInput);

            _vrGraphicsScroll = SetupScrollbar(vrGraphics);
            vrGraphicsObject = gSoundSettings.gameObject;
            UnityEngine.Object.Destroy(gSoundSettings);
            vrGraphicsObject.active = false;
        }

        private static ScrollRect _vrSettingsScroll;
        private static ScrollRect _vrGraphicsScroll;

        private static ScrollRect SetupScrollbar(GameObject vrSettings)
        {
            const float scrollbarWidth = 10f;
            const float reservedVertical = 200f;  // canvas space taken by header + tabs above the panel; tune to taste

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

        private static void ChangeLeftStickDriftSensitivity(int sensitivity)
        {
            settings.leftStickDriftSensitivity = Mathf.Clamp((float)sensitivity / 10, 0.1f, 0.9f);
        }
        private static void ChangeRightStickDriftSensitivity(int sensitivity)
        {
            float f = (float)sensitivity / 10;

            settings.rightStickDriftSensitivity = Mathf.Clamp(f, 0.1f, 0.9f);

        }
        private static void ChangeMovementMode(int mode)
        {
            settings.movementType = (MovementMode)mode;
            if (settings.movementType == MovementMode.HeadBased)
                movementMethod.DropDown.SetLabelText("Head-based movement");
            else if (settings.movementType == MovementMode.WandBased)
                movementMethod.DropDown.SetLabelText("Wand-based movement");
            else
                movementMethod.DropDown.SetLabelText("JoyStick only movement");
        }

        public static void ShowVRSettings()
        {
            if (settingsUi != null)
            {
                settingsUi.settingsTab_0.gameObject.active = false;
                if (vrGraphicsObject)
                    vrGraphicsObject.active = false;
                if (vrSettingsObject)
                {
                    vrSettingsObject.active = true;
                    if (_vrSettingsScroll != null)
                        _vrSettingsScroll.verticalNormalizedPosition = 1f;
                }
            }
        }

        public static void ShowVRGraphicsSettings()
        {
            if (settingsUi != null)
            {
                settingsUi.settingsTab_0.gameObject.active = false;
                if (vrSettingsObject)
                    vrSettingsObject.active = false;
                if (vrGraphicsObject)
                {
                    vrGraphicsObject.active = true;
                    if (_vrGraphicsScroll != null)
                        _vrGraphicsScroll.verticalNormalizedPosition = 1f;
                }
            }
        }

        public static void CloseVRSettings()
        {
            if (vrSettingsObject)
                vrSettingsObject.active = false;
            if (vrGraphicsObject)
                vrGraphicsObject.active = false;
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
            if (settings.leftHandedMode)
                return settings.leftHandVerticalAngle;
            else
                return settings.rightHandVerticalAngle;
        }
        public static float GetSecondaryHandVertOffset()
        {
            if (settings.leftHandedMode)
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

        public static float GetRightHandVerticalOffset()
        {
            return settings.rightHandVerticalAngle;
        }
        public static float GetHandPosOffset()
        {
            return settings.handPosOffset;
        }

        public static bool GetSnapToGun()
        {
            return settings.snapToGun;
        }
        private static void SetSnapToGun(bool turnOn)
        {
            settings.snapToGun = turnOn;
        }
        private static void SetSeatedMode(bool turnOn)
        {        
            settings.seatedMode = turnOn;
        }
        public static bool GetSeatedMode()
        {
            return settings.seatedMode;
        }
        public static bool GetSupportGunHoldToggle()
        {
            return settings.snapToGun;
        }
        private static void SetSupportGunHoldToggle(bool turnOn)
        {
            settings.supportGunHoldToggle = turnOn;
        }

        private static void SetSharpen(bool on)
        {
            if (on && VRGlobals.VRCam)
            {
                CC_Sharpen sharpen = VRGlobals.VRCam.GetComponent<CC_Sharpen>();
                if (sharpen != null)
                    sharpen.enabled = true;
            }
            else if (!on && VRGlobals.VRCam)
            {
                CC_Sharpen sharpen = VRGlobals.VRCam.GetComponent<CC_Sharpen>();
                if (sharpen != null)
                    sharpen.enabled = false;
            }
            settings.enableSharpen = on;
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

        private static void SetLeftHandedMode(bool toggle)
        {
            settings.leftHandedMode = toggle;
            if (toggle)
            {
                if (VRGlobals.vrPlayer)
                    VRGlobals.vrPlayer.LeftHandedMode();
                if (VRGlobals.menuVRManager)
                    VRGlobals.menuVRManager.LeftHandedMode();
            }
            else {
                if (VRGlobals.vrPlayer)
                    VRGlobals.vrPlayer.RightHandedMode();
                if (VRGlobals.menuVRManager)
                    VRGlobals.menuVRManager.RightHandedMode();
            }
        }

        public static bool GetLeftHandedMode()
        {
            return settings.leftHandedMode;
        }

        public static bool GetHideArms()
        {
            return settings.hideArms;
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
        private static void SetManualEating(bool turnOn)
        {
            settings.manualEating = turnOn;
        }
        public static bool GetManualEating()
        {
            return settings.manualEating;
        }

        public static bool GetDisableRunAnim()
        {
            return settings.disableRunAnimation;
        }

        private static void SetDisableRunAnim(bool turnOn)
        {
            settings.disableRunAnimation = turnOn;
        }
        public static bool GetDisablePrismEffects()
        {
            return settings.disablePrismEffects;
        }

        private static void SetDisablePrismEffects(bool turnOff)
        {
            settings.disablePrismEffects = turnOff;
            if (VRGlobals.VRCam)
            {
                var prismEffects = VRGlobals.VRCam.GetComponent<PrismEffects>();
                if (prismEffects != null)
                {
                    prismEffects.enabled = !turnOff;
                }
            }
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

        private static void SetOccCulling(bool turnOff)
        {
            settings.disableOccCulling = turnOff;
        }

        public static bool GetFrusCulling()
        {
            return settings.disableFrusCulling;
        }

        private static void SetFrusCulling(bool turnOff)
        {
            settings.disableFrusCulling = turnOff;
        }

        private static void ChangeShadowOpts(int mode)
        {
            settings.shadowOpt = (ShadowOpt)mode;
            if (settings.shadowOpt == ShadowOpt.Normal) { 
                shadowOptsToggle.DropDown.SetLabelText("Normal");
            }
            else if (settings.shadowOpt == ShadowOpt.DisableNearShadows)
                shadowOptsToggle.DropDown.SetLabelText("Disable Near Shadows");
            else
                shadowOptsToggle.DropDown.SetLabelText("Distant Shadows (FPS hit)");

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
            int res = (index >= 0 && index < opts.Length) ? opts[index] : 512;
            settings.opticRenderResolution = res;
            if (opticResolutionDropDown != null)
                opticResolutionDropDown.DropDown.SetLabelText(OpticResLabel(res));

            // Live re-apply if a scope camera already exists (otherwise it takes effect on next scope setup).
            // SetResolution gets intercepted by WeaponPatches.ScaleOpticResolution which reads this setting.
            try
            {
                if (CameraClass.Exist && CameraClass.Instance.OpticCameraManager != null && CameraClass.Instance.OpticCameraManager.Camera != null)
                    CameraClass.Instance.OpticCameraManager.SetResolution(res);
            }
            catch { }
        }


        private static void ChangeRotationType(int mode)
        {
            settings.rotationType = (RotationMode)mode;
            if (settings.rotationType == RotationMode.Smooth)
                rotationMethod.DropDown.SetLabelText("Smooth Turn");
            else
                rotationMethod.DropDown.SetLabelText("Snap Turn");
        }
        public static RotationMode GetRotationType()
        {
            return settings.rotationType;
        }

        private static void ChangeSnapAmount(int mode)
        {
            if (mode == 0)
            {
                settings.snapTurnAmount = SnapTurnAmount.thirty;
                snapTurnAmountDropDown.DropDown.SetLabelText("30");
            }
            else if (mode == 1)
            {
                settings.snapTurnAmount = SnapTurnAmount.fourtyFive;
                snapTurnAmountDropDown.DropDown.SetLabelText("45");
            }
            else { 
                settings.snapTurnAmount = SnapTurnAmount.ninety;
                snapTurnAmountDropDown.DropDown.SetLabelText("90");
            }
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
