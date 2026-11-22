using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Parts the held charging handle moves that Tarkov's held-bolt pose can't (data from gen_extras.py):
    //  - the bolt of a gun with no locked-back pose (Mk47, STM-9 have no *catch_on clip, so blending that pose in leaves the bolt shut).
    //    It moves back with the handle as far as the gun's own rack clip moves it.
    //  - a second handle on another part of the gun (the Spear's folding side handle, on its upper). The knob folds out as the hand
    //    takes it, the base slides back under the hand, and the slider it runs in follows once the base has come back sliderLag.
    //    The hand rides our own frame on the receiver, which none of those parts move.
    public static partial class VRReloadController
    {
        internal sealed class SideHandle
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
            public float gap = 0f;                          // m it rests behind where the rack starts; the handle travels that far before it moves the bolt
            public float room = float.MaxValue;             // m from where it rests to its full back
        }

        private static readonly Dictionary<string, SideHandle> sideHandles = new Dictionary<string, SideHandle>();
        private static readonly Dictionary<string, List<SavedBoltBone>> boltBonesByFamily = new Dictionary<string, List<SavedBoltBone>>();

        private static SideHandle heldSideHandle;           // null = the hand has the gun's main handle
        private static Transform sideSlider;
        private static Transform sideBase;
        private static Transform sideKnob;
        private static readonly List<MovedBolt> movedBolts = new List<MovedBolt>();

        // The centre of the knob in its folded position. Only a gun with that part fitted has one.
        private static bool TryGetSideKnobPoint(FirearmController firearmController, out Vector3 point)
        {
            point = Vector3.zero;
            SideHandle sideHandle = HandleParts.SideHandleOf(firearmController);
            Transform knob = sideHandle != null ? FindRigBone(firearmController, sideHandle.knobBone) : null;
            if (knob == null)
                return false;
            if (HandleParts.sideKnobMeshFor != knob)
            {
                HandleParts.sideKnobMeshFor = knob;
                HandleParts.sideKnobMesh = knob.GetComponentInChildren<MeshFilter>();
            }
            point = HandleParts.sideKnobMesh != null && HandleParts.sideKnobMesh.sharedMesh != null
                ? HandleParts.sideKnobMesh.transform.TransformPoint(HandleParts.sideKnobMesh.sharedMesh.bounds.center)
                : knob.position;
            return true;
        }

        private static bool FrameHandleOnSidePart(FirearmController firearmController, Vector3 backWorld)
        {
            heldSideHandle = HandleParts.SideHandleOf(firearmController);
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
            HandleParts.sideSliderRestLocalPosition = sideSlider.localPosition;
            HandleParts.sideBaseRestLocalPosition = sideBase.localPosition;
            HandleParts.sideKnobRestLocalRotation = sideKnob.localRotation;
            HandleParts.sideSliderAxis = sideSlider.parent.InverseTransformVector(backWorld);
            HandleParts.sideBaseAxis = sideSlider.InverseTransformVector(backWorld);
            HandleParts.sideKnobFold = 0f;
            return true;
        }

        private static void StartMovedBolts(FirearmController firearmController, Vector3 backWorld)
        {
            movedBolts.Clear();
            LoadRecordedGrips();
            if (!boltBonesByFamily.TryGetValue(AnimationFamily(firearmController), out List<SavedBoltBone> bones))
                return;
            foreach (SavedBoltBone saved in bones)
            {
                Transform bone = FindRigBone(firearmController, saved.bone);
                if (bone == null || bone.parent == null)
                    continue;
                MovedBolt moved = new MovedBolt { bone = bone, restLocalPosition = bone.localPosition, axis = bone.parent.InverseTransformVector(backWorld), ratio = saved.ratio };
                // An open bolt rests cocked (the Uzi's and AA-12's at full back). The handle only meets it after travelling that far,
                // and it stops at full back.
                if (saved.closed != null && saved.closed.Length >= 3 && saved.fullBack != null && saved.fullBack.Length >= 3 && moved.axis.sqrMagnitude > 1e-8f)
                {
                    Vector3 closed = new Vector3(saved.closed[0], saved.closed[1], saved.closed[2]);
                    Vector3 fullBack = new Vector3(saved.fullBack[0], saved.fullBack[1], saved.fullBack[2]);
                    moved.gap = Mathf.Max(0f, Vector3.Dot(moved.restLocalPosition - closed, moved.axis) / moved.axis.sqrMagnitude);
                    moved.room = Mathf.Max(0f, Vector3.Dot(fullBack - moved.restLocalPosition, moved.axis) / moved.axis.sqrMagnitude);
                }
                movedBolts.Add(moved);
            }
        }

        // Called in LateUpdate, after the animator has posed these parts this frame
        private static void ApplyHandleMovedParts()
        {
            if (heldSideHandle != null && sideSlider != null && sideBase != null && sideKnob != null)
            {
                float sliderPull = Mathf.Max(0f, handlePull - heldSideHandle.sliderLag);
                sideSlider.localPosition = HandleParts.sideSliderRestLocalPosition + HandleParts.sideSliderAxis * sliderPull;
                sideBase.localPosition = HandleParts.sideBaseRestLocalPosition + HandleParts.sideBaseAxis * (handlePull - sliderPull);
                HandleParts.sideKnobFold = Mathf.MoveTowards(HandleParts.sideKnobFold, 1f, Time.deltaTime / Mathf.Max(HandleParts.sideKnobFoldTime, 0.001f));
                sideKnob.localRotation = HandleParts.sideKnobRestLocalRotation * Quaternion.AngleAxis(heldSideHandle.foldAngle * HandleParts.sideKnobFold, heldSideHandle.foldAxis);
            }
            foreach (MovedBolt bolt in movedBolts)
            {
                if (bolt.bone != null)
                    bolt.bone.localPosition = bolt.restLocalPosition + bolt.axis * Mathf.Clamp(handlePull * bolt.ratio - bolt.gap, 0f, bolt.room);
            }
        }

        private static void EndHandleMovedParts()
        {
            if (heldSideHandle != null)
            {
                // Its knob folds away as the hand lets go (the Spear's HandleClose)
                if (handleForFirearm != null)
                    PlayFirstGunSound(handleForFirearm, SoundHand.GunBody, "HandleClose");
                if (sideSlider != null)
                    sideSlider.localPosition = HandleParts.sideSliderRestLocalPosition;
                if (sideBase != null)
                    sideBase.localPosition = HandleParts.sideBaseRestLocalPosition;
                if (sideKnob != null)
                    sideKnob.localRotation = HandleParts.sideKnobRestLocalRotation;
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
                SetZoneViz(ref HandleParts.sideHandleZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            bool inReach = OffHandTouchesCapsule(point, point, handleGrabRadius);
            SetZoneViz(ref HandleParts.sideHandleZoneViz, true, point, handleGrabRadius, inReach ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.4f, 0f, 0.3f));
        }

        public static class HandleParts
        {
            public static float sideKnobFoldTime = 0.1f;        // s the knob takes to fold out as the hand takes it
            internal static Vector3 sideSliderRestLocalPosition;
            internal static Vector3 sideBaseRestLocalPosition;
            internal static Quaternion sideKnobRestLocalRotation;
            internal static Vector3 sideSliderAxis;              // one metre back, in the slider's parent's frame
            internal static Vector3 sideBaseAxis;                // the same, in the slider's frame (the base's parent)
            internal static float sideKnobFold;                  // 0 = folded away, 1 = out

            internal static Transform sideKnobMeshFor;
            internal static MeshFilter sideKnobMesh;
            internal static GameObject sideHandleZoneViz;

            internal static SideHandle SideHandleOf(FirearmController firearmController)
            {
                if (firearmController == null)
                    return null;
                LoadRecordedGrips();
                sideHandles.TryGetValue(AnimationFamily(firearmController), out SideHandle sideHandle);
                return sideHandle;
            }

            // On a jammed gun, only while the hands may clear it, like the main handle
            internal static bool InSideHandleZone(FirearmController firearmController, Weapon weapon)
            {
                if (!HandsMayWorkAction(firearmController, weapon) || !TryGetSideKnobPoint(firearmController, out Vector3 point))
                    return false;
                return OffHandTouchesCapsule(point, point, handleGrabRadius);
            }
        }
    }
}
