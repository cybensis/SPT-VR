using EFT.UI;
using EFT.UI.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;
using Newtonsoft.Json;
using static HBAO_Core;
using EFT.UI.Ragfair;
using TarkovVR.Patches.Visuals;
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
            public int rotationSensitivity { get; set; }
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
            public int smoothingSensitivity { get; set; }
            public int scopeSmoothingSensitivity { get; set; }
            public float variableZoomSensitivity { get; set; }
            public bool scopeAimSmoothing { get; set; }
            public bool enableSharpen { get; set; }
            public int rightHandVerticalAngle { get; set; }
            public int rightHandHorizontalAngle { get; set; }
            public int leftHandHorizontalAngle { get; set; }
            public int leftHandVerticalAngle { get; set; }
            public float handPosOffset { get; set; }
            public bool weaponWeight { get; set; }
            public bool weaponInertia { get; set; }
            public bool hideArms { get; set; }
            public bool hideLegs { get; set; }
            public bool disableRunAnimation { get; set; }
            public bool disablePrismEffects { get; set; }
            public bool disableFog { get; set; }
            public ShadowOpt shadowOpt { get; set; }
            public bool disableOccCulling { get; set; }
            public bool disableFrusCulling { get; set; }
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
                hideArms = false;
                hideLegs = false;
                disableRunAnimation = true;
                disablePrismEffects = false;
                disableFog = false;               
                shadowOpt = ShadowOpt.IncreaseLighting;
                disableOccCulling = false;
                disableFrusCulling = false;
            }
            // Add more settings as needed
        }

        private static SettingsScreen settingsUi;
        public static GameObject vrSettingsObject;

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
        private static SettingSelectSlider aimSmoothingSlider;
        private static SettingSelectSlider scopeSmoothingSlider;
        private static SettingToggle scopeSmoothingToggle;
        private static SettingSelectSlider rightHandVerticalAngleSlider;
        private static SettingSelectSlider rightHandHorizontalAngleSlider;
        private static SettingSelectSlider leftHandHorizontalAngleSlider;
        private static SettingSelectSlider leftHandVerticalAngleSlider;
        private static SettingSelectSlider handPosOffsetSlider;
        private static SettingSelectSlider variableZoomSensitivitySlider;

        // Graphics Settings
        private static SettingToggle sharpenToggle;
        private static SettingToggle occCullingToggle;
        private static SettingToggle frusCullingToggle;
        private static SettingDropDown shadowOptsToggle;
        private static SettingToggle disablePrismEffectsToggle;
        private static SettingToggle disableFogToggle;

        // Other settings
        private static SettingToggle hideArmsToggle;
        private static SettingToggle hideLegsToggle;
        private static SettingToggle disableRunAnimationToggle;
        private static SettingToggle seatedModeToggle;



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
            sensitivitySlider.Slider.action_0 = ChangeRotationSensitivity;
            sensitivitySlider.Text.localizationKey = "Rotation Sensitivity:";
            sensitivitySlider.Slider.UpdateValue(settings.rotationSensitivity);
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


            /*
            emptySpacingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            emptySpacingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            emptySpacingSlider.Slider.gameObject.SetActive(false);
            emptySpacingSlider.Text.gameObject.SetActive(false);
            */

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

            aimSmoothingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            aimSmoothingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            aimSmoothingSlider.Slider.action_0 = SetSmoothingSensitivity;
            aimSmoothingSlider.Text.localizationKey = "Weapon Smoothing Strength:";
            aimSmoothingSlider.Slider.UpdateValue(11 - (settings.smoothingSensitivity / 2));

            scopeSmoothingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            scopeSmoothingSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            scopeSmoothingSlider.Slider.action_0 = SetScopeSensitivity;
            scopeSmoothingSlider.Text.localizationKey = "Hold Breath Sensitivity:";
            scopeSmoothingSlider.Slider.UpdateValue(settings.scopeSmoothingSensitivity);

            variableZoomSensitivitySlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            variableZoomSensitivitySlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            variableZoomSensitivitySlider.Slider.action_0 = SetVariableZoomSensitivity;
            variableZoomSensitivitySlider.Text.localizationKey = "Variable Zoom Sensitivity:";
            variableZoomSensitivitySlider.Slider.UpdateValue((int)(settings.variableZoomSensitivity * 10));

            rightHandVerticalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            rightHandVerticalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            rightHandVerticalAngleSlider.Slider.action_0 = SetRightHandVerticalOffset;
            rightHandVerticalAngleSlider.Text.localizationKey = "Right hand vertical rot offset:";
            rightHandVerticalAngleSlider.Slider.UpdateValue(settings.rightHandVerticalAngle / 10);


            rightHandHorizontalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            rightHandHorizontalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            rightHandHorizontalAngleSlider.Slider.action_0 = SetRightHandHorizontalOffset;
            rightHandHorizontalAngleSlider.Text.localizationKey = "Right hand horizontal rot offset:";
            rightHandHorizontalAngleSlider.Slider.UpdateValue((50 - settings.rightHandHorizontalAngle) / 10);

            leftHandVerticalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            leftHandVerticalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            leftHandVerticalAngleSlider.Slider.action_0 = SetLeftHandVerticalOffset;
            leftHandVerticalAngleSlider.Text.localizationKey = "Left hand vertical rot offset:";
            leftHandVerticalAngleSlider.Slider.UpdateValue((50 - settings.leftHandHorizontalAngle) / 10);

            leftHandHorizontalAngleSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            leftHandHorizontalAngleSlider.BindIndexTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            leftHandHorizontalAngleSlider.Slider.action_0 = SetLeftHandHorizontalOffset;
            leftHandHorizontalAngleSlider.Text.localizationKey = "Left hand horizontal rot offset:";
            leftHandHorizontalAngleSlider.Slider.UpdateValue((50 - settings.leftHandHorizontalAngle) / 10);

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
            handPosOffsetSlider.Slider.action_0 = SetHandPosOffset;
            handPosOffsetSlider.Text.localizationKey = "Hand position offset (up/down):";
            int sliderValue;
            if (settings.handPosOffset <= -0.10f)
                sliderValue = 1;
            else if (settings.handPosOffset >= 0.10f)
                sliderValue = 10;
            else
            {
                float normalizedValue = (settings.handPosOffset + 0.10f) / 0.20f;
                sliderValue = Mathf.RoundToInt(normalizedValue * 9f + 1f);
            }
            handPosOffsetSlider.Slider.UpdateValue(sliderValue);


            hideArmsToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            hideArmsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            hideArmsToggle.Toggle.action_0 = SetHideArms;
            hideArmsToggle.Text.localizationKey = "Hide Arms";
            hideArmsToggle.Toggle.UpdateValue(settings.hideArms);

            hideLegsToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            hideLegsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            hideLegsToggle.Toggle.action_0 = SetHideLegs;
            hideLegsToggle.Text.localizationKey = "Hide Legs";
            hideLegsToggle.Toggle.UpdateValue(settings.hideLegs);

            disableRunAnimationToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            disableRunAnimationToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disableRunAnimationToggle.Toggle.action_0 = SetDisableRunAnim;
            disableRunAnimationToggle.Text.localizationKey = "Disable Run Animation";
            disableRunAnimationToggle.Toggle.UpdateValue(settings.disableRunAnimation);

            disablePrismEffectsToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            disablePrismEffectsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disablePrismEffectsToggle.Toggle.action_0 = SetDisablePrismEffects;
            disablePrismEffectsToggle.Text.localizationKey = "Disable Prism Effects";
            disablePrismEffectsToggle.Toggle.UpdateValue(settings.disablePrismEffects);

            disableFogToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            disableFogToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            disableFogToggle.Toggle.action_0 = SetDisableFog;
            disableFogToggle.Text.localizationKey = "Disable Fog";
            disableFogToggle.Toggle.UpdateValue(settings.disableFog);
            /*
            emptySpacingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            emptySpacingSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass1050_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            emptySpacingSlider.Slider.gameObject.SetActive(false);
            emptySpacingSlider.Text.gameObject.SetActive(false);
            */
            ReadOnlyCollection<string> shadowOpts = new ReadOnlyCollection<string>(new List<string> { "Normal", "Disable Near Shadows", "Distant Shadows (FPS hit)" });
            shadowOptsToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, slidersPanel);
            shadowOptsToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.VoipDevice, shadowOpts, (x) => !(x == "Normal") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            shadowOptsToggle.Text.localizationKey = "Shadows Settings: ";
            if (settings.shadowOpt == ShadowOpt.Normal)
                shadowOptsToggle.DropDown.SetLabelText("Normal");
            else if (settings.shadowOpt == ShadowOpt.DisableNearShadows)
                shadowOptsToggle.DropDown.SetLabelText("Disable Near Shadows");
            else
                shadowOptsToggle.DropDown.SetLabelText("Distant Shadows (FPS hit)");

            shadowOptsToggle.DropDown.gclass1626_0.Action_0 = ChangeShadowOpts;

            
            sharpenToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            sharpenToggle.BindTo(settingsUi._soundSettingsScreen.soundSettingsControllerClass.MusicOnRaidEnd);
            sharpenToggle.Toggle.action_0 = SetSharpen;
            sharpenToggle.Text.localizationKey = "Enable Sharpen ";
            sharpenToggle.Toggle.UpdateValue(settings.enableSharpen);           
            
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
            //SetupScrollbar(vrSettings);

            vrSettingsObject = newSoundSettings.gameObject;
            UnityEngine.Object.Destroy(newSoundSettings);
        }

        private static void SetupScrollbar(GameObject vrSettings)
        {
            // Find or add ScrollRect component
            var scrollRect = vrSettings.GetComponentInChildren<ScrollRect>();

            if (scrollRect == null)
            {
                // If no ScrollRect exists, we need to find the content parent and set it up
                // The slidersPanel should be inside a viewport that has a ScrollRect
                Transform viewport = null;

                // Try to find the viewport by looking for a common structure
                // Usually it's: Settings -> Viewport -> Content (slidersPanel)
                var soundSettings = settingsUi._soundSettingsScreen;
                if (soundSettings != null)
                {
                    // Look for the parent of the sliders section which should be inside a viewport
                    Transform contentParent = soundSettings._slidersSection.parent;
                    if (contentParent != null)
                    {
                        viewport = contentParent.parent;
                        if (viewport != null)
                        {
                            scrollRect = viewport.GetComponent<ScrollRect>();
                        }
                    }
                }
            }

            // If we found or can access a ScrollRect, enable and configure it
            if (scrollRect != null)
            {
                scrollRect.enabled = true;
                scrollRect.vertical = true;
                scrollRect.horizontal = false;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                scrollRect.scrollSensitivity = 20f;

                // Make sure the scrollbar is visible if it exists
                if (scrollRect.verticalScrollbar != null)
                {
                    scrollRect.verticalScrollbar.gameObject.SetActive(true);
                }
            }
        }

        private static void ChangeRotationSensitivity(int sensitivity)
        {
            settings.rotationSensitivity = sensitivity;
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
                vrSettingsObject.active = true;

            }
        }

        public static void CloseVRSettings()
        {
            if (vrSettingsObject)
                vrSettingsObject.active = false;
        }

        public static int GetRotationSensitivity() { 
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

        public static int GetSmoothingSensitivity()
        {
            return settings.smoothingSensitivity;
        }
        private static void SetSmoothingSensitivity(int sensitivity)
        {
            settings.smoothingSensitivity = (11 - sensitivity) * 2;
        }

        public static int GetScopeSensitivity()
        {
            return settings.scopeSmoothingSensitivity;
        }
        private static void SetScopeSensitivity(int sensitivity)
        {
            settings.scopeSmoothingSensitivity = sensitivity;
        }
        private static void SetVariableZoomSensitivity(int sensitivity)
        {
            settings.variableZoomSensitivity = sensitivity / 10f;
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
        private static void SetLeftHandVerticalOffset(int offset) {
            settings.leftHandVerticalAngle = offset * 10;
        }
        public static int GetPrimaryHandVertOffset()
        {
            if (settings.leftHandedMode)
                return settings.leftHandVerticalAngle;
            else
                return settings.rightHandVerticalAngle;
        }
        public static int GetSecondaryHandVertOffset()
        {
            if (settings.leftHandedMode)
                return settings.rightHandVerticalAngle;
            else
                return settings.leftHandVerticalAngle;
        }

        public static int GetPrimaryHandHorOffset()
        {
            if (settings.leftHandedMode)
                return settings.leftHandHorizontalAngle;
            else
                return settings.rightHandHorizontalAngle;
        }
        public static int GetSecondaryHandHorOffset()
        {
            if (settings.leftHandedMode)
                return settings.rightHandHorizontalAngle;
            else
                return settings.leftHandHorizontalAngle;
        }

        public static int GetRightHandVerticalOffset()
        {
            return settings.rightHandVerticalAngle;
        }
        private static void SetRightHandVerticalOffset(int offset)
        {
            settings.rightHandVerticalAngle = offset * 10;
        }
        public static float GetHandPosOffset()
        {
            return settings.handPosOffset;
        }
        private static void SetHandPosOffset(int sliderValue)
        {
            sliderValue = Mathf.Clamp(sliderValue, 1, 10);

            float normalizedValue = (sliderValue - 1f) / 9f;
            float newValue = (normalizedValue * 0.20f) - 0.10f;

            settings.handPosOffset = newValue;
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


        public static int GetRightHandHorizontalOffset()
        {
            return settings.rightHandHorizontalAngle;
        }
        private static void SetRightHandHorizontalOffset(int offset)
        {
            settings.rightHandHorizontalAngle = 50 - (offset * 10);
        }

        public static int GetLeftHandHorizontalOffset()
        {
            return settings.leftHandHorizontalAngle;
        }
        private static void SetLeftHandHorizontalOffset(int offset)
        {
            settings.leftHandHorizontalAngle = 50 - (offset * 10);
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
            return settings.disableFog;
        }

        private static void SetDisableFog(bool turnOff)
        {
            settings.disableFog = turnOff;
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
    }
}
