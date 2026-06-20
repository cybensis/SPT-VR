using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Standalone FIKA companion plugin. Hard-depends on FIKA (useless without it) and has NO VR
    // dependency, so it loads on flatscreen players and headless clients too. Its whole job: register
    // the VR mod's custom packets (so those peers don't crash on an "Undefined packet"), relay them
    // as host, and render the synced arms / dragged bodies.
    [BepInPlugin("com.matsix.sptvr.fikasync", "SPT-VR FIKA Sync", "1.0.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    public class FikaSyncPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // Patches ArmSyncApply (the ObservedPlayer.LateUpdate postfix). Body-drag apply is driven
            // from the packet handlers, not a patch.
            new Harmony("com.matsix.sptvr.fikasync").PatchAll();
            FikaVrSync.Init();
            Log.LogInfo("SPT-VR FIKA Sync loaded.");
        }

        // Pump the body-drag receiver on every peer (incl. flatscreen/headless). FixedUpdate (physics
        // step) settles let-go bodies (keep active until landed + rest, then freeze); LateUpdate (per
        // frame) eases the synced ragdoll pose toward the latest packet so it renders smoothly.
        private bool _bodyTickErr;
        private void FixedUpdate()
        {
            try { BodyDragApply.Tick(); }
            catch (System.Exception e)
            {
                if (!_bodyTickErr) { _bodyTickErr = true; Log.LogError($"[FikaSync] body settle tick error: {e}"); }
            }
        }

        private bool _bodyRenderErr;
        private void LateUpdate()
        {
            try { BodyDragApply.RenderSmoothing(); }
            catch (System.Exception e)
            {
                if (!_bodyRenderErr) { _bodyRenderErr = true; Log.LogError($"[FikaSync] body render smoothing error: {e}"); }
            }
        }
    }
}
