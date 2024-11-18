using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            this.transform.rotation = VRGlobals.vrPlayer.rawRightHand.transform.rotation;

            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
        }

        private void LateUpdate()
        {
            if (!VRGlobals.vrPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return;
            VRGlobals.emptyHands.rotation = VRGlobals.vrPlayer.handsRotation;
            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);

        }


    }
}
