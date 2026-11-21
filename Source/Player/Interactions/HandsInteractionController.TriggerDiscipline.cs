using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Trigger discipline: while you hold a gun with your finger off the trigger, the gun hand's index finger lies straight along
    // the gun. This needs a touch sensor on the trigger (OpenXR on Index or Touch controllers): the mod's SteamVR manifest has no
    // touch action, and the trigger press alone isn't enough (the finger would only go on the trigger as you fire). The animator's
    // trigger curl is removed with ApplyFlatFlex in the right hand's own frame. The mag release press, later in LateUpdate, starts
    // from this pose.
    internal partial class HandsInteractionController
    {
        public static bool triggerDiscipline = true;
        public static float offTriggerBend = 0f;            // deg the knuckle still bends with the finger off (0 = straight); joints further out bend less
        public static float offTriggerBlendTime = 0.08f;    // s for the finger to come off or go back on

        private Transform triggerFingerPalm;
        private Transform[] triggerFingerJoints;
        private float offTriggerShown;

        private void UpdateTriggerDiscipline()
        {
            float target = FingerOffTrigger() ? 1f : 0f;
            offTriggerShown = Mathf.MoveTowards(offTriggerShown, target, Time.deltaTime / Mathf.Max(offTriggerBlendTime, 0.001f));
            if (offTriggerShown <= 0f || !ResolveTriggerFinger())
                return;
            for (int joint = 0; joint < triggerFingerJoints.Length; joint++)
            {
                float bend = offTriggerBend * Mathf.Pow(midJointScale, joint) * rightFlexSign;
                ApplyFlatFlex(triggerFingerJoints[joint], bend, 0f, offTriggerShown);
            }
        }

        private static bool FingerOffTrigger()
        {
            if (!triggerDiscipline || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.usingItem || EatingInteractionController.ManualActive)
                return false;
            EFT.Player player = VRGlobals.player;
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            if (player == null || firearmController == null || !ReferenceEquals(player.HandsController, firearmController)
                || WeaponPatches.grenadeEquipped || ChestHang.Detached)
                return false;
            VRSingle trigger = Handedness.GunHandTrigger;
            if (!trigger.TryGetTouch(out bool touched))
                return false;
            return !touched && trigger.axis < 0.05f;
        }

        // The gun hand's index finger, knuckle to tip: the right arm IK's wrist bone is the palm (Digit2 = index)
        private bool ResolveTriggerFinger()
        {
            var rightArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            Transform palm = rightArmIk != null && rightArmIk.solver != null && rightArmIk.solver.bone3 != null ? rightArmIk.solver.bone3.transform : null;
            if (palm == null)
                return false;
            if (palm == triggerFingerPalm)
                return triggerFingerJoints != null;
            triggerFingerPalm = palm;
            triggerFingerJoints = null;
            for (int child = 0; child < palm.childCount; child++)
            {
                Transform knuckle = palm.GetChild(child);
                if (knuckle.name.IndexOf("Digit2", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (knuckle.childCount > 0 && knuckle.GetChild(0).childCount > 0)
                    triggerFingerJoints = new[] { knuckle, knuckle.GetChild(0), knuckle.GetChild(0).GetChild(0) };
                break;
            }
            return triggerFingerJoints != null;
        }
    }
}
