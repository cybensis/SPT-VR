using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace TarkovVR.Source.Settings
{
    // Makes one of EFT's SelectSlider controls behave as a smooth, continuous slider.
    //
    // SelectSlider.Awake() sets the inner Slider's wholeNumbers = true and adds an int-flooring onValueChanged listener.
    // The VR panels are cloned from the sound-settings tab while it is inactive, so that Awake only runs the first time the
    // VR tab opens, after initVrSettings(), and would undo a one-time setup. OnEnable runs after Awake on every show, so the
    // config is re-applied there.
    public class SmoothSlider : MonoBehaviour
    {
        public Slider slider;                 // the real UnityEngine.UI.Slider (SelectSlider._slider)
        public RectTransform notchContainer;  // SelectSlider._notchContainer (the discrete tick marks)
        public TextMeshProUGUI valueText;     // SelectSlider._valueText (the live number label)
        public float min = 0f;
        public float max = 1f;
        public Func<float> getValue;          // reads the current stored setting (onChange keeps it up to date)
        public Action<float> onChange;
        public Func<float, string> format;

        private UnityAction<float> listener;

        private void OnEnable()
        {
            Apply();
        }

        // Removes EFT's stepping, keeps the notch ticks as the track, and drives our own float callback and label. Safe to repeat.
        public void Apply()
        {
            if (slider == null || onChange == null || getValue == null)
                return;

            // Drops SelectSlider.Awake()'s int-flooring listener
            slider.onValueChanged.RemoveAllListeners();
            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;

            // The notch ticks are the slider's only visible track; the handle moves continuously over them
            if (notchContainer != null)
                notchContainer.gameObject.SetActive(true);

            // Reset the handle from the stored value, because Awake's wholeNumbers rounded it (0.10 -> 0). This runs while no
            // listener is attached, so it can't write a rounded value back over the setting.
            slider.value = Mathf.Clamp(getValue(), min, max);
            if (valueText != null)
                valueText.text = format(slider.value);

            if (listener == null)
            {
                listener = (v) =>
                {
                    onChange(v);
                    if (valueText != null)
                    {
                        valueText.text = format(v);
                        valueText.SetAllDirty();
                    }
                };
            }
            slider.onValueChanged.AddListener(listener);
        }
    }
}
