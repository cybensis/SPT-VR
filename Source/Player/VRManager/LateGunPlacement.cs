using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // The held gun's positioner is put on the hand in HandsPositioner.Update, then Player.VisualPass moves its parent (Tarkov's hands
    // root: the walk, the body turning after the view, the crouch animation, sway) and the rig is only pinned to the body for the frame
    // in HandsPositioner.LateUpdate, after the arm IK - so the gun and the arms fitted to it sat a frame of body motion off the hand.
    // Here, right before the arm IK: that end-of-frame pin first, then the gun back on the hand at the rig's final pose, the hands
    // root's recoil-free move undone (RecoilPose.FreeFrame: the recoil still shows). The later pin then finds the rig in place.
    public static class LateGunPlacement
    {
        public static float maxCorrection = 0.5f;   // m: more in one frame is a teleport (respawn, weapon switch), left alone
        public static bool debugLog = false;        // a line a second: the most the gun and the rig moved here in a frame

        private static Transform positioner;
        private static Transform root;
        private static int notedFrame = -1;
        private static Vector3 handPosition;
        private static Quaternion handRotation = Quaternion.identity;
        private static Matrix4x4 rootFree = Matrix4x4.identity;

        // The frame the rig got its end-of-frame pin here (HandsPositioner.LateUpdate leaves it then)
        public static int PinnedFrame { get; private set; } = -1;

        private static float nextLog;
        private static float mostGunMoved, mostRigMoved;

        // HandsPositioner.Update: the held gun's positioner just put on the hand
        public static void Note(Transform placed)
        {
            if (placed == null || placed.parent == null || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.rawRightHand == null
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

        // Player.IkProcess prefix, after the others (IKPatches' airborne zeroing moves the hands root too)
        public static void BeforeArmIk(EFT.Player player)
        {
            if (notedFrame != Time.frameCount || positioner == null || root == null || positioner.parent != root)
                return;
            notedFrame = -1;
            if (player.MovementContext != null && player.MovementContext.StationaryWeapon != null)
                return;
            Transform hand = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.rawRightHand != null ? VRGlobals.vrPlayer.rawRightHand.transform : null;
            if (hand == null || VRGlobals.camRoot == null || VRGlobals.emptyHands == null)
                return;

            // The end-of-frame pin (HandsPositioner.LateUpdate's), now
            Vector3 rigBefore = VRGlobals.camRoot.transform.position;
            VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            PinnedFrame = Time.frameCount;

            // The hands root's own move since the placement, recoil left out, undone; the hand's (the rig's pins, a new pose) added
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
            if (moved > maxCorrection)
                return;
            if (debugLog)
            {
                mostGunMoved = Mathf.Max(mostGunMoved, moved);
                mostRigMoved = Mathf.Max(mostRigMoved, Vector3.Distance(rigBefore, VRGlobals.camRoot.transform.position));
                if (Time.time >= nextLog)
                {
                    nextLog = Time.time + 1f;
                    Plugin.MyLog.LogInfo($"[LateGunPlacement] the gun moved up to {mostGunMoved * 100f:0.0} cm a frame onto the hand, the rig's pin up to {mostRigMoved * 100f:0.0} cm");
                    mostGunMoved = mostRigMoved = 0f;
                }
            }
            positioner.SetPositionAndRotation(position, rotation);
        }

        // A bone's frame without the recoil: Tarkov puts it on HandsContainer.WeaponRootAnim, so anything at or under that bone rides it
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
