using EFT;
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
    internal class MagazinePatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_49")]
        private static void AddNewMagToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            EquippablesShared.DisableEquippedRender();

            if (__instance.weaponPrefab_0)
            {
                if (__instance.weaponPrefab_0.Renderers != null)
                {
                    for (int i = 0; i < __instance.weaponPrefab_0.Renderers.Length; i++)
                    {
                        if (__instance.weaponPrefab_0.Renderers[i].transform.parent.GetComponent<MagazineInHandsVisualController>())
                        {
                            EquippablesShared.currentGunInteractController.SetMagazine(__instance.weaponPrefab_0.Renderers[i].transform, false);
                            return;
                        }
                    }

                }
            }
            //Plugin.MyLog.LogWarning(__instance.transform.root+"\n\n\n");
            //Plugin.MyLog.LogWarning(new StackTrace());

        }
    }
}
