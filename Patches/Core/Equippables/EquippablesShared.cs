using EFT;
using UnityEngine;

namespace TarkovVR.Patches.Core.Equippables
{

    internal class EquippablesShared
    {
        //------------------------------------------------------   EQUIPPABLES GLOBALS  ---------------------------------------------------------------------------
        public static GunInteractionController currentGunInteractController;
        public static Transform previousLeftHandMarker;
        public static Vector3 weaponOffset = Vector3.zero;
        public static PortableRangeFinderController rangeFinder;


        //------------------------------------------------------   EQUIPPABLES SHARED METHODS  --------------------------------------------------------------------
        public static void DisableEquippedRender() {
            if (VRGlobals.menuOpen)
            {
                if (currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is UnityEngine.Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<UnityEngine.Renderer>(true))
                            renderer.enabled = false;
                }
            }
        }
    }
}
