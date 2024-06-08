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

        public const string LEFT_ARM_OBJECT_NAME = "Base HumanLCollarbone";
        public const string RIGHT_ARM_OBJECT_NAME = "Base HumanRCollarbone";
        public const float MIN_JOYSTICK_AXIS_FOR_MOVEMENT = 0.5f;

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

        public Transform playerCam;
        public static Transform emptyHands;
        public static Transform leftWrist;

        public static EFT.Player player;
        public static VROpticController vrOpticController;
        public static HandsInteractionController handsInteractionController;
        public static bool menuOpen = false;
        public static bool inGame = false;

        public static Transform backHolster;
        public static BoxCollider backCollider;
        public static FirearmController firearmController;
        public static float scopeSensitivity = 0;
        public static Transform scope;
        public static IKManager ikManager;

        public static GameObject weaponHolder;
        public static GameObject oldWeaponHolder;

        public static bool inspectWeapon = false;
        public static bool checkMagazine = false;
        public static bool changeFireMode = false;
        public static int quickSlot = -1;

        public static bool blockRightJoystick = false;
    }
}