using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace SptVrFikaSync
{
    // Replays a remote VR-melee SURFACE hit so this client spawns the same sparks/decal/impact sound
    // AND breaks the same destructible (glass). The custom VR melee runs its collider only on the
    // swinger, so observers never reproduce the hit; this carries the hit point/normal/swing-dir, we
    // re-find the SAME world surface collider on THIS client, rebuild a melee DamageInfoStruct and call
    // GameWorld.HackShot -- which on a FIKA client/host (a ClientGameWorld) runs BOTH
    // BallisticCollider.ApplyHit (-> the collider's OnHitAction, i.e. the destructible/glass-break
    // handler) AND EffectsCommutator.PlayKnifeHitEffect (-> Effects.Emit(isKnife:true), the
    // sparks/decal/impact sound). SURFACE ONLY -- body parts are filtered both ends (their damage
    // already syncs through ApplyShot; replaying HackShot on a body part would double-damage).
    //
    // The previous version cast along the SWING DIRECTION to find the collider, which for a slash is
    // ~tangent to a wall, so the ray missed and nothing happened (no sparks AND no glass break). We now
    // cast straight INTO the surface along -normal (always reliable, the swing dir is irrelevant for
    // locating the surface), with the knife's own layer mask + trigger-collide, plus an OverlapSphere
    // fallback for grazing/edge hits.
    internal static class MeleeHitApply
    {
        // Cast from raycastBack metres OUTSIDE the surface, along -normal, for (raycastBack+raycastRange).
        public static float raycastBack = 0.3f;
        public static float raycastRange = 0.3f;
        public static float overlapRadius = 0.2f;   // fallback: nearest surface collider within this sphere
        public static bool debug = false;           // log every apply attempt + outcome (throttled by the caller)

        // The exact layers the knife collider hits (BaseKnifeController int_2):
        // HitCollider, HighPolyCollider, TransparentCollider (glass), Water. Computed lazily — layer
        // indices aren't valid until the scene's layers exist.
        private static int _surfaceMask = -1;
        private static int SurfaceMask
        {
            get
            {
                if (_surfaceMask == -1)
                    _surfaceMask = LayerMask.GetMask("HitCollider", "HighPolyCollider", "TransparentCollider", "Water");
                return _surfaceMask;
            }
        }

        public static void Apply(VRMeleeHitPacket p)
        {
            if (!FikaVrSync.enableMeleeHitSync)
                return;
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null)
                return;

            BallisticCollider bc = FindSurfaceCollider(p, out Collider col);
            if (bc == null)
            {
                if (debug)
                    FikaSyncPlugin.Log.LogWarning($"[FikaSync] melee apply: no surface collider near {p.Point} (n={p.Normal})");
                return;
            }

            DamageInfoStruct di = default;
            di.DamageType = EDamageType.Melee;
            di.Damage = p.Damage;
            di.ArmorDamage = 1f;
            di.HitPoint = p.Point;
            di.HitNormal = p.Normal;
            di.Direction = p.Direction.sqrMagnitude > 1e-6f ? p.Direction.normalized : -SafeNormal(p.Normal);
            di.HitCollider = col;
            di.HittedBallisticCollider = bc;
            di.IsForwardHit = true;
            // ApplyHit/effects want a valid Player; for a surface effect the attribution is purely
            // cosmetic, so use our own main player (also avoids a null deref in any subscriber).
            if (gw.MainPlayer != null)
                di.Player = gw.GetEverExistedBridgeByProfileID(gw.MainPlayer.ProfileId);

            // ClientGameWorld.HackShot = base.HackShot (ApplyHit -> glass break) + PlayKnifeHitEffect
            // (sparks/decal/impact sound). One call reproduces the full local visual.
            gw.HackShot(di);

            if (debug)
                FikaSyncPlugin.Log.LogWarning($"[FikaSync] melee apply OK: {col.name} mat={bc.TypeOfMaterial} at {p.Point}");
        }

        // Find the same surface collider the swinger hit. Primary: raycast straight into the surface
        // along -normal (independent of the swing direction). Fallback: the nearest non-body surface
        // BallisticCollider overlapping a small sphere at the hit point.
        private static BallisticCollider FindSurfaceCollider(VRMeleeHitPacket p, out Collider col)
        {
            col = null;
            Vector3 n = SafeNormal(p.Normal);
            Vector3 start = p.Point + n * raycastBack;
            if (Physics.Raycast(start, -n, out RaycastHit hit, raycastBack + raycastRange,
                    SurfaceMask, QueryTriggerInteraction.Collide))
            {
                BallisticCollider bc = hit.collider != null ? hit.collider.GetComponent<BallisticCollider>() : null;
                if (bc != null && !(bc is BodyPartCollider))
                {
                    col = hit.collider;
                    return bc;
                }
            }

            // Fallback — grazing/edge hit where the -normal ray slips past the collider.
            Collider[] overlaps = Physics.OverlapSphere(p.Point, overlapRadius, SurfaceMask, QueryTriggerInteraction.Collide);
            BallisticCollider best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider c = overlaps[i];
                if (c == null)
                    continue;
                BallisticCollider bc = c.GetComponent<BallisticCollider>();
                if (bc == null || bc is BodyPartCollider)
                    continue;
                float sqr = (c.ClosestPoint(p.Point) - p.Point).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = bc;
                    col = c;
                }
            }
            return best;
        }

        private static Vector3 SafeNormal(Vector3 n)
        {
            return n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.up;
        }
    }
}
