using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    // Keeps the item in your hands (gun, knife, grenade) on its most detailed model. Every weapon part has its own LODGroup
    // sized to the part, and Unity picks the model by how big the part looks against the camera's vertical FOV. The headset's
    // ~110 deg (flatscreen 50-75) drops small parts to their coarse LOD1 well within arm's length: the Glock front sight at
    // ~13 cm, most pistol rear sights at 20-45 cm = an iron sight you can't line up. Vanilla never forces LOD0.
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
        private static readonly HashSet<LODGroup> waitingRelease = new HashSet<LODGroup>();

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
            if (Time.unscaledTime < nextScan)
                return;
            nextScan = Time.unscaledTime + rescanInterval;
            ReleaseWaiting();
            if (root == null)
                return;

            scan.Clear();
            root.GetComponentsInChildren(true, scan);
            int added = 0;
            foreach (LODGroup group in scan)
            {
                // (a hidden part is forced once it shows, on a later scan)
                if (Showing(group) && forced.Add(group))
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
                Release(group);
                forced.Remove(group);
            }
            if (debugLog && (added > 0 || released.Count > 0))
                Plugin.MyLog.LogInfo($"[HeldGunDetail] {hands.Item?.LocalizedShortName() ?? "?"}: {forced.Count} parts on full detail (+{added}, -{released.Count})");
        }

        // Rig torn down (raid end, menu): back to Unity's choice, and no references kept past the raid
        internal static void Teardown()
        {
            ReleaseAll();
            waitingRelease.Clear();
        }

        private static void ReleaseAll()
        {
            foreach (LODGroup group in forced)
                Release(group);
            forced.Clear();
            heldRoot = null;
        }

        // Unity refuses (and warns about) a forced LOD on a disabled or hidden group: one hidden now is let go once it shows
        private static bool Showing(LODGroup group) => group != null && group.enabled && group.gameObject.activeInHierarchy;

        private static void Release(LODGroup group)
        {
            if (Showing(group))
                group.ForceLOD(-1);
            else if (group != null)
                waitingRelease.Add(group);
        }

        private static void ReleaseWaiting()
        {
            if (waitingRelease.Count == 0)
                return;
            released.Clear();
            foreach (LODGroup group in waitingRelease)
            {
                if (group == null || forced.Contains(group))
                    released.Add(group);
                else if (Showing(group))
                {
                    group.ForceLOD(-1);
                    released.Add(group);
                }
            }
            foreach (LODGroup group in released)
                waitingRelease.Remove(group);
        }    }
}
