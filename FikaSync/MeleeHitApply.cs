using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace SptVrFikaSync
{
    // Replays a remote VR-melee SURFACE hit so this client spawns the same sparks/decal/glass-break.
    // The swinger broadcasts the hit point/normal/dir; we raycast the SAME world geometry to find the
    // surface collider, rebuild a melee DamageInfoStruct, and call GameWorld.HackShot -> ApplyHit (the
    // exact path the swinger's hit took). SURFACE ONLY -- player/AI body parts are filtered out (their
    // damage already syncs; replaying HackShot on a body part would double-damage them).
    internal static class MeleeHitApply
    {
        public static float raycastBack = 0.35f;   // start the probe this far back along the swing dir
        public static float raycastRange = 0.8f;

        public static void Apply(VRMeleeHitPacket p)
        {
            if (!FikaVrSync.enableMeleeHitSync)
                return;
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null)
                return;

            Vector3 dir = p.Direction.sqrMagnitude > 1e-6f ? p.Direction.normalized : -p.Normal.normalized;
            // Find the surface collider at the synced hit point on THIS client.
            if (!Physics.Raycast(p.Point - dir * raycastBack, dir, out RaycastHit hit,
                    raycastBack + raycastRange, ~0, QueryTriggerInteraction.Ignore))
                return;
            BallisticCollider bc = hit.collider != null ? hit.collider.GetComponent<BallisticCollider>() : null;
            if (bc == null || bc is BodyPartCollider)
                return; // surface only -- never a player/AI

            DamageInfoStruct di = default;
            di.DamageType = EDamageType.Melee;
            di.Damage = p.Damage;
            di.ArmorDamage = 1f;
            di.HitPoint = p.Point;
            di.HitNormal = p.Normal;
            di.Direction = dir;
            di.HitCollider = hit.collider;
            di.HittedBallisticCollider = bc;
            di.IsForwardHit = true;
            // ApplyHit wants a valid Player; for a surface effect the attribution is cosmetic, so use
            // our own main player (avoids a null deref).
            if (gw.MainPlayer != null)
                di.Player = gw.GetEverExistedBridgeByProfileID(gw.MainPlayer.ProfileId);

            gw.HackShot(di); // -> HittedBallisticCollider.ApplyHit -> sparks/decal/destructible break
        }
    }
}
