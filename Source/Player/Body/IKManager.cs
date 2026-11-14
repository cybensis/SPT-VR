using EFT;
using RootMotion.FinalIK;
using UnityEngine;

namespace TarkovVR.Source.Player.VR
{
    internal class IKManager : MonoBehaviour
    {
        private Vector3 upperArmPos = new Vector3(-0.1f, 0.1f, 0);

        private Transform leftUpperArm;
        private Transform rightUpperArm;
        public GameObject leftHandIK;
        public GameObject rightHandIK;
        public LimbIK leftArmIk;
        public LimbIK rightArmIk;

        // The elbow bend solve is in IKPatches.DriveElbowBendNormals (BendModifier.Custom). Don't use BendModifier.Arm on this
        // rig: the LimbIK sits on the collarbone, so the Arm axis hints resolve in the collarbone's local frame (the right elbow
        // solves backwards, the left misaligned).

        private void Awake()
        {
            VRGlobals.VRCam = Camera.main;
            
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "Base HumanRibcage")
                {
                    for (int a = 0; a < child.childCount; a++)
                    {
                        Transform innerChild = child.GetChild(a);
                        if (innerChild.name == "Base HumanLCollarbone")
                            leftUpperArm = innerChild.GetChild(0);
                        else if (innerChild.name == "Base HumanRCollarbone")
                            rightUpperArm = innerChild.GetChild(0);
                    }
                }
            }
            
        }

        // The body under the head (BodyFollow)
        public void MatchLegsToArms()
        {
            // On a mounted gun the body stays where Tarkov sat it (Interactions.MountedGun)
            if (VRGlobals.vrPlayer.initPos.y == 0 || Interactions.MountedGun.Active)
                return;

            if (VRGlobals.inGame && VRGlobals.player &&
                VRGlobals.player.PointOfView == EPointOfView.FirstPerson &&
                name == "Base HumanSpine3")
            {
                TarkovVR.Source.Player.Body.BodyFollow.Step(transform.root);
            }
            else
            {
                transform.localPosition = Vector3.zero;
            }
        }

        void Update()
        {
            if (!VRGlobals.vrPlayer || VRGlobals.menuOpen)
                return;

            if (leftUpperArm)
                leftUpperArm.localPosition = upperArmPos;
            if (rightUpperArm)
                rightUpperArm.localPosition = upperArmPos;
            
            // (every frame in every hands state: the other callers in the same frame are no-ops)
            MatchLegsToArms();
        }
    }
}