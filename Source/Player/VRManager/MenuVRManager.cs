using UnityEngine;
using Valve.VR;
using TarkovVR.Patches.Misc;
using TarkovVR.Source.Misc;
using TarkovVR.Source.UI;
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
        // The laser and the menu input (VRUIInteracter) on their own object, on the controller the "Laser Pointer In Left Hand" setting
        // picks. Not on RightHand: the raid rig reuses that as its gun hand. Placed with RightHand's offsets.
        public GameObject LaserHand = null;
        private float x = 42;
        private float y = 355;
        private float z = 0;
        private Vector3 pos;
        public VRVibration hapticAction;

        public void Awake()
        {
            SpawnHands();
            if (RightHand) {
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            }
            if (LaserHand)
                LaserHand.transform.parent = VRGlobals.vrOffsetter.transform;
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

        // Each hand on its own controller; the laser on the one the setting picks
        private void AddPoseListeners()
        {
            RemovePoseListeners();
            VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            if (VRSettings.GetLaserInLeftHand())
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLaserHand);
            else
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLaserHand);
        }

        // The setting toggled: the laser to that controller now (only while the menus are up; else at the next enable)
        public void SetLaserHand(bool left)
        {
            if (isActiveAndEnabled)
                AddPoseListeners();
        }

        private void RemovePoseListeners()
        {
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            VRInput.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLaserHand);
            VRInput.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLaserHand);
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
        // The laser, on its controller, placed as RightHand is. Kept beside the hands: they're moved under other parents on the way
        // into and out of raids (MenuPatches), the laser follows.
        private void UpdateLaserHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (!LaserHand)
                return;
            Transform handsParent = RightHand ? RightHand.transform.parent : null;
            if (handsParent != null && LaserHand.transform.parent != handsParent)
                LaserHand.transform.SetParent(handsParent, false);
            LaserHand.transform.localPosition = fromAction.localPosition + new Vector3(0, -0.03f, 0);
            LaserHand.transform.localRotation = fromAction.localRotation;
            LaserHand.transform.Rotate(45, 10, 0);
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
                // SteamVR's pose component throws without SteamVR input; under OpenXR the pose listeners above move the hands
                if (!VRInput.UsingOpenXR)
                    RightHand.AddComponent<SteamVR_Behaviour_Pose>();
                //RightHand.AddComponent<SteamVR_Skeleton_Poser>();
                RightHand.transform.parent = VRGlobals.camHolder.transform.parent;
            }
            if (!LaserHand)
            {
                LaserHand = new GameObject("LaserHand");
                LaserHand.transform.parent = VRGlobals.camHolder.transform.parent;
                MenuPatches.vrUiInteracter = LaserHand.AddComponent<VRUIInteracter>();
                pointer = LaserHand.AddComponent<LaserPointer>();
                pointer.color = Color.cyan;
            }
            if (!LeftHand)
            {
                LeftHand = new GameObject("LeftHand");
                if (!VRInput.UsingOpenXR)
                    LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                LeftHand.transform.parent = VRGlobals.camHolder.transform.parent;
            }
        }
    }
}
