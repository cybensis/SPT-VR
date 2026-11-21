using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Version compatibility between peers:
    // 2.1.0 adds packet types (VRReloadPacket, VRReloadSoundPacket). Every peer needs 2.1.0 or newer, or FIKA's packet loop throws
    // on them and the hands get stuck busy.
    // 2.1.1 (ReloadSyncApply.SkipMalfunctionOperation): on an older peer a malfunction cleared by hand stays jammed on the VR player's copy.
    // 2.1.2 (BoltRoundSync): on an older host, tube-shotgun shells loaded by hand stay in the VR player's pouch.
    // 2.1.3 (ReloadSyncApply.SkipTarkovReload): with Manual Reload off, an older host plays Tarkov's reload clip for magazine moves made
    // by hand, and FIKA holds that player's trigger until it ends.
    // 2.1.4 (ArmSyncApply.gunOnWeaponRoot): no packet change. An older peer draws a VR player's gun pulled toward its third-person hold
    // spot, which is off the hands when the gun is in the left hand.
    // 2.2.0 adds a packet type (VRDoorPacket, doors moved by hand). Every peer needs 2.2.0 or newer, or FIKA's packet loop throws on it.
    // 2.3.0 adds a packet type (VRHeadPacket). Every peer needs it. 2.3.1 makes the upper body solve run (Tarkov's skeleton forks at Spine3).
    // 2.4.0 sends the hands, gun and eating poses in the body's frame (Player.Transform), not the ribcage's. It needs an SPT-VR that
    // sends them that way. If either side is older, the hands are drawn off by the chest's pose.
    // 2.4.1 (ArmSyncApply): no packet change. An older observer puts a VR player's mounted gun in their hand at their last held gun's pose.
    // 2.4.2 (HeadSyncApply, UpperBodySolver): no packet change. An older observer leaves a VR player's hips as animated (side-on).
    // 2.4.3 (UpperBodySolver): no packet change. The hips are squared to the body's facing instead of turned half way to the look.
    // 2.4.4 (DoorSyncApply, BreachReplay): no packet change. On an older observer, a door moved by hand moves in steps at the packet rate,
    // its shut sound is missing, and a door a VR player breaches with the gun can stay shut (the kick swing is skipped or dropped).
    // 2.4.5 (HostHandsUnstick): no packet change, only the host needs it. An older host can leave a hands event open on its copy of a
    // client after a quick hands change, and then refuse that item ("currently being modified") for the rest of the raid.
    // 2.4.6 (BoltRoundSync): no packet change. On an older host, shells loaded into or taken out of a break-action by hand stay where they
    // were, because the host plays Tarkov's chamber clip on its copy instead.
    // 2.4.7 (BoltRoundSync): no packet change. The same for a revolver's cylinder loaded or emptied by hand.
    [BepInPlugin("com.matsix.sptvr.fikasync", "SPT-VR FIKA Sync", "2.4.7")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    // Soft dependency: ORBIT (a bot-AI mod) runs on the host/headless next to this module. When it is present, a dragged corpse's
    // loot waypoint is moved here, because SPT-VR.dll doesn't run on a headless. The dependency only makes sure ORBIT is in the
    // plugin registry before the check below.
    [BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
    public class FikaSyncPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // Detect ORBIT by its plugin id string, so no ORBIT type is loaded on a peer without it.
            OrbitSync.Available = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.chazut.orbit");
            // Applies the Harmony patches, such as ArmSyncApply's ObservedPlayer.LateUpdate postfix. Body-drag sync is driven from the
            // packet handlers instead.
            new Harmony("com.matsix.sptvr.fikasync").PatchAll();
            FikaVrSync.Init();
            Log.LogInfo($"SPT-VR FIKA Sync loaded. ORBIT corpse-loot sync: {(OrbitSync.Available ? "ON" : "off (ORBIT not present)")}");
        }

        // Body-drag receiver, on every peer (including flatscreen and headless). FixedUpdate settles bodies that were let go: they stay
        // active until they have landed and come to rest, then they are frozen. LateUpdate eases the synced ragdoll pose toward the
        // latest packet.
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
        private bool _doorTickErr;
        private void LateUpdate()
        {
            try { BodyDragApply.RenderSmoothing(); DownedDragApply.RenderSmoothing(); }
            catch (System.Exception e)
            {
                if (!_bodyRenderErr) { _bodyRenderErr = true; Log.LogError($"[FikaSync] body render smoothing error: {e}"); }
            }
            try { DoorSyncApply.Tick(); }
            catch (System.Exception e)
            {
                if (!_doorTickErr) { _doorTickErr = true; Log.LogError($"[FikaSync] door smoothing error: {e}"); }
            }
        }
    }
}
