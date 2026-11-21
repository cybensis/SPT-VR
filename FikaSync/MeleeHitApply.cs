using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace SptVrFikaSync
{
    // Replays a remote player's VR melee hit on a surface, so this client shows the same sparks, decal and impact sound and
    // breaks the same glass. The melee collider only runs on the swinger's machine, so the packet carries the hit point, normal
    // and swing direction. Here we find the same surface collider, build a melee DamageInfo and call GameWorld.HackShot.
    // On a FIKA ClientGameWorld that runs BallisticCollider.ApplyHit (the glass break) and EffectsCommutator.PlayKnifeHitEffect
    // (sparks, decal, impact sound). Surfaces only: body-part hits are filtered on both ends, because their damage already
    // syncs through ApplyShot and HackShot would apply it twice. The collider is found by casting into the surface along
    // -normal, because a slash moves almost along the wall and its swing direction would miss.
    internal static class MeleeHitApply
    {
        // m. The ray starts raycastBack outside the surface and runs raycastBack + raycastRange along -normal.
        public static float raycastBack = 0.3f;
        public static float raycastRange = 0.3f;
        public static float overlapRadius = 0.2f;   // fallback: nearest surface collider within this sphere
        public static bool debug = false;           // log every apply attempt + outcome (throttled by the caller)

        // The layers the knife collider hits (BaseKnifeController.int_2): HitCollider, HighPolyCollider,
        // TransparentCollider (glass) and Water. Computed lazily, because the layer indices aren't valid
        // until the scene's layers exist.
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

            DamageInfo di = default;
            di.DamageType = EDamageType.Melee;
            di.Damage = p.Damage;
            di.ArmorDamage = 1f;
            di.HitPoint = p.Point;
            di.HitNormal = p.Normal;
            di.Direction = p.Direction.sqrMagnitude > 1e-6f ? p.Direction.normalized : -SafeNormal(p.Normal);
            di.HitCollider = col;
            di.HittedBallisticCollider = bc;
            di.IsForwardHit = true;
            // ApplyHit and the effects need a valid Player. For a surface hit the attribution is only
            // cosmetic, so use our own main player (this also avoids a null reference in any subscriber).
            if (gw.MainPlayer != null)
                di.Player = gw.GetEverExistedBridgeByProfileID(gw.MainPlayer.ProfileId);

            // ClientGameWorld.HackShot runs base.HackShot (ApplyHit, the glass break) and PlayKnifeHitEffect
            // (sparks, decal, impact sound), so this one call reproduces the whole hit.
            gw.HackShot(di);

            if (debug)
                FikaSyncPlugin.Log.LogWarning($"[FikaSync] melee apply OK: {col.name} mat={bc.TypeOfMaterial} at {p.Point}");
        }

        // Finds the surface collider the swinger hit. First a raycast straight into the surface along -normal
        // (independent of the swing direction), then, as a fallback, the nearest non-body BallisticCollider
        // overlapping a small sphere at the hit point.
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

            // Fallback for a grazing or edge hit, where the -normal ray slips past the collider.
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
