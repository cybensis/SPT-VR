using System.Collections.Generic;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Misc
{
    // Every gun / reload / eating buzz goes through here, one mix per controller. Each vibration sent to a controller REPLACES the
    // one it's playing, so without this the sound stream (a new one every 20 ms) cut a clunk off within a frame and a clunk cut
    // the stream off: whichever came last won. Here the strongest wins, the others add a little on top, the pitches blend, and a
    // new vibration goes out only when the mix changes (or the last one is about to run out).
    public static class HapticMixer
    {
        public static bool enabled = true;          // false = each one goes straight to the controller, the last replacing the rest (as before)
        public static float sendLength = 0.04f;     // s a sent vibration lasts at most; sent again before it runs out while something plays
        public static float resendAmplitude = 0.06f; // sent again when the mix moves this much...
        public static float resendPitch = 0.1f;     // ...or its pitch moves this fraction
        public static float minAmplitude = 0.03f;   // quieter than this isn't sent
        public static float layerBlend = 0.35f;     // how much the weaker ones add on top of the strongest
        public static float pulseFade = 0.5f;       // the share of a pulse spent fading out: a knock dies away instead of stopping dead (0 = flat)
        // A hand holding the thing something happens to (another hand's work on the gun, the can the other hand opens)
        public static float throughGain = 0.5f;     // feels it at this much...
        public static float throughPitch = 0.7f;    // ...and this much lower: a solid body carries the thump, not the fine texture
        public static float lowestPitch = 45f;      // Hz; lower barely moves the controller's motor

        private struct Pulse
        {
            public float start, end, amplitude, frequency;
        }

        private class HandMix
        {
            public readonly List<Pulse> pulses = new List<Pulse>();
            public float streamAmplitude, streamFrequency, streamUntil;
            public float sentAmplitude, sentFrequency, sentUntil;
            public bool newPulse;
        }

        private static readonly HandMix[] hands = { new HandMix(), new HandMix() };
        private static GameObject runner;

        private static int HandIndex(SteamVR_Input_Sources hand)
        {
            return hand == SteamVR_Input_Sources.LeftHand ? 0 : hand == SteamVR_Input_Sources.RightHand ? 1 : -1;
        }

        private static SteamVR_Input_Sources HandSource(int index)
        {
            return index == 0 ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        }

        // A knock or buzz of this length on one controller
        public static void PlayPulse(SteamVR_Input_Sources hand, float amplitude, float duration, float frequency, float delay = 0f)
        {
            int index = HandIndex(hand);
            if (index < 0 || amplitude <= 0f || duration <= 0f)
                return;
            frequency = Mathf.Max(frequency, lowestPitch);
            if (!enabled)
            {
                VRInput.Haptic.Execute(delay, duration, frequency, Mathf.Clamp01(amplitude), hand);
                return;
            }
            EnsureRunner();
            float now = Time.unscaledTime;
            HandMix mix = hands[index];
            mix.pulses.Add(new Pulse { start = now + delay, end = now + delay + duration, amplitude = Mathf.Clamp01(amplitude), frequency = frequency });
            if (delay <= 0f)
            {
                mix.newPulse = true;
                Send(index, now);
            }
        }

        // The sound stream (AudioHaptics): the level and pitch now, held until the next update or `hold` runs out
        public static void Stream(SteamVR_Input_Sources hand, float amplitude, float frequency, float hold)
        {
            int index = HandIndex(hand);
            if (index < 0)
                return;
            frequency = Mathf.Max(frequency, lowestPitch);
            if (!enabled)
            {
                if (amplitude > 0f)
                    VRInput.Haptic.Execute(0f, hold, frequency, Mathf.Clamp01(amplitude), hand);
                return;
            }
            EnsureRunner();
            float now = Time.unscaledTime;
            HandMix mix = hands[index];
            if (now >= mix.streamUntil && amplitude > 0f)
                mix.newPulse = true;
            mix.streamAmplitude = Mathf.Clamp01(amplitude);
            mix.streamFrequency = frequency;
            mix.streamUntil = now + hold;
            Send(index, now);
        }

        private static void Send(int index, float now)
        {
            HandMix mix = hands[index];
            float strongest = 0f, total = 0f, weightedPitch = 0f, lastEnd = now;
            for (int i = mix.pulses.Count - 1; i >= 0; i--)
            {
                Pulse pulse = mix.pulses[i];
                if (now >= pulse.end)
                {
                    mix.pulses.RemoveAt(i);
                    continue;
                }
                if (now < pulse.start)
                    continue;
                float progress = (now - pulse.start) / (pulse.end - pulse.start);
                float fadeFrom = 1f - pulseFade;
                float level = pulse.amplitude * (progress <= fadeFrom || pulseFade <= 0f ? 1f : 1f - (progress - fadeFrom) / pulseFade);
                Add(level, pulse.frequency, ref strongest, ref total, ref weightedPitch);
                lastEnd = Mathf.Max(lastEnd, pulse.end);
            }
            if (now < mix.streamUntil && mix.streamAmplitude > 0f)
            {
                Add(mix.streamAmplitude, mix.streamFrequency, ref strongest, ref total, ref weightedPitch);
                lastEnd = Mathf.Max(lastEnd, mix.streamUntil);
            }

            float amplitude = Mathf.Clamp01(strongest + layerBlend * (total - strongest));
            if (amplitude < minAmplitude)
            {
                mix.newPulse = false;
                return;
            }
            float frequency = weightedPitch / total;
            bool runningOut = now >= mix.sentUntil - Time.unscaledDeltaTime;
            bool changed = Mathf.Abs(amplitude - mix.sentAmplitude) > resendAmplitude
                || Mathf.Abs(frequency / Mathf.Max(mix.sentFrequency, 1f) - 1f) > resendPitch;
            if (!mix.newPulse && !runningOut && !changed)
                return;
            // No longer than what's playing: a vibration left running past the end of its pulses would stretch every knock
            float length = Mathf.Min(sendLength, lastEnd - now);
            if (length <= 0f)
                return;
            VRInput.Haptic.Execute(0f, length, frequency, amplitude, HandSource(index));
            mix.sentAmplitude = amplitude;
            mix.sentFrequency = frequency;
            mix.sentUntil = now + length;
            mix.newPulse = false;
        }

        private static void Add(float level, float frequency, ref float strongest, ref float total, ref float weightedPitch)
        {
            if (level <= 0f)
                return;
            strongest = Mathf.Max(strongest, level);
            total += level;
            weightedPitch += level * frequency;
        }

        private static void EnsureRunner()
        {
            if (runner != null)
                return;
            runner = new GameObject("VRHapticMixer");
            Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
        }

        // Fades, delayed pulses starting, and the resend before a vibration runs out happen here, once a frame
        private class Runner : MonoBehaviour
        {
            private void LateUpdate()
            {
                float now = Time.unscaledTime;
                for (int i = 0; i < hands.Length; i++)
                {
                    HandMix mix = hands[i];
                    if (mix.pulses.Count == 0 && now >= mix.streamUntil)
                        continue;
                    // A delayed pulse starting now is a new attack: send it straight away
                    foreach (Pulse pulse in mix.pulses)
                    {
                        if (pulse.start <= now && pulse.start > now - Time.unscaledDeltaTime)
                            mix.newPulse = true;
                    }
                    Send(i, now);
                }
            }
        }
    }
}
