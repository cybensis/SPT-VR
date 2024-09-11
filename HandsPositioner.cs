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

        private void Update() {
            if (!VRGlobals.vrPlayer)
                return;



            //if (VRGlobals.vrPlayer.isSupporting && VRGlobals.vrPlayer.LeftHand != VRGlobals.player._markers[0])
            //{

            //    //leftHandIk.position = VRGlobals.vrPlayer.LeftHand.transform.position;
            //    //leftHandIk.rotation = VRGlobals.vrPlayer.LeftHand.transform.rotation;
            //}
            if (rightHandIk)
            {
                rightHandIk.position = VRGlobals.vrPlayer.rawRightHand.transform.position;
                rightHandIk.rotation = VRGlobals.vrPlayer.rawRightHand.transform.rotation;

            }
        }
    }
}
