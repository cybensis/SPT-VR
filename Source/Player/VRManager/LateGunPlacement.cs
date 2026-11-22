using TarkovVR.Source.Weapons;
using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.VRManager
{
    // HandsPositioner.Update puts the held gun's positioner on the hand. Player.VisualPass then moves its parent, Tarkov's hands root
    // (walking, the body turning after the view, the crouch animation, sway), and the rig is only pinned to the body in
    // HandsPositioner.LateUpdate, after the arm IK. Without this, the gun and the arms fitted to it lag one frame of body motion behind
    // the hand. So right before the arm IK this does the end-of-frame rig pin first, then puts the gun back on the hand at the rig's
    // final pose, undoing the hands root's move without its recoil (RecoilPose.FreeFrame), so the recoil still shows.
    // The later pin then finds the rig already in place.
    public static class LateGunPlacement
    {
        public static float maxCorrection = 0.5f;   // m: a larger move in one frame is a teleport (respawn, weapon switch) and is left alone
        [Tunable("Sling, stock & weight", TunableKind.Log, "A [LateGunPlacement] line a second: the most the gun was moved back onto your hand in one frame, and the rig's "
            + "pin.", Suggested = true)]
        public static bool debugLog = false;        // logs once a second the most the gun and the rig moved here in a frame

        private static Transform positioner;
        private static Transform root;
        private static int notedFrame = -1;
        private static Vector3 handPosition;
        private static Quaternion handRotation = Quaternion.identity;
        private static Matrix4x4 rootFree = Matrix4x4.identity;

        // The frame in which the rig got its end-of-frame pin here (HandsPositioner.LateUpdate skips its pin in that frame)
        public static int PinnedFrame { get; private set; } = -1;

        private static float nextLog;
        private static float mostGunMoved, mostRigMoved;

        // Called from HandsPositioner.Update right after it puts the held gun's positioner on the hand
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

        // Player.IkProcess prefix, run after the other prefixes (IKPatches' airborne zeroing also moves the hands root)
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

            // HandsPositioner.LateUpdate's end-of-frame pin, done now
            Vector3 rigBefore = VRGlobals.camRoot.transform.position;
            VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            PinnedFrame = Time.frameCount;

            // Undo the hands root's own move since the placement (without recoil), and add the hand's move (the rig pin, a new pose)
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

        // A bone's frame without the recoil. Tarkov applies recoil to HandsContainer.WeaponRootAnim, so anything at or under that bone moves with it.
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
