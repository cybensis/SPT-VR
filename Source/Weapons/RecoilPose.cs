using EFT.Animations;
using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    // Tarkov puts all of a gun's recoil on HandsContainer.WeaponRootAnim (ProceduralWeaponAnimation.ApplyPosition / ApplyComplexRotation):
    // the kick is added to its position and the climb turns it about the gun's RecoilPivot. Two things of ours would remove it. The grip
    // anchor (GunInteractionController.UpdateWeaponPosition) pins the gun's grip marker, which hangs under that bone, to the hand: that
    // cancels the kick and turns the climb about the grip. And the airborne / no-run-animation zeroing of that bone (IKPatches) wipes it.
    // This class stores the recoil as a step from the bone's recoiled pose back to its recoil-free one, so the anchor can put the
    // recoil-free grip on the hand (the recoil shows on top) and the zeroing can put the recoil back.
    internal static class RecoilPose
    {
        public static float kickShare = 1f;     // share of Tarkov's recoil the gun shows against the hand: 1 = all of it, 0 = the grip pinned to the hand

        private static Transform stepRoot;
        private static Matrix4x4 stepToFree = Matrix4x4.identity;  // the bone's recoiled frame -> its recoil-free one, in the bone's own frame
        private static int stepFrame = -10;

        private static ProceduralWeaponAnimation OurAnimation => VRGlobals.player != null ? VRGlobals.player.ProceduralWeaponAnimation : null;

        // Called from a ProceduralWeaponAnimation.ApplyComplexRotation postfix, when the bone has its final pose for the frame, recoil included
        public static void Capture(ProceduralWeaponAnimation animation)
        {
            if (animation == null || animation != OurAnimation || animation.HandsContainer == null || animation.HandsContainer.WeaponRootAnim == null)
                return;
            // A mounted machine gun adds its kick its own way (ApplyStationaryWeaponPosition): skipped, so the anchor uses the bone as it is
            if (VRGlobals.player.MovementContext != null && VRGlobals.player.MovementContext.StationaryWeapon != null)
                return;
            Transform root = animation.HandsContainer.WeaponRootAnim;
            Read(animation, out Vector3 positionRecoil, out Vector3 rotationRecoil, out Vector3 pivotInRoot);
            Vector3 recoiledPosition = root.position;
            Quaternion recoiledRotation = root.rotation;
            Quaternion scopeRotation = animation._scopeRotation;
            // Undo the climb. Tarkov turned the pose about the pivot by Euler(pose * rotationRecoil) (DeferredRotate), where pose is the pose
            // before the turn; it is found here with one fixed-point step from the turned pose. The sight alignment applied after it
            // (ApplyAimingAlignment, a fraction of a degree while aiming) is left in.
            Quaternion turned = recoiledRotation * Quaternion.Inverse(scopeRotation);
            Quaternion turn = Quaternion.Euler(turned * rotationRecoil);
            Quaternion unturned = Quaternion.Inverse(turn) * turned;
            turn = Quaternion.Euler(unturned * rotationRecoil);
            unturned = Quaternion.Inverse(turn) * turned;
            Vector3 pivotArm = unturned * pivotInRoot;
            Vector3 unturnedPosition = recoiledPosition + turn * pivotArm - pivotArm;
            // Undo the kick (Tarkov adds it to the bone's local position before the turn)
            Vector3 freePosition = unturnedPosition - (root.parent != null ? root.parent.TransformVector(positionRecoil) : positionRecoil);
            Quaternion freeRotation = unturned * scopeRotation;
            StoreStep(root, recoiledPosition, recoiledRotation, freePosition, freeRotation);
        }

        // Called from IKPatches' airborne / no-run-animation path right after the bone is zeroed. Puts Tarkov's recoil back on it the way
        // ApplyPosition / ApplyComplexRotation do, and keeps the step back to the zeroed pose for the anchor.
        public static void PutBackOnZeroed(Transform root)
        {
            ProceduralWeaponAnimation animation = OurAnimation;
            if (root == null || animation == null || animation.HandsContainer == null || animation.HandsContainer.WeaponRootAnim != root)
                return;
            Vector3 freePosition = root.position;
            Quaternion freeRotation = root.rotation;
            Read(animation, out Vector3 positionRecoil, out Vector3 rotationRecoil, out Vector3 pivotInRoot);
            root.localPosition = positionRecoil;
            Vector3 kickedPosition = root.position;
            Quaternion kickedRotation = root.rotation;
            Vector3 pivot = kickedPosition + kickedRotation * pivotInRoot;
            Quaternion turn = Quaternion.Euler(kickedRotation * rotationRecoil);
            root.SetPositionAndRotation(pivot + turn * (kickedPosition - pivot), turn * kickedRotation);
            StoreStep(root, root.position, root.rotation, freePosition, freeRotation);
        }

        // Where the grip anchor lands the gun's grip marker: kickShare of the way from where it is to where it would be without the recoil
        public static Vector3 GripAnchor(Transform marker)
        {
            Transform root = stepRoot;
            if (kickShare <= 0f || marker == null || root == null || Time.frameCount - stepFrame > 2 || !marker.IsChildOf(root))
                return marker != null ? marker.position : Vector3.zero;
            Vector3 freeMarker = root.localToWorldMatrix.MultiplyPoint3x4(stepToFree.MultiplyPoint3x4(root.InverseTransformPoint(marker.position)));
            return Vector3.LerpUnclamped(marker.position, freeMarker, kickShare);
        }

        // A bone's frame without its recoil (its own frame when it isn't the one Tarkov puts the recoil on, or the step is stale)
        public static Matrix4x4 FreeFrame(Transform root)
        {
            if (root == null)
                return Matrix4x4.identity;
            if (root != stepRoot || Time.frameCount - stepFrame > 2)
                return root.localToWorldMatrix;
            return root.localToWorldMatrix * stepToFree;
        }

        // This frame's recoil as Tarkov applies it: the kick (bone-parent frame), the climb (Euler, taken through the pose) and its pivot (bone frame)
        private static void Read(ProceduralWeaponAnimation animation, out Vector3 positionRecoil, out Vector3 rotationRecoil, out Vector3 pivotInRoot)
        {
            var effect = animation.Shootingg?.CurrentRecoilEffect;
            positionRecoil = Vector3.zero;
            rotationRecoil = Vector3.zero;
            if (effect != null)
            {
                positionRecoil = effect.GetHandPositionRecoil();
                rotationRecoil = effect.GetHandRotationRecoil();
                if (effect.WeaponRecoilEffect != null)
                {
                    positionRecoil += effect.WeaponRecoilEffect.GetPositionRecoil();
                    rotationRecoil += effect.WeaponRecoilEffect.GetRotationRecoil();
                }
            }
            if (animation._compensatoryScale < 1f && animation.ShotNeedsFovAdjustments)
            {
                rotationRecoil.x = Mathf.Atan(Mathf.Tan(rotationRecoil.x * Mathf.Deg2Rad) * animation._compensatoryScale) * Mathf.Rad2Deg;
                rotationRecoil.z = Mathf.Atan(Mathf.Tan(rotationRecoil.z * Mathf.Deg2Rad) * animation._compensatoryScale) * Mathf.Rad2Deg;
            }
            pivotInRoot = !animation.IsMountedState ? animation.HandsContainer.RecoilPivot
                : (!animation.IsBipodUsed || animation.IsVerticalMounting) ? animation.HandsContainer.MountingRotationCenter
                : animation.HandsContainer.MountingRotationCenterBipods;
        }

        private static void StoreStep(Transform root, Vector3 recoiledPosition, Quaternion recoiledRotation, Vector3 freePosition, Quaternion freeRotation)
        {
            // In world space this maps a point on the recoiled bone to the same point on the recoil-free bone. It is then moved into the
            // bone's frame, which also keeps scale right (the mirrored gun).
            Matrix4x4 worldStep = Matrix4x4.Translate(freePosition) * Matrix4x4.Rotate(freeRotation * Quaternion.Inverse(recoiledRotation)) * Matrix4x4.Translate(-recoiledPosition);
            stepToFree = root.worldToLocalMatrix * worldStep * root.localToWorldMatrix;
            stepRoot = root;
            stepFrame = Time.frameCount;
        }
    }
}
