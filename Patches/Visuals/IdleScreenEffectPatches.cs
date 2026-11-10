using HarmonyLib;
using System;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{
    // BSG's screen effects copy the whole image every frame even while they show nothing (death fade off, no blood, no infection,
    // grayscale 0, vintage filter None): a full-screen copy per effect per eye, ~0.25 ms a frame in VR. These prefixes skip the call
    // only in the state where the game's code would just copy; the components stay enabled (DeathFade/BloodOnScreen animate in
    // their own Update). Safe with BSG's SSAAPropagator: every effect reads/writes its ping-pong pair and the "current" index only
    // moves on ReleaseSourceDestination, so a skipped effect leaves the image where the next one reads it (NightVision.Switch
    // disables chain effects the same way). Without the propagator the effects draw into Unity's source/destination, which must be written.
    [HarmonyPatch]
    internal class IdleScreenEffectPatches
    {
        public static bool skipIdleEffects = true;     // kill switch (live)

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeathFade), "OnRenderImage")]
        private static bool SkipIdleDeathFade(DeathFade __instance)
        {
            return !(skipIdleEffects && __instance._ssaaPropagator != null
                     && Math.Abs(__instance._closeEyesValue) < Mathf.Epsilon && Math.Abs(__instance._fadeValue) < Mathf.Epsilon);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BloodOnScreen), "OnRenderImage")]
        private static bool SkipIdleBlood(BloodOnScreen __instance)
        {
            return !(skipIdleEffects && __instance._ssaaPropagator != null && Time.time - __instance._lastHitTime > __instance.MaxBloodTime);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InfectionEffect), "OnRenderImage")]
        private static bool SkipIdleInfection(InfectionEffect __instance)
        {
            if (!skipIdleEffects || __instance._ssaaPropagator == null) return true;
            __instance._effectValue = Mathf.Clamp01(__instance._effectValue);   // the original's side effect
            return __instance._effectValue > 0f;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PostprocessGrayscale), "OnRenderImage")]
        private static bool SkipIdleGrayscale(PostprocessGrayscale __instance)
        {
            return !(skipIdleEffects && __instance._ssaaPropagator != null && Math.Abs(__instance.EffectAmount) < Mathf.Epsilon);
        }

        // Idle = no filter AND nothing pending for the filter bookkeeping AND no lookup texture (a serialized LUT with
        // filter None would still be applied by CC_LookupFilter).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CC_Vintage), "OnRenderImage")]
        private static bool SkipIdleVintage(CC_Vintage __instance)
        {
            return !(skipIdleEffects && __instance._ssaaPropagator != null && __instance.filter == CC_Vintage.Filter.None
                     && __instance.m_CurrentFilter == CC_Vintage.Filter.None && __instance.lookupTexture == null);
        }
    }
}
