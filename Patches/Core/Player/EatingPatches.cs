using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using TarkovVR.Source.Controls;
using static EFT.Player;
using static EFT.Player.MedsController;

namespace TarkovVR.Patches.Core.Player
{
    // Routes the game's MedsController lifecycle into EatingInteractionController, the gesture-driven manual eating.
    // The food's heal/nutrition effect (method_5 -> DoMedEffect) is held back until the gestures allow it.
    [HarmonyPatch]
    internal class EatingPatches
    {

        // Arms the controller before the original Spawn body runs Start() -> method_5(), so AllowEffect can hold back
        // that first effect.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPre(MedsController __instance, float animationSpeed)
        {
            EatingInteractionController.OnSpawnPre(__instance, animationSpeed);
        }

        // Freezes the held item's animator (Spawn just set it to play).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        public static void OnSpawnPost(MedsController __instance)
        {
            // Lets ChestHang send the gun Tarkov draws back after eating to its body spot (ChestHang.hangAfterMeds)
            if (__instance._player != null && __instance._player.IsYourPlayer)
                TarkovVR.Source.Player.Interactions.ChestHang.NoteMedsInHands();
            EatingInteractionController.OnSpawnPost(__instance);
        }

        // Holds back the real DoMedEffect until the gestures allow it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsInHandsOperation), "GoNextBodyPart")]
        public static bool GateEffect(MedsInHandsOperation __instance)
        {
            return EatingInteractionController.AllowEffect(__instance);
        }

        // Runs the gesture state machine once per frame of the held-item controller
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "ManualUpdate")]
        public static void OnUpdate(MedsController __instance)
        {
            EatingInteractionController.Tick(__instance);
        }

        // Blocks the body eat animation during manual eating. IEventsConsumerOnThirdAction only feeds BodyAnimatorCommon's
        // FIRST_PERSON_ACTION (via TranslateAnimatorParameter). Blocking it keeps LEFT_HAND_ANIMATOR_HASH at 0, so
        // UpdateLeftHand keeps both hands tracking.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnThirdAction")]
        public static bool OnThirdAction(MedsController __instance, int IntParam)
        {
            return EatingInteractionController.AllowThirdAction(__instance);
        }

        // Switching away from the item (weapon swap or cancel to empty hands). This fires for every MedsController in the raid;
        // OnControllerGone only acts on our armed controller, so a bot's weapon-out doesn't end the local eat.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "IEventsConsumerOnWeapOut")]
        public static void OnWeapOut(MedsController __instance)
        {
            EatingInteractionController.OnControllerGone(__instance);
        }

        // Restores the reparented props before the original Destroy returns the controller object to the pool
        // (a postfix is too late). Fires for every MedsController and is filtered like OnWeapOut.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MedsController), "Destroy")]
        public static void OnDestroy(MedsController __instance)
        {
            EatingInteractionController.OnControllerGone(__instance);
        }

        // After the animator runs (LateUpdate), zeroes the props' animator-driven local transforms so they sit on
        // their holders.
        // Player.LateUpdate postfix stage (FramePipeline)
        internal static void LateZeroProps(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer) return;
            EatingInteractionController.LateZeroProps();
        }

        // Mutes our controller's animation-driven sounds so they don't all play at once at the finish; the gesture steps
        // play their own.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "SoundEventHandler")]
        public static bool OnSoundGate(BaseSoundPlayer __instance, string soundName)
        {
            bool allow = EatingInteractionController.AllowSound(__instance, soundName);
            // A sound that passes on our eat's sound player is forwarded so co-op observers hear it (they mute the looping
            // vanilla observed-meds sound). Checked for FIKA first, so the send that touches FikaPlayer is only JIT-compiled
            // with FIKA installed. The try/catch matters: an exception here would kill the local eater's own audio.
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

        // Our controller's clips play head-locked in 2D (PlayClipHeadLocked). The vanilla PlayClip leaves the pooled source
        // 3D-blended on the VR-reparented weapon transform, which is sometimes quiet, misplaced or silent.
        // Skips the original when it handled the clip; the haptics postfix below still runs.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "PlayClip")]
        public static bool ForceHeadLockedEatAudio(BaseSoundPlayer __instance, AudioClip clip, int rolloff, float volume)
        {
            return !EatingInteractionController.PlayClipHeadLocked(__instance, clip, rolloff, volume);
        }

        // Audio to haptics: every audible clip on our controller's sound player also plays its waveform envelope on the
        // controllers. Every clip goes through PlayClip after the distance and occlusion culls.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "PlayClip")]
        public static void AudioToHaptics(BaseSoundPlayer __instance, AudioClip clip, float volume)
        {
            EatingInteractionController.OnEatAudio(__instance, clip, volume);
        }

        // Keeps the left hand tracking the controller during manual eating. UpdateLeftHand returns early while
        // LEFT_HAND_ANIMATOR_HASH == 1 (the eat animation), which freezes LeftHand and disables leftArmIk. So this does the
        // plain off-hand placement and skips the original.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VRPlayerManager), "UpdateLeftHand")]
        public static bool ForceLeftHandTracking(VRPlayerManager __instance, VRPose fromAction)
        {
            if (!EatingInteractionController.ManualActive) return true; // normal behavior
            GameObject lh = __instance.LeftHand;
            if (lh == null || fromAction == null) return true;

            __instance.PlaceOffHand(fromAction);

            // While a pull-to-open has the left hand latched onto the held item, the rig target still updates (so on release
            // the hand lands on the controller), but the IK is not pointed back at it. This runs on the SteamVR pose callback,
            // after the eat Tick aimed the IK at the latch, and would pull the hand off the item mid-pull.
            if (VRGlobals.ikManager?.leftArmIk != null && !EatingInteractionController.OffHandLatched)
            {
                VRGlobals.ikManager.leftArmIk.solver.target = lh.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
            return false; // skip the original (which would freeze the left hand)
        }
    }
}
