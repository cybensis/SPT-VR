using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    internal class ControllerVelocity
    {
        // Controller velocity and pose read straight from the pose actions, used for throwing items and grenades
        public static Vector3 GetSteamVRVelocity(SteamVR_Input_Sources inputSource)
        {
            VRPose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? VRInput.LeftHandPose
                : VRInput.RightHandPose;

            if (poseAction != null)
            {
                Vector3 velocity = poseAction.GetVelocity(inputSource);
                return velocity;
            }
            return Vector3.zero;
        }

        public static Vector3 GetSteamVRPosition(SteamVR_Input_Sources inputSource)
        {
            VRPose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? VRInput.LeftHandPose
                : VRInput.RightHandPose;

            return poseAction != null ? poseAction.GetLocalPosition(inputSource) : Vector3.zero;
        }

        public static Quaternion GetSteamVRRotation(SteamVR_Input_Sources inputSource)
        {
            VRPose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? VRInput.LeftHandPose
                : VRInput.RightHandPose;

            return poseAction != null ? poseAction.GetLocalRotation(inputSource) : Quaternion.identity;
        }

        public static Vector3 GetSteamVRAngularVelocity(SteamVR_Input_Sources inputSource)
        {
            VRPose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? VRInput.LeftHandPose
                : VRInput.RightHandPose;

            if (poseAction != null)
            {
                Vector3 angularVelocity = poseAction.GetAngularVelocity(inputSource);
                return angularVelocity;
            }
            return Vector3.zero;
        }
    }
}
