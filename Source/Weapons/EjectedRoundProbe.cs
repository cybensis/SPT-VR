using EFT;
using EFT.Interactive;
using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    // Logs a round the local player's rechamber throws out (Firearms.ThrowPatronAsLoot -> GameWorld.ThrowItem): where it spawned relative
    // to the shell port, and where it is half a second later and whether it's shown. Debug only (VRMagazinePatches.ProbeEjectedRound).
    internal sealed class EjectedRoundProbe : MonoBehaviour
    {
        public static bool enabled = false;

        private LootItem lootItem;
        private Vector3 spawnPosition;
        private float spawnedAt;

        public static void Attach(LootItem lootItem, Vector3 velocity)
        {
            if (!enabled || lootItem == null)
                return;
            Transform port = VRGlobals.player != null && VRGlobals.player.HandsController is EFT.Player.FirearmController firearmController
                && firearmController.Firearms != null && firearmController.Firearms._shellPortsTransforms.Count > 0
                ? firearmController.Firearms._shellPortsTransforms[0] : null;
            Plugin.MyLog.LogInfo($"[EjectedRound] {lootItem.Item?.LocalizedShortName()} thrown ({lootItem.GetType().Name}): "
                + $"{(port != null ? $"{Vector3.Distance(port.position, lootItem.transform.position) * 100f:F1} cm from the shell port" : "no shell port")}, "
                + $"velocity {velocity.magnitude:F2} m/s, {Describe(lootItem)}");
            var probe = lootItem.gameObject.AddComponent<EjectedRoundProbe>();
            probe.lootItem = lootItem;
            probe.spawnPosition = lootItem.transform.position;
            probe.spawnedAt = Time.time;
        }

        private void Update()
        {
            if (Time.time - spawnedAt < 0.5f)
                return;
            Plugin.MyLog.LogInfo($"[EjectedRound] 0.5 s later: {(lootItem != null && lootItem.gameObject.activeInHierarchy ? $"moved {Vector3.Distance(spawnPosition, lootItem.transform.position) * 100f:F0} cm, {Describe(lootItem)}" : "gone")}");
            Destroy(this);
        }

        private void OnDisable()
        {
            if (Time.time - spawnedAt < 0.5f)
                Plugin.MyLog.LogInfo($"[EjectedRound] disabled {Time.time - spawnedAt:F2} s after it was thrown (pooled / taken away)");
        }

        private static string Describe(LootItem lootItem)
        {
            int shown = 0, total = 0;
            foreach (Renderer renderer in lootItem.GetComponentsInChildren<Renderer>(true))
            {
                total++;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                    shown++;
            }
            Rigidbody body = lootItem.RigidBody;
            return $"active {lootItem.gameObject.activeInHierarchy}, layer {LayerMask.LayerToName(lootItem.gameObject.layer)}, {shown}/{total} renderers shown, "
                + $"rigidbody {(body != null ? $"{(body.isKinematic ? "kinematic" : "dynamic")} {body.velocity.magnitude:F2} m/s" : "none")}";
        }
    }
}
