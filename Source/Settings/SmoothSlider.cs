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
        public Func<float> getValue;          // reads the CURRENT stored setting (kept live by onChange)
        public Action<float> onChange;
        public Func<float, string> format;

        private UnityAction<float> listener;

        private void OnEnable()
        {
            Apply();
        }

        // Strips EFT's stepping, keeps the notch ticks as a track, drives our own float callback + label. Idempotent.
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

            // Reseed from the live stored value: Awake's wholeNumbers rounded the handle (0.10 -> 0). Set while no listener
            // is attached, so the reseed can't write a rounded value back over the setting.
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
