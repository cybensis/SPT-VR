using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Version compatibility between peers:
    // 2.1.0 adds packet types (VRReloadPacket / VRReloadSoundPacket): every peer needs 2.1.0 or newer, or FIKA's packet loop throws
    // on them (the "busy hands" drop).
    // 2.1.1 (ReloadSyncApply.SkipMalfunctionOperation): on an older peer a malfunction cleared by hand stays jammed on the VR player's copy.
    // 2.1.2 (BoltRoundSync): on an older host, tube-shotgun shells loaded by hand stay in the VR player's pouch.
    // 2.1.3 (ReloadSyncApply.SkipTarkovReload): an older host plays Tarkov's reload clip for by-hand magazine moves with Manual Reload
    // off, and FIKA holds that player's trigger until it ends.
    // 2.1.4 (ArmSyncApply.gunOnWeaponRoot): no packet change. An older peer draws a VR player's gun pulled toward its third-person hold
    // spot, off the hands with the gun in the left hand.
    // 2.2.0 adds a packet type (VRDoorPacket, doors moved by hand): every peer needs 2.2.0 or newer, or FIKA's packet loop throws
    // on it.
    // 2.3.0 adds a packet type (VRHeadPacket): every peer needs it. 2.3.1: the upper body solve runs (Tarkov's skeleton forks at Spine3).
    // 2.4.0: hands, gun and eating poses in the body's frame (Player.Transform), not the ribcage's. Pairs with an SPT-VR that sends them
    // so; either side older = the hands drawn off by the chest's pose.
    // 2.4.1 (ArmSyncApply): no packet change. An older observer puts a VR player's mounted gun in their hand at their last held gun's pose.
    // 2.4.2 (HeadSyncApply, UpperBodySolver): no packet change. An older observer keeps a VR player's hips as animated (side-on).
    [BepInPlugin("com.matsix.sptvr.fikasync", "SPT-VR FIKA Sync", "2.4.2")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    // Soft: ORBIT (bot-AI mod) runs on the host/headless alongside this module; when present, a dragged corpse's loot waypoint is
    // relocated here (SPT-VR.dll doesn't run on a headless). The dep only guarantees ORBIT is in the registry checked below.
    [BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
    public class FikaSyncPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // ORBIT detected via the BepInEx registry (a string, so no ORBIT type is bound on a peer without it)
            OrbitSync.Available = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.chazut.orbit");
            // Patches ArmSyncApply (the ObservedPlayer.LateUpdate postfix). Body-drag apply is driven from the packet handlers.
            new Harmony("com.matsix.sptvr.fikasync").PatchAll();
            FikaVrSync.Init();
            Log.LogInfo($"SPT-VR FIKA Sync loaded. ORBIT corpse-loot sync: {(OrbitSync.Available ? "ON" : "off (ORBIT not present)")}");
        }

        // Body-drag receiver, on every peer (incl. flatscreen/headless). FixedUpdate settles let-go bodies (active until landed
        // and at rest, then frozen); LateUpdate eases the synced ragdoll pose toward the latest packet.
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
