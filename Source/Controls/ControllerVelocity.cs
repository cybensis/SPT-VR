using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    internal class ControllerVelocity
    {
        //These two method handle getting velocity of your controller straight from SteamVR to handle throwing items/grenades
        public static Vector3 GetSteamVRVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 velocity = poseAction.GetVelocity(inputSource);
                return velocity;
            }
            return Vector3.zero;
        }

        public static Vector3 GetSteamVRPosition(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalPosition(inputSource) : Vector3.zero;
        }

        public static Quaternion GetSteamVRRotation(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalRotation(inputSource) : Quaternion.identity;
        }

        public static Vector3 GetSteamVRAngularVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 angularVelocity = poseAction.GetAngularVelocity(inputSource);
                return angularVelocity;
            }
            return Vector3.zero;
        }
    }
}
