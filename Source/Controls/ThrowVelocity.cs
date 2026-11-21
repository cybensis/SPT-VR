using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // A grenade throw's velocity on OpenXR: the palm (grip pose) velocity as the runtime reports it. It is IMU-backed and has no
    // spin term. OpenXR has no raw tracking point, so OpenXRInput rebuilds that point's velocity as the grip's velocity plus spin
    // times the 10-15 cm lever, and a fast-spinning throw computed that way curves sideways. A velocity measured from the poses
    // overshoots instead, because the predicted poses run ahead of an accelerating hand.
    public static class ThrowVelocity
    {
        public static float palmScale = 1.25f;      // the top of the controller (the raw point) moves ~1.25x faster than the palm; scale up to match

        // The palm's velocity in tracking space, scaled by palmScale (OpenXR only)
        public static bool TryPalmVelocity(SteamVR_Input_Sources source, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!VRInput.UsingOpenXR)
                return false;
            velocity = OpenXRInput.GetGripVelocity(source == SteamVR_Input_Sources.LeftHand ? OpenXRInput.LEFT_HAND : OpenXRInput.RIGHT_HAND) * palmScale;
            return true;
        }
    }
}
