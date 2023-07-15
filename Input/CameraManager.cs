using UnityEngine;
using Valve.VR.Extras;
using Valve.VR;

namespace TarkovVR.Input
{
    internal class CameraManager : MonoBehaviour
    {
        private float x, y, z;
        private float rx, ry, rz;

        public Vector3 initPos;

        private Vector3 rightHandOffset = new Vector3(0.05f, -0.1f, -0.2f);
        private Vector3 leftHandOffset = new Vector3(-0.04f, -0.05f, -0.1f);

        public void Awake()
        {
            //x = rightHandOffset.x;
            //y = rightHandOffset.y;
            //z = rightHandOffset.z;
            SpawnHands();
            if (RightHand)
                RightHand.transform.parent = CamPatches.vrOffsetter.transform;
            if (LeftHand)
                LeftHand.transform.parent = CamPatches.vrOffsetter.transform;

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.Any, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.Any, UpdateLeftHand);
        }

        private void Update() {
            if (initPos.y != 0)
                CamPatches.vrOffsetter.transform.localPosition = initPos * -1;
            else if (Camera.main != null)
                CamPatches.vrOffsetter.transform.localPosition = Camera.main.transform.localPosition * -1;

        }

        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {
                RightHand.transform.localPosition = fromAction.localPosition + new Vector3(x,y,z);
                RightHand.transform.localRotation = fromAction.localRotation;
                //RightHand.transform.Rotate(70, 170, 50);
                RightHand.transform.Rotate(70, 170, 50);

            }

        }

        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (LeftHand)
            {
                LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                LeftHand.transform.localRotation = fromAction.localRotation;
                LeftHand.transform.Rotate(-20, 0, 70);
                


            }
        }


        public void SpawnHands()
        {
            if (!RightHand)
            {
                //RightHand = GameObject.Instantiate(AssetLoader.RightHandBase, Vector3.zero, Quaternion.identity);
                RightHand = new GameObject("RightHand");
                RightHand.AddComponent<SteamVR_Behaviour_Pose>();
                //RightHand.AddComponent<SteamVR_Skeleton_Poser>();
                RightHand.transform.parent = CamPatches.camHolder.transform.parent;

            }
            if (!LeftHand)
            {
                //LeftHand = GameObject.Instantiate(AssetLoader.LeftHandBase, Vector3.zero, Quaternion.identity);
                LeftHand = new GameObject("LeftHand");
                LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                LeftHand.transform.parent = CamPatches.camHolder.transform.parent;
            }
        }


        


        // VR Origin and body stuff
        public static GameObject LeftHand = null;
        public static GameObject RightHand = null;
    }
}
