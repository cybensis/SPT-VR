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

namespace TarkovVR.Source.Settings
{
    public static class VRSettings
    {
        public enum MovementMode
        {
            HeadBased = 0,
            WandBased = 1,
        }
        public class ModSettings
        {
            public int rotationSensitivity { get; set; }
            public float leftStickDriftSensitivity { get; set; }
            public float rightStickDriftSensitivity { get; set; }
            public MovementMode movementType { get; set; }
            public bool weaponAimSmoothing { get; set; }
            public int smoothingSensitivity { get; set; }

            public bool scopeAimSmoothing { get; set; }


            public ModSettings()
            {
                rotationSensitivity = 4;
                leftStickDriftSensitivity = 0.1f;
                rightStickDriftSensitivity = 0.1f;
                movementType = MovementMode.HeadBased;
                weaponAimSmoothing = false;
                scopeAimSmoothing = true;
                smoothingSensitivity = 1;
            }
            // Add more settings as needed
        }

        private static SettingsScreen settingsUi;
        public static GameObject vrSettingsObject;

        private static SettingSelectSlider sensitivitySlider;
        private static SettingSelectSlider leftStickDriftSlider;
        private static SettingSelectSlider rightStickDriftSlider;
        private static SettingDropDown movementMethod;
        private static SettingToggle aimSmoothingToggle;
        private static SettingSelectSlider aimSmoothingSlider;
        private static SettingToggle scopeSmoothingToggle;


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
            sensitivitySlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            sensitivitySlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            sensitivitySlider.Slider.action_0 = ChangeRotationSensitivity;
            sensitivitySlider.Text.localizationKey = "Rotation Sensitivity:";
            sensitivitySlider.Slider.UpdateValue(settings.rotationSensitivity);
            sensitivitySlider.transform.localPosition = new Vector3(0, -20, 0);

            leftStickDriftSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            leftStickDriftSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            leftStickDriftSlider.Slider.action_0 = ChangeLeftStickDriftSensitivity;
            leftStickDriftSlider.Text.localizationKey = "Left Stick Drift Sensitivity: ";
            leftStickDriftSlider.Slider.UpdateValue((int) settings.leftStickDriftSensitivity * 10);
            leftStickDriftSlider.transform.localPosition = new Vector3(0, -70, 0);

            rightStickDriftSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            rightStickDriftSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            rightStickDriftSlider.Slider.action_0 = ChangeRightStickDriftSensitivity;
            rightStickDriftSlider.Text.localizationKey = "Right Stick Drift Sensitivity: ";
            rightStickDriftSlider.Slider.UpdateValue((int) settings.rightStickDriftSensitivity * 10);
            rightStickDriftSlider.transform.localPosition = new Vector3(0, -120, 0);

            ReadOnlyCollection<string> movementMethods = new ReadOnlyCollection<string>(new List<string> { "Head-based movement", "Wand-based movement" });
            movementMethod = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._dropDownPrefab, slidersPanel);
            movementMethod.BindTo(settingsUi._soundSettingsScreen.gclass957_0.VoipDevice, movementMethods, (x) => !(x == "Head-based movement") && !(x == "Settings/UnavailablePressType") ? x : x.Localized());
            movementMethod.Text.localizationKey = "Movement method: ";
            if (settings.movementType == MovementMode.HeadBased)
                movementMethod.DropDown.SetLabelText("Head-based movement");
            else
                movementMethod.DropDown.SetLabelText("Wand-based movement");

            movementMethod.DropDown.onEventClass.action_0 = ChangeMovementMode;
            movementMethod.transform.localPosition = new Vector3(300, -250, 0);


            scopeSmoothingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            scopeSmoothingToggle.BindTo(settingsUi._soundSettingsScreen.gclass957_0.MusicOnRaidEnd);
            scopeSmoothingToggle.Toggle.action_0 = ToggleScopeSmoothingSensitivity;
            scopeSmoothingToggle.Text.localizationKey = "Toggle Scope Aim Smoothing ";
            scopeSmoothingToggle.Toggle.UpdateValue(settings.scopeAimSmoothing);
            scopeSmoothingToggle.transform.localPosition = new Vector3(0, -320, 0);

            aimSmoothingToggle = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._togglePrefab, slidersPanel);
            aimSmoothingToggle.BindTo(settingsUi._soundSettingsScreen.gclass957_0.MusicOnRaidEnd);
            aimSmoothingToggle.Toggle.action_0 = ToggleSmoothingSensitivity;
            aimSmoothingToggle.Text.localizationKey = "Toggle Weapon Aim Smoothing ";
            aimSmoothingToggle.Toggle.UpdateValue(settings.weaponAimSmoothing);
            aimSmoothingToggle.transform.localPosition = new Vector3(0, -370, 0);

            aimSmoothingSlider = newSoundSettings.CreateControl(settingsUi._soundSettingsScreen._selectSliderPrefab, slidersPanel);
            aimSmoothingSlider.BindIndexTo(settingsUi._soundSettingsScreen.gclass957_0.OverallVolume, settingsUi._soundSettingsScreen.readOnlyCollection_0, (x) => x.ToString());
            aimSmoothingSlider.Slider.action_0 = SetSmoothingSensitivity;
            aimSmoothingSlider.Text.localizationKey = "Aim Smoothing Sensitivity:";
            aimSmoothingSlider.Slider.UpdateValue(settings.smoothingSensitivity);
            aimSmoothingSlider.transform.localPosition = new Vector3(0, -440, 0);



            vrSettingsObject = newSoundSettings.gameObject;
            UnityEngine.Object.Destroy(newSoundSettings);
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
            else
                movementMethod.DropDown.SetLabelText("Wand-based movement");
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
        private static void ToggleSmoothingSensitivity(bool turnOn)
        {
            settings.weaponAimSmoothing = turnOn;
        }
        public static bool SmoothWeaponAim()
        {
            return settings.weaponAimSmoothing;
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
    }
}
