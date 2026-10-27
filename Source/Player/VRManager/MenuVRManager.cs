using UnityEngine;
using Valve.VR;
using TarkovVR.Patches.Misc;
using TarkovVR.Source.Misc;
using TarkovVR.Source.UI;
using System.Reflection;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Controls;

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
        private Vector3 pos;
        public VRVibration hapticAction;
        private float timeHeld = 0;
        private bool _listenersAdded = false;

        public void Awake()
        {
            SpawnHands();
            if (RightHand) { 
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            }
            if (LeftHand)
                LeftHand.transform.parent = VRGlobals.vrOffsetter.transform;

            if (VRKeyboardController.Instance == null) {
                GameObject kbHandler = new GameObject("VRKeyboardManager");
                kbHandler.AddComponent<VRKeyboardController>();
                DontDestroyOnLoad(kbHandler);
            }

            AddPoseListeners();

        }

        /*
        public void OnDisable()
        {
            
            if (pointer && pointer.holder)
            {
                pointer.enabled = false;
                pointer.holder.active = false;
                MenuPatches.vrUiInteracter.enabled = false;
                VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
                VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
                VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            }
            
        }
        
        public void OnEnable()
        {
            if (pointer && pointer.holder)
            {
                pointer.enabled = true;
                pointer.holder.active = true;
                MenuPatches.vrUiInteracter.enabled = true;
                if (VRSettings.GetLeftHandedMode())
                {
                    VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
                    VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
                }
                else
                {
                    VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                    VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
                }
            }
        }
        */

        public void OnDisable()
        {

            if (!pointer || !pointer.holder) return;

            pointer.enabled = false;
            pointer.holder.active = false;
            if (MenuPatches.vrUiInteracter)
                MenuPatches.vrUiInteracter.enabled = false;

            RemovePoseListeners();
        }

        public void OnEnable()
        {
            if (!pointer || !pointer.holder) return;

            pointer.enabled = true;
            pointer.holder.active = true;
            if (MenuPatches.vrUiInteracter)
                MenuPatches.vrUiInteracter.enabled = true;

            AddPoseListeners();
        }

        private void AddPoseListeners()
        {
            RemovePoseListeners();

            if (VRSettings.GetLeftHandedMode())
            {
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            }
            else
            {
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            }
        }

        private void RemovePoseListeners()
        {
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
        }

        public void LeftHandedMode() {
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
        }
        public void RightHandedMode()
        {
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
        }

        //private void Update()
        //{
        //    if (Camera.main == null)
        //        return;
        //    if (initPos.y == 0)
        //        initPos = Camera.main.transform.localPosition;

        //    VRGlobals.vrOffsetter.transform.localPosition = initPos * -1;


        //    if (VRInput.ClickRightJoystick.GetState(SteamVR_Input_Sources.Any))
        //    {
        //        timeHeld += Time.deltaTime;
        //        if (Camera.main != null && timeHeld > 0.75f)
        //        {
        //            initPos = Camera.main.transform.localPosition;
        //        }
        //    }
        //    else if (timeHeld != 0)
        //    {
        //        timeHeld = 0;
        //    }


        //    //VRInput.Haptic.Execute(0, 0.1f, 1, 1, SteamVR_Input_Sources.RightHand);

        //}


        private void UpdateRightHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {

                RightHand.transform.localPosition = fromAction.localPosition + new Vector3(0,-0.03f,0);
                //RightHand.transform.localPosition = fromAction.localPosition;
                //RightHand.transform.localEulerAngles = fromAction.localRotation.eulerAngles + new Vector3(x,y,z);
                RightHand.transform.localRotation = fromAction.localRotation;
                RightHand.transform.Rotate(45, 10, 0);

            }
        }
        private void UpdateLeftHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
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
                LeftHand = new GameObject("LeftHand");
                LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                LeftHand.transform.parent = VRGlobals.camHolder.transform.parent;
            }
        }
    }
}
