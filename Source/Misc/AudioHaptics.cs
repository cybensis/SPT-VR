using System.Collections;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Misc
{
    // Audio -> controller haptics, live: a read-only OnAudioFilterRead tap on the AudioSource playing the sound publishes
    // per-buffer RMS + zero-crossing rate from the audio thread; a main-thread pump turns those into haptics (one level per
    // ~20ms window, to each hand that feels it, through HapticMixer). Used for eating sounds and the gun's reload sounds
    // (VRReloadController.OnGunClipPlayed).
    //
    // AudioClip.GetData envelopes don't work: EFT's foley is compressed/streaming (not DecompressOnLoad), so GetData is
    // invalid. The filter tap hears the decoded stream, so compression doesn't matter.
    //
    // Pooled-source safety: BetterAudio sources are pooled and reused. The pump renders only while the tracked BetterSource
    // reports Playing and its _lastPlayingClipName is our clip (BetterSource.Play sets both; OnStop nulls the clip), so a
    // source re-grabbed for someone else's audio goes silent immediately.
    public static class AudioHaptics
    {
        public static bool enabled = true;
        public static float strength = 1f;        // master amplitude scale (0..~1.5)
        public static float windowSec = 0.02f;     // one haptic pulse per window
        public static float minAmp = 0.04f;        // skip windows quieter than this
        public static float minFreq = 60f;         // LRA band the audio frequency maps into
        public static float maxFreq = 200f;        // higher: a sustained full-strength buzz makes the controller audibly hum
        public static float peakDecay = 0.995f;    // auto-gain: running peak decay per window
        public static float freqSmoothing = 0.35f; // per-window lerp toward the measured texture
        // Onsets over sustain: a click / ratchet / slam lands as a short sharp hit, the steady part after it falls back. Crisper,
        // and the controller hums less (the hum is the sustained part). sustainGain 1 + onsetBoost 0 = a flat envelope.
        public static float sustainGain = 0.55f;   // a sound's steady part comes through at this much...
        public static float onsetBoost = 1.6f;     // ...and its sudden rises this much harder
        public static float onsetFollow = 0.3f;    // per window, how fast the steady level catches up with the sound (0.3 = ~60 ms)

        private static HapticAudioTap tap;          // the filter component (on the pooled source GO)
        private static BetterSource trackedBs;      // the BetterSource the clip was played on
        private static string expectedClipName;     // what should be playing for us to render
        private static float leftGain, rightGain;   // how much each controller gets: the hand doing it 1, one feeling it through the thing it holds less
        private static float leftPitch = 1f, rightPitch = 1f; // and how much lower (HapticMixer.throughPitch)
        private static float clipStrength = 1f;      // the caller's scale on top of strength
        private static float clipIdleStop = 1f;      // the caller's idleStopSec
        private static Coroutine pump;

        // Main thread, whenever a clip is audibly played for us: re-points the tap at the source actually used (the pool
        // re-issues sources between clips), picks the hand(s), starts the pump.
        // idleStop < 0 = idleStopSec; it must end before the clip's owner hands the source back to the pool (see DestroyTap).
        public static void OnClipPlayed(BetterSource bs, AudioClip clip, float leftHandGain, float rightHandGain, float leftHandPitch = 1f, float rightHandPitch = 1f,
            float scale = 1f, float idleStop = -1f)
        {
            if (!enabled || bs == null || bs.source1 == null || clip == null || VRGlobals.vrPlayer == null) return;
            if (leftHandGain <= 0f && rightHandGain <= 0f) return;
            leftGain = leftHandGain;
            rightGain = rightHandGain;
            leftPitch = leftHandPitch;
            rightPitch = rightHandPitch;
            clipStrength = scale;
            clipIdleStop = idleStop < 0f ? idleStopSec : idleStop;
            if (tap == null || tap.gameObject != bs.source1.gameObject)
            {
                DestroyTap(); // never leave a filter script behind on a pooled source GO
                tap = bs.source1.gameObject.AddComponent<HapticAudioTap>();
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
            DestroyTap();
            trackedBs = null; expectedClipName = null;
        }

        // Destroy the tap rather than disable it. An OnAudioFilterRead script inserts a custom DSP filter on the
        // AudioSource's GameObject, and on a pooled source release/re-borrow toggles source1.enabled under the filter,
        // after which the source can report Playing while producing silence (all volume/mute/active flags clean).
        // Removing the component restores the vanilla DSP chain; OnClipPlayed re-adds it fresh per clip.
        private static void DestroyTap()
        {
            if (tap != null)
            {
                tap.enabled = false;
                Object.Destroy(tap);
            }
            tap = null;
        }

        // Trace helper for the eating debug log: is one of our filter taps sitting on this
        // source GameObject right now?
        public static string DescribeTap(GameObject go)
        {
            if (go == null) return "noGO";
            var t = go.GetComponent<HapticAudioTap>();
            return t == null ? "none" : (t.enabled ? "ENABLED" : "disabled");
        }

        // After this long with nothing of ours playing, the pump shuts down and destroys the tap (see DestroyTap).
        // OnClipPlayed restarts everything on the next clip.
        public static float idleStopSec = 1f;

        private static IEnumerator Pump()
        {
            var wait = new WaitForSeconds(windowSec);
            float peak = 0.02f;   // auto-gain reference (foley levels vary wildly)
            float steadyLevel = 0f; // the slow follower the onsets are measured over
            float freq = 140f;
            int lastBuffers = -1;
            float idle = 0f;
            while (true)
            {
                float amplitude = 0f;
                bool live = tap != null && trackedBs != null && expectedClipName != null
                    && trackedBs.PlayBackState == BetterSource.EPlayBackState.Playing
                    && trackedBs._lastPlayingClipName == expectedClipName;
                if (live) idle = 0f;
                else steadyLevel = 0f; // the next clip's first hit is an onset, not measured over the last one's tail
                if (!live && (idle += windowSec) >= clipIdleStop)
                {
                    DestroyTap();
                    trackedBs = null; expectedClipName = null;
                    pump = null;
                    yield break;
                }
                if (live)
                {
                    int b = tap.Buffers;
                    if (b != lastBuffers) // a fresh audio buffer landed since last window
                    {
                        lastBuffers = b;
                        float rms = tap.Rms;
                        peak = Mathf.Max(peak * peakDecay, rms, 0.02f);
                        float level = Mathf.Clamp01(rms / peak);
                        float onset = Mathf.Max(0f, level - steadyLevel);
                        steadyLevel = Mathf.Lerp(steadyLevel, level, onsetFollow);
                        amplitude = Mathf.Clamp01(sustainGain * level + onsetBoost * onset) * strength * clipStrength;
                        // The raw audio ZCR runs hundreds of Hz..kHz: log-map it into the LRA band so textures
                        // differentiate (linear clamping pegs everything at max).
                        float z = Mathf.Clamp(tap.ZcrHz, 50f, 8000f);
                        float n = Mathf.Log(z / 50f) / Mathf.Log(8000f / 50f);
                        freq = Mathf.Lerp(freq, Mathf.Lerp(minFreq, maxFreq, n), freqSmoothing);
                    }
                }
                if (amplitude >= minAmp)
                {
                    // Held 1.5x the window so it bridges to the next one: reads as a continuous buzz, not ticks. Through the
                    // mixer, so it plays alongside the knocks instead of cutting them off.
                    if (leftGain > 0f) HapticMixer.Stream(SteamVR_Input_Sources.LeftHand, amplitude * leftGain, freq * leftPitch, windowSec * 1.5f);
                    if (rightGain > 0f) HapticMixer.Stream(SteamVR_Input_Sources.RightHand, amplitude * rightGain, freq * rightPitch, windowSec * 1.5f);
                }
                yield return wait;
            }
        }

        // The audio-thread side. OnAudioFilterRead: math only, read-only on the buffer, no Unity API, no allocations.
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
                    int index = i * channels;
                    for (int c = 0; c < channels; c++) v += data[index + c];
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
