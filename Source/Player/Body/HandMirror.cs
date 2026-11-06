using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // A hand's finger pose mirrored onto the other hand's bones - the piece a real left-handed mode needs (every grip, hold and finger
    // pose in the mod is measured on one hand). Copying the joint angles across can't work (headset dead end, the open-hand bake): the
    // two hands' bones aren't set up as mirror images, only their REST poses are. So each bone's turn away from its rest pose (the
    // skinned mesh's bind pose), relative to its palm, is reflected through the body's centre plane and applied to the other hand's
    // bone from that bone's own rest pose:
    //     to = toPalmRest^-1 * Mirror(fromPalmRest * (fromPalm^-1 * from) * fromRest^-1) * toRest      (rest rotations in mesh space)
    // The rest pose maps exactly onto the rest pose; the centre plane is read off the two palms' bind positions (logged once).
    //
    // Stage 2 test (live, UnityExplorer): mirrorTest = true, hold a gun right-handed, raise the free off hand - it should show a
    // natural left-hand version of the gun hand's grip (fingers curled the right way, thumb on the correct side).
    public static class HandMirror
    {
        public static bool mirrorTest = false;

        private const string RIGHT = "Base HumanR";
        private const string LEFT = "Base HumanL";

        private sealed class BonePair
        {
            public string digit;                  // "Digit11" .. "Digit53"
            public Transform from, to;
            public Quaternion fromRest, toRest;   // in the mesh's bind space
        }

        // A right-hand finger pose given as joint locals (by digit, "Digit11".."Digit53"; each finger's first joint is relative to the
        // palm) -> the left hand's joint locals for its mirror image. No bones are touched. False until the hands mesh is found.
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

        // End of HandsInteractionController.LateUpdate: both hands' fingers are final for this frame
        public static void ApplyTest()
        {
            if (!mirrorTest || VRGlobals.player == null || VRGlobals.firearmController == null || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.isSupporting)
                return;
            if (!EnsureBones())
                return;
            Mirror(rightPalm, rightPalmRest, leftPalm, leftPalmRest, rightToLeft);
        }

        // Each pair's 'to' bone, parents first (palm order), so a child's parent already carries its new pose
        private static void Mirror(Transform fromPalm, Quaternion fromPalmRest, Transform toPalm, Quaternion toPalmRest, BonePair[] pairs)
        {
            Quaternion fromPalmInverse = Quaternion.Inverse(fromPalm.rotation);
            Quaternion toPalmRestInverse = Quaternion.Inverse(toPalmRest);
            foreach (BonePair pair in pairs)
            {
                Quaternion turn = fromPalmRest * (fromPalmInverse * pair.from.rotation) * Quaternion.Inverse(pair.fromRest);
                Quaternion inToPalm = toPalmRestInverse * Reflect(turn, centrePlaneNormal) * pair.toRest;
                pair.to.rotation = toPalm.rotation * inToPalm;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The gun in the left hand (Handedness.GunInLeftHand, right-handed mode)

        private static readonly Matrix4x4 FlipX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
        private static Matrix4x4 palmRightToLeft, palmLeftToRight;
        private static Quaternion[] swapLeft, swapRight;

        // A palm's world rotation -> the OTHER palm's for its mirror image through a world plane (e.g. the gun's centre plane). The
        // frame reflected through the plane, then turned into the other palm's bone frame by the rest poses:
        //     mirrored = Reflection(plane) * palm * FlipX * (FlipX * fromRest^-1 * Reflection(mesh centre plane) * toRest)
        // At rest, mirrored through the body's own centre plane, that is exactly the other palm's rest (same rule as the fingers).
        public static bool TryMirrorPalm(Quaternion palmRotation, Vector3 planeNormal, bool rightToLeft, out Quaternion mirrored)
        {
            mirrored = palmRotation;
            if (VRGlobals.player == null || !EnsureBones())
                return false;
            Matrix4x4 result = Reflection(planeNormal) * Matrix4x4.Rotate(palmRotation) * FlipX * (rightToLeft ? palmRightToLeft : palmLeftToRight);
            mirrored = Quaternion.LookRotation(result.GetColumn(2), result.GetColumn(1));
            return true;
        }

        // End of HandsInteractionController.LateUpdate, with RealLeftHandedPatches' arm swap: the gun's grip pose (animated on the right
        // bones) onto the left hand now on the grip, the support pose onto the right
        public static void ApplyRealLeftHanded()
        {
            if (TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active && VRGlobals.player != null)
                SwapFingers();
        }

        // Both hands' fingers at once, each the mirror image of the other's (both read before either is written): the gun hand's grip
        // shows on the hand now holding the gun, the support hand's pose on the other
        public static void SwapFingers()
        {
            if (VRGlobals.player == null || !EnsureBones())
                return;
            int count = rightToLeft.Length;
            if (swapLeft == null || swapLeft.Length != count)
            {
                swapLeft = new Quaternion[count];
                swapRight = new Quaternion[count];
            }
            Quaternion rightPalmInverse = Quaternion.Inverse(rightPalm.rotation), leftPalmInverse = Quaternion.Inverse(leftPalm.rotation);
            Quaternion rightPalmRestInverse = Quaternion.Inverse(rightPalmRest), leftPalmRestInverse = Quaternion.Inverse(leftPalmRest);
            for (int index = 0; index < count; index++)
            {
                BonePair pair = rightToLeft[index];
                Quaternion rightTurn = rightPalmRest * (rightPalmInverse * pair.from.rotation) * Quaternion.Inverse(pair.fromRest);
                Quaternion leftTurn = leftPalmRest * (leftPalmInverse * pair.to.rotation) * Quaternion.Inverse(pair.toRest);
                swapLeft[index] = leftPalmRestInverse * Reflect(rightTurn, centrePlaneNormal) * pair.toRest;
                swapRight[index] = rightPalmRestInverse * Reflect(leftTurn, centrePlaneNormal) * pair.fromRest;
            }
            for (int index = 0; index < count; index++)
            {
                rightToLeft[index].to.rotation = leftPalm.rotation * swapLeft[index];
                rightToLeft[index].from.rotation = rightPalm.rotation * swapRight[index];
            }
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

        // The palms sit mirrored across the body's centre plane: its normal is the axis they're furthest apart on. How far off a true
        // mirror image they are (should be ~0) goes in the log.
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
