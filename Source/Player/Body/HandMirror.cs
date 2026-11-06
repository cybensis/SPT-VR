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
            public Transform from, to;
            public Quaternion fromRest, toRest;   // in the mesh's bind space
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
