using System.Collections.Generic;
using EFT.InventoryLogic;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // Virtual gunstock, modelled on the VR Expansion Plugin's GS_GunTools stock. The gun stays in the rear hand and only its aim changes.
    // The stock's mount follows the head's position (never its rotation) under the neck, and its influence fades with distance instead
    // of snapping. The support hand is drawn on the foregrip, so the gun must keep passing through it: the pivot starts on the gun's line
    // at the butt, then moves with the neck and only slowly follows the real butt, which filters the rear hand's shake out of the aim.
    // Only guns with a shouldered stock count: a mod Tarkov marks HasShoulderContact that isn't Weapon.Folded, or a built-in stock
    // (no stock slot: AUG, MDR, ASh-12, RFB, AXMC, AK-50) judged by its measured length.
    // All maths is in tracking space (hand rig-locals and the raw headset pose), because the hideout rewrites the camera's local transform.
    public static class ShoulderStock
    {


        // Influence = reach x height x posture, each fading from full to zero.
        // Reach (m): the butt's horizontal distance from the line straight down from the neck. A butt in the shoulder pocket sits
        // ~15-18 cm out; a gun held out at arm's length has it 30+ cm ahead.
        public static float fullInfluenceReach = 0.20f;
        public static float zeroInfluenceReach = 0.30f;
        // Height (m): how far the butt sits below the neck point. Full between these, fading over heightFade outside them.
        public static float minBelowNeck = -0.05f;
        public static float maxBelowNeck = 0.40f;
        public static float heightFade = 0.08f;
        // Posture (deg): the gun must point away from you along its line. Measured as the angle between the gun and the line from the neck
        // to a point 1 m down the gun.
        public static float fullInfluenceAngle = 25f;
        public static float zeroInfluenceAngle = 40f;
        public static float influenceRate = 6f;      // 1/s, the fastest the influence can change (so a fast hand move can't jerk the aim)

        // s, how slowly the resting pivot follows the gun's real butt. Longer = steadier aim, but slower to follow a re-shoulder.
        public static float pivotFollowTime = 0.35f;

        public static float integralStockMinLength = 0.12f; // m behind the grip for a gun without a stock slot to count as stocked
        public static float stockLengthFallback = 0.17f;    // m grip -> buttplate when the measurement finds nothing
        public static float braceStiffness = 1.6f;   // WeaponWeightFeel spring x this at full influence (braced = tighter)
        public static bool seatHaptic = false;       // a thunk on the hands on the gun when the stock is fully on
        public static bool debugLog = false;         // 1/sec state line
        public static bool debugViz = false;         // spheres: full-influence zone (blue), the gun's butt (yellow), the resting pivot (green)

        private static float influence;
        private static bool engaged;                 // the pivot is live (influence above zero since it was captured)
        private static Vector3 pivotOffset;          // resting pivot - neck, tracking space
        private static float lastSupportYaw;
        private static bool thunkArmed = true;
        private static float lastUpdateTime = -10f;
        private static float nextLogTime;

        public static float Blend => influence;
        public static float SpringMult => 1f + (braceStiffness - 1f) * influence;

        // Called from the two-handed aim each frame. Returns the aim base in world space: the rear hand, moved toward the
        // resting pivot by the influence. The gun aims from it toward the support hand.
        public static Vector3 UpdateAimBase(Transform rearHandTransform, Transform supportHandTransform, float deltaTime)
        {
            Vector3 rearWorld = rearHandTransform.position;
            if (Time.time - lastUpdateTime > 0.25f)   // wasn't two-handing (holster / eat / menu)
                ResetState();
            lastUpdateTime = Time.time;

            if (!VRSettings.GetVirtualStockOn() || TarkovVR.Source.Player.Interactions.ChestHang.Detached)
            {
                // Detached = the rear hand is off the gun (slung / support-held), so there is no stock butt
                ResetState();
                HideViz();
                return rearWorld;
            }

            Transform rig = rearHandTransform.parent;
            Vector3 rearHand = rearHandTransform.localPosition;
            Vector3 supportHand = rig != null ? rig.InverseTransformPoint(supportHandTransform.position) : supportHandTransform.position;
            if (!HeadTracking.RawHmdPose(out Vector3 headPosition, out Quaternion headRotation))
            {
                Camera camera = VRGlobals.VRCam != null ? VRGlobals.VRCam : Camera.main;
                if (camera == null)
                    return rearWorld;
                headPosition = camera.transform.localPosition;
                headRotation = camera.transform.localRotation;
            }
            Vector3 neck = headPosition + headRotation * HeadTracking.neckLocalOffset;

            Vector3 gunDirection = (supportHand - rearHand).normalized;
            bool stocked = TryGetStock(out float stockLength, out string stockReason);
            Vector3 butt = rearHand - gunDirection * stockLength;

            Vector3 buttFromNeck = butt - neck;
            float reach = new Vector2(buttFromNeck.x, buttFromNeck.z).magnitude;
            float belowNeck = -buttFromNeck.y;
            Vector3 toSight = (rearHand + gunDirection - neck).normalized;
            float postureAngle = Vector3.Angle(gunDirection, toSight);

            float target = 0f;
            if (stocked)
            {
                float reachFade = Fade(reach, fullInfluenceReach, zeroInfluenceReach);
                float heightFadeValue = belowNeck < minBelowNeck ? Fade(minBelowNeck - belowNeck, 0f, heightFade)
                    : Fade(belowNeck - maxBelowNeck, 0f, heightFade);
                float postureFade = Fade(postureAngle, fullInfluenceAngle, zeroInfluenceAngle);
                target = reachFade * heightFadeValue * postureFade;
            }
            influence = Mathf.MoveTowards(influence, target, influenceRate * deltaTime);

            // The resting pivot is captured on the gun's line at the butt, so the aim doesn't change when the stock comes on. After that
            // it is held relative to the neck, turns around it with the gun (not with the head), and is pulled slowly toward the real butt.
            float supportYaw = HorizontalYaw(supportHand - neck, lastSupportYaw);
            if (!engaged && influence > 0f)
            {
                engaged = true;
                pivotOffset = buttFromNeck;
                lastSupportYaw = supportYaw;
            }
            else if (engaged && influence <= 0f)
            {
                engaged = false;
            }
            if (engaged)
            {
                pivotOffset = Quaternion.AngleAxis(Mathf.DeltaAngle(lastSupportYaw, supportYaw), Vector3.up) * pivotOffset;
                lastSupportYaw = supportYaw;
                float follow = pivotFollowTime > 0f ? 1f - Mathf.Exp(-deltaTime / pivotFollowTime) : 1f;
                pivotOffset = Vector3.Lerp(pivotOffset, buttFromNeck, follow);
            }
            Vector3 pivot = neck + pivotOffset;

            if (influence >= 0.95f && thunkArmed)
            {
                thunkArmed = false;
                if (seatHaptic)
                    Pulse();
            }
            else if (influence < 0.5f)
            {
                thunkArmed = true;
            }

            if (debugLog && Time.time >= nextLogTime)
            {
                nextLogTime = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[ShoulderStock] influence {influence:0.00} (target {target:0.00}), butt {reach * 100f:0} cm out and "
                    + $"{belowNeck * 100f:0} cm below the neck, posture {postureAngle:0}°, stock {stockLength * 100f:0} cm ({stockReason}), "
                    + $"pivot {(pivot - butt).magnitude * 100f:0.0} cm off the butt");
            }

            Vector3 aimBase = engaged ? Vector3.Lerp(rearHand, pivot, influence) : rearHand;
            if (debugViz && rig != null)
                DrawViz(rig, neck, butt, pivot, stocked);
            else
                HideViz();
            return rig != null ? rig.TransformPoint(aimBase) : aimBase;
        }

        private static void ResetState()
        {
            influence = 0f;
            engaged = false;
            thunkArmed = true;
        }

        // 1 at or under full, 0 at or past zero, smooth between
        private static float Fade(float value, float full, float zero)
        {
            if (zero <= full)
                return value <= full ? 1f : 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(zero, full, value));
        }

        // Yaw of a direction in the horizontal plane; the previous value when it points nearly straight up or down
        private static float HorizontalYaw(Vector3 direction, float previous)
        {
            if (direction.x * direction.x + direction.z * direction.z < 0.0025f)
                return previous;
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        //--- The gun's stock --------------------------------------------------------------------------------------------------------
        // Re-checked 4x a second (folding / unfolding, parts swapped in the inventory); the length is re-measured when the parts change
        private static object stockOwner;
        private static float nextStockCheck;
        private static int stockPartsSignature;
        private static bool stockShoulderable;
        private static float stockMeasuredLength = -1f;
        private static string stockReasonText = "no gun";
        private static readonly List<Renderer> stockRenderers = new List<Renderer>();

        private static bool TryGetStock(out float length, out string reason)
        {
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.WeaponRoot == null)
            {
                length = stockLengthFallback;
                reason = "no gun";
                return false;
            }
            if (!ReferenceEquals(stockOwner, firearmController) || Time.time >= nextStockCheck)
            {
                bool newOwner = !ReferenceEquals(stockOwner, firearmController);
                stockOwner = firearmController;
                nextStockCheck = Time.time + 0.25f;
                CheckStock(firearmController, newOwner);
            }
            length = stockMeasuredLength > 0.01f ? stockMeasuredLength : stockLengthFallback;
            reason = stockReasonText;
            return stockShoulderable;
        }

        private static void CheckStock(EFT.Player.FirearmController firearmController, bool newOwner)
        {
            Weapon weapon = firearmController.Item as Weapon;
            if (weapon == null)
            {
                stockShoulderable = false;
                stockReasonText = "no weapon item";
                return;
            }
            bool hasStockSlot = false;
            foreach (Slot slot in weapon.AllSlots)
            {
                if (slot != null && slot.ID != null && slot.ID.StartsWith("mod_stock"))
                {
                    hasStockSlot = true;
                    break;
                }
            }
            bool shoulderContact = false;
            int signature = 17;
            foreach (Mod mod in weapon.Mods)
            {
                signature = signature * 31 + mod.Id.GetHashCode();
                if (mod.Template.HasShoulderContact)
                    shoulderContact = true;
            }
            bool folded = weapon.Folded;
            signature = signature * 31 + (folded ? 1 : 0);
            if (newOwner || signature != stockPartsSignature)
            {
                stockPartsSignature = signature;
                stockMeasuredLength = MeasureStock(firearmController, hasStockSlot);
            }

            if (folded)
            {
                stockShoulderable = false;
                stockReasonText = "stock folded";
            }
            else if (shoulderContact)
            {
                stockShoulderable = true;
                stockReasonText = "stock";
            }
            else if (!hasStockSlot && stockMeasuredLength >= integralStockMinLength)
            {
                stockShoulderable = true;
                stockReasonText = "built-in stock";
            }
            else
            {
                stockShoulderable = false;
                stockReasonText = hasStockSlot ? "no stock fitted" : "no stock";
            }
        }

        // Grip-to-buttplate length: how far the stock's own renderers (under the mod_stock* slot transforms) reach behind the grip marker
        // along the barrel's back axis (WeaponRoot +up; the muzzle is -up). Not the whole gun: its receiver caps, scope eyecups and
        // stock-release bones sit behind the grip too. A gun without a stock slot (built-in stock) measures every renderer.
        private static float MeasureStock(EFT.Player.FirearmController firearmController, bool hasStockSlot)
        {
            Transform weaponRoot = firearmController.WeaponRoot;
            Transform grip = TarkovVR.Patches.Core.Player.WeaponPatches.RightGripMarker(firearmController);
            if (grip == null)
                grip = weaponRoot;
            stockRenderers.Clear();
            if (hasStockSlot)
                CollectStockRenderers(weaponRoot);
            else
                weaponRoot.GetComponentsInChildren(false, stockRenderers);
            if (stockRenderers.Count == 0)
                return -1f;

            Vector3 back = weaponRoot.up;
            Vector3 gripPosition = grip.position;
            float rearMost = 0f;
            foreach (Renderer renderer in stockRenderers)
            {
                // Use the mesh-local bounds corners transformed to world, not Renderer.bounds: a world-aligned box grows as the gun turns
                // (~40% on a diagonal), so the length would depend on facing.
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                    mesh = skinned.sharedMesh;
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null)
                        mesh = filter.sharedMesh;
                }
                if (mesh == null)
                    continue;
                Bounds bounds = mesh.bounds;
                Transform rendererTransform = renderer.transform;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = BoundsCorners.Corner(bounds, corner);
                    float behindGrip = Vector3.Dot(rendererTransform.TransformPoint(local) - gripPosition, back);
                    if (behindGrip > rearMost)
                        rearMost = behindGrip;
                }
            }
            stockRenderers.Clear();
            return rearMost > 0.01f ? Mathf.Min(rearMost, 0.6f) : -1f;
        }

        private static void CollectStockRenderers(Transform parent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                string name = child.name;
                if (name.StartsWith("mod_stock") && !name.Contains("release"))
                    stockRenderers.AddRange(child.GetComponentsInChildren<Renderer>(false));
                else
                    CollectStockRenderers(child);
            }
        }


        //--- Debug spheres ------------------------------------------------------------------------------------------------------------
        private static GameObject zoneSphere;
        private static GameObject buttSphere;
        private static GameObject pivotSphere;

        private static void DrawViz(Transform rig, Vector3 neck, Vector3 butt, Vector3 pivot, bool stocked)
        {
            // The full-influence zone, drawn at the butt's height under the neck. The zone is really a column; a sphere there shows the same.
            Vector3 zoneCentre = new Vector3(neck.x, butt.y, neck.z);
            Color zoneColor = !stocked ? new Color(0.5f, 0.5f, 0.5f, 0.15f)
                : Color.Lerp(new Color(0.2f, 0.4f, 1f, 0.15f), new Color(0.2f, 1f, 0.3f, 0.25f), influence);
            SetSphere(ref zoneSphere, rig.TransformPoint(zoneCentre), fullInfluenceReach, zoneColor);
            SetSphere(ref buttSphere, rig.TransformPoint(butt), 0.02f, new Color(1f, 0.85f, 0.1f, 0.9f));
            SetSphere(ref pivotSphere, rig.TransformPoint(pivot), 0.015f, engaged ? new Color(0.1f, 1f, 0.2f, 0.9f) : new Color(0.1f, 1f, 0.2f, 0f));
        }

        private static void HideViz()
        {
            if (zoneSphere != null) zoneSphere.SetActive(false);
            if (buttSphere != null) buttSphere.SetActive(false);
            if (pivotSphere != null) pivotSphere.SetActive(false);
        }

        private static void SetSphere(ref GameObject sphere, Vector3 position, float radius, Color color)
        {
            DebugShapes.Sphere(ref sphere, "ShoulderStockViz", true, position, radius, color);
        }

        // The stock coming fully on: a pulse to every hand on the gun (GunHaptics)
        private static void Pulse()
        {
            TarkovVR.Source.Weapons.GunHaptics.Pulse(null, TarkovVR.Source.Weapons.GunHaptics.Feel.Mechanism, 0.5f, 0.06f, 110f);
        }
    }
}
