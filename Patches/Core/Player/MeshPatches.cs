using EFT;
using EFT.UI;
using EFT.Visual;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MeshPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerBody), "UpdatePlayerRenders")]
        private static void LoadNoArmsModel(PlayerBody __instance, EPointOfView pointOfView)
        {
            if (VRGlobals.handsOnlyModel || pointOfView != EPointOfView.FirstPerson || __instance.PlayerBones == null || __instance.PlayerBones.Player == null || !__instance.PlayerBones.Player.IsYourPlayer)
                return;

            string bundlePath = Path.Combine(Application.dataPath, "StreamingAssets", "Windows", "assets", "content", "hands", "usec", "usec_hands_skin.bundle");
            string bundleAssetPath = "assets/content/hands/usec/usec_hands_skin.bundle";
            string rootPath = Directory.GetParent(Application.dataPath).FullName;
            string handsBundlePath = Path.Combine(rootPath, "BepInEx", "plugins", "sptvr", "Assets", "handsbundle");

            AssetBundle handsBundle = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b => b.name == bundleAssetPath);
            bool bundleLoadedHere = false;

            if (!handsBundle)
            {
                handsBundle = AssetBundle.LoadFromFile(bundlePath);
                if (!handsBundle) return;
                bundleLoadedHere = true;
            }

            GameObject[] assets = handsBundle.LoadAllAssets<GameObject>();
            if (assets.Length == 0)
            {
                if (bundleLoadedHere) handsBundle.Unload(true);
                return;
            }

            LoddedSkin handLod = assets[0].GetComponent<LoddedSkin>();

            if (VRGlobals.handsBundle == null) VRGlobals.handsBundle = AssetBundle.LoadFromFile(handsBundlePath);
            if (!VRGlobals.handsBundle)
            {
                if (bundleLoadedHere) handsBundle.Unload(true);
                return;
            }

            GameObject[] handsOnlyAssets = VRGlobals.handsBundle.LoadAllAssets<GameObject>();
            if (handsOnlyAssets.Length == 0)
            {
                VRGlobals.handsBundle.Unload(true);
                if (bundleLoadedHere) handsBundle.Unload(true);
                if (bundleLoadedHere) handsBundle.Unload(true);
                return;
            }

            LoddedSkin handsOnlyLod = GameObject.Instantiate(handsOnlyAssets[0]).GetComponent<LoddedSkin>();
            handsOnlyLod.Init(__instance.SkeletonHands, __instance);
            handsOnlyLod.Skin();
            handsOnlyLod.SetLayer(__instance._layer);
            handsOnlyLod.transform.SetParent(__instance.MeshTransform, false);

            VRGlobals.handsOnlyModel = handsOnlyLod._lods[0].SkinnedMeshRenderer;
            VRGlobals.origArmsModel = __instance.BodySkins[EBodyModelPart.Hands]._lods[0].SkinnedMeshRenderer;
            VRGlobals.legsModel = __instance.BodySkins[EBodyModelPart.Feet]._lods[0].SkinnedMeshRenderer;

            Material[] originalMaterials = handLod._lods[0].SkinnedMeshRenderer.materials;


            if (bundleLoadedHere)
            {

                for (int i = 0; i < VRGlobals.handsOnlyModel.materials.Length && i < originalMaterials.Length; i++)
                {
                    VRGlobals.handsOnlyModel.materials[i].shader = originalMaterials[i].shader;
                    // When loading in the bundle ourselves, the watch textures are part of another bundle so it doesn't get loaded in with them and will
                    // cause the material copy to fail, so we first copy over the texture to the original hands, then we load the material properties
                    originalMaterials[i].mainTexture = VRGlobals.handsOnlyModel.materials[i].mainTexture;
                    VRGlobals.handsOnlyModel.materials[i].CopyPropertiesFromMaterial(originalMaterials[i]);
                }
            }
            else {
                for (int i = 0; i < VRGlobals.handsOnlyModel.materials.Length && i < originalMaterials.Length; i++)
                {
                    VRGlobals.handsOnlyModel.materials[i].shader = originalMaterials[i].shader;
                    VRGlobals.handsOnlyModel.materials[i].CopyPropertiesFromMaterial(originalMaterials[i]);
                }
            }

            if (VRSettings.GetHideArms())
                VRGlobals.origArmsModel.transform.parent.gameObject.SetActive(false);
            else
                VRGlobals.handsOnlyModel.transform.parent.gameObject.SetActive(false);

            if (VRSettings.GetHideLegs())
                VRGlobals.legsModel.transform.parent.gameObject.SetActive(false);


            // Tarkov keeps track of bundles loaded internally so when something is loaded externally it will throw a fit when it tries to load it itself,
            // so unload the bundle here in case it needs to be loaded internally
            if (bundleLoadedHere) handsBundle.Unload(false);
        }


    }
}
