using EFT;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // An upper body that follows a tracked head: the local VR player's (LocalUpperBody) and the other players' copy of a VR player
    // (SPT-VR-FikaSync's HeadSyncApply - this file is linked into it, one copy of the maths for both). On the pose the animator gave this
    // frame, the hips where they are (or squared to the body's facing, where the caller says): the chest turns part of the way to the head's yaw, the neck takes a share of the head's turn off the
    // chest, the spine bends (CCD, a weight and a limit per bone) so the head's pivot comes to where the tracked head's is, and the head
    // turns to the tracked head. The face is the head bone's rest-pose forward and up (not LootRaycastOrigin: that's the first-person
    // camera point on the ribcage rig), the eyes eyeForward / eyeUp off its pivot.
    internal static class UpperBodySolver
    {
        // The eyes off the head bone's pivot, m (the mod's neck point under the headset, HeadTracking.neckLocalOffset, the other way)
        public static float eyeForward = 0.08f;
        public static float eyeUp = 0.075f;
        public static int iterations = 3;
        public static float[] spineWeights = { 0.3f, 0.35f, 0.4f, 0.45f };  // per spine bone from the hips up; more bones take the last
        public static float spineMaxTurn = 20f;     // deg a spine bone may turn off its animated pose (twist and bend; the 4 bones: 80 at most)
        public static float neckWeight = 0.3f;
        public static float neckMaxTurn = 30f;
        public static float twistShare = 0.6f;      // of the head's yaw off the chest the chest takes
        public static float maxTwist = 45f;         // deg, the whole spine
        public static float neckLookShare = 0.35f;  // of the head's turn off the chest the neck takes
        // The hips (and the legs under them) squared to the body's facing, where the caller lets them (Solve's hipWeight: standing still -
        // a walk's legs go the way you walk). Tarkov's rifle stance stands side-on to where you look; the body's facing follows the head.
        public static float hipShare = 0.25f;       // of the head's yaw off the body's facing the hips turn on top (a glance; the chest takes the rest)
        public static float maxHipTurn = 60f;       // deg off their animated pose

        internal sealed class Rig
        {
            public PlayerBones bones;
            public Transform root;
            public Transform pelvis;
            public Transform[] spine;               // from the hips up to the head's branch, the pelvis not in it
            public Transform chest;                 // the ribcage: the arms hang from it (on the spine, or a branch off it)
            public Transform neck;                  // null = none between the spine and the head
            public Transform head;
            public Vector3 eyeInHead;               // the eyes off the head bone, in its rotation's frame (world scale)
            public Quaternion lookInHead;           // the look's rotation in the head's frame
            public Vector3 chestForwardLocal;       // the way the chest faces, in the ribcage's frame
            public Transform[] posed;               // every bone the solve turns: the spine, the neck, the head, the pelvis
            public Quaternion[] animated;
            public Quaternion[] written;
            public bool haveWritten;
            public bool valid;
            public string description;
            public float hipsOffBody;               // deg the animation had the hips off the body's facing, last solve that turned them
            public float hipsTurned;                // deg they were turned
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

            // The bones' axes in Tarkov's rest pose, not the nearest to this frame's: the rifle stance turns the ribcage ~40 deg off the
            // body, where the nearest axis flips - each peer picked its own
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

        // Base HumanHead / Base HumanRibcage / Base HumanPelvis axes along the body's forward and up in the rest pose (playersuperior.bundle,
        // exact; the pelvis 0.2 deg off)
        private static readonly Vector3 HEAD_FACE = Vector3.up;
        private static readonly Vector3 HEAD_UP = Vector3.left;
        private static readonly Vector3 RIBCAGE_FACE = Vector3.forward;
        private static readonly Vector3 PELVIS_FACE = Vector3.up;

        // The body's eyes, world: off the head bone by the rest-pose face and up (BodyFollow keeps these under the headset)
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

        // eyePosition / eyeRotation: the tracked head (the headset), world. weight 0..1 (0 = the animator's pose, nothing written).
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
            // The hips first (everything above turns with them: the chest's twist below measures from there): squared to the body's facing,
            // which follows the head, then hipShare of the head's yaw off it
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

            // The chest part of the way round to the head's yaw
            Vector3 chestFlat = Vector3.ProjectOnPlane(rig.chest.rotation * rig.chestForwardLocal, up);
            if (lookFlat.sqrMagnitude > 1e-4f && chestFlat.sqrMagnitude > 1e-4f)
            {
                float yaw = Vector3.SignedAngle(chestFlat, lookFlat, up);
                float twist = Mathf.Clamp(yaw * twistShare, -maxTwist, maxTwist) * weight / spineCount;
                Quaternion twistTurn = Quaternion.AngleAxis(twist, up);
                for (int i = 0; i < spineCount; i++)
                    rig.spine[i].rotation = twistTurn * rig.spine[i].rotation;
            }

            Quaternion headRotation = eyeRotation * Quaternion.Inverse(rig.lookInHead);
            Quaternion headTarget = Quaternion.Slerp(head.rotation, headRotation, weight);
            // The neck's share of the head's turn, before the bend: it moves the head's pivot, which the bend then takes to the target
            if (rig.neck != null && neckLookShare > 0f)
            {
                Quaternion turn = headTarget * Quaternion.Inverse(head.rotation);
                rig.neck.rotation = Quaternion.Slerp(Quaternion.identity, turn, neckLookShare) * rig.neck.rotation;
                LimitTurn(rig.neck, rig.animated[neckIndex], neckMaxTurn);
            }

            // The head's pivot where the tracked head's is: the eyes off the pivot as the head will be turned
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

        // One CCD step: the bone turns its share of the way that brings the head's pivot toward the target, within its limit
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

        // The pose to start from: the animator's this frame. A bone still at what the solve wrote last frame wasn't animated since (a culled or
        // paused animator): back to the animated pose it had then, or the turns would pile up frame on frame.
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
