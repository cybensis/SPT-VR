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
            // The sprint sway, on the held gun's positioner only (an old gun's positioner can outlive a switch)
            if (VRGlobals.weaponHolder != null && VRGlobals.weaponHolder.transform.parent == this.transform)
                gunRotation = Source.Player.VRManager.RunSway.Offset(gunRotation) * gunRotation;
            this.transform.rotation = gunRotation;
            // Tarkov moves this one's parent again before the arm IK, and the rig gets its pin after it: put back on the hand there
            Source.Player.VRManager.LateGunPlacement.Note(this.transform);
            // A gun held by the support hand alone or slung is stamped in world space under this: put it back after the move
            Source.Player.Interactions.ChestHang.RestampDetached();

            if (VRGlobals.emptyHands != null)
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
        }

        private void OnDisable()
        {
            // Weapon is being holstered/swapped - drop any collision pushback so it can't go stale.
            Source.Player.Interactions.WeaponCollision.Reset();
        }

        private void LateUpdate()
        {
            if (!VRGlobals.vrPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return;

            // Already pinned this frame, before the arm IK (LateGunPlacement): the gun is on the hand at that rig pose
            if (VRGlobals.emptyHands != null && Source.Player.VRManager.LateGunPlacement.PinnedFrame != Time.frameCount)
            {
                VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }
        }
    }
}
