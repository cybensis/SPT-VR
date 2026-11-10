using TarkovVR.Source.Controls;
using TarkovVR.Source.XR;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VRManager
{
    // The headset in TRACKING space - the frame the hand poses report in. Head-to-hand measures (the gunstock, the step sprint,
    // arm length) pair these two pure-tracking sources and never a camera: the hideout rewrites the camera's local transform.
    public static class HeadTracking
    {
        // The headset pivots ~10 cm around the NECK when you look around, so body measures use the neck point: headset-local,
        // it rides the head and stays nearly still under head turns.
        public static Vector3 neckLocalOffset = new Vector3(0f, -0.075f, -0.08f);

        private static readonly TrackedDevicePose_t[] hmdPoses = new TrackedDevicePose_t[1];

        // The headset's pose straight from the runtime
        internal static bool RawHmdPose(out Vector3 position, out Quaternion rotation)
        {
            if (OpenXRBootstrap.Active)
                return OpenXRInput.TryGetHeadPose(out position, out rotation);
            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            ETrackingUniverseOrigin origin = OpenVR.Compositor != null
                ? OpenVR.Compositor.GetTrackingSpace() : ETrackingUniverseOrigin.TrackingUniverseStanding;
            system.GetDeviceToAbsoluteTrackingPose(origin, 0f, hmdPoses);
            if (!hmdPoses[0].bPoseIsValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            SteamVR_Utils.RigidTransform headTransform = new SteamVR_Utils.RigidTransform(hmdPoses[0].mDeviceToAbsoluteTracking);
            position = headTransform.pos;
            rotation = headTransform.rot;
            return true;
        }
    }
}
