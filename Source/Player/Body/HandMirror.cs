using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // Mirrors a hand's finger pose onto the other hand's bones. Every grip, hold and finger pose in the mod is measured on one hand.
    // Copying the joint angles across doesn't work: the two hands' bones aren't set up as mirror images, only their rest poses are.
    // So each bone's rotation away from its rest pose (the skinned mesh's bind pose), relative to its palm, is reflected through the
    // body's centre plane and applied to the other hand's bone from that bone's own rest pose:
    //     to = toPalmRest^-1 * Mirror(fromPalmRest * (fromPalm^-1 * from) * fromRest^-1) * toRest      (rest rotations in mesh space)
    // The rest pose maps exactly onto the rest pose. The centre plane is read off the two palms' bind positions (logged once).
    public static class HandMirror
    {
        private const string RIGHT = "Base HumanR";
        private const string LEFT = "Base HumanL";

        private sealed class BonePair
        {
            public string digit;                  // "Digit11" .. "Digit53"
            public Transform from, to;
            public Quaternion fromRest, toRest;   // in the mesh's bind space
        }

        // Turns a right-hand finger pose (joint local rotations by digit, "Digit11".."Digit53"; each finger's first joint is relative to
        // the palm) into the left hand's joint locals for its mirror image. No bones are touched. Returns false until the hands mesh is found.
        public static bool TryMirrorRightToLeft(Dictionary<string, Quaternion> rightLocals, Dictionary<string, Quaternion> leftLocals)
        {
            leftLocals.Clear();
            if (VRGlobals.player == null || !EnsureBones())
                return false;
            Quaternion leftPalmRestInverse = Quaternion.Inverse(leftPalmRest);
            Quaternion rightInPalm = Quaternion.identity, leftInPalm = Quaternion.identity;
            foreach (BonePair pair in rightToLeft)
            {
                if (!rightLocals.TryGetValue(pair.digit, out Quaternion local))
                    return false;
                // Each finger restarts at its first joint (the pairs run Digit11, 12, 13, 21, ...)
                bool firstJoint = pair.digit[pair.digit.Length - 1] == '1';
                Quaternion leftParentInPalm = firstJoint ? Quaternion.identity : leftInPalm;
                rightInPalm = (firstJoint ? Quaternion.identity : rightInPalm) * local;
                Quaternion turn = rightPalmRest * rightInPalm * Quaternion.Inverse(pair.fromRest);
                leftInPalm = leftPalmRestInverse * Reflect(turn, centrePlaneNormal) * pair.toRest;
                leftLocals[pair.digit] = Quaternion.Inverse(leftParentInPalm) * leftInPalm;
            }
            return true;
        }

        private static SkinnedMeshRenderer handsMesh;
        private static Transform rightPalm, leftPalm;
        private static Quaternion rightPalmRest, leftPalmRest;
        private static BonePair[] rightToLeft;
        private static Vector3 centrePlaneNormal = Vector3.right;
        private static float nextFindTime;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The gun in the left hand (Handedness.GunInLeftHand, right-handed mode)

        private static readonly Matrix4x4 FlipX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
        private static Matrix4x4 palmRightToLeft, palmLeftToRight;

        // Turns a palm's world rotation into the other palm's rotation for its mirror image through a world plane (for example the gun's
        // centre plane). The frame is reflected through the plane, then turned into the other palm's bone frame by the rest poses:
        //     mirrored = Reflection(plane) * palm * FlipX * (FlipX * fromRest^-1 * Reflection(mesh centre plane) * toRest)
        // A rest pose mirrored through the body's own centre plane gives exactly the other palm's rest pose (the same rule as the fingers).
        public static bool TryMirrorPalm(Quaternion palmRotation, Vector3 planeNormal, bool rightToLeft, out Quaternion mirrored)
        {
            mirrored = palmRotation;
            if (VRGlobals.player == null || !EnsureBones())
                return false;
            Matrix4x4 result = Reflection(planeNormal) * Matrix4x4.Rotate(palmRotation) * FlipX * (rightToLeft ? palmRightToLeft : palmLeftToRight);
            mirrored = Quaternion.LookRotation(result.GetColumn(2), result.GetColumn(1));
            return true;
        }

        // Turns a palm frame that is already mirrored (a world matrix under a -1 scale, e.g. a grenade mirrored whole in the left hand)
        // into the other palm's rotation. This is TryMirrorPalm without the reflection step. It reads the matrix directly, so it never
        // goes through a Transform.rotation under that scale.
        public static bool TryOtherPalmFromMirroredFrame(Matrix4x4 frame, bool rightToLeft, out Quaternion palm)
        {
            palm = Quaternion.identity;
            if (VRGlobals.player == null || !EnsureBones())
                return false;
            Matrix4x4 unit = Matrix4x4.identity;
            for (int column = 0; column < 3; column++)
            {
                Vector3 axis = ((Vector3)frame.GetColumn(column)).normalized;
                unit.SetColumn(column, new Vector4(axis.x, axis.y, axis.z, 0f));
            }
            Matrix4x4 result = unit * FlipX * (rightToLeft ? palmRightToLeft : palmLeftToRight);
            palm = Quaternion.LookRotation(result.GetColumn(2), result.GetColumn(1));
            return true;
        }

        // Finger poses while the gun is in the left hand. EFT still animates the right hand gripping the gun and the left hand as the
        // support hand. So the left hand gets the right's animated grip, mirrored, and the right hand gets the left's animated foregrip
        // pose, mirrored, but only while it is on the foregrip (off it, it uses its own open pose: HandsInteractionController.UpdateRightHandFingers).
        // Each pose is read as EFT animated it. A joint that still holds its value from the end of last frame wasn't re-animated, so it
        // keeps its last animated value; otherwise our own output (or the open pose's) would feed back in.
        private static Quaternion[] rightAnimated, leftAnimated, rightFinal, leftFinal, mirroredOut;

        // Captures the gun grip. Called after trigger discipline and before the right hand's own finger update, which may put the open pose on.
        public static void CaptureGunHandGrip()
        {
            bool active = TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active;
            if (!(active || TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld) || VRGlobals.player == null || !EnsureBones())
            {
                rightAnimated = leftAnimated = rightFinal = leftFinal = null;
                return;
            }
            if (active)
                Capture(right: true, ref rightAnimated, rightFinal);
        }

        // True while the left's foregrip grip is read (as EFT animated it) before the left hand's own finger update. When the right hand
        // holds the gun alone (RealLeftHandedPatches.SupportHeld), the left hand is free and that update puts its open pose on.
        // It stays true after the left takes the gun back while the right stays on the foregrip: the left's open pose eases out over a few
        // frames, and reading after it would make the right hand open and close again. A fresh foregrip grab reads after the left's
        // update (that ease is the hand closing on the grip).
        private static bool supportGripEarly;

        public static void CaptureSupportGrip()
        {
            bool onForegrip = TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;
            supportGripEarly = TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld || (supportGripEarly && onForegrip);
            if (!supportGripEarly || VRGlobals.player == null || !EnsureBones())
                return;
            Capture(right: false, ref leftAnimated, leftFinal);
        }

        // Called at the end of HandsInteractionController.LateUpdate, together with RealLeftHandedPatches' arm swap.
        public static void ApplyRealLeftHanded()
        {
            if (TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld && !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active)
            {
                ApplySupportHeld();
                return;
            }
            if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active || VRGlobals.player == null || rightAnimated == null || !EnsureBones())
                return;
            int count = rightToLeft.Length;
            if (mirroredOut == null || mirroredOut.Length != count)
                mirroredOut = new Quaternion[count];
            bool supporting = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;
            if (supporting && !supportGripEarly)
                Capture(right: false, ref leftAnimated, leftFinal);

            MirrorLocals(rightAnimated, rightToLeft: true, mirroredOut);
            for (int index = 0; index < count; index++)
                rightToLeft[index].to.localRotation = mirroredOut[index];
            if (supporting && leftAnimated != null)
            {
                MirrorLocals(leftAnimated, rightToLeft: false, mirroredOut);
                for (int index = 0; index < count; index++)
                    rightToLeft[index].from.localRotation = mirroredOut[index];
            }
            Record(right: true, ref rightFinal);
            Record(right: false, ref leftFinal);
        }

        // Poses only the right hand: the left's foregrip grip (from CaptureSupportGrip) is mirrored onto it. The left keeps its own open pose.
        private static void ApplySupportHeld()
        {
            if (VRGlobals.player == null || leftAnimated == null || !EnsureBones())
                return;
            int count = rightToLeft.Length;
            if (mirroredOut == null || mirroredOut.Length != count)
                mirroredOut = new Quaternion[count];
            MirrorLocals(leftAnimated, rightToLeft: false, mirroredOut);
            for (int index = 0; index < count; index++)
                rightToLeft[index].from.localRotation = mirroredOut[index];
            Record(right: true, ref rightFinal);
            Record(right: false, ref leftFinal);
        }

        private static void Capture(bool right, ref Quaternion[] animated, Quaternion[] final)
        {
            int count = rightToLeft.Length;
            bool fresh = animated == null || animated.Length != count;
            if (fresh)
                animated = new Quaternion[count];
            for (int index = 0; index < count; index++)
            {
                Quaternion current = right ? rightToLeft[index].from.localRotation : rightToLeft[index].to.localRotation;
                if (!fresh && final != null && final.Length == count && Quaternion.Angle(current, final[index]) < 0.05f)
                    continue;
                animated[index] = current;
            }
        }

        // A press check (VRReloadController.PressCheck) with the gun in the left hand. The press-check fingers are worked out for the right
        // hand, which holds the gun as far as EFT knows, and the left hand shows their mirror image. They are given per right-hand bone;
        // a bone that isn't given keeps the captured grip. Runs after ApplyRealLeftHanded, which has already put on the grip's mirror.
        private static Quaternion[] mirrorPressFrom, mirrorPressOut;

        public static void ApplyMirroredRight(Dictionary<Transform, Quaternion> rightLocals)
        {
            if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active || VRGlobals.player == null || rightAnimated == null || rightLocals == null || !EnsureBones())
                return;
            int count = rightToLeft.Length;
            if (mirrorPressFrom == null || mirrorPressFrom.Length != count)
            {
                mirrorPressFrom = new Quaternion[count];
                mirrorPressOut = new Quaternion[count];
            }
            for (int index = 0; index < count; index++)
                mirrorPressFrom[index] = rightLocals.TryGetValue(rightToLeft[index].from, out Quaternion local) ? local
                    : index < rightAnimated.Length ? rightAnimated[index] : rightToLeft[index].from.localRotation;
            MirrorLocals(mirrorPressFrom, rightToLeft: true, mirrorPressOut);
            for (int index = 0; index < count; index++)
                rightToLeft[index].to.localRotation = mirrorPressOut[index];
            Record(right: false, ref leftFinal);
        }

        private static void Record(bool right, ref Quaternion[] final)
        {
            int count = rightToLeft.Length;
            if (final == null || final.Length != count)
                final = new Quaternion[count];
            for (int index = 0; index < count; index++)
                final[index] = right ? rightToLeft[index].from.localRotation : rightToLeft[index].to.localRotation;
        }

        // Turns one hand's finger joint locals into the other hand's for its mirror image (the pairs run finger by finger; joint 1 is
        // relative to the palm).
        private static void MirrorLocals(Quaternion[] fromLocals, bool rightToLeft, Quaternion[] toLocals)
        {
            Quaternion fromPalmRest = rightToLeft ? rightPalmRest : leftPalmRest;
            Quaternion toPalmRestInverse = Quaternion.Inverse(rightToLeft ? leftPalmRest : rightPalmRest);
            Quaternion fromInPalm = Quaternion.identity, toInPalm = Quaternion.identity;
            for (int index = 0; index < HandMirror.rightToLeft.Length; index++)
            {
                BonePair pair = HandMirror.rightToLeft[index];
                bool firstJoint = pair.digit[pair.digit.Length - 1] == '1';
                Quaternion toParentInPalm = firstJoint ? Quaternion.identity : toInPalm;
                fromInPalm = (firstJoint ? Quaternion.identity : fromInPalm) * fromLocals[index];
                Quaternion turn = fromPalmRest * fromInPalm * Quaternion.Inverse(rightToLeft ? pair.fromRest : pair.toRest);
                toInPalm = toPalmRestInverse * Reflect(turn, centrePlaneNormal) * (rightToLeft ? pair.toRest : pair.fromRest);
                toLocals[index] = Quaternion.Inverse(toParentInPalm) * toInPalm;
            }
        }

        // Mirrors a finger pose given as joint locals in the reload code's order: index 0 is the palm (left alone), then three joints per
        // finger, thumb first (DigitFJ at (F - 1) * 3 + J). With the gun in the left hand, the right hand holds the magazine, and the
        // magazine finger poses were all recorded on the left hand (VRReloadController).
        private static Quaternion[] poseFrom, poseTo;

        public static bool TryMirrorPose(Quaternion[] fromPose, bool rightToLeft, Quaternion[] toPose)
        {
            if (VRGlobals.player == null || fromPose == null || toPose == null || !EnsureBones())
                return false;
            int count = HandMirror.rightToLeft.Length;
            if (poseFrom == null || poseFrom.Length != count)
            {
                poseFrom = new Quaternion[count];
                poseTo = new Quaternion[count];
            }
            for (int index = 0; index < count; index++)
            {
                int poseIndex = PoseIndex(HandMirror.rightToLeft[index].digit);
                if (poseIndex >= fromPose.Length || poseIndex >= toPose.Length)
                    return false;
                poseFrom[index] = fromPose[poseIndex];
            }
            MirrorLocals(poseFrom, rightToLeft, poseTo);
            for (int index = 0; index < count; index++)
                toPose[PoseIndex(HandMirror.rightToLeft[index].digit)] = poseTo[index];
            return true;
        }

        // "Digit23" -> 2nd finger, 3rd joint -> 6
        private static int PoseIndex(string digit)
        {
            return (digit[5] - '1') * 3 + (digit[6] - '0');
        }

        // Reflection through the plane with this normal (through the origin)
        private static Matrix4x4 Reflection(Vector3 normal)
        {
            normal.Normalize();
            Matrix4x4 reflection = Matrix4x4.identity;
            reflection.m00 = 1f - 2f * normal.x * normal.x; reflection.m01 = -2f * normal.x * normal.y; reflection.m02 = -2f * normal.x * normal.z;
            reflection.m10 = -2f * normal.y * normal.x; reflection.m11 = 1f - 2f * normal.y * normal.y; reflection.m12 = -2f * normal.y * normal.z;
            reflection.m20 = -2f * normal.z * normal.x; reflection.m21 = -2f * normal.z * normal.y; reflection.m22 = 1f - 2f * normal.z * normal.z;
            return reflection;
        }

        // A rotation seen in a mirror (reflection through the plane with this normal): same angle, axis reflected, turning the other way
        private static Quaternion Reflect(Quaternion rotation, Vector3 normal)
        {
            Vector3 axisPart = new Vector3(rotation.x, rotation.y, rotation.z);
            Vector3 reflected = axisPart - 2f * Vector3.Dot(axisPart, normal) * normal;
            return new Quaternion(-reflected.x, -reflected.y, -reflected.z, rotation.w);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The skinned mesh that carries both hands (palm + the 15 finger joints each) and their bind poses
        private static bool EnsureBones()
        {
            if (handsMesh != null && rightPalm != null && leftPalm != null && rightToLeft != null)
                return true;
            if (Time.unscaledTime < nextFindTime)
                return false;
            nextFindTime = Time.unscaledTime + 2f;
            handsMesh = null;
            rightToLeft = null;
            foreach (SkinnedMeshRenderer mesh in VRGlobals.player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (mesh.sharedMesh == null || mesh.bones == null)
                    continue;
                Dictionary<string, int> byName = new Dictionary<string, int>();
                for (int index = 0; index < mesh.bones.Length; index++)
                {
                    if (mesh.bones[index] != null && !byName.ContainsKey(mesh.bones[index].name))
                        byName[mesh.bones[index].name] = index;
                }
                if (!byName.ContainsKey(RIGHT + "Palm") || !byName.ContainsKey(LEFT + "Palm"))
                    continue;
                List<BonePair> pairs = new List<BonePair>();
                Matrix4x4[] bindPoses = mesh.sharedMesh.bindposes;
                for (int finger = 1; finger <= 5; finger++)
                {
                    for (int joint = 1; joint <= 3; joint++)
                    {
                        string digit = "Digit" + finger + joint;
                        if (!byName.TryGetValue(RIGHT + digit, out int rightIndex) || !byName.TryGetValue(LEFT + digit, out int leftIndex))
                            continue;
                        pairs.Add(new BonePair
                        {
                            digit = digit,
                            from = mesh.bones[rightIndex],
                            to = mesh.bones[leftIndex],
                            fromRest = RestRotation(bindPoses[rightIndex]),
                            toRest = RestRotation(bindPoses[leftIndex]),
                        });
                    }
                }
                if (pairs.Count == 0)
                    continue;
                int rightPalmIndex = byName[RIGHT + "Palm"];
                int leftPalmIndex = byName[LEFT + "Palm"];
                handsMesh = mesh;
                rightPalm = mesh.bones[rightPalmIndex];
                leftPalm = mesh.bones[leftPalmIndex];
                rightPalmRest = RestRotation(bindPoses[rightPalmIndex]);
                leftPalmRest = RestRotation(bindPoses[leftPalmIndex]);
                rightToLeft = pairs.ToArray();
                ReadCentrePlane(bindPoses[rightPalmIndex].inverse.GetColumn(3), bindPoses[leftPalmIndex].inverse.GetColumn(3));
                Matrix4x4 rightRest = Matrix4x4.Rotate(rightPalmRest), leftRest = Matrix4x4.Rotate(leftPalmRest), meshMirror = Reflection(centrePlaneNormal);
                palmRightToLeft = FlipX * rightRest.inverse * meshMirror * leftRest;
                palmLeftToRight = FlipX * leftRest.inverse * meshMirror * rightRest;
                Plugin.MyLog.LogInfo($"[HandMirror] hands on '{mesh.name}': {pairs.Count} finger joint pairs, centre plane normal {centrePlaneNormal} (mesh space)");
                return true;
            }
            if (!warnedNoMesh)
            {
                warnedNoMesh = true;
                Plugin.MyLog.LogWarning("[HandMirror] no skinned mesh on the player carries both palms: nothing to mirror");
            }
            return false;
        }

        private static bool warnedNoMesh;

        // A bone's rotation at bind time, in the mesh's space (the bind pose is that bone's world-to-local at bind)
        private static Quaternion RestRotation(Matrix4x4 bindPose)
        {
            Matrix4x4 rest = bindPose.inverse;
            return Quaternion.LookRotation(rest.GetColumn(2), rest.GetColumn(1));
        }

        // The palms sit mirrored across the body's centre plane, so its normal is the axis they are furthest apart on. The log shows how
        // far they are from a true mirror image (should be about 0).
        private static void ReadCentrePlane(Vector3 rightPalmPosition, Vector3 leftPalmPosition)
        {
            Vector3 apart = rightPalmPosition - leftPalmPosition;
            Vector3 size = new Vector3(Mathf.Abs(apart.x), Mathf.Abs(apart.y), Mathf.Abs(apart.z));
            centrePlaneNormal = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            Vector3 mirroredRight = rightPalmPosition - 2f * Vector3.Dot(rightPalmPosition, centrePlaneNormal) * centrePlaneNormal;
            Plugin.MyLog.LogInfo($"[HandMirror] bind palms R {rightPalmPosition} L {leftPalmPosition}: {Vector3.Distance(mirroredRight, leftPalmPosition) * 100f:0.0} cm off a mirror image");
        }
    }
}
