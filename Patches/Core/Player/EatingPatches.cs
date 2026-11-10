using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using TarkovVR.Source.Controls;
using static EFT.Player;
using static EFT.Player.MedsController;

namespace TarkovVR.Patches.Core.Player
{
    // Routes the game's MedsController lifecycle into the manual (gesture-gated) eating controller,
    // EatingInteractionController. The food's heal/nutrition effect (method_5 -> DoMedEffect) is
    // suppressed until the gestures allow it.
    [HarmonyPatch]
    internal class EatingPatches
    {

        // Arm the controller before the original Spawn body runs Start()->method_5(), so AllowEffect can suppress
        // that initial effect.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPre(MedsController __instance, float animationSpeed)
        {
            EatingInteractionController.OnSpawnPre(__instance, animationSpeed);
        }

        // Freeze the held item's animator (Spawn just set it to play).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPost(MedsController __instance)
        {
            // The gun drawn back after it goes to its spot (ChestHang.hangAfterMeds)
            if (__instance._player != null && __instance._player.IsYourPlayer)
                TarkovVR.Source.Player.Interactions.ChestHang.NoteMedsInHands();
            EatingInteractionController.OnSpawnPost(__instance);
        }

        // The gate: suppress the real DoMedEffect until the gestures allow it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsInHandsOperation), "GoNextBodyPart")]
        public static bool GateEffect(MedsInHandsOperation __instance)
        {
            return EatingInteractionController.AllowEffect(__instance);
        }

        // Per-frame tick on the held-item controller (the gesture machine)
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "ManualUpdate")]
        public static void OnUpdate(MedsController __instance)
        {
            EatingInteractionController.Tick(__instance);
        }

        // Suppress the body eat animation during the manual sequence. IEventsConsumerOnThirdAction only feeds
        // BodyAnimatorCommon's FIRST_PERSON_ACTION (via TranslateAnimatorParameter); blocking it keeps
        // LEFT_HAND_ANIMATOR_HASH at 0 so UpdateLeftHand keeps both hands tracking.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnThirdAction")]
        public static bool OnThirdAction(MedsController __instance, int IntParam)
        {
            return EatingInteractionController.AllowThirdAction(__instance);
        }

        // Switching off the item (swap weapon / cancel to empty hands). Fires for every MedsController in the raid:
        // OnControllerGone gates on our armed controller so a bot's weap-out doesn't tear down the local eat.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnWeapOut")]
        public static void OnWeapOut(MedsController __instance)
        {
            EatingInteractionController.OnControllerGone(__instance);
        }

        // Restore the reparented props before the original Destroy returns the controller object to the pool
        // (a postfix is too late). Fires for every MedsController, gated like OnWeapOut.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Destroy")]
        public static void OnDestroy(MedsController __instance)
        {
            EatingInteractionController.OnControllerGone(__instance);
        }

        // After the animator runs (LateUpdate), zero the props' animator-driven local transforms so they sit on
        // their holders.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        public static void LateZeroProps(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer) return;
            EatingInteractionController.LateZeroProps();
        }

        // Suppress our controller's animation-driven sounds so they don't burst at the finish; the steps play their own.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "SoundEventHandler")]
        public static bool OnSoundGate(BaseSoundPlayer __instance, string soundName)
        {
            bool allow = EatingInteractionController.AllowSound(__instance, soundName);
            // A sound passing the gate on our eat's sound player is forwarded so co-op observers hear it (they mute the
            // looping vanilla observed-meds sound). FIKA-gated so the FikaPlayer-touching send only JITs with FIKA.
            // Wrapped: this is the eat-audio path, so a throw here would kill the local eater's own audio.
            if (allow && TarkovVR.ModSupport.InstalledMods.FIKAInstalled
                && EatingInteractionController.IsOwnEatSound(__instance))
            {
                try { TarkovVR.ModSupport.FIKA.VRArmSync.SendEatSound(soundName); }
                catch (System.Exception exception)
                {
                    if (!_loggedSoundFwdErr) { _loggedSoundFwdErr = true; Plugin.MyLog.LogError($"[ManualEat] eat-sound forward failed: {exception}"); }
                }
            }
            return allow;
        }
        private static bool _loggedSoundFwdErr;

        // Our controller's clips play head-locked 2D (PlayClipHeadLocked): the vanilla PlayClip path leaves the pooled
        // source 3D-blended on the VR-reparented weapon transform (intermittently quiet/mislocated/silent).
        // Skips the original when it handled the clip; the haptics postfix below still runs.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "PlayClip")]
        public static bool ForceHeadLockedEatAudio(BaseSoundPlayer __instance, AudioClip clip, int rolloff, float volume)
        {
            return !EatingInteractionController.PlayClipHeadLocked(__instance, clip, rolloff, volume);
        }

        // Audio -> haptics: every audible clip on our controller's sound player also plays its waveform envelope on the
        // controllers. PlayClip is the one funnel every clip goes through, after the distance/occlusion culls.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "PlayClip")]
        public static void AudioToHaptics(BaseSoundPlayer __instance, AudioClip clip, float volume)
        {
            EatingInteractionController.OnEatAudio(__instance, clip, volume);
        }

        // Keep the left hand tracking the controller during manual eating. UpdateLeftHand early-returns while
        // LEFT_HAND_ANIMATOR_HASH==1 (eat anim), freezing LeftHand and disabling leftArmIk, so this does the simple
        // non-supporting placement and skips the original.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VRPlayerManager), "UpdateLeftHand")]
        public static bool ForceLeftHandTracking(VRPlayerManager __instance, VRPose fromAction)
        {
            if (!EatingInteractionController.ManualActive) return true; // normal behavior
            GameObject lh = __instance.LeftHand;
            if (lh == null || fromAction == null) return true;

            __instance.PlaceOffHand(fromAction);

            // While a pull-to-open has the left hand latched onto the held item, the rig target still updates (so release
            // lands on the controller) but the IK isn't re-pointed at it: this runs on the SteamVR pose callback, after the
            // eat Tick aimed the IK at the latch, and would yank the hand off the item mid-pull.
            if (VRGlobals.ikManager?.leftArmIk != null && !EatingInteractionController.OffHandLatched)
            {
                VRGlobals.ikManager.leftArmIk.solver.target = lh.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
            return false; // skip the original (which would freeze the left hand)
        }
    }
}
