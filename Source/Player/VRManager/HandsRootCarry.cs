using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // The gun's positioner (HandsPositioner) is put on the hand in Update, but it hangs under Tarkov's hands root (Weapon_root_anim),
    // which Player.VisualPass moves again before the arm IK: the body's turn from the look input (MouseLook: the stick turn), the stance
    // height (HeightInterpolation, our crouch easing), the procedural sway and bob. Each carried the gun (and the hand Tarkov fits onto
    // it) a frame's worth of the body's motion off the hand. Right before the IK the positioner gets that move undone - the bone's
    // recoil-free motion (RecoilPose.FreeFrame), so the recoil still shows - and the rig's own move since added.
    public static class HandsRootCarry
    {
        public static bool enabled = true;
        public static float maxCarry = 0.5f;    // m: a bigger correction in one frame is a teleport, left alone
        public static bool debugLog = true;     // a line a second: the most it undid in a frame, and the rig's own move

        private static Transform positioner;
        private static Transform root;
        private static int notedFrame = -1;
        private static Vector3 handPosition;
        private static Quaternion handRotation = Quaternion.identity;
        private static Matrix4x4 rootFree = Matrix4x4.identity;

        private static float nextLog;
        private static float mostCarried, mostTurned, mostRigMoved;

        // HandsPositioner.Update: the held gun's positioner just put on the hand
        public static void Note(Transform placed)
        {
            if (!enabled || placed == null || placed.parent == null || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.rawRightHand == null
                || VRGlobals.weaponHolder == null || VRGlobals.weaponHolder.transform.parent != placed)
                return;
            Transform hand = VRGlobals.vrPlayer.rawRightHand.transform;
            positioner = placed;
            root = placed.parent;
            notedFrame = Time.frameCount;
            handPosition = hand.position;
            handRotation = hand.rotation;
            rootFree = FreeFrameOf(root);
        }

        // Player.IkProcess prefix, after the others (the airborne zeroing moves the bone too)
        public static void Undo(EFT.Player player)
        {
            if (!enabled || notedFrame != Time.frameCount || positioner == null || root == null || positioner.parent != root)
                return;
            notedFrame = -1;
            if (player.MovementContext != null && player.MovementContext.StationaryWeapon != null)
                return;
            Transform hand = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.rawRightHand != null ? VRGlobals.vrPlayer.rawRightHand.transform : null;
            if (hand == null)
                return;
            // The hands root's own move since the note, recoil left out
            Matrix4x4 rootMove = FreeFrameOf(root) * rootFree.inverse;
            Vector4 forward = rootMove.GetColumn(2);
            Vector4 up = rootMove.GetColumn(1);
            if (((Vector3)forward).sqrMagnitude < 1e-8f || ((Vector3)up).sqrMagnitude < 1e-8f)
                return;
            Quaternion rootTurn = Quaternion.LookRotation(forward, up);
            Quaternion handTurn = hand.rotation * Quaternion.Inverse(handRotation);
            Vector3 uncarried = rootMove.inverse.MultiplyPoint3x4(positioner.position);
            Vector3 position = hand.position + handTurn * (uncarried - handPosition);
            Quaternion rotation = handTurn * Quaternion.Inverse(rootTurn) * positioner.rotation;
            float moved = Vector3.Distance(position, positioner.position);
            if (moved > maxCarry)
                return;
            if (debugLog)
            {
                mostCarried = Mathf.Max(mostCarried, Vector3.Distance(uncarried, positioner.position));
                mostTurned = Mathf.Max(mostTurned, Quaternion.Angle(rootTurn, Quaternion.identity));
                mostRigMoved = Mathf.Max(mostRigMoved, Vector3.Distance(hand.position, handPosition));
                if (Time.time >= nextLog)
                {
                    nextLog = Time.time + 1f;
                    Plugin.MyLog.LogInfo($"[HandsRootCarry] undid up to {mostCarried * 100f:0.0} cm / {mostTurned:0.00} deg a frame of the hands root's move,"
                        + $" the hand moved up to {mostRigMoved * 100f:0.0} cm after it was placed");
                    mostCarried = mostTurned = mostRigMoved = 0f;
                }
            }
            positioner.SetPositionAndRotation(position, rotation);
        }

        // A bone's frame without the recoil: Tarkov puts it on HandsContainer.WeaponRootAnim, so anything at or under that bone
        // rides it; a bone outside it doesn't
        private static Matrix4x4 FreeFrameOf(Transform bone)
        {
            var hands = VRGlobals.player != null && VRGlobals.player.ProceduralWeaponAnimation != null ? VRGlobals.player.ProceduralWeaponAnimation.HandsContainer : null;
            Transform recoilBone = hands != null ? hands.WeaponRootAnim : null;
            if (recoilBone != null && bone.IsChildOf(recoilBone))
                return RecoilPose.FreeFrame(recoilBone) * recoilBone.worldToLocalMatrix * bone.localToWorldMatrix;
            return bone.localToWorldMatrix;
        }
    }
}
