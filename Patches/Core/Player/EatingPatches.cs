using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Patches.Core.Player
{
    // Routes the game's MedsController lifecycle into the manual (gesture-gated)
    // eating controller. See EatingInteractionController for the full design — the
    // short version is: we suppress the food's heal/nutrition effect (method_5 ->
    // DoMedEffect) until the player physically brings the item to their mouth, then
    // let the vanilla consume play out normally.
    [HarmonyPatch]
    internal class EatingPatches
    {

        // PREFIX: arm the controller BEFORE the original Spawn body runs
        // Start()->method_5(), so AllowEffect can suppress that initial effect.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPre(MedsController __instance, float animationSpeed)
        {
            EatingInteractionController.OnSpawnPre(__instance, animationSpeed);
        }

        // POSTFIX: freeze the held item's animator (Spawn just set it to play).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPost(MedsController __instance)
        {
            EatingInteractionController.OnSpawnPost(__instance);
        }

        // The gate: suppress the real DoMedEffect until the player has raised the
        // food to their mouth (returns false to skip the original method).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController.ObservedMedsControllerClass), "method_5")]
        public static bool GateEffect(MedsController.ObservedMedsControllerClass __instance)
        {
            return EatingInteractionController.AllowEffect(__instance);
        }

        // Per-frame tick on the held-item controller — reads the gesture and, once
        // satisfied, kicks off the suppressed effect.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "ManualUpdate")]
        public static void OnUpdate(MedsController __instance)
        {
            EatingInteractionController.Tick(__instance);
        }

        // Suppress the body eat animation while we're running the manual sequence.
        // IEventsConsumerOnThirdAction only feeds BodyAnimatorCommon's
        // FIRST_PERSON_ACTION (via TranslateAnimatorParameter); blocking it keeps
        // LEFT_HAND_ANIMATOR_HASH at 0 so UpdateLeftHand keeps both hands tracking.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnThirdAction")]
        public static bool OnThirdAction(MedsController __instance, int IntParam)
        {
            return EatingInteractionController.AllowThirdAction(__instance);
        }

        // Switching off the item (swap weapon / cancel to empty hands).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnWeapOut")]
        public static void OnWeapOut()
        {
            EatingInteractionController.End();
        }

        // PREFIX: clean up (restore the reparented props) BEFORE the original
        // Destroy returns the controller object to the pool — a postfix is too late.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Destroy")]
        public static void OnDestroy()
        {
            EatingInteractionController.End();
        }

        // After the animator runs (LateUpdate), zero the props' animator-driven local
        // transforms so they sit on their (tunable) holders. See LateZeroProps.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        public static void LateZeroProps(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer) return;
            EatingInteractionController.LateZeroProps();
        }

        // Suppress our controller's animation-driven sounds so they don't burst at the
        // finish — we play the right ones per-step ourselves.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "OnSound")]
        public static bool OnSoundGate(BaseSoundPlayer __instance, string StringParam)
        {
            return EatingInteractionController.AllowSound(__instance);
        }

        // Keep the LEFT hand tracking the controller during manual eating. Normally
        // UpdateLeftHand early-returns while LEFT_HAND_ANIMATOR_HASH==1 (eat anim),
        // freezing LeftHand + disabling leftArmIk. While we're active we replicate
        // the simple non-supporting placement and skip the original. controllerLength
        // is 0.175 (VRPlayerManager). Returns false to bypass the original.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VRPlayerManager), "UpdateLeftHand")]
        public static bool ForceLeftHandTracking(VRPlayerManager __instance, SteamVR_Action_Pose fromAction)
        {
            if (!EatingInteractionController.ManualActive) return true; // normal behavior
            GameObject lh = __instance.LeftHand;
            if (lh == null || fromAction == null) return true;

            // Match the rig's normal off-hand placement (VRPlayerManager.UpdateLeftHand
            // ~line 1020), INCLUDING the rotation offsets — omitting them left the left
            // hand ~110° twisted vs the controller.
            Vector3 secPosOff = __instance.secondaryHandPosOffset;
            Vector3 secRotOff = __instance.secondaryHandRotOffset;

            Transform t = lh.transform;
            t.localPosition = (fromAction.localPosition - fromAction.localRotation * Vector3.forward * 0.175f) + secPosOff;
            t.localRotation = fromAction.localRotation;
            t.Rotate(VRSettings.GetSecondaryHandVertOffset() + secRotOff.x, secRotOff.y, VRSettings.GetSecondaryHandHorOffset() + secRotOff.z);

            if (VRGlobals.ikManager?.leftArmIk != null)
            {
                VRGlobals.ikManager.leftArmIk.solver.target = t;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
            return false; // skip the original (which would freeze the left hand)
        }
    }
}
