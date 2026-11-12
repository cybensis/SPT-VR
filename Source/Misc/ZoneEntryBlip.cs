using UnityEngine;

namespace TarkovVR.Source.Misc
{
    // A haptic blip on a hand coming into reach of something, once. It re-arms only after the hand has been out of reach for rearmTime:
    // a hand resting on the reach's edge flips in and out every few frames with tracking jitter, which re-armed at once buzzes on and on.
    internal sealed class ZoneEntryBlip
    {
        public static float rearmTime = 0.3f;   // s out of reach before coming back in blips again

        private bool inside;
        private bool armed = true;
        private float leftAt = float.NegativeInfinity;

        // This frame's in / out; true on the frame to blip
        public bool Enter(bool nowInside)
        {
            float now = Time.time;
            if (!nowInside)
            {
                if (inside)
                    leftAt = now;
                inside = false;
                if (now - leftAt >= rearmTime)
                    armed = true;
                return false;
            }
            bool blip = !inside && armed;
            inside = true;
            if (blip)
                armed = false;
            return blip;
        }

        // Already there without a blip (the grip was taken another way)
        public void MarkInside()
        {
            inside = true;
            armed = false;
        }

        // Out, and the next coming into reach blips (the hand was taken off for something else)
        public void Rearm()
        {
            inside = false;
            armed = true;
            leftAt = float.NegativeInfinity;
        }
    }
}
