using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

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
            this.transform.rotation = VRGlobals.vrPlayer.rawRightHand.transform.rotation;
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

            if (VRGlobals.emptyHands != null)
            {
                VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }
        }
    }
}
