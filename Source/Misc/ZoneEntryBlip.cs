using UnityEngine;

namespace TarkovVR.Source.Misc
{
    // Fires a haptic blip once when a hand comes into reach of something. It re-arms only after the hand has been out of reach for
    // rearmTime: a hand resting on the edge flips in and out every few frames with tracking jitter, and re-arming at once would buzz nonstop.
    internal sealed class ZoneEntryBlip
    {
        public static float rearmTime = 0.3f;   // s out of reach before coming back in blips again

        private bool inside;
        private bool armed = true;
        private float leftAt = float.NegativeInfinity;

        // Call every frame with whether the hand is in reach; returns true on the frame to blip
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

        // Marks the hand as already in reach, without a blip (the grip was taken another way)
        public void MarkInside()
        {
            inside = true;
            armed = false;
        }

        // Marks the hand as out of reach so its next entry blips (the hand was taken off for something else)
        public void Rearm()
        {
            inside = false;
            armed = true;
            leftAt = float.NegativeInfinity;
        }
    }
}
