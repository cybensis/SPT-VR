using EFT.UI;
using EFT.Visual;
using System.Collections.Generic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;
using static EFT.Player;

namespace TarkovVR
{
    internal class VRGlobals
    {
        internal static float upscalingMultiplier;
        public const string LEFT_ARM_OBJECT_NAME = "Base HumanLCollarbone";
        public const string RIGHT_ARM_OBJECT_NAME = "Base HumanRCollarbone";
        public const float MIN_JOYSTICK_AXIS_FOR_MOVEMENT = 0.5f;
        public const float NEAR_CLIP_PLANE = 0.01f;

        //public static HideoutVRPlayerManager hideoutVRPlayer;
        //public static RaidVRPlayerManager raidVRPlayer;
        public static VRPlayerManager vrPlayer;
        public static MenuVRManager menuVRManager;
        public static Transform commonUi;
        public static Transform preloaderUi;
        public static Transform menuUi;
        public static Camera VRCam;
        public static GameObject camHolder;
        public static GameObject vrOffsetter;
        public static GameObject camRoot;
        public static float camRootY;
        public static string vrControllerType;

        public Transform playerCam;
        public static Transform emptyHands;
        public static Transform leftWrist;

        public static EFT.Player player;
        public static VROpticController vrOpticController;
        public static HandsInteractionController handsInteractionController;
        public static Vector3 grenadeOffset = new Vector3(22.5f,0,0);
        public static Vector3 test = new Vector3(0.035f, 0.04f, -0.02f);
        public static Vector3 testRot;
        public static float randomMultiplier = 1;
        public static bool menuOpen = false;
        public static bool inGame = false;

        public static Transform backHolster;
        public static Transform backpackCollider;
        public static Transform leftArmBendGoal;
        public static Transform rightArmBendGoal;
        public static Transform sidearmHolster;
        public static BoxCollider backCollider;
        public static FirearmController firearmController;
        public static float scopeSensitivity = 0.1f;
        //public static Transform scope;
        public static List<Transform> scopes = new List<Transform>();
        public static IKManager ikManager;

        public static SkinnedMeshRenderer handsOnlyModel;
        public static SkinnedMeshRenderer origArmsModel;
        public static SkinnedMeshRenderer legsModel;
        public static AssetBundle handsBundle;
        public static GameObject cloudPrefab;
        public static Shader prismShader;

        public static GameObject weaponHolder;
        public static GameObject oldWeaponHolder;

        public static bool inspectWeapon = false;
        public static bool checkMagazine = false;
        public static bool changeFireMode = false;
        public static bool usingItem = false;
        public static bool switchingWeapon = false;
        public static int quickSlot = -1;

        public static bool blockRightJoystick = false;
        public static bool blockLeftJoystick = false;
        
    }
}