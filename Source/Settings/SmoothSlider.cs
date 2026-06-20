using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace TarkovVR.Source.Settings
{
    // Makes one of EFT's SelectSlider controls behave as a smooth, continuous slider.
    //
    // Why a component instead of configuring the slider once: EFT's SelectSlider is discrete by
    // design - SelectSlider.Awake() sets the inner UnityEngine.UI.Slider's wholeNumbers = true and
    // adds an int-flooring onValueChanged listener (plus one notch per bound value). Our VR settings
    // panels are CLONED from the sound-settings tab while it is INACTIVE, so each cloned
    // SelectSlider.Awake() does not run until the VR tab is opened for the first time - i.e. AFTER
    // initVrSettings(). A one-shot reconfigure done at build time is therefore overwritten by that
    // deferred Awake (symptom: notches vanish but the handle still snaps in steps).
    //
    // OnEnable runs AFTER every Awake on the same activation, and fires every time the panel is
    // shown, so re-applying the continuous config here always wins.
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

        // Strip EFT's stepping (wholeNumbers + the flooring listener), keep the notch ticks as a
        // track, and drive our own continuous float callback + number label. Idempotent - runs on
        // every OnEnable (every time the panel is shown).
        public void Apply()
        {
            if (slider == null || onChange == null || getValue == null)
                return;

            // Drop SelectSlider.Awake()'s listener that floored the value to an int and re-snapped it.
            slider.onValueChanged.RemoveAllListeners();
            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;

            // Keep the notch ticks VISIBLE - they form the slider's visible track/line, which is the
            // only thing tying a handle to its label. The handle still moves continuously over them
            // (wholeNumbers = false); the ticks are now just a decorative scale.
            if (notchContainer != null)
                notchContainer.gameObject.SetActive(true);

            // ALWAYS reseed from the LIVE stored value (not a captured initial). Two reasons:
            //  - SelectSlider.Awake() sets wholeNumbers=true on first show, which rounds the handle to
            //    the nearest integer - a fraction like 0.10 (hand offset) collapses to 0. Reseeding
            //    here, after we set wholeNumbers=false, restores the real position.
            //  - getValue() returns the setting the user last dragged to (onChange keeps it current),
            //    so re-opening the panel shows the saved value instead of a stale/rounded one.
            // We set the value while NO listener is attached (removed above, re-added below) so this
            // reseed can't fire onChange and write a rounded value back over the setting.
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
