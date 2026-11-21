using UnityEngine;

namespace TarkovVR
{
    internal class HandsPositioner : MonoBehaviour
    {
        public Transform leftHandIk;
        public Transform leftHandDummyIk;
        public Transform rightHandIk;

        public Quaternion armsRotation;

        public int i = 0;

        private void Update() {
            if (!VRGlobals.vrPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return;

            VRGlobals.ikManager.MatchLegsToArms();

            this.transform.position = VRGlobals.vrPlayer.rawRightHand.transform.position;
            Quaternion gunRotation = VRGlobals.vrPlayer.rawRightHand.transform.rotation;
            // Add the sprint sway, but only on the held gun's positioner (an old gun's positioner can outlive a weapon switch)
            if (VRGlobals.weaponHolder != null && VRGlobals.weaponHolder.transform.parent == this.transform)
                gunRotation = Source.Player.VRManager.RunSway.Offset(gunRotation) * gunRotation;
            this.transform.rotation = gunRotation;
            // Tarkov moves this transform's parent again before the arm IK, and the rig is pinned to the body after that.
            // LateGunPlacement puts the gun back on the hand at that point.
            Source.Player.VRManager.LateGunPlacement.Note(this.transform);
            // A slung gun, or one held by the support hand alone, is placed in world space under this transform: restore it after the move
            Source.Player.Interactions.ChestHang.RestampDetached();

            if (VRGlobals.emptyHands != null)
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
        }

        private void OnDisable()
        {
            // The weapon is being holstered or swapped. Clear any collision pushback so it can't go stale.
            Source.Player.Interactions.WeaponCollision.Reset();
        }

        private void LateUpdate()
        {
            if (!VRGlobals.vrPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return;

            // Skipped when LateGunPlacement already pinned the rig this frame, before the arm IK (the gun is on the hand at that pose)
            if (VRGlobals.emptyHands != null && Source.Player.VRManager.LateGunPlacement.PinnedFrame != Time.frameCount)
            {
                VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }
        }
    }
}
