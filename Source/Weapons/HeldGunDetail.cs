using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    // Keeps the item in your hands (gun, knife, grenade) on its most detailed model. Every weapon part has its own LODGroup
    // sized to the part, and Unity picks the model by how big the part looks against the camera's vertical FOV. The headset's
    // ~110 deg (flatscreen 50-75) drops small parts to their coarse LOD1 well within arm's length: the Glock front sight at
    // ~13 cm, most pistol rear sights at 20-45 cm (bundle scan, 2026-11-09) = an iron sight you can't line up. Vanilla never
    // forces LOD0; on a flat screen the gun just never gets that far from a narrow camera.
    // Guns are pooled (bots and dropped loot reuse the objects), so a part is released (ForceLOD(-1)) as soon as it leaves the hands.
    internal static class HeldGunDetail
    {
        // Kill-switch (live): off releases everything
        public static bool enabled = true;
        // Re-scan for attachments added / taken off while held; the hands change itself is caught every frame
        public static float rescanInterval = 0.5f;
        public static bool debugLog = false;

        private static Transform heldRoot;
        private static float nextScan;
        private static readonly HashSet<LODGroup> forced = new HashSet<LODGroup>();
        private static readonly List<LODGroup> scan = new List<LODGroup>();
        private static readonly List<LODGroup> released = new List<LODGroup>();

        // From the rig's Update (raid + hideout)
        internal static void Tick()
        {
            EFT.Player.AbstractHandsController hands =VRGlobals.player != null ? VRGlobals.player.HandsController : null;
            Transform root = enabled && hands != null ? hands.WeaponRoot : null;
            // By reference: a destroyed root compares equal to null under Unity's ==, and its parts must still be let go
            if (!ReferenceEquals(root, heldRoot))
            {
                ReleaseAll();
                heldRoot = root;
                nextScan = 0f;
            }
            if (root == null || Time.unscaledTime < nextScan)
                return;
            nextScan = Time.unscaledTime + rescanInterval;

            scan.Clear();
            root.GetComponentsInChildren(true, scan);
            int added = 0;
            foreach (LODGroup group in scan)
            {
                if (forced.Add(group))
                {
                    group.ForceLOD(0);
                    added++;
                }
            }
            released.Clear();
            foreach (LODGroup group in forced)
            {
                if (group == null || !group.transform.IsChildOf(root))
                    released.Add(group);
            }
            foreach (LODGroup group in released)
            {
                if (group != null)
                    group.ForceLOD(-1);
                forced.Remove(group);
            }
            if (debugLog && (added > 0 || released.Count > 0))
                Plugin.MyLog.LogInfo($"[HeldGunDetail] {hands.Item?.LocalizedShortName() ?? "?"}: {forced.Count} parts on full detail (+{added}, -{released.Count})");
        }

        // Rig torn down (raid end, menu): back to Unity's choice, and no references kept past the raid
        internal static void ReleaseAll()
        {
            foreach (LODGroup group in forced)
            {
                if (group != null)
                    group.ForceLOD(-1);
            }
            forced.Clear();
            heldRoot = null;
        }
    }
}
