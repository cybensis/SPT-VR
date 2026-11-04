using System;
using System.Collections.Generic;
using TarkovVR.Source.XR;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // Every controller read in the mod goes through here instead of SteamVR_Actions._default. Same action names and
    // members as SteamVR's, so call sites only changed their prefix. With SteamVR each action just calls the SteamVR
    // action. With OpenXR the values come from OpenXRInput, bound like the mod's SteamVR bindings: each action
    // belongs to one controller, and asking for the other hand reads it as not pressed.
    public static class VRInput
    {
        public static readonly VRBoolean RightGrip = new VRBoolean(() => SteamVR_Actions._default.RightGrip, OpenXRInput.RIGHT_HAND, OpenXRInput.Button.Grip);
        public static readonly VRBoolean LeftGrip = new VRBoolean(() => SteamVR_Actions._default.LeftGrip, OpenXRInput.LEFT_HAND, OpenXRInput.Button.Grip);
        public static readonly VRBoolean ButtonA = new VRBoolean(() => SteamVR_Actions._default.ButtonA, OpenXRInput.RIGHT_HAND, OpenXRInput.Button.Primary);
        public static readonly VRBoolean ButtonB = new VRBoolean(() => SteamVR_Actions._default.ButtonB, OpenXRInput.RIGHT_HAND, OpenXRInput.Button.Secondary);
        public static readonly VRBoolean ButtonX = new VRBoolean(() => SteamVR_Actions._default.ButtonX, OpenXRInput.LEFT_HAND, OpenXRInput.Button.Primary);
        public static readonly VRBoolean ButtonY = new VRBoolean(() => SteamVR_Actions._default.ButtonY, OpenXRInput.LEFT_HAND, OpenXRInput.Button.Secondary);
        public static readonly VRBoolean ClickRightJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickRightJoystick, OpenXRInput.RIGHT_HAND, OpenXRInput.Button.StickClick);
        // The Index bindings also map touching the left trackpad to this
        public static readonly VRBoolean ClickLeftJoystick = new VRBoolean(() => SteamVR_Actions._default.ClickLeftJoystick, OpenXRInput.LEFT_HAND, OpenXRInput.Button.StickClickOrTrackpadTouch);
        // Not bound to anything in the Index / Touch bindings
        public static readonly VRBoolean Start = new VRBoolean(() => SteamVR_Actions._default.Start, OpenXRInput.LEFT_HAND, OpenXRInput.Button.None);

        public static readonly VRSingle RightTrigger = new VRSingle(() => SteamVR_Actions._default.RightTrigger, OpenXRInput.RIGHT_HAND);
        public static readonly VRSingle LeftTrigger = new VRSingle(() => SteamVR_Actions._default.LeftTrigger, OpenXRInput.LEFT_HAND);

        public static readonly VRVector2 RightJoystick = new VRVector2(() => SteamVR_Actions._default.RightJoystick, OpenXRInput.RIGHT_HAND);
        public static readonly VRVector2 LeftJoystick = new VRVector2(() => SteamVR_Actions._default.LeftJoystick, OpenXRInput.LEFT_HAND);

        public static readonly VRPose RightHandPose = new VRPose(() => SteamVR_Actions._default.RightHandPose, OpenXRInput.RIGHT_HAND);
        public static readonly VRPose LeftHandPose = new VRPose(() => SteamVR_Actions._default.LeftHandPose, OpenXRInput.LEFT_HAND);

        public static readonly VRVibration Haptic = new VRVibration(() => SteamVR_Actions._default.Haptic);

        public static readonly VRSkeleton SkeletonRightHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonRightHand, OpenXRInput.RIGHT_HAND);
        public static readonly VRSkeleton SkeletonLeftHand = new VRSkeleton(() => SteamVR_Actions._default.SkeletonLeftHand, OpenXRInput.LEFT_HAND);

        internal static bool UsingOpenXR => OpenXRBootstrap.Active;
    }

    // The SteamVR actions are looked up on first use, SteamVR_Actions._default only exists after SteamVR_Actions.PreInitialize()

    public class VRBoolean
    {
        private readonly Func<SteamVR_Action_Boolean> getSteamAction;
        private SteamVR_Action_Boolean steamAction;
        private readonly int hand;
        private readonly OpenXRInput.Button button;

        internal VRBoolean(Func<SteamVR_Action_Boolean> getSteamAction, int hand, OpenXRInput.Button button)
        {
            this.getSteamAction = getSteamAction;
            this.hand = hand;
            this.button = button;
        }

        private SteamVR_Action_Boolean SteamAction => steamAction ?? (steamAction = getSteamAction());

        public bool state => VRInput.UsingOpenXR ? OpenXRInput.GetButton(hand, button, OpenXRInput.ButtonState.Held) : SteamAction.state;
        public bool stateDown => VRInput.UsingOpenXR ? OpenXRInput.GetButton(hand, button, OpenXRInput.ButtonState.PressedThisFrame) : SteamAction.stateDown;
        public bool stateUp => VRInput.UsingOpenXR ? OpenXRInput.GetButton(hand, button, OpenXRInput.ButtonState.ReleasedThisFrame) : SteamAction.stateUp;

        public bool GetState(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) && state;
            return SteamAction.GetState(inputSource);
        }

        public bool GetStateDown(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) && stateDown;
            return SteamAction.GetStateDown(inputSource);
        }

        public bool GetStateUp(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) && stateUp;
            return SteamAction.GetStateUp(inputSource);
        }
    }

    public class VRSingle
    {
        private readonly Func<SteamVR_Action_Single> getSteamAction;
        private SteamVR_Action_Single steamAction;
        private readonly int hand;

        internal VRSingle(Func<SteamVR_Action_Single> getSteamAction, int hand)
        {
            this.getSteamAction = getSteamAction;
            this.hand = hand;
        }

        private SteamVR_Action_Single SteamAction => steamAction ?? (steamAction = getSteamAction());

        public float axis => VRInput.UsingOpenXR ? OpenXRInput.GetTrigger(hand) : SteamAction.axis;

        // A finger resting on the trigger. False = can't tell: SteamVR has no touch action in the mod's manifest, and some
        // controllers have no sensor there.
        public bool TryGetTouch(out bool touched)
        {
            touched = false;
            return VRInput.UsingOpenXR && OpenXRInput.TryGetTriggerTouch(hand, out touched);
        }

        public float GetAxis(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? axis : 0f;
            return SteamAction.GetAxis(inputSource);
        }
    }

    public class VRVector2
    {
        private readonly Func<SteamVR_Action_Vector2> getSteamAction;
        private SteamVR_Action_Vector2 steamAction;
        private readonly int hand;

        internal VRVector2(Func<SteamVR_Action_Vector2> getSteamAction, int hand)
        {
            this.getSteamAction = getSteamAction;
            this.hand = hand;
        }

        private SteamVR_Action_Vector2 SteamAction => steamAction ?? (steamAction = getSteamAction());

        public Vector2 axis => VRInput.UsingOpenXR ? OpenXRInput.GetStick(hand) : SteamAction.axis;

        public Vector2 GetAxis(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? axis : Vector2.zero;
            return SteamAction.GetAxis(inputSource);
        }
    }

    public class VRPose
    {
        public delegate void UpdateHandler(VRPose fromAction, SteamVR_Input_Sources fromSource);

        private readonly Func<SteamVR_Action_Pose> getSteamAction;
        private SteamVR_Action_Pose steamAction;
        private readonly int hand;

        // Our own listeners per input source. With SteamVR, one SteamVR listener per source calls them, which keeps
        // SteamVR's calling order. With OpenXR, OpenXRInput calls them from onBeforeRender.
        private readonly Dictionary<SteamVR_Input_Sources, UpdateHandler> listeners = new Dictionary<SteamVR_Input_Sources, UpdateHandler>();
        private readonly Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler> steamListeners = new Dictionary<SteamVR_Input_Sources, SteamVR_Action_Pose.UpdateHandler>();
        private readonly List<SteamVR_Input_Sources> sourcesToFire = new List<SteamVR_Input_Sources>();

        internal VRPose(Func<SteamVR_Action_Pose> getSteamAction, int hand)
        {
            this.getSteamAction = getSteamAction;
            this.hand = hand;
        }

        private SteamVR_Action_Pose SteamAction => steamAction ?? (steamAction = getSteamAction());

        // The pose listeners read these off fromAction
        public Vector3 localPosition => VRInput.UsingOpenXR ? OpenXRInput.GetRawPosition(hand) : SteamAction.localPosition;
        public Quaternion localRotation => VRInput.UsingOpenXR ? OpenXRInput.GetRawRotation(hand) : SteamAction.localRotation;

        public Vector3 GetLocalPosition(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? localPosition : Vector3.zero;
            return SteamAction.GetLocalPosition(inputSource);
        }

        public Quaternion GetLocalRotation(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? localRotation : Quaternion.identity;
            return SteamAction.GetLocalRotation(inputSource);
        }

        public Vector3 GetVelocity(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? OpenXRInput.GetRawVelocity(hand) : Vector3.zero;
            return SteamAction.GetVelocity(inputSource);
        }

        public Vector3 GetAngularVelocity(SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                return OpenXRInput.IsSourceForHand(inputSource, hand) ? OpenXRInput.GetAngularVelocity(hand) : Vector3.zero;
            return SteamAction.GetAngularVelocity(inputSource);
        }

        public void AddOnUpdateListener(SteamVR_Input_Sources inputSource, UpdateHandler handler)
        {
            listeners.TryGetValue(inputSource, out UpdateHandler existingHandlers);
            listeners[inputSource] = existingHandlers + handler;

            if (VRInput.UsingOpenXR || steamListeners.ContainsKey(inputSource))
                return;

            SteamVR_Action_Pose.UpdateHandler steamListener = (action, source) => FireListeners(source);
            steamListeners[inputSource] = steamListener;
            SteamAction.AddOnUpdateListener(inputSource, steamListener);
        }

        public void RemoveOnUpdateListener(SteamVR_Input_Sources inputSource, UpdateHandler handler)
        {
            if (listeners.TryGetValue(inputSource, out UpdateHandler existingHandlers))
                listeners[inputSource] = existingHandlers - handler;
        }

        // Removes every listener on that source, SteamVR's included, same as SteamVR's own RemoveAllListeners
        public void RemoveAllListeners(SteamVR_Input_Sources inputSource)
        {
            listeners.Remove(inputSource);
            steamListeners.Remove(inputSource);
            if (!VRInput.UsingOpenXR)
                SteamAction.RemoveAllListeners(inputSource);
        }

        // Called once a frame by OpenXRInput. Copies the sources first, a listener may add or remove listeners.
        internal void FireOpenXRListeners()
        {
            sourcesToFire.Clear();
            sourcesToFire.AddRange(listeners.Keys);
            foreach (SteamVR_Input_Sources inputSource in sourcesToFire)
            {
                if (OpenXRInput.IsSourceForHand(inputSource, hand))
                    FireListeners(inputSource);
            }
        }

        private void FireListeners(SteamVR_Input_Sources inputSource)
        {
            if (listeners.TryGetValue(inputSource, out UpdateHandler handlers) && handlers != null)
                handlers(this, inputSource);
        }
    }

    public class VRVibration
    {
        private readonly Func<SteamVR_Action_Vibration> getSteamAction;
        private SteamVR_Action_Vibration steamAction;

        internal VRVibration(Func<SteamVR_Action_Vibration> getSteamAction)
        {
            this.getSteamAction = getSteamAction;
        }

        private SteamVR_Action_Vibration SteamAction => steamAction ?? (steamAction = getSteamAction());

        public void Execute(float secondsFromNow, float durationSeconds, float frequency, float amplitude, SteamVR_Input_Sources inputSource)
        {
            if (VRInput.UsingOpenXR)
                OpenXRInput.SendHaptic(secondsFromNow, durationSeconds, frequency, amplitude, inputSource);
            else
                SteamAction.Execute(secondsFromNow, durationSeconds, frequency, amplitude, inputSource);
        }
    }

    public class VRSkeleton
    {
        private readonly Func<SteamVR_Action_Skeleton> getSteamAction;
        private SteamVR_Action_Skeleton steamAction;
        private readonly int hand;

        internal VRSkeleton(Func<SteamVR_Action_Skeleton> getSteamAction, int hand)
        {
            this.getSteamAction = getSteamAction;
            this.hand = hand;
        }

        private SteamVR_Action_Skeleton SteamAction => steamAction ?? (steamAction = getSteamAction());

        // With OpenXR and no finger data (not an Index), this reads like a standard controller and
        // FingerTracking keeps the baked poses, same as SteamVR does for Touch controllers
        public bool active => VRInput.UsingOpenXR ? OpenXRHandTracking.IsActive(hand) : SteamAction.active;
        public float[] fingerCurls => VRInput.UsingOpenXR ? OpenXRHandTracking.GetCurls(hand) : SteamAction.fingerCurls;

        public EVRSkeletalTrackingLevel skeletalTrackingLevel
        {
            get
            {
                if (!VRInput.UsingOpenXR)
                    return SteamAction.skeletalTrackingLevel;
                if (OpenXRHandTracking.IsActive(hand))
                    return EVRSkeletalTrackingLevel.VRSkeletalTracking_Partial;
                return EVRSkeletalTrackingLevel.VRSkeletalTracking_Estimated;
            }
        }
    }
}
