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
        // The laser and the menu input (VRUIInteracter) live on their own object, on the controller the "Laser Pointer In Left Hand"
        // setting picks. They aren't on RightHand because the raid rig reuses that as its gun hand. Placed with RightHand's offsets.
        public GameObject LaserHand = null;
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

        // Called when the setting changes: moves the laser to that controller now while the menus are up, otherwise at the next OnEnable
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


        private void UpdateRightHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {

                RightHand.transform.localPosition = fromAction.localPosition + new Vector3(0,-0.03f,0);
                RightHand.transform.localRotation = fromAction.localRotation;
                RightHand.transform.Rotate(45, 10, 0);

            }
        }
        // Places the laser on its controller the same way RightHand is placed. It is kept under the hands' parent, which MenuPatches
        // changes on the way into and out of raids.
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
