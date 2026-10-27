using System;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // Every controller read in the mod goes through here instead of SteamVR_Actions._default. The members mirror
    // SteamVR's action API (same names, same SteamVR_Input_Sources hands) so call sites changed only their prefix.
    // Stage 1: each action forwards to its SteamVR action - behaviour identical to calling SteamVR directly.
    // The OpenXR backend plugs in behind these same members. Actions resolve lazily: SteamVR_Actions._default only
    // exists after SteamVR_Actions.PreInitialize() (Plugin.InitializeVR).
    public static class VRInput
    {
        public static readonly VRBoolean RightGrip = new VRBoolean(() => SteamVR_Actions._default.RightGrip);
        public static readonly VRBoolean LeftGrip = new VRBoolean(() => SteamVR_Actions._default.LeftGrip);
        public static readonly VRBoolean ButtonA = new VRBoolean(() => SteamVR_Actions._default.ButtonA);
        public static readonly VRBoolean ButtonB = new VRBoolean(() => SteamVR_Actions._default.ButtonB);
        public static readonly VRBoolean ButtonX = new VRBoolean(() => SteamVR_Actions._default.ButtonX);
        public static readonly VRBoolean ButtonY = new VRBoolean(() => SteamVR_Actions._default.ButtonY);
        public static readonly VRBoolean ClickRightJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickRightJoystick);
        public static readonly VRBoolean ClickLeftJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickLeftJoystick);
        public static readonly VRBoolean Start = new VRBoolean(() => SteamVR_Actions._default.Start);

        public static readonly VRSingle RightTrigger = new VRSingle(() => SteamVR_Actions._default.RightTrigger);
        public static readonly VRSingle LeftTrigger = new VRSingle(() => SteamVR_Actions._default.LeftTrigger);

        public static readonly VRVector2 RightJoystick = new VRVector2(() => SteamVR_Actions._default.RightJoystick);
        public static readonly VRVector2 LeftJoystick = new VRVector2(() => SteamVR_Actions._default.LeftJoystick);

        public static readonly VRPose RightHandPose = new VRPose(() => SteamVR_Actions._default.RightHandPose);
        public static readonly VRPose LeftHandPose = new VRPose(() => SteamVR_Actions._default.LeftHandPose);

        public static readonly VRVibration Haptic = new VRVibration(() => SteamVR_Actions._default.Haptic);

        public static readonly VRSkeleton SkeletonRightHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonRightHand);
        public static readonly VRSkeleton SkeletonLeftHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonLeftHand);
    }

    public sealed class VRBoolean
    {
        private readonly Func<SteamVR_Action_Boolean> resolve;
        private SteamVR_Action_Boolean steam;
        internal VRBoolean(Func<SteamVR_Action_Boolean> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Boolean S => steam ?? (steam = resolve());

        // "Any" hand, like SteamVR's action-level properties
        public bool state => S.state;
        public bool stateDown => S.stateDown;
        public bool stateUp => S.stateUp;
        public bool GetState(SteamVR_Input_Sources hand) => S.GetState(hand);
        public bool GetStateDown(SteamVR_Input_Sources hand) => S.GetStateDown(hand);
        public bool GetStateUp(SteamVR_Input_Sources hand) => S.GetStateUp(hand);
    }

    public sealed class VRSingle
    {
        private readonly Func<SteamVR_Action_Single> resolve;
        private SteamVR_Action_Single steam;
        internal VRSingle(Func<SteamVR_Action_Single> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Single S => steam ?? (steam = resolve());

        public float axis => S.axis;
        public float GetAxis(SteamVR_Input_Sources hand) => S.GetAxis(hand);
    }

    public sealed class VRVector2
    {
        private readonly Func<SteamVR_Action_Vector2> resolve;
        private SteamVR_Action_Vector2 steam;
        internal VRVector2(Func<SteamVR_Action_Vector2> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Vector2 S => steam ?? (steam = resolve());

        public Vector2 axis => S.axis;
        public Vector2 GetAxis(SteamVR_Input_Sources hand) => S.GetAxis(hand);
    }

    public sealed class VRPose
    {
        public delegate void UpdateHandler(VRPose fromAction, SteamVR_Input_Sources fromSource);

        private readonly Func<SteamVR_Action_Pose> resolve;
        private SteamVR_Action_Pose steam;
        // Our listeners per hand, fanned out from ONE SteamVR listener per hand (keeps SteamVR's call order).
        private readonly Dictionary<SteamVR_Input_Sources, UpdateHandler> listeners = new Dictionary<SteamVR_Input_Sources, UpdateHandler>();
        private readonly Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler> forwarders = new Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler>();

        internal VRPose(Func<SteamVR_Action_Pose> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Pose S => steam ?? (steam = resolve());

        // "Any" hand - what the pose handlers read from fromAction
        public Vector3 localPosition => S.localPosition;
        public Quaternion localRotation => S.localRotation;
        public Vector3 GetLocalPosition(SteamVR_Input_Sources hand) => S.GetLocalPosition(hand);
        public Quaternion GetLocalRotation(SteamVR_Input_Sources hand) => S.GetLocalRotation(hand);
        public Vector3 GetVelocity(SteamVR_Input_Sources hand) => S.GetVelocity(hand);
        public Vector3 GetAngularVelocity(SteamVR_Input_Sources hand) => S.GetAngularVelocity(hand);

        public void AddOnUpdateListener(SteamVR_Input_Sources hand, UpdateHandler handler)
        {
            listeners.TryGetValue(hand, out UpdateHandler current);
            listeners[hand] = current + handler;
            if (!forwarders.ContainsKey(hand))
            {
                SteamVR_Action_Pose.UpdateHandler forward = (action, source) =>
                {
                    if (listeners.TryGetValue(source, out UpdateHandler h) && h != null)
                        h(this, source);
                };
                forwarders[hand] = forward;
                S.AddOnUpdateListener(hand, forward);
            }
        }

        public void RemoveOnUpdateListener(SteamVR_Input_Sources hand, UpdateHandler handler)
        {
            if (listeners.TryGetValue(hand, out UpdateHandler current))
                listeners[hand] = current - handler;
        }

        // Same reach as SteamVR's: drops every listener on that hand, including the forwarder.
        public void RemoveAllListeners(SteamVR_Input_Sources hand)
        {
            listeners.Remove(hand);
            forwarders.Remove(hand);
            S.RemoveAllListeners(hand);
        }
    }

    public sealed class VRVibration
    {
        private readonly Func<SteamVR_Action_Vibration> resolve;
        private SteamVR_Action_Vibration steam;
        internal VRVibration(Func<SteamVR_Action_Vibration> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Vibration S => steam ?? (steam = resolve());

        public void Execute(float secondsFromNow, float durationSeconds, float frequency, float amplitude, SteamVR_Input_Sources hand)
            => S.Execute(secondsFromNow, durationSeconds, frequency, amplitude, hand);
    }

    public sealed class VRSkeleton
    {
        private readonly Func<SteamVR_Action_Skeleton> resolve;
        private SteamVR_Action_Skeleton steam;
        internal VRSkeleton(Func<SteamVR_Action_Skeleton> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Skeleton S => steam ?? (steam = resolve());

        public bool active => S.active;
        public float[] fingerCurls => S.fingerCurls;
        public EVRSkeletalTrackingLevel skeletalTrackingLevel => S.skeletalTrackingLevel;
    }
}
