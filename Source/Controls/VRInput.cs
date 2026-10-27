using System;
using System.Collections.Generic;
using TarkovVR.Source.XR;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // Every controller read in the mod goes through here instead of SteamVR_Actions._default. The members mirror
    // SteamVR's action API (same names, same SteamVR_Input_Sources hands) so call sites changed only their prefix.
    // SteamVR running: each action forwards to its SteamVR action (identical behaviour). OpenXR running
    // (OpenXRBootstrap.Active): values come from OpenXRInput, bound the way bindings_knuckles / bindings_oculus_touch
    // bind them - each action belongs to ONE physical hand, other hands read as idle, like an unbound SteamVR source.
    // SteamVR actions resolve lazily: SteamVR_Actions._default only exists after SteamVR_Actions.PreInitialize().
    public static class VRInput
    {
        public static readonly VRBoolean RightGrip = new VRBoolean(() => SteamVR_Actions._default.RightGrip, OpenXRInput.Right, OpenXRInput.Button.Grip);
        public static readonly VRBoolean LeftGrip = new VRBoolean(() => SteamVR_Actions._default.LeftGrip, OpenXRInput.Left, OpenXRInput.Button.Grip);
        public static readonly VRBoolean ButtonA = new VRBoolean(() => SteamVR_Actions._default.ButtonA, OpenXRInput.Right, OpenXRInput.Button.Primary);
        public static readonly VRBoolean ButtonB = new VRBoolean(() => SteamVR_Actions._default.ButtonB, OpenXRInput.Right, OpenXRInput.Button.Secondary);
        public static readonly VRBoolean ButtonX = new VRBoolean(() => SteamVR_Actions._default.ButtonX, OpenXRInput.Left, OpenXRInput.Button.Primary);
        public static readonly VRBoolean ButtonY = new VRBoolean(() => SteamVR_Actions._default.ButtonY, OpenXRInput.Left, OpenXRInput.Button.Secondary);
        public static readonly VRBoolean ClickRightJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickRightJoystick, OpenXRInput.Right, OpenXRInput.Button.StickClick);
        // Index binds the left trackpad TOUCH here too
        public static readonly VRBoolean ClickLeftJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickLeftJoystick, OpenXRInput.Left, OpenXRInput.Button.StickClickOrPadTouch);
        // Unbound in the Index / Touch bindings
        public static readonly VRBoolean Start = new VRBoolean(() => SteamVR_Actions._default.Start, OpenXRInput.Left, OpenXRInput.Button.None);

        public static readonly VRSingle RightTrigger = new VRSingle(() => SteamVR_Actions._default.RightTrigger, OpenXRInput.Right);
        public static readonly VRSingle LeftTrigger = new VRSingle(() => SteamVR_Actions._default.LeftTrigger, OpenXRInput.Left);

        public static readonly VRVector2 RightJoystick = new VRVector2(() => SteamVR_Actions._default.RightJoystick, OpenXRInput.Right);
        public static readonly VRVector2 LeftJoystick = new VRVector2(() => SteamVR_Actions._default.LeftJoystick, OpenXRInput.Left);

        public static readonly VRPose RightHandPose = new VRPose(() => SteamVR_Actions._default.RightHandPose, OpenXRInput.Right);
        public static readonly VRPose LeftHandPose = new VRPose(() => SteamVR_Actions._default.LeftHandPose, OpenXRInput.Left);

        public static readonly VRVibration Haptic = new VRVibration(() => SteamVR_Actions._default.Haptic);

        public static readonly VRSkeleton SkeletonRightHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonRightHand, OpenXRInput.Right);
        public static readonly VRSkeleton SkeletonLeftHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonLeftHand, OpenXRInput.Left);

        internal static bool OpenXR => OpenXRBootstrap.Active;
    }

    public sealed class VRBoolean
    {
        private readonly Func<SteamVR_Action_Boolean> resolve;
        private SteamVR_Action_Boolean steam;
        private readonly int hand;
        private readonly OpenXRInput.Button button;
        internal VRBoolean(Func<SteamVR_Action_Boolean> resolve, int hand, OpenXRInput.Button button)
        {
            this.resolve = resolve; this.hand = hand; this.button = button;
        }
        private SteamVR_Action_Boolean S => steam ?? (steam = resolve());

        // "Any" hand, like SteamVR's action-level properties
        public bool state => VRInput.OpenXR ? OpenXRInput.Get(hand, button, OpenXRInput.Edge.Held) : S.state;
        public bool stateDown => VRInput.OpenXR ? OpenXRInput.Get(hand, button, OpenXRInput.Edge.Down) : S.stateDown;
        public bool stateUp => VRInput.OpenXR ? OpenXRInput.Get(hand, button, OpenXRInput.Edge.Up) : S.stateUp;
        public bool GetState(SteamVR_Input_Sources src) => VRInput.OpenXR ? OpenXRInput.Matches(src, hand) && state : S.GetState(src);
        public bool GetStateDown(SteamVR_Input_Sources src) => VRInput.OpenXR ? OpenXRInput.Matches(src, hand) && stateDown : S.GetStateDown(src);
        public bool GetStateUp(SteamVR_Input_Sources src) => VRInput.OpenXR ? OpenXRInput.Matches(src, hand) && stateUp : S.GetStateUp(src);
    }

    public sealed class VRSingle
    {
        private readonly Func<SteamVR_Action_Single> resolve;
        private SteamVR_Action_Single steam;
        private readonly int hand;
        internal VRSingle(Func<SteamVR_Action_Single> resolve, int hand) { this.resolve = resolve; this.hand = hand; }
        private SteamVR_Action_Single S => steam ?? (steam = resolve());

        public float axis => VRInput.OpenXR ? OpenXRInput.Trigger(hand) : S.axis;
        public float GetAxis(SteamVR_Input_Sources src) => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? axis : 0f) : S.GetAxis(src);
    }

    public sealed class VRVector2
    {
        private readonly Func<SteamVR_Action_Vector2> resolve;
        private SteamVR_Action_Vector2 steam;
        private readonly int hand;
        internal VRVector2(Func<SteamVR_Action_Vector2> resolve, int hand) { this.resolve = resolve; this.hand = hand; }
        private SteamVR_Action_Vector2 S => steam ?? (steam = resolve());

        public Vector2 axis => VRInput.OpenXR ? OpenXRInput.Stick(hand) : S.axis;
        public Vector2 GetAxis(SteamVR_Input_Sources src) => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? axis : Vector2.zero) : S.GetAxis(src);
    }

    public sealed class VRPose
    {
        public delegate void UpdateHandler(VRPose fromAction, SteamVR_Input_Sources fromSource);

        private readonly Func<SteamVR_Action_Pose> resolve;
        private SteamVR_Action_Pose steam;
        private readonly int hand;
        // Our listeners per hand. SteamVR: fanned out from ONE SteamVR listener per hand (keeps SteamVR's call order).
        // OpenXR: OpenXRInput fires them from onBeforeRender, where SteamVR fires its pose events.
        private readonly Dictionary<SteamVR_Input_Sources, UpdateHandler> listeners = new Dictionary<SteamVR_Input_Sources, UpdateHandler>();
        private readonly Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler> forwarders = new Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler>();
        private readonly List<SteamVR_Input_Sources> fireKeys = new List<SteamVR_Input_Sources>();

        internal VRPose(Func<SteamVR_Action_Pose> resolve, int hand) { this.resolve = resolve; this.hand = hand; }
        private SteamVR_Action_Pose S => steam ?? (steam = resolve());

        // "Any" hand - what the pose handlers read from fromAction (SteamVR "raw" pose, tracking space)
        public Vector3 localPosition => VRInput.OpenXR ? OpenXRInput.RawPosition(hand) : S.localPosition;
        public Quaternion localRotation => VRInput.OpenXR ? OpenXRInput.RawRotation(hand) : S.localRotation;
        public Vector3 GetLocalPosition(SteamVR_Input_Sources src)
            => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? localPosition : Vector3.zero) : S.GetLocalPosition(src);
        public Quaternion GetLocalRotation(SteamVR_Input_Sources src)
            => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? localRotation : Quaternion.identity) : S.GetLocalRotation(src);
        public Vector3 GetVelocity(SteamVR_Input_Sources src)
            => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? OpenXRInput.RawVelocity(hand) : Vector3.zero) : S.GetVelocity(src);
        public Vector3 GetAngularVelocity(SteamVR_Input_Sources src)
            => VRInput.OpenXR ? (OpenXRInput.Matches(src, hand) ? OpenXRInput.AngularVelocity(hand) : Vector3.zero) : S.GetAngularVelocity(src);

        public void AddOnUpdateListener(SteamVR_Input_Sources src, UpdateHandler handler)
        {
            listeners.TryGetValue(src, out UpdateHandler current);
            listeners[src] = current + handler;
            if (!VRInput.OpenXR && !forwarders.ContainsKey(src))
            {
                SteamVR_Action_Pose.UpdateHandler forward = (action, source) =>
                {
                    if (listeners.TryGetValue(source, out UpdateHandler h) && h != null)
                        h(this, source);
                };
                forwarders[src] = forward;
                S.AddOnUpdateListener(src, forward);
            }
        }

        public void RemoveOnUpdateListener(SteamVR_Input_Sources src, UpdateHandler handler)
        {
            if (listeners.TryGetValue(src, out UpdateHandler current))
                listeners[src] = current - handler;
        }

        // Same reach as SteamVR's: drops every listener on that hand, including the forwarder.
        public void RemoveAllListeners(SteamVR_Input_Sources src)
        {
            listeners.Remove(src);
            forwarders.Remove(src);
            if (!VRInput.OpenXR)
                S.RemoveAllListeners(src);
        }

        // OpenXR: once a frame after the pose sample. Keys are copied first - handlers may add / remove listeners.
        internal void FireOpenXR()
        {
            fireKeys.Clear();
            fireKeys.AddRange(listeners.Keys);
            foreach (SteamVR_Input_Sources src in fireKeys)
                if (OpenXRInput.Matches(src, hand) && listeners.TryGetValue(src, out UpdateHandler h) && h != null)
                    h(this, src);
        }
    }

    public sealed class VRVibration
    {
        private readonly Func<SteamVR_Action_Vibration> resolve;
        private SteamVR_Action_Vibration steam;
        internal VRVibration(Func<SteamVR_Action_Vibration> resolve) { this.resolve = resolve; }
        private SteamVR_Action_Vibration S => steam ?? (steam = resolve());

        public void Execute(float secondsFromNow, float durationSeconds, float frequency, float amplitude, SteamVR_Input_Sources src)
        {
            if (VRInput.OpenXR)
                OpenXRInput.Haptic(secondsFromNow, durationSeconds, amplitude, src);
            else
                S.Execute(secondsFromNow, durationSeconds, frequency, amplitude, src);
        }
    }

    public sealed class VRSkeleton
    {
        private readonly Func<SteamVR_Action_Skeleton> resolve;
        private SteamVR_Action_Skeleton steam;
        private readonly int hand;
        internal VRSkeleton(Func<SteamVR_Action_Skeleton> resolve, int hand) { this.resolve = resolve; this.hand = hand; }
        private SteamVR_Action_Skeleton S => steam ?? (steam = resolve());

        // OpenXR: curls from XR_EXT_hand_tracking (OpenXRHandTracking). Without them it reads as a standard
        // controller ("estimated"), so FingerTracking keeps the baked poses - what SteamVR does for Touch.
        public bool active => VRInput.OpenXR ? OpenXRHandTracking.Active(hand) : S.active;
        public float[] fingerCurls => VRInput.OpenXR ? OpenXRHandTracking.Curls(hand) : S.fingerCurls;
        public EVRSkeletalTrackingLevel skeletalTrackingLevel
            => VRInput.OpenXR
                ? (OpenXRHandTracking.Active(hand) ? EVRSkeletalTrackingLevel.VRSkeletalTracking_Partial : EVRSkeletalTrackingLevel.VRSkeletalTracking_Estimated)
                : S.skeletalTrackingLevel;
    }
}
