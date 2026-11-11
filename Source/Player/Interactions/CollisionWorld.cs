using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // What the gun (WeaponPhysicsProxy) and the free hands (HandCollision) hit, beyond their layer mask. The bullet layer
    // (HighPolyCollider) is the detailed world, except that each tree's bullet mesh is the whole tree with every branch
    // ("LOD0Collider"): those are skipped for the tree's movement-layer capsule (its trunk). The rest of the movement layer stays
    // out: it has pane-sized blockers in window openings. Triggers aren't solid, glass excepted.
    internal static class CollisionWorld
    {
        public static string treeBranchesName = "LOD0Collider";

        private static int highPolyLayer = -2, lowPolyLayer = -2, doorLayer = -2;
        private static readonly Dictionary<int, bool> solid = new Dictionary<int, bool>(1024);
        private static readonly Dictionary<int, Door> doors = new Dictionary<int, Door>(256);
        private static float solidClearTime;

        private static void ResolveLayers()
        {
            if (highPolyLayer != -2)
                return;
            highPolyLayer = LayerMask.NameToLayer("HighPolyCollider");
            lowPolyLayer = LayerMask.NameToLayer("LowPolyCollider");
            doorLayer = LayerMask.NameToLayer("DoorLowPolyCollider");
        }

        // A world mask + the movement layer (for the trunks) + the doors' own colliders
        internal static int Mask(int worldMask)
        {
            ResolveLayers();
            return worldMask | (lowPolyLayer >= 0 ? 1 << lowPolyLayer : 0) | (doorLayer >= 0 ? 1 << doorLayer : 0);
        }

        // The door a collider is part of, if the hands move it (PhysicalDoors.Supports); null otherwise
        internal static Door DoorOf(Collider collider)
        {
            ClearCachesNow();
            int id = collider.GetInstanceID();
            if (!doors.TryGetValue(id, out Door door))
            {
                door = collider.GetComponentInParent<Door>();
                if (door != null && !PhysicalDoors.Supports(door))
                    door = null;
                doors[id] = door;
            }
            return door;
        }

        // Out of a raid (WeaponPhysicsProxy): the doors held here would keep the map's doors alive
        internal static void Clear()
        {
            solid.Clear();
            doors.Clear();
        }

        private static void ClearCachesNow()
        {
            if (Time.time <= solidClearTime)
                return;
            solid.Clear();
            doors.Clear();
            solidClearTime = Time.time + 10f;
        }

        // Cached by instance id (a collider's name and kind don't change; reading a name allocates)
        internal static bool Solid(Collider collider)
        {
            ClearCachesNow();
            int id = collider.GetInstanceID();
            if (solid.TryGetValue(id, out bool isSolid))
                return isSolid;
            ResolveLayers();
            int layer = collider.gameObject.layer;
            isSolid = !(collider.isTrigger && layer != WeaponCollision.GlassLayer)
                && !(layer == highPolyLayer && collider.name == treeBranchesName)
                && !(layer == lowPolyLayer && !(collider is CapsuleCollider));
            solid[id] = isSolid;
            return isSolid;
        }
    }
}
