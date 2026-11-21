using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // A grenade throw's velocity on OpenXR: the palm's (grip pose's) velocity as the runtime reports it. It is IMU-backed and has no
    // spin term: OpenXR has no raw tracking point, so OpenXRInput rebuilds that point's velocity as the grip's + spin x the 10-15 cm
    // lever, and a fast-spinning throw read that way bends sideways. A velocity measured from the poses overshoots instead (the
    // predicted poses run ahead of an accelerating hand).
    public static class ThrowVelocity
    {
        public static float palmScale = 1.25f;      // the palm moves slower than the controller's top (the raw point, ~1.25x): scaled up to that strength

        // The palm's velocity, tracking space, scaled by palmScale (OpenXR only)
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
