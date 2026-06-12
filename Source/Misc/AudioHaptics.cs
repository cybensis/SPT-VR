using System.Collections;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Misc
{
    // Audio -> controller haptics, LIVE: a read-only OnAudioFilterRead tap on the
    // AudioSource that's actually playing the eating sound publishes per-buffer RMS +
    // zero-crossing rate from the AUDIO THREAD; a main-thread pump converts those into
    // SteamVR haptic pulses (one Haptic.Execute per ~20ms window, both hands) so you
    // feel exactly what you hear — the lid roll's ratchet, each gulp, the holster thump.
    //
    // WHY a live tap: a clip-sampling envelope (AudioClip.GetData) was tried first and
    // is a DEAD END — EFT's foley is compressed/streaming (not DecompressOnLoad), GetData
    // is invalid there, and the synthetic fallback made every sound the same generic
    // rumble (user-confirmed "doesn't feel good"). The filter tap hears the DECODED
    // stream, so the clip's compression doesn't matter.
    //
    // Pooled-source safety: BetterAudio sources are pooled and reused. The pump renders
    // ONLY while the tracked BetterSource reports Playing AND its _lastPlayingClipName is
    // the clip we were told about (BetterSource.Play always sets both; OnStop nulls the
    // clip) — a source re-grabbed for someone else's audio goes silent immediately. The
    // tap component itself is disabled whenever it isn't ours.
    public static class AudioHaptics
    {
        public static bool enabled = true;
        public static float strength = 1f;        // master amplitude scale (0..~1.5)
        public static float windowSec = 0.02f;     // one haptic pulse per window
        public static float minAmp = 0.04f;        // skip windows quieter than this
        public static float minFreq = 60f;         // LRA band the audio frequency maps into
        public static float maxFreq = 300f;
        public static float peakDecay = 0.995f;    // auto-gain: running peak decay per window
        public static float freqSmoothing = 0.35f; // per-window lerp toward the measured texture

        private static HapticAudioTap tap;          // the filter component (on the pooled source GO)
        private static BetterSource trackedBs;      // the BetterSource the clip was played on
        private static string expectedClipName;     // what should be playing for us to render
        private static bool renderLeft, renderRight; // which controller(s) get the pulses
        private static Coroutine pump;

        // Called (main thread) whenever a clip is audibly played for us — re-points the
        // tap at the source actually used (the pool re-issues sources between clips),
        // picks the hand(s) to render on, and makes sure the pump is running.
        public static void OnClipPlayed(BetterSource bs, AudioClip clip, bool leftHand = true, bool rightHand = true)
        {
            if (!enabled || bs == null || bs.source1 == null || clip == null || VRGlobals.vrPlayer == null) return;
            renderLeft = leftHand;
            renderRight = rightHand;
            if (tap == null || tap.gameObject != bs.source1.gameObject)
            {
                if (tap != null) tap.enabled = false; // old pooled source — stop its callbacks
                tap = bs.source1.gameObject.GetComponent<HapticAudioTap>();
                if (tap == null) tap = bs.source1.gameObject.AddComponent<HapticAudioTap>();
            }
            tap.sampleRate = AudioSettings.outputSampleRate;
            tap.enabled = true;
            trackedBs = bs;
            expectedClipName = clip.name;
            if (pump == null) pump = VRGlobals.vrPlayer.StartCoroutine(Pump());
        }

        public static void Stop()
        {
            if (pump != null && VRGlobals.vrPlayer != null) VRGlobals.vrPlayer.StopCoroutine(pump);
            pump = null;
            if (tap != null) tap.enabled = false;
            tap = null; trackedBs = null; expectedClipName = null;
        }

        private static IEnumerator Pump()
        {
            var wait = new WaitForSeconds(windowSec);
            float peak = 0.02f;   // auto-gain reference (foley levels vary wildly)
            float freq = 140f;
            int lastBuffers = -1;
            while (true)
            {
                float amp = 0f;
                bool live = tap != null && trackedBs != null && expectedClipName != null
                    && trackedBs.PlayBackState == BetterSource.EPlayBackState.Playing
                    && trackedBs._lastPlayingClipName == expectedClipName;
                if (live)
                {
                    int b = tap.Buffers;
                    if (b != lastBuffers) // a fresh audio buffer landed since last window
                    {
                        lastBuffers = b;
                        float rms = tap.Rms;
                        peak = Mathf.Max(peak * peakDecay, rms, 0.02f);
                        amp = Mathf.Clamp01(rms / peak) * strength;
                        // The raw audio ZCR runs hundreds of Hz..kHz — LOG-map it into the
                        // LRA band so textures differentiate (a thuddy glug lands low, a
                        // crinkle lands high; linear clamping pegged everything at max).
                        float z = Mathf.Clamp(tap.ZcrHz, 50f, 8000f);
                        float n = Mathf.Log(z / 50f) / Mathf.Log(8000f / 50f);
                        freq = Mathf.Lerp(freq, Mathf.Lerp(minFreq, maxFreq, n), freqSmoothing);
                    }
                }
                if (amp >= minAmp)
                {
                    // Pulses run 1.5x the window so they bridge to the next one — reads as a
                    // continuous buzz, not ticks.
                    if (renderLeft) SteamVR_Actions._default.Haptic.Execute(0f, windowSec * 1.5f, freq, amp, SteamVR_Input_Sources.LeftHand);
                    if (renderRight) SteamVR_Actions._default.Haptic.Execute(0f, windowSec * 1.5f, freq, amp, SteamVR_Input_Sources.RightHand);
                }
                yield return wait;
            }
        }

        // The audio-thread side. OnAudioFilterRead: math ONLY, read-only on the buffer,
        // no Unity API, no allocations — it runs inside the audio pipeline.
        private class HapticAudioTap : MonoBehaviour
        {
            public volatile float Rms;
            public volatile float ZcrHz;
            public volatile int Buffers;  // bumped per callback (the pump's freshness check)
            public int sampleRate;        // set from the main thread on attach

            private void OnAudioFilterRead(float[] data, int channels)
            {
                int frames = channels > 0 ? data.Length / channels : 0;
                if (frames == 0 || sampleRate <= 0) return;
                float sum = 0f; int crossings = 0; float prev = 0f;
                for (int i = 0; i < frames; i++)
                {
                    float v = 0f;
                    int idx = i * channels;
                    for (int c = 0; c < channels; c++) v += data[idx + c];
                    v /= channels;
                    sum += v * v;
                    if ((v > 0f && prev <= 0f) || (v < 0f && prev >= 0f)) crossings++;
                    prev = v;
                }
                Rms = Mathf.Sqrt(sum / frames);
                ZcrHz = crossings * sampleRate / (2f * frames);
                Buffers++;
            }
        }
    }
}
