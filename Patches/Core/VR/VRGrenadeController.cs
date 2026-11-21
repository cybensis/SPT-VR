using HarmonyLib;
using System;
using System.Text;
using static EFT.Player;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;
using EFT;
using TarkovVR.ModSupport;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class VRGrenadeController
    {
        
        public static bool throwInProgress = false;
        public static BaseGrenadeHandsController activeGrenadeController = null;
        private static BaseGrenadeHandsController thrownBy;
        private static float lastNormalizedTime = 0f;
        private static float freezeStartTime = 0f;
        private static bool isFrozen = false;
        private static readonly float freezeDuration = 0.5f; // Or use Random.Range(2f, 3f) when setting

        // true: the grenade leaves the hand at the release and its use ends that same frame, doing what the throw clip's end does
        // (FireEnd -> OnDropFinishedAction: the hands go back to what they held before) without the clip. false: the clip plays on from
        // 40 % after the release and the hands come back at its end, 0.3-0.5 s later.
        private static float releasedAt;

        public static float tripwireHoldDuration = 0.4f;        // long-hold time (s) on the face button to toggle plant mode
        public static float tripwireRayForwardOffset = 0.3f;    // ray start nudge ahead of the controller (matches native beacon code)

        // Dominant hand = the throw/gun hand, always VRGlobals.vrPlayer.RightHand (the pose listeners swap in left-handed mode).
        // Falls back to the headset ray if the rig isn't up yet.
        public static Ray GetTripwirePlacementRay()
        {
            if (VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.RightHand != null)
            {
                Transform hand = VRGlobals.vrPlayer.RightHand.transform;
                return new Ray(hand.position + hand.forward * tripwireRayForwardOffset, hand.forward);
            }
            Transform cameraTransform = VRGlobals.VRCam.transform;
            return new Ray(cameraTransform.position + cameraTransform.forward * tripwireRayForwardOffset, cameraTransform.forward);
        }

        // The first post is seeded by Player.InitFirstTripwirePoint(), which natively passes the head-bone
        // InteractionRay. Redirect it to the dominant controller ray so the locked first post matches the ghost.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "InitFirstTripwirePoint")]
        private static bool VRTripwireFirstPoint(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || VRGlobals.vrPlayer == null || __instance.TripwirePlanner == null)
                return true;

            __instance.TripwirePlanner.InitFirstPoint(GetTripwirePlacementRay());
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GrenadeHandsController), "Spawn")]
        static void HandleGrenade(GrenadeHandsController __instance, float animationSpeed, Action callback)
        {
            var player = __instance._player;

            if (!player.IsYourPlayer)
                return;
            GrenadeHold.NoteSpawn(__instance);

            WeaponPatches.grenadeEquipped = true;
            WeaponPatches.pinPulled = false;
            VRGlobals.switchingWeapon = true;
            InitVRPatches.rightPointerFinger.enabled = false;

            if (WeaponPatches.currentGunInteractController != null)
                WeaponPatches.currentGunInteractController.enabled = false;

            VRGlobals.player = player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.usingItem = false;

            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];
            VRGlobals.oldWeaponHolder = __instance.HandsHierarchy.gameObject;
            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    WeaponPatches.currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    WeaponPatches.currentGunInteractController.enabled = true;
                    WeaponPatches.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;

                }
                else
                {
                    WeaponPatches.currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    WeaponPatches.currentGunInteractController.Init();
                    WeaponPatches.currentGunInteractController.initialized = true;
                    WeaponPatches.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());

                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //Transform handTransform = VRGlobals.vrPlayer.RightHand.transform; //figure out why this works
                //VRGlobals.weaponHolder.transform.SetParent(handTransform, false);

                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                WeaponPatches.weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");
            }
            else if (__instance.WeaponRoot.parent.parent.name == "RightHandPositioner")
            {
                __instance.WeaponRoot.transform.parent = __instance.WeaponRoot.transform.parent.parent.parent;
                HandleGrenade(__instance, animationSpeed, callback);
                return;
            }
            //Plugin.MyLog.LogError($"Weaponroot parent: {__instance.WeaponRoot.parent.name}");
            if (VRGlobals.player)
            {
                WeaponPatches.previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.leftArmBendGoal.localPosition = new Vector3(-0.5f, -0.3f, -0.4f);
                //VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            }

            if (VRSettings.GetLeftHandedMode())
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }

            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
            VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
        }

        // After the prefix above placed it: a grenade taken off the chest (GrenadeSlot) has its draw skipped, and goes into the left hand
        // if the left took it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController), "Spawn")]
        private static void GrenadeFromChestSlot(GrenadeHandsController __instance)
        {
            if (__instance?._player == null || !__instance._player.IsYourPlayer)
                return;
            TarkovVR.Source.Player.Interactions.GrenadeSlot.OnGrenadeSpawned(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void CheckGrenadeAnimation(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || !throwInProgress)
                return;
            // The grenade left the hands before the clip reached the freeze below: the throw is over. Left set, throwInProgress keeps
            // GrenadeHold's rushes off the next grenade (its draw and pin pull would play out in full)
            if (activeGrenadeController == null || activeGrenadeController._firearmsAnimator == null
                || !ReferenceEquals(__instance.HandsController, activeGrenadeController))
            {
                EndThrow();
                return;
            }
            // Thrown this frame (the release runs in the input update): end the use here, not inside the release. Ending it swaps the
            // hands' controller, which the rest of the release (the holder, emptyHands) mustn't then touch
            // Co-op client: ending the throw sends a hands change, which the host refuses until it has answered the last move
            if (CoopHost.HoldsBack(releasedAt))
                return;
            BaseGrenadeHandsController thrownNow = activeGrenadeController;
            EndThrow();
            thrownNow.OnDropFinishedAction();
        }

        // The grenade's own animators while its clip is frozen: their speed is restored if the throw ends before the freeze does (the
        // hands object goes back to Tarkov's pool with them)
        private static Animator[] frozenAnimators;

        private static void EndThrow()
        {
            if (isFrozen && frozenAnimators != null)
            {
                foreach (Animator animator in frozenAnimators)
                {
                    if (animator != null && animator.speed == 0f)
                        animator.speed = 1f;
                }
            }
            frozenAnimators = null;
            throwInProgress = false;
            activeGrenadeController = null;
            isFrozen = false;
            lastNormalizedTime = 0f;
        }

        // GrenadeHold: is this grenade's own throw playing (its animator ours to freeze)
        internal static bool Throwing(BaseGrenadeHandsController grenade)
        {
            return throwInProgress && grenade != null && ReferenceEquals(activeGrenadeController, grenade);
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "ThrowGrenadeInner")]
        private static bool RepositionGrenadeThrow(BaseGrenadeHandsController __instance, ref Vector3? throwPosition, float timeSinceSafetyLevelRemoved, float lowHighThrow, Vector3 direction, float forcePower, bool lowThrow, bool withVelocity)
        {
            if (!__instance._player.IsYourPlayer)
                return true;
            if (!WeaponPatches.pinPulled)
                return false;
            // Thrown already: the throw clip's own drop event calls this again (Throw), with the hand slowing down by then
            if (ReferenceEquals(__instance, thrownBy))
                return false;
            thrownBy = __instance;

            // The hand holding it (the left if the left hand took it off the chest: GrenadeSlot). On OpenXR the palm's own runtime
            // velocity (ThrowVelocity.TryPalmVelocity): the raw point's rebuilt velocity bends throws sideways. Else the runtime's.
            SteamVR_Input_Sources throwingHand = Handedness.GunHandSource;
            Vector3 throwVelocity = ThrowVelocity.TryPalmVelocity(throwingHand, out Vector3 palmVelocity)
                ? palmVelocity : ControllerVelocity.GetSteamVRVelocity(throwingHand);
            Vector3 force;
            Vector3 throwPos = ControllerVelocity.GetSteamVRPosition(throwingHand);
            Quaternion throwRot = ControllerVelocity.GetSteamVRRotation(throwingHand);
            // Released where the grenade is in the hand (Tarkov's own spot, pulled in off a wall between it and the body), not at the
            // controller's tracking point ~13 cm from it
            bool fromHand = __instance._grenadeTransform != null;
            // A fresh throw: nothing carried over from one that ended early (its freeze state, the clip time it last saw)
            EndThrow();
            throwInProgress = true;
            activeGrenadeController = __instance;
            releasedAt = Time.time;
            // Co-op client: the hands' way back (and any grab) waits for the host's copy to finish this throw
            CoopHost.GrenadeThrown(TarkovVR.Source.Weapons.GrenadeHold.PinSentAt);

            if (throwVelocity.magnitude > 0.1f)
            {
                // Transform from controller local space to world space
                Vector3 worldSpaceVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(throwVelocity);

                float grenadeVelocityMultiplier = 1.5f;
                force = worldSpaceVelocity * grenadeVelocityMultiplier;

                if (force.magnitude > 15f)
                {
                    force = force.normalized * 15f;
                }
            }
            else
            {
                Vector3 defaultDirection = VRGlobals.vrPlayer.RightHand.transform.forward;
                force = defaultDirection * (forcePower * lowHighThrow * 0.5f);
            }

            if (withVelocity)
            {
                force += __instance._player.Velocity;
            }

            __instance.ThrowGrenade(
                timeSinceSafetyLevelRemoved,
                fromHand ? __instance.FindThrowPosition() : VRGlobals.vrOffsetter.transform.TransformPoint(throwPos),
                fromHand ? __instance._grenadeTransform.rotation : VRGlobals.vrOffsetter.transform.rotation * throwRot,
                force,
                lowThrow
            );

            // The throw clip's fuse crack (SoundAtPoint SndFuse, just after its drop on every grenade) doesn't play when the throw ends
            // at the release. Played now, while the hands are out: their sound player lets a playing clip finish when they go back to
            // the pool. No SndFuse in a grenade's sounds (M67, RDG-2) = silent, as in the clip.
            if (__instance._controllerObject != null)
            {
                BaseSoundPlayer grenadeSounds = __instance._controllerObject.GetComponent<BaseSoundPlayer>();
                if (grenadeSounds != null)
                    grenadeSounds.SoundAtPointEventHandler("SndFuse");
            }

            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            VRGlobals.emptyHands = null;
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Diagnostics (GrenadeSlot.debugLog): a grenade drawn without a grab - who drew it (e.g. PlayerOwner's after-use callback
        // drawing the grenade swapped from, see GrenadeSlot.SwapHeldTo)

        // The calls that led here, the game's and ours (no Harmony / System frames), newest first
        internal static string ShortStack()
        {
            System.Diagnostics.StackFrame[] frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            if (frames == null)
                return "(no stack)";
            StringBuilder line = new StringBuilder();
            int kept = 0;
            foreach (System.Diagnostics.StackFrame frame in frames)
            {
                System.Reflection.MethodBase method = frame.GetMethod();
                string space = method?.DeclaringType?.Namespace ?? "";
                if (method == null || space.StartsWith("System") || space.StartsWith("HarmonyLib") || space.StartsWith("MonoMod"))
                    continue;
                if (kept++ > 0)
                    line.Append(" < ");
                line.Append(method.DeclaringType != null ? method.DeclaringType.Name + "." : "").Append(method.Name);
                if (kept >= 18)
                    break;
            }
            return line.ToString();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "IEventsConsumerOnWeapOut")]
        private static bool DisableGrenadeStuffAfterCancel(BaseGrenadeHandsController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return true;
            // The hide clip ending after the put-away was already skipped (GrenadeSlot.SkipPutAway calls this event early): the hands
            // are the next item's by now - its holder isn't ours to turn, and the put-away mustn't finish twice
            if (!ReferenceEquals(__instance._player.HandsController, __instance))
                return false;

            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            VRGlobals.emptyHands = null;
            WeaponPatches.pinPulled = false;

            return true;
        }
    }
}
