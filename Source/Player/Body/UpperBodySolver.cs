using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // An upper body that follows a tracked head: for the local VR player (LocalUpperBody) and for other players' copy of a VR player
    // (SPT-VR-FikaSync's HeadSyncApply; this file is linked into it, so both use one copy of the maths). Starting from this frame's
    // animated pose, the hips stay as animated or are squared to the body's facing when the caller asks. The chest is squared to the
    // body's facing and turns part of the way on toward the head's yaw, and the neck takes a share of the head's turn relative to the chest. The spine bends (CCD, a weight and a
    // limit per bone) to bring the head's pivot to the tracked head's, and the head turns to the tracked rotation. The face is the head
    // bone's rest-pose forward and up, not LootRaycastOrigin (that is the first-person camera point on the ribcage rig). The eyes sit
    // eyeForward / eyeUp off the head's pivot.
    internal static class UpperBodySolver
    {
        // m, the eyes' offset from the head bone's pivot (HeadTracking.neckLocalOffset is the same offset the other way)
        public static float eyeForward = 0.08f;
        public static float eyeUp = 0.075f;
        public static int iterations = 3;
        public static float[] spineWeights = { 0.3f, 0.35f, 0.4f, 0.45f };  // per spine bone from the hips up; extra bones use the last
        public static float spineMaxTurn = 20f;     // deg a spine bone may turn from its animated pose (twist and bend; 80 at most over 4 bones)
        public static float neckWeight = 0.3f;
        public static float neckMaxTurn = 30f;
        // The chest squared to the body's facing (Tarkov's rifle stance turns it ~35 deg side-on), then turned by twistShare of the
        // head's yaw off the body. false = twistShare of the head's yaw off the chest as animated (the stance's turn partly stays).
        public static bool squareChest = true;
        public static float twistShare = 0.6f;      // share of the head's yaw (relative to the body's facing, or the chest's) the chest turns
        public static float maxTwist = 60f;         // deg, for the whole spine (spineMaxTurn per bone caps it too)
        public static float neckLookShare = 0.35f;  // share of the head's turn (relative to the chest) that the neck takes
        // The hips (and the legs under them) are squared to the body's facing when the caller allows it (Solve's hipWeight, used while
        // standing still: when walking, the legs go the way you walk). Tarkov's rifle stance stands side-on to the look direction; the
        // body's facing follows the head.
        public static float hipShare = 0.25f;       // share of the head's yaw (relative to the body's facing) the hips turn on top; the chest takes the rest
        public static float maxHipTurn = 60f;       // deg from their animated pose

        internal sealed class Rig
        {
            public PlayerBones bones;
            public Transform root;
            public Transform pelvis;
            public Transform[] spine;               // from the hips up to the head's branch, without the pelvis
            public Transform chest;                 // the ribcage, which the arms hang from (on the spine or a branch off it)
            public Transform neck;                  // null if there is no bone between the spine and the head
            public Transform head;
            public Vector3 eyeInHead;               // the eyes' offset from the head bone, in its rotation frame (world scale)
            public Quaternion lookInHead;           // the look's rotation in the head's frame
            public Vector3 chestForwardLocal;       // the way the chest faces, in the ribcage's frame
            public Transform[] posed;               // every bone the solve turns: the spine, the neck, the head, the pelvis
            public Quaternion[] animated;
            public Quaternion[] written;
            public bool haveWritten;
            public bool valid;
            public string description;
            public float hipsOffBody;               // deg the animation had the hips off the body's facing, in the last solve that turned them
            public float hipsTurned;                // deg they were turned
            public float chestOffBody;              // deg the chest was off the body's facing before its twist (after the hips), last solve
        }

        public static Rig Build(PlayerBones bones, Transform root)
        {
            var rig = new Rig { bones = bones, root = root };
            Transform pelvis = bones != null && bones.Pelvis != null ? bones.Pelvis.Original : null;
            Transform ribcage = bones != null && bones.Ribcage != null ? bones.Ribcage.Original : null;
            Transform head = bones != null && bones.Head != null ? bones.Head.Original : null;
            if (root == null || pelvis == null || ribcage == null || head == null)
            {
                rig.description = "bones missing";
                return rig;
            }
            // Tarkov's skeleton forks at Spine3: Ribcage (collarbones, arms) on one side, Neck > Head on the other
            if (!head.IsChildOf(pelvis) || !ribcage.IsChildOf(pelvis))
            {
                rig.description = "the head or the ribcage isn't under the pelvis";
                return rig;
            }
            rig.neck = bones.Neck != null && bones.Neck != head && head.IsChildOf(bones.Neck) && bones.Neck.IsChildOf(pelvis) ? bones.Neck : null;
            var chain = new System.Collections.Generic.List<Transform>();
            for (Transform bone = (rig.neck != null ? rig.neck : head).parent; bone != null && bone != pelvis; bone = bone.parent)
                chain.Insert(0, bone);
            rig.spine = chain.ToArray();
            if (rig.spine.Length == 0 || !ribcage.IsChildOf(rig.spine[0]))
            {
                rig.description = "the ribcage isn't on the spine the head hangs from";
                return rig;
            }
            rig.chest = ribcage;
            rig.head = head;
            rig.pelvis = pelvis;

            // Use the bones' axes from Tarkov's rest pose, not the axis nearest to this frame's pose. The rifle stance turns the ribcage
            // ~40 deg off the body, where the nearest axis flips, so each peer could pick a different one.
            Vector3 faceAxis = HEAD_FACE, upAxis = HEAD_UP, chestAxis = RIBCAGE_FACE;
            float faceOff = Vector3.Angle(head.rotation * faceAxis, root.forward);
            float upOff = Vector3.Angle(head.rotation * upAxis, root.up);
            float chestOff = Vector3.Angle(ribcage.rotation * chestAxis, root.forward);
            rig.eyeInHead = faceAxis * eyeForward + upAxis * eyeUp;
            rig.lookInHead = Quaternion.LookRotation(faceAxis, upAxis);
            rig.chestForwardLocal = chestAxis;

            int count = rig.spine.Length + (rig.neck != null ? 1 : 0) + 2;
            rig.posed = new Transform[count];
            for (int i = 0; i < rig.spine.Length; i++)
                rig.posed[i] = rig.spine[i];
            if (rig.neck != null)
                rig.posed[rig.spine.Length] = rig.neck;
            rig.posed[count - 2] = head;
            rig.posed[count - 1] = pelvis;
            rig.animated = new Quaternion[count];
            rig.written = new Quaternion[count];
            rig.valid = rig.spine.Length > 0;

            var names = new System.Text.StringBuilder();
            for (int i = 0; i < count - 1; i++)
                names.Append(names.Length > 0 ? " > " : "").Append(rig.posed[i].name);
            rig.description = rig.valid
                ? $"{names} (chest {ribcage.name}); face = the head's {AxisName(faceAxis)} ({faceOff:F0} deg off the body's forward now), up {AxisName(upAxis)} ({upOff:F0} deg), chest = the ribcage's {AxisName(chestAxis)} ({chestOff:F0} deg)"
                : "no spine bones";
            return rig;
        }

        // The local axes of Base HumanHead, Base HumanRibcage and Base HumanPelvis that point along the body's forward and up in the rest
        // pose (read from playersuperior.bundle: exact, the pelvis within 0.2 deg)
        private static readonly Vector3 HEAD_FACE = Vector3.up;
        private static readonly Vector3 HEAD_UP = Vector3.left;
        private static readonly Vector3 RIBCAGE_FACE = Vector3.forward;
        private static readonly Vector3 PELVIS_FACE = Vector3.up;

        // The body's eyes in world space: offset from the head bone along its rest-pose face and up axes. BodyFollow keeps these under the headset.
        internal static Vector3 EyesOf(Transform head)
        {
            return head.position + head.rotation * (HEAD_FACE * eyeForward + HEAD_UP * eyeUp);
        }

        private static string AxisName(Vector3 axis)
        {
            if (axis.x != 0f)
                return axis.x > 0f ? "+x" : "-x";
            if (axis.y != 0f)
                return axis.y > 0f ? "+y" : "-y";
            return axis.z > 0f ? "+z" : "-z";
        }

        // eyePosition / eyeRotation: the tracked head (the headset) in world space. weight 0..1 (0 = the animator's pose, nothing written).
        // hipWeight 0..1: how much the hips turn too (0 = as animated).
        public static void Solve(Rig rig, Vector3 eyePosition, Quaternion eyeRotation, float weight, float hipWeight = 0f)
        {
            if (rig == null || !rig.valid)
                return;
            if (weight <= 0f || rig.head == null || rig.root == null)
            {
                rig.haveWritten = false;
                return;
            }
            RecoverAnimated(rig);
            Transform head = rig.head;
            Vector3 up = rig.root.up;
            int spineCount = rig.spine.Length;
            int neckIndex = rig.neck != null ? spineCount : -1;

            Vector3 lookFlat = Vector3.ProjectOnPlane(eyeRotation * Vector3.forward, up);
            // The hips go first, because everything above turns with them and the chest twist below is measured from there. They are
            // squared to the body's facing (which follows the head), then turned by hipShare of the head's yaw relative to it.
            Vector3 bodyFlat = Vector3.ProjectOnPlane(rig.root.forward, up);
            if (hipWeight > 0f && rig.pelvis != null && bodyFlat.sqrMagnitude > 1e-4f)
            {
                Vector3 hipsFlat = Vector3.ProjectOnPlane(rig.pelvis.rotation * PELVIS_FACE, up);
                if (hipsFlat.sqrMagnitude > 1e-4f)
                {
                    float hipsOffBody = Vector3.SignedAngle(hipsFlat, bodyFlat, up);
                    float lookOffBody = lookFlat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(bodyFlat, lookFlat, up) : 0f;
                    float hipYaw = Mathf.Clamp(hipsOffBody + lookOffBody * hipShare, -maxHipTurn, maxHipTurn) * weight * Mathf.Clamp01(hipWeight);
                    rig.pelvis.rotation = Quaternion.AngleAxis(hipYaw, up) * rig.pelvis.rotation;
                    rig.hipsOffBody = hipsOffBody;
                    rig.hipsTurned = hipYaw;
                }
            }

            // The chest squared to the body's facing and part of the way on toward the head's yaw (squareChest), or part of the way from
            // its animated facing
            Vector3 chestFlat = Vector3.ProjectOnPlane(rig.chest.rotation * rig.chestForwardLocal, up);
            bool squared = squareChest && bodyFlat.sqrMagnitude > 1e-4f;
            if ((squared || lookFlat.sqrMagnitude > 1e-4f) && chestFlat.sqrMagnitude > 1e-4f)
            {
                float yaw;
                if (squared)
                {
                    float lookOffBody = lookFlat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(bodyFlat, lookFlat, up) : 0f;
                    yaw = Vector3.SignedAngle(chestFlat, bodyFlat, up) + lookOffBody * twistShare;
                }
                else
                {
                    yaw = Vector3.SignedAngle(chestFlat, lookFlat, up) * twistShare;
                }
                rig.chestOffBody = bodyFlat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(chestFlat, bodyFlat, up) : 0f;
                float twist = Mathf.Clamp(yaw, -maxTwist, maxTwist) * weight / spineCount;
                Quaternion twistTurn = Quaternion.AngleAxis(twist, up);
                for (int i = 0; i < spineCount; i++)
                    rig.spine[i].rotation = twistTurn * rig.spine[i].rotation;
            }

            Quaternion headRotation = eyeRotation * Quaternion.Inverse(rig.lookInHead);
            Quaternion headTarget = Quaternion.Slerp(head.rotation, headRotation, weight);
            // The neck takes its share of the head's turn before the bend. This moves the head's pivot, and the bend then brings it to the target.
            if (rig.neck != null && neckLookShare > 0f)
            {
                Quaternion turn = headTarget * Quaternion.Inverse(head.rotation);
                rig.neck.rotation = Quaternion.Slerp(Quaternion.identity, turn, neckLookShare) * rig.neck.rotation;
                LimitTurn(rig.neck, rig.animated[neckIndex], neckMaxTurn);
            }

            // Target for the head's pivot: the tracked eyes minus the eye offset, rotated the way the head will be turned
            Vector3 pivotTarget = eyePosition - headTarget * rig.eyeInHead;
            Vector3 target = Vector3.Lerp(head.position, pivotTarget, weight);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                if (rig.neck != null)
                    Bend(rig.neck, head, target, neckWeight, rig.animated[neckIndex], neckMaxTurn);
                for (int i = spineCount - 1; i >= 0; i--)
                    Bend(rig.spine[i], head, target, SpineWeight(i), rig.animated[i], spineMaxTurn);
            }

            head.rotation = headTarget;

            for (int i = 0; i < rig.posed.Length; i++)
                rig.written[i] = rig.posed[i].localRotation;
            rig.haveWritten = true;
        }

        private static float SpineWeight(int index)
        {
            if (spineWeights == null || spineWeights.Length == 0)
                return 0.35f;
            return spineWeights[Mathf.Min(index, spineWeights.Length - 1)];
        }

        // One CCD step: the bone turns its share of the rotation that brings the head's pivot toward the target, within its limit
        private static void Bend(Transform bone, Transform effector, Vector3 target, float boneWeight, Quaternion animatedLocal, float maxTurn)
        {
            Vector3 toEffector = effector.position - bone.position;
            Vector3 toTarget = target - bone.position;
            if (toEffector.sqrMagnitude < 1e-6f || toTarget.sqrMagnitude < 1e-6f)
                return;
            Quaternion turn = Quaternion.FromToRotation(toEffector, toTarget);
            bone.rotation = Quaternion.Slerp(Quaternion.identity, turn, boneWeight) * bone.rotation;
            LimitTurn(bone, animatedLocal, maxTurn);
        }

        private static void LimitTurn(Transform bone, Quaternion animatedLocal, float maxTurn)
        {
            bone.localRotation = Quaternion.RotateTowards(animatedLocal, bone.localRotation, maxTurn);
        }

        // Start from this frame's animated pose. A bone still at the rotation the solve wrote last frame has not been animated since (the
        // animator is culled or paused): put it back to its animated pose from then, or the turns would add up frame after frame.
        private static void RecoverAnimated(Rig rig)
        {
            for (int i = 0; i < rig.posed.Length; i++)
            {
                Transform bone = rig.posed[i];
                if (rig.haveWritten && Mathf.Abs(Quaternion.Dot(bone.localRotation, rig.written[i])) > 0.9999999f)
                    bone.localRotation = rig.animated[i];
                rig.animated[i] = bone.localRotation;
            }
        }
    }
}
