using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SptVrFikaSync
{
    // 2.1.0: the manual reload sync adds packet types (VRReloadPacket / VRReloadSoundPacket). Every peer needs this version or newer:
    // a peer whose module doesn't register them throws on them inside FIKA's packet loop (the "busy hands" drop).
    // 2.1.1: malfunctions cleared by hand finish on the VR player's copy (ReloadSyncApply.SkipMalfunctionOperation). No new packets;
    // a 2.1.0 peer's copy of them just stays jammed.
    // 2.1.2: a tube shotgun's shells loaded by hand are accepted on the VR player's copy (BoltRoundSync). No new packets; on a host
    // with an older module the shells stay in the VR player's pouch.
    [BepInPlugin("com.matsix.sptvr.fikasync", "SPT-VR FIKA Sync", "2.1.2")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    // Soft: ORBIT (bot-AI mod) runs on the host/headless alongside this module. When present we relocate a
    // dragged corpse's loot waypoint here (the VR mod can't — it doesn't run on a headless). Soft so this
    // module still loads on a peer without ORBIT; the dep just guarantees ORBIT is in the registry below.
    [BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
    public class FikaSyncPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // Detect ORBIT via the BepInEx registry (string literal → never binds an ORBIT type on a
            // no-ORBIT peer). Gates OrbitSync's ORBIT-typed corpse-relocation.
            OrbitSync.Available = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.chazut.orbit");
            // Patches ArmSyncApply (the ObservedPlayer.LateUpdate postfix). Body-drag apply is driven
            // from the packet handlers, not a patch.
            new Harmony("com.matsix.sptvr.fikasync").PatchAll();
            FikaVrSync.Init();
            Log.LogInfo($"SPT-VR FIKA Sync loaded. ORBIT corpse-loot sync: {(OrbitSync.Available ? "ON" : "off (ORBIT not present)")}");
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
            try { BodyDragApply.RenderSmoothing(); DownedDragApply.RenderSmoothing(); }
            catch (System.Exception e)
            {
                if (!_bodyRenderErr) { _bodyRenderErr = true; Log.LogError($"[FikaSync] body render smoothing error: {e}"); }
            }
        }
    }
}
