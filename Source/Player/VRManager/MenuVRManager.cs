using UnityEngine;
using Valve.VR;
using TarkovVR.Patches.Misc;
using TarkovVR.Source.Misc;
using TarkovVR.Source.UI;

namespace TarkovVR.Source.Player.VRManager
{
    internal class MenuVRManager : MonoBehaviour
    {
        public Vector3 initPos;

        // VR Origin and body stuff
        public GameObject LeftHand = null;
        public GameObject RightHand = null;
        public LaserPointer pointer = null;
        private float x = 42;
        private float y = 355;
        private float z = 0;
        public SteamVR_Action_Vibration hapticAction;
        private float timeHeld = 0;

        public void Awake()
        {
            SpawnHands();
            if (RightHand)
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            if (LeftHand)
                LeftHand.transform.parent = VRGlobals.vrOffsetter.transform;

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);

        }


        public void OnDisable()
        {
            if (pointer && pointer.holder)
            {
                pointer.enabled = false;
                pointer.holder.active = false;
                SteamVR_Actions._default.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                SteamVR_Actions._default.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            }
        }
        public void OnEnable()
        {
            if (pointer && pointer.holder)
            {
                pointer.enabled = true;
                pointer.holder.active = true;
                SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            }
        }


        private void Update()
        {
            if (Camera.main == null)
                return;
            if (initPos.y == 0)
                initPos = Camera.main.transform.localPosition;

            VRGlobals.vrOffsetter.transform.localPosition = initPos * -1;


            if (SteamVR_Actions._default.ClickRightJoystick.GetState(SteamVR_Input_Sources.Any))
            {
                timeHeld += Time.deltaTime;
                if (Camera.main != null && timeHeld > 0.75f)
                {
                    initPos = Camera.main.transform.localPosition;
                }
            }
            else if (timeHeld != 0)
            {
                timeHeld = 0;
            }


            //SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 1, SteamVR_Input_Sources.RightHand);

        }


        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {

                RightHand.transform.localPosition = fromAction.localPosition + new Vector3(-0.1f, 0, 0);
                //RightHand.transform.localEulerAngles = fromAction.localRotation.eulerAngles + new Vector3(x,y,z);
                RightHand.transform.localEulerAngles = fromAction.localRotation.eulerAngles;
                RightHand.transform.Rotate(x, y, z);

            }
        }
        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (LeftHand)
            {
                LeftHand.transform.localPosition = fromAction.localPosition;
                LeftHand.transform.localRotation = fromAction.localRotation;

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
                RightHand.transform.parent = VRGlobals.camHolder.transform.parent;
                MenuPatches.vrUiInteracter = RightHand.AddComponent<VRUIInteracter>();
                pointer = RightHand.AddComponent<LaserPointer>();
                pointer.color = Color.cyan;

            }
            if (!LeftHand)
            {
                //LeftHand = GameObject.Instantiate(AssetLoader.LeftHandBase, Vector3.zero, Quaternion.identity);
                LeftHand = new GameObject("LeftHand");
                LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                LeftHand.transform.parent = VRGlobals.camHolder.transform.parent;
            }
        }
    }
}
