using EFT.InventoryLogic;
using RootMotion.FinalIK;
using System;
using System.Text;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // Prop wiring: find + reparent the food props onto the hands at spawn
    // (Setup*Props), re-pin them every LateUpdate (LateZeroProps - the animator
    // rewrites prop locals even at speed 0), the per-archetype prop actions
    // (detach/reattach/shake/take), and the restore-on-teardown path.
    internal static partial class EatingInteractionController
    {
        // Canned (tushonka, sprats): can in the LEFT hand; spoon (if any) + food in the RIGHT.
        // Food hangs off the spoon holder (HasSpoon) or the right hand. False+Reset if a prop's
        // missing. Optional extras: a SECOND food piece (sugar's pair of cubes, own holder on
        // the same hand) and a glued SIBLING group (sugar's remaining pile sits NEXT TO the box
        // in the rig — without the glue it stays behind on the body-space rig).
        private static bool SetupCannedProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            foodT = FindDeep(root, def.foodPieceName);
            spoonT = def.HasSpoon ? FindDeep(root, def.spoonName) : null;
            foodT2 = string.IsNullOrEmpty(def.foodPiece2Name) ? null : FindDeep(root, def.foodPiece2Name);
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);

            if (baseT != null && foodT == null && spoonT == null && wrapperT == null)
            {
                Save(baseT, out baseParent0, out basePos0, out baseRot0);

                baseHolder = NewHolder("EatBaseHolder", leftHandBone, def.basePos, def.baseRot);
                baseT.SetParent(baseHolder.transform, false);
                reparented = true;
                return true;
            }

            if (baseT == null || foodT == null || (def.HasSpoon && spoonT == null))
                return BailToVanilla($"Missing props (base={baseT != null} spoon={(def.HasSpoon ? (spoonT != null).ToString() : "n/a")} food={foodT != null})");
            if (!string.IsNullOrEmpty(def.foodPiece2Name) && foodT2 == null)
                Plugin.MyLog.LogWarning($"[ManualEat] second piece '{def.foodPiece2Name}' not found — taking one.");

            spoonR = spoonT != null ? spoonT.GetComponentInChildren<Renderer>(true) : null;
            foodR = foodT.GetComponentInChildren<Renderer>(true);
            foodR2 = foodT2 != null ? foodT2.GetComponentInChildren<Renderer>(true) : null;

            // Capture the glued group's rest offset BEFORE anything moves.
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
                Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (spoonT != null) Save(spoonT, out spoonParent0, out spoonPos0, out spoonRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);
            if (foodT2 != null) Save(foodT2, out food2Parent0, out food2Pos0, out food2Rot0);

            baseHolder = NewHolder("EatBaseHolder", leftHandBone, def.basePos, def.baseRot);
            if (def.HasSpoon)
            {
                // Utensil in the right hand; the food piece sits in the utensil bowl.
                spoonHolder = NewHolder("EatSpoonHolder", rightHandBone, def.spoonPos, def.spoonRot);
                foodHolder = NewHolder("EatFoodHolder", spoonHolder.transform, def.foodPos, def.foodRot);
            }
            else
            {
                // No utensil: the food piece is grabbed directly by the right hand.
                spoonHolder = null;
                foodHolder = NewHolder("EatFoodHolder", rightHandBone, def.foodPos, def.foodRot);
            }
            foodHolder2 = foodT2 != null
                ? NewHolder("EatFood2Holder", def.HasSpoon ? spoonHolder.transform : rightHandBone, def.food2Pos, def.food2Rot)
                : null;

            baseT.SetParent(baseHolder.transform, false);
            if (spoonT != null) spoonT.SetParent(spoonHolder.transform, false);
            foodT.SetParent(foodHolder.transform, false);
            if (foodT2 != null) foodT2.SetParent(foodHolder2.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false); // ride the held box at its rest offset
                wrapperT.localPosition = wrapperLocalPos;
                wrapperT.localRotation = wrapperLocalRot;
            }
            reparented = true;

            SetRenderer(spoonR, false); // appears on "open" (no-op if no spoon)
            SetRenderer(foodR, false);  // appears on "scoop"/grab
            SetRenderer(foodR2, false); // the pair cube appears with it
            // (Draw/Open/Open2[/SpoonTake] fire from the STATE_OPEN segment itself.)
            return true;
        }

        // Drink: container in the HOLDING hand (right unless holdInOffHand). The cap (if any)
        // is a SIBLING of the container in the rig (cap + mod_item both under weapon), so
        // reparenting the container alone would strand it — instead it's glued ON the
        // container at its rest offset (visible from the draw), detached to the free hand by
        // the open gesture, and glued back on a recap.
        private static bool SetupDrinkProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            capT = def.HasCap ? FindDeep(root, def.capName) : null;

            if (baseT == null || (def.HasCap && capT == null))
                return BailToVanilla($"Missing props (base={baseT != null} cap={(def.HasCap ? (capT != null).ToString() : "n/a")})");

            capR = capT != null ? capT.GetComponentInChildren<Renderer>(true) : null;

            // Capture the cap's on-container offset BEFORE anything moves.
            if (capT != null)
            {
                capLocalPos = baseT.InverseTransformPoint(capT.position);
                capLocalRot = Quaternion.Inverse(baseT.rotation) * capT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (capT != null) Save(capT, out capParent0, out capPos0, out capRot0);

            Transform holdBone = def.holdInOffHand ? leftHandBone : rightHandBone;
            capHandBoneRef = def.holdInOffHand ? rightHandBone : leftHandBone;

            baseHolder = NewHolder("EatBaseHolder", holdBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (capT != null)
            {
                capT.SetParent(baseT, false);
                capT.localPosition = capLocalPos;
                capT.localRotation = capLocalRot;
            }
            reparented = true;
            // (Draw/Open fire from the STATE_OPEN segment itself.)
            return true;
        }

        // Handheld (chocolate bar): bar (rootName) in the RIGHT hand; the wrapper group is reparented
        // UNDER the bar (pinned to its rest offset each frame in LateZeroProps) while its sn_cover
        // child still animates the peel. No food-piece toggling.
        private static bool SetupHandheldProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);                       // the bar (held item)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            coverT = string.IsNullOrEmpty(def.coverName) ? null : FindDeep(root, def.coverName);
            if (baseT == null)
                return BailToVanilla($"Handheld missing bar '{def.rootName}'");

            // Capture the wrapper's offset relative to the bar at the rest pose so it stays glued.
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            if (coverT != null) Save(coverT, out coverParent0, out coverPos0, out coverRot0);

            baseHolder = NewHolder("EatBarHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false);                 // ride the bar (carries sn_feces + sn_cover)
                wrapperT.localPosition = wrapperLocalPos;        // glue at the captured offset
                wrapperT.localRotation = wrapperLocalRot;
            }
            reparented = true;
            // Everything visible from the start; the wrapper (coverT) stays under wrapperT
            // and peels in place until the open gesture detaches it to the left hand.
            return true;
        }

        // Bag (croutons): bag root (rootName) in the RIGHT hand. The hold-crackers (names starting
        // with foodPieceName) ride the bag, hidden, until a SHAKE pours them into the left hand;
        // each is captured relative to the clump anchor (crackerT[0]) to keep its arrangement.
        private static bool SetupBagProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName); // the bag (held item, holds everything)
            if (baseT == null)
                return BailToVanilla($"Bag missing root '{def.rootName}'");

            var found = new System.Collections.Generic.List<Transform>();
            FindAllByPrefix(root, def.foodPieceName, found);
            int n = found.Count;
            if (n == 0)
                return BailToVanilla($"Bag found no crackers with prefix '{def.foodPieceName}'");
            crackerT = found.ToArray();
            crackerParent0 = new Transform[n]; crackerPos0 = new Vector3[n]; crackerRot0 = new Quaternion[n];
            crackerLocalPos = new Vector3[n]; crackerLocalRot = new Quaternion[n]; crackerR = new Renderer[n];
            Transform anchor = crackerT[0];
            for (int i = 0; i < n; i++)
            {
                Save(crackerT[i], out crackerParent0[i], out crackerPos0[i], out crackerRot0[i]);
                // clump layout relative to the anchor (so anchor lands on the holder, the
                // rest cluster around it regardless of where the holder grip is tuned).
                crackerLocalPos[i] = anchor.InverseTransformPoint(crackerT[i].position);
                crackerLocalRot[i] = Quaternion.Inverse(anchor.rotation) * crackerT[i].rotation;
                crackerR[i] = crackerT[i].GetComponentInChildren<Renderer>(true);
                SetRenderer(crackerR[i], false); // hidden until the shake pours them out
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            baseHolder = NewHolder("EatBagHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            reparented = true;
            return true;
        }

        // Pack (galette): pack (rootName) in the RIGHT hand; the wrapper group is glued to it
        // (carries the cover, which opens in place, + the food piece). The piece (foodPieceName)
        // is hidden until the LEFT hand takes it onto a left-hand holder. Mirror of CanHand.
        // ReachBag (iskra/MRE) shares this wiring verbatim (held root + hidden piece, no wrapper).
        private static bool SetupPackProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);                                            // the pack (held)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            foodT = FindDeep(root, def.foodPieceName);                                      // the piece to take
            if (baseT == null || foodT == null)
                return BailToVanilla($"{def.kind} missing prop (held={baseT != null} food={foodT != null})");
            foodR = foodT.GetComponentInChildren<Renderer>(true);

            // Glue the wrapper group to the pack at its rest offset (carries the cover + food).
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);

            baseHolder = NewHolder("EatPackHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false);
                wrapperT.localPosition = wrapperLocalPos;
                wrapperT.localRotation = wrapperLocalRot;
            }
            SetRenderer(foodR, false); // appears in the left hand on "take"
            reparented = true;
            return true;
        }

        private static GameObject NewHolder(string name, Transform parent, Vector3 pos, Vector3 rot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(rot);
            return go;
        }

        // LateUpdate (after the animator): the animator keeps writing each prop's local, so zero
        // it back onto its holder (the holder = the clean, tunable hold offset).
        public static void LateZeroProps()
        {
            if (!active || !reparented || def == null) return;
            // Re-pin each archetype's props onto their holders (the animator keeps rewriting the
            // props' locals every frame). One case per FoodKind — a new type adds its case here.
            switch (def.kind)
            {
                case FoodKind.Handheld:
                    ZeroLocal(baseT); // bar sits on its holder
                    // wrapper stays glued to the bar; cover zeroes onto the left-hand holder once detached.
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    if (coverT != null && coverDetached) ZeroLocal(coverT);
                    break;
                case FoodKind.Bag:
                    ZeroLocal(baseT); // bag sits on its holder (carries the in-bag crackers + corner)
                    // Once shaken out, the hold-crackers live under the left-hand holder; pin
                    // each to its captured clump layout so the animator can't drag them off.
                    if (crackersShown && crackerT != null)
                        for (int i = 0; i < crackerT.Length; i++)
                        {
                            if (crackerT[i] == null) continue;
                            crackerT[i].localPosition = crackerLocalPos[i];
                            crackerT[i].localRotation = crackerLocalRot[i];
                        }
                    break;
                case FoodKind.Pack:
                case FoodKind.ReachBag: // same wiring as Pack (held root + taken piece, no wrapper)
                    ZeroLocal(baseT); // pack sits on its holder
                    // wrapper group (cover + pile + food) stays glued to the pack.
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    // once taken, the food piece sits on the left-hand holder.
                    if (foodHolder != null && foodT != null && foodT.parent == foodHolder.transform) ZeroLocal(foodT);
                    break;
                case FoodKind.Drink:
                    ZeroLocal(baseT); // drink sits on its holder
                    if (capT != null)
                    {
                        if (capDetached) ZeroLocal(capT); // on its free-hand holder
                        else { capT.localPosition = capLocalPos; capT.localRotation = capLocalRot; } // glued on the container
                    }
                    break;
                default: // FoodKind.CannedFood
                    ZeroLocal(baseT);
                    ZeroLocal(spoonT);
                    ZeroLocal(foodT);
                    ZeroLocal(foodT2);
                    // glued sibling group (sugar's remaining pile) stays welded to the held box
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    break;
            }

            // LateUpdate-phase rig pins (after the IK solve), like HandsPositioner.LateUpdate —
            // without this mid-eat the rig drifts post-solve and the hands jitter while walking.
            if (driveBodyFollowDuringEat)
            {
                if (medsBody != null && VRGlobals.vrPlayer != null)
                    medsBody.rotation = VRGlobals.vrPlayer.handsRotation;
                PinRigToBody();
            }

            // Pull-to-open latch LAST: it needs the prop re-zeroing above already applied
            // (the grip is read through the holding wrist's rigid chain, which must have
            // baseT back on its holder) and the rig pins final (the latch's parent target
            // rides camRoot). Still pre-FinalIK, so the IK + pin aim at the result.
            DriveLatch();
            DriveReachLatch(); // the ReachBag rail (mutually exclusive with the pull latch)

            // Zone debug spheres last, off the final hand/head poses for this frame
            // (self-cleans when debugZones is toggled off mid-eat).
            UpdateZoneViz();
        }

        private static void ZeroLocal(Transform t)
        {
            if (t == null) return;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
        }

        private static void RestoreProps()
        {
            if (!reparented) return;
            try
            {
                // Props back to their original rig parents, then drop the holders. Restore
                // the wrapper group BEFORE the cover (the cover's original parent sn_root
                // lives under the wrapper group, so it must be back in place first).
                Restore(baseT, baseParent0, basePos0, baseRot0);
                Restore(spoonT, spoonParent0, spoonPos0, spoonRot0);
                Restore(foodT, foodParent0, foodPos0, foodRot0);
                Restore(foodT2, food2Parent0, food2Pos0, food2Rot0);         // sugar's 2nd cube (null-safe)
                Restore(wrapperT, wrapperParent0, wrapperPos0, wrapperRot0); // handheld/glued sibling (null-safe)
                Restore(coverT, coverParent0, coverPos0, coverRot0);         // handheld (null-safe)
                Restore(capT, capParent0, capPos0, capRot0);                     // drink (null-safe)
                if (crackerT != null) // bag crackers back to the bag, re-shown
                    for (int i = 0; i < crackerT.Length; i++)
                    {
                        Restore(crackerT[i], crackerParent0[i], crackerPos0[i], crackerRot0[i]);
                        SetRenderer(crackerR[i], true);
                    }
                SetRenderer(spoonR, true);
                SetRenderer(foodR, true);
                SetRenderer(foodR2, true);
                SetRenderer(capR, true);
                if (segR != null) // segmented snack: re-show whatever bites hid
                    for (int i = 0; i < segR.Length; i++) SetRenderer(segR[i], true);
                if (hideOnOpenR != null) // re-show the torn cover hidden on open (noodles)
                    for (int i = 0; i < hideOnOpenR.Length; i++) SetRenderer(hideOnOpenR[i], true);
                if (baseHolder != null) UnityEngine.Object.Destroy(baseHolder);
                if (spoonHolder != null) UnityEngine.Object.Destroy(spoonHolder);
                if (foodHolder != null) UnityEngine.Object.Destroy(foodHolder);
                if (foodHolder2 != null) UnityEngine.Object.Destroy(foodHolder2);
                if (crackerHolder != null) UnityEngine.Object.Destroy(crackerHolder);
                if (capHolder != null) UnityEngine.Object.Destroy(capHolder);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] RestoreProps error: {ex}"); }
            baseHolder = spoonHolder = foodHolder = foodHolder2 = crackerHolder = capHolder = null;
            reparented = false;
        }

        // Segmented snack (sausage): hide the held item's renderers progressively — stepped
        // at the audible bite sounds (called when a sound passes while held at the mouth)
        // but SYNCED to the consumed fraction, so the sausage is gone exactly when the
        // resource is, however many sounds fired. Hides from the tip inward
        // (segmentHideFromEnd flips the end). All re-shown on restore.
        private static void UpdateSnackSegments()
        {
            if (def == null || !def.segmentedBites || segR == null || segR.Length == 0) return;
            float progress = drinkRemainingFrac > 0f ? Mathf.Clamp01(drinkAppliedFrac / drinkRemainingFrac) : 0f;
            int want = Mathf.Min(Mathf.CeilToInt(progress * segR.Length), segR.Length);
            for (int i = 0; i < segR.Length; i++)
            {
                int idx = segmentHideFromEnd ? segR.Length - 1 - i : i;
                SetRenderer(segR[idx], i >= want); // the first `want` from the hide-end are gone
            }
        }

        // Hide every poured cracker (Bag onEatHide). Named (not a lambda) so the loop body in
        // the for-each stays readable.
        private static void HideCrackers()
        {
            if (crackerR != null)
                for (int i = 0; i < crackerR.Length; i++) SetRenderer(crackerR[i], false);
            crackersShown = false;
        }

        // Handheld: once peeled, move the WRAPPER (coverT, e.g. sn_cover) from the bar onto
        // a LEFT-hand holder so the player holds the peeled wrapper in their off hand (like
        // the spoon for a can). The bar (canT) AND the chocolate (sn_feces, still under
        // wrapperT) stay in the right hand. From here coverT's local is pinned to zero so it
        // sits on the (tunable) left-hand holder; its mesh keeps its own shape.
        private static void DetachWrapperToLeftHand()
        {
            if (coverT == null || coverDetached || leftHandBoneRef == null) return;
            try
            {
                foodHolder = NewHolder("EatWrapperHolder", leftHandBoneRef, def.foodPos, def.foodRot);
                coverT.SetParent(foodHolder.transform, false);
                coverDetached = true;
                Plugin.MyLog.LogInfo("[ManualEat] Wrapper (cover) detached to the left hand.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DetachWrapper error: {ex}"); }
        }

        // Drink: the open gesture pops the cap off the container onto a holder in the FREE
        // hand (the one that worked the cap — flips with holdInOffHand).
        private static void DetachCapToFreeHand()
        {
            // Hinged flip-top: never detaches — it stays welded to the bottle (LateZeroProps
            // keeps it glued while !capDetached) and animated open in place.
            if (def != null && def.capHinged) return;
            if (capT == null || capDetached || capHandBoneRef == null) return;
            try
            {
                if (capHolder == null) capHolder = NewHolder("EatCapHolder", capHandBoneRef, def.capPos, def.capRot);
                capT.SetParent(capHolder.transform, false);
                capDetached = true;
                Plugin.MyLog.LogInfo("[ManualEat] Cap popped to the free hand.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DetachCap error: {ex}"); }
        }

        // Drink: glue the cap back onto the container at its captured rest offset (the recap
        // that stops a partial drink — also makes the put-away animation look right).
        private static void ReattachCap()
        {
            if (capT == null || !capDetached) return;
            try
            {
                capT.SetParent(baseT, false);
                capT.localPosition = capLocalPos;
                capT.localRotation = capLocalRot;
                capDetached = false;
                Plugin.MyLog.LogInfo("[ManualEat] Cap back on.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] ReattachCap error: {ex}"); }
        }

        // Pour the hold-crackers into the LEFT hand: move them onto a left-hand holder in
        // their captured clump layout and show them. (Re-shows them for later rounds too.)
        private static void ShakeOutCrackers()
        {
            if (crackerT == null) return;
            if (crackerHolder == null && leftHandBoneRef != null)
                crackerHolder = NewHolder("EatCrackerHolder", leftHandBoneRef, def.foodPos, def.foodRot);
            for (int i = 0; i < crackerT.Length; i++)
            {
                if (crackerT[i] == null) continue;
                if (crackerHolder != null)
                {
                    crackerT[i].SetParent(crackerHolder.transform, false);
                    crackerT[i].localPosition = crackerLocalPos[i];
                    crackerT[i].localRotation = crackerLocalRot[i];
                }
                SetRenderer(crackerR[i], true);
            }
            crackersShown = true;
        }

        // Move the single food piece (foodT) onto a left-hand holder and show it (re-shows for
        // later picks too). Mirrors the bag/cracker move, but for one piece into the off hand.
        private static void TakeFoodToLeftHand()
        {
            if (foodT == null) return;
            if (foodHolder == null && leftHandBoneRef != null)
                foodHolder = NewHolder("EatPackFoodHolder", leftHandBoneRef, def.foodPos, def.foodRot);
            if (foodHolder != null) foodT.SetParent(foodHolder.transform, false);
            SetRenderer(foodR, true);
        }

        private static void SetRenderer(Renderer r, bool on) { if (r != null) r.enabled = on; }

        private static void Save(Transform t, out Transform parent, out Vector3 pos, out Quaternion rot)
        {
            parent = t.parent; pos = t.localPosition; rot = t.localRotation;
        }

        private static void Restore(Transform t, Transform parent, Vector3 pos, Quaternion rot)
        {
            if (t == null) return;
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localRotation = rot;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // Collect every transform whose name starts with 'prefix' (the bag's hold-crackers).
        private static void FindAllByPrefix(Transform root, string prefix, System.Collections.Generic.List<Transform> into)
        {
            if (root == null || string.IsNullOrEmpty(prefix)) return;
            if (root.name.StartsWith(prefix, StringComparison.Ordinal)) into.Add(root);
            for (int i = 0; i < root.childCount; i++) FindAllByPrefix(root.GetChild(i), prefix, into);
        }
    }
}
