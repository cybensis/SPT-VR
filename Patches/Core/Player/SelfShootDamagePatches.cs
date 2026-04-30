using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using static EFT.Player;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class SelfShootDamagePatches
    {
        private const float MUZZLE_MARGIN = 0.05f;

        /// <summary>
        /// Radius of the virtual head sphere centered on the VR camera position.
        /// Used instead of body model's head colliders which are offset from the camera in VR.
        /// </summary>
        private const float VR_HEAD_RADIUS = 0.12f;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EftBulletClass), nameof(EftBulletClass.IsHitIgnored))]
        private static bool AllowSelfHitInVR(EftBulletClass __instance, ref bool __result,
            IPlayerOwner player, RaycastHit hit, bool isFirstFragment)
        {
            if (player == null)
            {
                return true;
            }

            if (!isFirstFragment || VRGlobals.player == null ||
                player.iPlayer.ProfileId != VRGlobals.player.ProfileId)
            {
                return true;
            }

            // Skip in hideout — no health controller to apply damage to.
            if (VRGlobals.player.ActiveHealthController == null)
            {
                return true;
            }

            if (!player.HasBodyPartCollider(hit.collider))
            {
                return true;
            }

            // Head self-hits are handled exclusively via the VR camera sphere in MuzzleCheck.
            // Ignore ballistic hits on head colliders to prevent false damage from offset body model,
            // but stop the bullet so it doesn't fly through the head.
            BodyPartCollider bpc = hit.collider.GetComponent<BodyPartCollider>();
            if (bpc != null && bpc.BodyPartType == EBodyPart.Head)
            {
                __instance.BulletState = EftBulletClass.EBulletState.StopHit;
                return true;
            }

            FirearmController fc = VRGlobals.firearmController;
            if (fc != null && fc.WeaponLn > 0f)
            {
                Vector3 gunBase = fc.GunBaseTransform.position;
                Vector3 muzzle = fc.CurrentFireport.position;
                Vector3 barrelDir = (muzzle - gunBase).normalized;
                float checkLength = fc.WeaponLn - MUZZLE_MARGIN;

                if (checkLength > 0f)
                {
                    Ray barrelRay = new(gunBase, barrelDir);
                    if (hit.collider.Raycast(barrelRay, out _, checkLength))
                    {
                        // Barrel intersects body collider — weapon pushed through body, ignore
                        return true;
                    }

                    // Raycast missed — gunBase may be inside the collider (ray from inside
                    // doesn't detect the collider in Unity). Check with ClosestPoint.
                    Vector3 closestToBase = hit.collider.ClosestPoint(gunBase);
                    if ((closestToBase - gunBase).sqrMagnitude < 0.0001f)
                    {
                        return true;
                    }
                }
            }

            __result = false;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.method_58))]
        private static void MuzzleHeadDamageCheck(FirearmController __instance, AmmoItemClass ammo)
        {
            EFT.Player player = VRGlobals.player;
            if (player == null || !__instance._player.IsYourPlayer)
            {
                return;
            }

            // Skip in hideout — no health controller to apply damage to.
            if (player.ActiveHealthController == null)
            {
                return;
            }

            if (__instance._player.ProfileId != player.ProfileId)
            {
                return;
            }

            Camera vrCam = VRGlobals.VRCam;
            if (vrCam == null)
            {
                return;
            }

            Vector3 muzzle = __instance.CurrentFireport.position;
            Vector3 gunBase = __instance.GunBaseTransform.position;
            Vector3 barrelDir = (muzzle - gunBase).normalized;

            // Offset from eye position (camera) to approximate head center:
            // ~8cm back from the eyes along the camera's forward axis.
            Vector3 headCenter = vrCam.transform.position - vrCam.transform.forward * 0.08f;
            float distToHead = Vector3.Distance(muzzle, headCenter);

            Vector3 toHead = (headCenter - muzzle).normalized;
            float aimDot = Vector3.Dot(barrelDir, toHead);

            bool headHit = false;
            if (distToHead < VR_HEAD_RADIUS)
            {
                // Muzzle inside head sphere — require barrel aimed at head center
                headHit = aimDot > 0.5f;
            }
            else if (RaySphereIntersect(muzzle, barrelDir, headCenter, VR_HEAD_RADIUS))
            {
                headHit = true;
            }

            if (!headHit)
            {
                return;
            }

            BodyPartCollider[] hitColliders = player._hitColliders;
            if (hitColliders == null)
            {
                return;
            }

            // Determine head zone by projecting bullet direction onto VR camera orientation.
            EBodyPartColliderType zone = GetHeadZoneFromDirection(barrelDir, vrCam.transform);

            BodyPartCollider headCollider = null;
            BodyPartCollider fallback = null;
            foreach (BodyPartCollider bodyPartCollider in hitColliders)
            {
                if (bodyPartCollider.BodyPartColliderType == zone)
                {
                    headCollider = bodyPartCollider;
                    break;
                }

                if (bodyPartCollider.BodyPartColliderType == EBodyPartColliderType.HeadCommon)
                {
                    fallback = bodyPartCollider;
                }
            }

            headCollider ??= fallback;

            if (headCollider == null)
            {
                return;
            }

            IPlayerOwner playerOwner =
                Singleton<GameWorld>.Instance.GetEverExistedBridgeByProfileID(player.ProfileId);
            if (playerOwner == null)
            {
                return;
            }

            // Simulate ricochet and penetration checks, matching vanilla SetShotStatus flow.
            // Without this, ProceedDamageThroughArmor treats every hit as full penetration.
            MongoID? blockedBy = null;
            MongoID? deflectedBy = null;
            List<ArmorComponent> armors = [];
            player.Inventory.GetPutOnArmorsNonAlloc(armors);

            // Compute hit normal on VR head sphere for ricochet angle calculation.
            Vector3 sphereHitNormal = (muzzle - headCenter).normalized;

            foreach (ArmorComponent armor in armors)
            {
                if (!armor.ShotMatches(headCollider.BodyPartColliderType, (EArmorPlateCollider)0))
                    continue;
                if (armor.Repairable.Durability <= 0f)
                    continue;

                // Ricochet check (same logic as ArmorComponent.Deflects / SetShotStatus)
                Vector3 ricochetVals = armor.Template.RicochetVals;
                if (ricochetVals.x > 0f)
                {
                    float angle = Vector3.Angle(-barrelDir, sphereHitNormal);
                    if (angle > ricochetVals.z)
                    {
                        float t = Mathf.InverseLerp(90f, ricochetVals.z, angle);
                        float ricochetChance = Mathf.Lerp(ricochetVals.x, ricochetVals.y, t);
                        if (Random.value < ricochetChance)
                        {
                            deflectedBy = armor.Item.Id;
                            break;
                        }
                    }
                }

                // Penetration check
                float penPower = ammo.PenetrationPower;
                ArmorResistanceStruct resist = GClass659.RealResistance(
                    armor.Repairable.Durability, armor.Repairable.TemplateDurability,
                    armor.ArmorClass, penPower);
                float penChance = resist.GetPenetrationChance(penPower);

                if (Random.value * 100f > penChance)
                {
                    blockedBy = armor.Item.Id;
                }
                break;
            }

            DamageInfoStruct damageInfo = new()
            {
                DamageType = EDamageType.Bullet,
                Damage = ammo.Damage,
                PenetrationPower = ammo.PenetrationPower,
                ArmorDamage = ammo.ArmorDamagePortion,
                BlockedBy = blockedBy,
                DeflectedBy = deflectedBy,
                Direction = barrelDir,
                HitPoint = muzzle,
                HitNormal = sphereHitNormal,
                MasterOrigin = gunBase,
                HittedBallisticCollider = headCollider,
                Player = playerOwner,
                Weapon = __instance.Item,
                IsForwardHit = true,
                StaminaBurnRate = ammo.StaminaBurnRate,
                HeavyBleedingDelta = ammo.HeavyBleedingDelta,
                LightBleedingDelta = ammo.LightBleedingDelta,
                SourceId = ammo.TemplateId
            };

            float hpBefore = player.ActiveHealthController.GetBodyPartHealth(EBodyPart.Head).Current;

            ShotInfoClass shotInfo = player.ApplyShot(damageInfo, headCollider.BodyPartType, headCollider.BodyPartColliderType,
                (EArmorPlateCollider)0, ShotIdStruct.EMPTY_SHOT_ID);

            float hpAfter = player.ActiveHealthController.GetBodyPartHealth(EBodyPart.Head).Current;
            float absorbed = ammo.Damage - (hpBefore - hpAfter);

            // ApplyShot always passes absorbed=0 to ApplyDamageInfo, so BluntContusion
            // never triggers from bullets. Apply contusion on any armored head hit.
            if (shotInfo != null && shotInfo.Material != MaterialType.Body && absorbed > 0f)
            {
                player.ActiveHealthController.DoContusion(30f, 0.5f);
            }
        }

        /// <summary>
        /// Maps bullet direction to a head zone using VR camera orientation.
        /// The impact normal (-barrelDir) in camera-local space tells us which part of the head is hit:
        /// camera forward = face, camera up = top of head, camera right = right ear.
        /// </summary>
        private static EBodyPartColliderType GetHeadZoneFromDirection(Vector3 barrelDir, Transform camTransform)
        {
            // Impact direction in camera-local space: where on the head the bullet lands
            Vector3 local = camTransform.InverseTransformDirection(-barrelDir);
            float fwd = local.z;   // positive = hits face, negative = hits back of head
            float up = local.y;    // positive = hits top, negative = hits bottom/neck
            float side = Mathf.Abs(local.x);

            // NeckFront/NeckBack are not EBodyPart.Head — they're handled by ballistics
            // with their own armor checks, so we only map to actual head zones here.
            if (up > 0.7f) return EBodyPartColliderType.ParietalHead;
            if (side > 0.7f) return EBodyPartColliderType.Ears;
            if (fwd < -0.3f) return EBodyPartColliderType.BackHead;
            if (up < -0.2f) return EBodyPartColliderType.Jaw;
            if (up < 0.3f) return EBodyPartColliderType.Eyes;

            return EBodyPartColliderType.HeadCommon;
        }

        private static bool RaySphereIntersect(Vector3 origin, Vector3 dir, Vector3 center,
            float radius)
        {
            Vector3 oc = origin - center;
            float b = Vector3.Dot(oc, dir);
            float c = oc.sqrMagnitude - radius * radius;

            if (c <= 0f)
            {
                return true;
            }

            float discriminant = b * b - c;
            if (discriminant < 0f)
            {
                return false;
            }

            float t = -b - Mathf.Sqrt(discriminant);
            return !(t < 0f);
        }
    }
}
