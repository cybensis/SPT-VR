using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Core.Equippables;
using TarkovVR.Patches.Core.Player;
using UnityEngine;

namespace TarkovVR.Patches.Core.WeaponMods
{
    [HarmonyPatch]
    internal class TacDevicePatches
    {

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_46")]
        public static void AddNewTacDeviceToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer || __instance.weaponManagerClass == null)
                return;

            EquippablesShared.DisableEquippedRender();

            var weaponManager = __instance.weaponManagerClass;

            // Handle tactical devices
            if (weaponManager.tacticalComboVisualController_0 != null)
                foreach (var tacDevice in weaponManager.tacticalComboVisualController_0)
                    if (!EquippablesShared.currentGunInteractController.TacDeviceAlreadyRegistered(tacDevice.transform))
                        EquippablesShared.currentGunInteractController.AddTacticalDevice(tacDevice.transform, __instance.FirearmsAnimator);

            // Handle scopes
            var sightControllers = weaponManager.sightModVisualControllers_0;
            if (sightControllers == null) return;

            // Find valid scope
            foreach (var sightController in sightControllers)
            {
                if (sightController.scopePrefabCache_0 == null) continue;

                // Find scope camera
                VRGlobals.scope = sightController.transform.Find("mod_aim_camera") ??
                                 sightController.transform.Find("mod_aim_camera_001");

                if (VRGlobals.scope == null) continue;

                // Get visual controller
                var visualController = sightController.GetComponent<SightModVisualControllers>() ??
                                     sightController.transform.parent.GetComponent<SightModVisualControllers>();

                if (visualController == null || !VRGlobals.vrOpticController) continue;

                // Setup collider
                if (visualController.TryGetComponent<BoxCollider>(out var collider))
                {
                    collider.gameObject.layer = 6;
                    collider.size = new Vector3(0.09f, 0.04f, 0.02f);
                    collider.center = new Vector3(-0.04f, 0, -0.075f);
                    collider.enabled = true;
                }

                return;
            }

            VRGlobals.scope = null;
        }
    }
}
