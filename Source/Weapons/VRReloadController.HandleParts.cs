using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Parts the held handle moves that Tarkov's held bolt pose can't (data from gen_extras.py):
    //  - the bolt of a gun with no locked-back pose (Mk47, STM-9: no *catch_on clip, so blending that pose in leaves the bolt shut),
    //    moved back with the handle as far as the gun's own rack clip moves it
    //  - a second handle on a part of the gun (the Spear's folding side handle, on its upper): the knob folds out as the hand takes it,
    //    the base slides back under the hand, and the slider it runs in follows once the base has come sliderLag back. The hand rides
    //    our own frame on the receiver, which none of those parts moves.
    public static partial class VRReloadController
    {
        public static bool sideChargingHandles = true;
        public static float sideKnobFoldTime = 0.1f;        // s the knob takes to fold out as the hand takes it

        private sealed class SideHandle
        {
            public HandleHold hold;                         // the palm in the base's frame
            public string sliderBone;
            public string baseBone;
            public string knobBone;
            public float sliderLag;                         // m the base comes back before the slider moves with it
            public Vector3 foldAxis;                        // in the knob's frame
            public float foldAngle;
        }

        private sealed class MovedBolt
        {
            public Transform bone;
            public Vector3 restLocalPosition;
            public Vector3 axis;                            // one metre back, in the bone's parent's frame
            public float ratio;                             // m of bolt per m of handle
        }

        private static readonly Dictionary<string, SideHandle> sideHandles = new Dictionary<string, SideHandle>();
        private static readonly Dictionary<string, List<SavedBoltBone>> boltBonesByFamily = new Dictionary<string, List<SavedBoltBone>>();

        private static SideHandle heldSideHandle;           // null = the hand has the gun's main handle
        private static Transform sideSlider;
        private static Transform sideBase;
        private static Transform sideKnob;
        private static Vector3 sideSliderRestLocalPosition;
        private static Vector3 sideBaseRestLocalPosition;
        private static Quaternion sideKnobRestLocalRotation;
        private static Vector3 sideSliderAxis;              // one metre back, in the slider's parent's frame
        private static Vector3 sideBaseAxis;                // and in the slider's, which the base sits in
        private static float sideKnobFold;                  // 0 = folded away, 1 = out
        private static readonly List<MovedBolt> movedBolts = new List<MovedBolt>();

        private static Transform sideKnobMeshFor;
        private static MeshFilter sideKnobMesh;
        private static GameObject sideHandleZoneViz;

        private static SideHandle SideHandleOf(FirearmController firearmController)
        {
            if (!sideChargingHandles || firearmController == null)
                return null;
            LoadRecordedGrips();
            sideHandles.TryGetValue(AnimationFamily(firearmController), out SideHandle sideHandle);
            return sideHandle;
        }

        // The middle of the knob, folded away: only a gun with that part fitted has one
        private static bool TryGetSideKnobPoint(FirearmController firearmController, out Vector3 point)
        {
            point = Vector3.zero;
            SideHandle sideHandle = SideHandleOf(firearmController);
            Transform knob = sideHandle != null ? FindRigBone(firearmController, sideHandle.knobBone) : null;
            if (knob == null)
                return false;
            if (sideKnobMeshFor != knob)
            {
                sideKnobMeshFor = knob;
                sideKnobMesh = knob.GetComponentInChildren<MeshFilter>();
            }
            point = sideKnobMesh != null && sideKnobMesh.sharedMesh != null
                ? sideKnobMesh.transform.TransformPoint(sideKnobMesh.sharedMesh.bounds.center)
                : knob.position;
            return true;
        }

        // Not on a jammed gun, like the main handle: clearing one is still the interaction menu's
        private static bool InSideHandleZone(FirearmController firearmController, Weapon weapon)
        {
            if (!chargingHandle || weapon.MalfState.State != Weapon.EMalfunctionState.None || !TryGetSideKnobPoint(firearmController, out Vector3 point))
                return false;
            return OffHandTouchesCapsule(point, point, handleGrabRadius);
        }

        private static bool FrameHandleOnSidePart(FirearmController firearmController, Vector3 backWorld)
        {
            heldSideHandle = SideHandleOf(firearmController);
            sideSlider = heldSideHandle != null ? FindRigBone(firearmController, heldSideHandle.sliderBone) : null;
            sideBase = heldSideHandle != null ? FindRigBone(firearmController, heldSideHandle.baseBone) : null;
            sideKnob = heldSideHandle != null ? FindRigBone(firearmController, heldSideHandle.knobBone) : null;
            if (sideSlider == null || sideBase == null || sideKnob == null || sideSlider.parent == null)
            {
                heldSideHandle = null;
                return false;
            }
            EnsureHandleFrame();
            handleFrame.SetParent(sideSlider.parent, worldPositionStays: false);
            handleFrame.SetPositionAndRotation(sideBase.position, sideBase.rotation);
            handleRider.localPosition = Vector3.zero;
            handleRider.localRotation = Quaternion.identity;
            handleBone = handleFrame;
            handleView = handleRider;
            sideSliderRestLocalPosition = sideSlider.localPosition;
            sideBaseRestLocalPosition = sideBase.localPosition;
            sideKnobRestLocalRotation = sideKnob.localRotation;
            sideSliderAxis = sideSlider.parent.InverseTransformVector(backWorld);
            sideBaseAxis = sideSlider.InverseTransformVector(backWorld);
            sideKnobFold = 0f;
            return true;
        }

        private static void StartMovedBolts(FirearmController firearmController, Vector3 backWorld)
        {
            movedBolts.Clear();
            if (!boltFollowsHandle)
                return;
            LoadRecordedGrips();
            if (!boltBonesByFamily.TryGetValue(AnimationFamily(firearmController), out List<SavedBoltBone> bones))
                return;
            foreach (SavedBoltBone saved in bones)
            {
                Transform bone = FindRigBone(firearmController, saved.bone);
                if (bone == null || bone.parent == null)
                    continue;
                movedBolts.Add(new MovedBolt { bone = bone, restLocalPosition = bone.localPosition, axis = bone.parent.InverseTransformVector(backWorld), ratio = saved.ratio });
            }
        }

        // LateUpdate, after the animator has posed them this frame
        private static void ApplyHandleMovedParts()
        {
            if (heldSideHandle != null && sideSlider != null && sideBase != null && sideKnob != null)
            {
                float sliderPull = Mathf.Max(0f, handlePull - heldSideHandle.sliderLag);
                sideSlider.localPosition = sideSliderRestLocalPosition + sideSliderAxis * sliderPull;
                sideBase.localPosition = sideBaseRestLocalPosition + sideBaseAxis * (handlePull - sliderPull);
                sideKnobFold = Mathf.MoveTowards(sideKnobFold, 1f, Time.deltaTime / Mathf.Max(sideKnobFoldTime, 0.001f));
                sideKnob.localRotation = sideKnobRestLocalRotation * Quaternion.AngleAxis(heldSideHandle.foldAngle * sideKnobFold, heldSideHandle.foldAxis);
            }
            foreach (MovedBolt bolt in movedBolts)
            {
                if (bolt.bone != null)
                    bolt.bone.localPosition = bolt.restLocalPosition + bolt.axis * (handlePull * bolt.ratio);
            }
        }

        private static void EndHandleMovedParts()
        {
            if (heldSideHandle != null)
            {
                if (sideSlider != null)
                    sideSlider.localPosition = sideSliderRestLocalPosition;
                if (sideBase != null)
                    sideBase.localPosition = sideBaseRestLocalPosition;
                if (sideKnob != null)
                    sideKnob.localRotation = sideKnobRestLocalRotation;
            }
            heldSideHandle = null;
            sideSlider = null;
            sideBase = null;
            sideKnob = null;
            foreach (MovedBolt bolt in movedBolts)
            {
                if (bolt.bone != null)
                    bolt.bone.localPosition = bolt.restLocalPosition;
            }
            movedBolts.Clear();
        }

        private static void UpdateSideHandleViz(FirearmController firearmController, Weapon weapon, bool show)
        {
            if (!show || HoldingChargingHandle || !TryGetSideKnobPoint(firearmController, out Vector3 point))
            {
                SetZoneViz(ref sideHandleZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            bool inReach = OffHandTouchesCapsule(point, point, handleGrabRadius);
            SetZoneViz(ref sideHandleZoneViz, true, point, handleGrabRadius, inReach ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.4f, 0f, 0.3f));
        }
    }
}
