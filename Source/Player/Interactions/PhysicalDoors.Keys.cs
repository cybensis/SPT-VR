using System;
using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Keys by hand. The trigger while a hand grips a locked door you have the key for: the hand lets go of the door and the key comes out
    // into it, held the way Tarkov's own unlock holds it (the key's "pivot" on the index's first bone). Its tip into the lock (the lock
    // handle's pivot, else the door handle's) seats it, the hand on it; a wrist turn of keyUnlockTurn about the keyway, either way, unlocks
    // the door through Tarkov's own unlock (the key's uses, FIKA's packet) without its walk-up and animation (DoorKeyPatches). The trigger
    // again puts the key away; not the grip (a hand pinching a key reads as letting go: OpenXRInput's fast-open). Keycard doors stay vanilla.
    internal static partial class PhysicalDoors
    {
        public static bool keysByHand = true;
        public static float keyAppearDelay = 0.15f;     // s after the trigger: the hand off the door before the key is in it
        public static float keyholeRadius = 0.035f;     // m the key's tip may be off the keyway's line...
        public static float keyholeAxialReach = 0.07f;  // ...and along it from the lock (either face of the door)
        public static float keyInsertAngle = 40f;       // deg the key may be off the keyway
        public static float keyInsertDepth = 0.03f;     // m the tip goes in past the door's face
        // The face is the door's own surface along the keyway within this of the lock's pivot (a door's handles and thick colliders stand
        // further out); none there = the pivot less keyFaceFallback
        public static float keyFaceBand = 0.06f;        // m
        public static float keyFaceFallback = 0.02f;    // m
        public static float keyInsertTime = 0.08f;      // s the hand takes onto the lock
        public static float keyUnlockTurn = 60f;        // deg of wrist turn about the keyway that unlocks
        public static float keyMaxTurn = 90f;
        public static float keyTurnTickDegrees = 15f;   // a light pulse every this much of turn
        public static float keyPullOutDistance = 0.1f;  // m the controller may get from where it seated the key before it comes out
        public static float keyReseatDelay = 0.3f;      // s after it comes out before it seats again
        public static float keyStowDelay = 0.5f;        // s the key stays turned in the unlocked lock before it goes away
        public static float keySoundVolume = 0.6f;
        public static Vector3 keyHoldOffsetPosition = Vector3.zero;   // m, the index bone's frame: a nudge on Tarkov's hold
        public static Vector3 keyHoldOffsetEuler = Vector3.zero;
        public static bool keyTipFlip = false;          // the blade at the other end of the key (one whose grip point is on its blade)
        // Tarkov holds keys in the left hand only: the right gets that hold mirrored. false = Tarkov's alignment on the right bone as is.
        public static bool mirrorKeyInRightHand = true;
        public static float triggerPressLevel = 0.7f, triggerReleaseLevel = 0.3f;
        public static bool keyDebugViz = false;         // spheres: the keyhole (blue, its radius), the key's tip (yellow; green when it can seat)

        private enum KeyState { None, Coming, InHand, Seated, Unlocked }

        private sealed class KeyHold
        {
            public bool dominant;
            public KeyState state;
            public float stateTime;
            public Door door;
            public KeyComponent key;
            public GameObject prop;
            public readonly List<Collider> disabledColliders = new List<Collider>();
            public readonly Dictionary<Transform, int> originalLayers = new Dictionary<Transform, int>();
            public Vector3 tipLocal;                    // the prop's frame: the blade's end...
            public Vector3 axisLocal;                   // ...the way to it from the grip...
            public Vector3 widthLocal;                  // ...across the blade's flat...
            public Vector3 gripLocal;                   // ...the grip point (Tarkov's pivot)...
            public Vector3 centerLocal;                 // ...and the middle of its box
            public int flatAxis;                        // the prop's axis through the blade's flat (its thinnest)
            // In the lock
            public Transform rig;
            public LimbIK armIk;
            public Transform savedTarget;
            public Transform latch;
            public bool easingBack;
            public float latchTime;
            public Vector3 latchFromPosition;           // in the rig's parent frame
            public Quaternion latchFromRotation;
            public Vector3 inward;                      // along the keyway, into the door
            public Vector3 seatedTip;
            public Quaternion seatedRotation;           // the key's, unturned
            public Quaternion rigRotationAtSeat;
            public Vector3 gripInRig;
            public Vector3 gripAtSeat;                  // world: the controller's grip on the key as it seated
            public float turn;
            public float lastTickTurn;
            public float unseatTime = -10f;
            public GameObject keyholeSphere, tipSphere;
        }

        private static readonly KeyHold[] keyHolds = { new KeyHold { dominant = false }, new KeyHold { dominant = true } };
        private static readonly bool[] triggerDown = new bool[2];
        private static readonly bool[] triggerPressed = new bool[2];      // this frame's press
        private static readonly HashSet<string> measuredKeys = new HashSet<string>();
        private static readonly HashSet<string> noKeyLogged = new HashSet<string>();
        private static readonly HashSet<string> seatLogged = new HashSet<string>();
        private static readonly RaycastHit[] keyholeHits = new RaycastHit[16];
        private static Door unlockingByHand;
        private static HandsInteractionController keyHands;
        private static GamePlayerOwner playerOwner;
        private static EFT.Player playerOwnerOf;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // For the rest of the mod

        // The key a hand holds out: the fingers close on it (HandsInteractionController.Grasp)
        internal static bool KeyGraspTarget(bool dominant, out Component key)
        {
            KeyHold keyHold = keyHolds[dominant ? 1 : 0];
            key = keyHold.state >= KeyState.InHand && keyHold.prop != null ? keyHold.prop.transform : null;
            return key != null;
        }

        // DoorKeyPatches: this unlock is the key a hand turned, not Tarkov's walk-up
        internal static bool UnlocksByHand(MovementContext movementContext, WorldInteractiveObject door)
        {
            return door != null && door == unlockingByHand && VRGlobals.player != null && movementContext == VRGlobals.player.MovementContext;
        }

        // What MovementContext.StartInteraction's walk-up and the door state's execute would end in, at once
        internal static void UnlockNow(MovementContext movementContext, WorldInteractiveObject door, InteractionResult result, Action callback)
        {
            try
            {
                door.SetUser(VRGlobals.player);
                door.LockForInteraction();
                door.Interact(result);
            }
            finally
            {
                callback?.Invoke();
            }
            movementContext.LockedDoorOpened(breached: false);
        }

        private static bool HoldsKey(int index) => keyHolds[index].state != KeyState.None;

        private static bool UnlockingWithKey(Door door)
        {
            for (int i = 0; i < keyHolds.Length; i++)
                if (keyHolds[i].state == KeyState.Unlocked && keyHolds[i].door == door)
                    return true;
            return false;
        }

        private static bool KeyHoldsRig(Transform rig)
        {
            for (int i = 0; i < keyHolds.Length; i++)
            {
                KeyHold keyHold = keyHolds[i];
                bool onLock = keyHold.state == KeyState.Seated || keyHold.state == KeyState.Unlocked || keyHold.easingBack;
                if (onLock && keyHold.rig == rig)
                    return true;
            }
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Per frame (Tick)

        private static void ReadTriggers()
        {
            for (int i = 0; i < 2; i++)
            {
                VRSingle trigger = i == 1 ? Handedness.GunHandTrigger : Handedness.OffHandTrigger;
                float axis = trigger != null ? trigger.axis : 0f;
                bool down = triggerDown[i] ? axis > triggerReleaseLevel : axis >= triggerPressLevel;
                triggerPressed[i] = down && !triggerDown[i];
                triggerDown[i] = down;
            }
        }

        // The trigger on a held locked door: the key out of the inventory into that hand, which lets go of the door
        private static void TryTakeKey(Hold hold)
        {
            Door door = hold.door;
            if (!keysByHand || door == null || door.DoorState != EDoorState.Locked || door is KeycardDoor || string.IsNullOrEmpty(door.KeyId))
                return;
            GamePlayerOwner owner = Owner();
            KeyComponent key = owner != null ? owner.GetKey(door) : null;
            if (key == null || InteractionContextHelper.CanUnlockBySkill(owner, door).Failed)
            {
                // Not yours to open: two short knocks in the hand
                PulseHand(hold.dominant, 0.3f, 0.025f, 50f, 0f);
                PulseHand(hold.dominant, 0.3f, 0.025f, 50f, 0.09f);
                if (logGrabs && noKeyLogged.Add(door.Id ?? door.name))
                    Plugin.MyLog.LogInfo($"[Doors] no key{(key != null ? " skill" : "")} for '{door.name}' ({door.Id})");
                return;
            }
            KeyHold keyHold = keyHolds[hold.dominant ? 1 : 0];
            keyHold.door = door;
            keyHold.key = key;
            SetKeyState(keyHold, KeyState.Coming);
            Release(hold);
        }

        private static GamePlayerOwner Owner()
        {
            if (playerOwnerOf != VRGlobals.player)
            {
                playerOwnerOf = VRGlobals.player;
                playerOwner = playerOwnerOf != null ? playerOwnerOf.GetComponent<GamePlayerOwner>() : null;
            }
            return playerOwner;
        }

        // A hand with a key out (UpdateHand leaves the hand to it). The trigger again, the hand taking something else (keepsKey) or the
        // door gone puts it away.
        private static void UpdateKey(KeyHold keyHold, Hold hold, bool keepsKey)
        {
            Door door = keyHold.door;
            bool doorThere = door != null && door.isActiveAndEnabled && (door.DoorState == EDoorState.Locked || keyHold.state == KeyState.Unlocked);
            bool cancelled = triggerPressed[keyHold.dominant ? 1 : 0] && keyHold.state != KeyState.Unlocked;
            if (!keepsKey || !doorThere || cancelled || keyHands == null)
            {
                StowKey(keyHold);
                return;
            }
            switch (keyHold.state)
            {
                case KeyState.Coming:
                    if (!hold.releasing && Time.time - keyHold.stateTime >= keyAppearDelay && !SpawnKey(keyHold))
                        StowKey(keyHold);
                    break;
                case KeyState.InHand:
                    if (keyHold.prop == null || !keyHold.prop.activeInHierarchy)
                        StowKey(keyHold);
                    else if (Time.time - keyHold.unseatTime >= keyReseatDelay)
                        TrySeat(keyHold);
                    break;
                case KeyState.Seated:
                    UpdateSeated(keyHold);
                    break;
                case KeyState.Unlocked:
                    if (PulledOut(keyHold) || Time.time - keyHold.stateTime >= keyStowDelay)
                        StowKey(keyHold);
                    break;
            }
        }

        private static void SetKeyState(KeyHold keyHold, KeyState state)
        {
            keyHold.state = state;
            keyHold.stateTime = Time.time;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The key in the hand

        private static bool SpawnKey(KeyHold keyHold)
        {
            Transform palm = keyHands.PalmBone(keyHold.dominant);
            ObjectsFactory factory = Singleton<ObjectsFactory>.Instance;
            if (palm == null || factory == null || keyHold.key == null || keyHold.key.Item == null)
                return false;
            GameObject prop = factory.CreateItem(keyHold.key.Item, isAnimated: false);
            if (prop == null)
                return false;
            keyHold.prop = prop;
            keyHold.disabledColliders.Clear();
            keyHold.originalLayers.Clear();
            foreach (Collider collider in prop.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                keyHold.disabledColliders.Add(collider);
            }
            int playerLayer = LayerMask.NameToLayer("Player");
            foreach (Transform child in prop.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                keyHold.originalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = playerLayer;
            }
            prop.SetActive(true);

            // Tarkov's hold (Player.SpawnInHands): the key's pivot on the index's first bone, in that bone's frame. The palm's pose here is
            // last frame's, the fingers open (the door's grasp let go).
            Transform propTransform = prop.transform;
            Transform pivot = FindDescendant(propTransform, "pivot", suffix: false);
            MeasureKey(keyHold, pivot);
            Transform anchor = FindDescendant(palm, "Digit21", suffix: true) ?? palm;
            propTransform.SetParent(anchor, worldPositionStays: false);
            Vector3 scale = propTransform.localScale;
            Quaternion pivotInProp = pivot != null ? Quaternion.Inverse(propTransform.rotation) * pivot.rotation : Quaternion.identity;
            Quaternion keyInBone = Quaternion.Euler(keyHoldOffsetEuler) * Quaternion.Inverse(pivotInProp);
            Vector3 keyInBonePosition = keyHoldOffsetPosition - keyInBone * Vector3.Scale(keyHold.gripLocal, scale);
            if (mirrorKeyInRightHand && anchor.name.IndexOf("HumanR", StringComparison.OrdinalIgnoreCase) >= 0)
                MirrorHold(keyHold, scale, ref keyInBone, ref keyInBonePosition);
            propTransform.localRotation = keyInBone;
            propTransform.localPosition = keyInBonePosition;
            // It rides the palm, not the index: the index curls onto it as the fingers close
            propTransform.SetParent(palm, worldPositionStays: true);

            SetKeyState(keyHold, KeyState.InHand);
            PlayKeySound(keyHold, keySoundVolume);
            PulseHand(keyHold.dominant, 0.2f, 0.03f, 120f, 0f);
            return true;
        }

        // The left hand's hold in the right hand. The right hand's bones are the left's mirrored with their local z turned round (the
        // fingers of both point down -x and curl about +z; the rest poses bear it out), so the mirror in a bone's frame is z -> -z. The
        // key mirrored too would be a reflection: it's also turned over through the middle of its flat (the same key to the eye), which
        // makes the two a rotation.
        private static void MirrorHold(KeyHold keyHold, Vector3 scale, ref Quaternion keyInBone, ref Vector3 keyInBonePosition)
        {
            Vector3 flatAxis = Vector3.zero;
            flatAxis[keyHold.flatAxis] = 1f;
            Vector3 overFlat = Vector3.one;
            overFlat[keyHold.flatAxis] = -1f;
            Matrix4x4 boneMirror = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 rotation = boneMirror * Matrix4x4.Rotate(keyInBone) * Matrix4x4.Scale(overFlat);
            Vector3 throughMiddle = keyInBone * (flatAxis * (2f * keyHold.centerLocal[keyHold.flatAxis] * scale[keyHold.flatAxis]));
            keyInBonePosition = boneMirror.MultiplyPoint3x4(keyInBonePosition + throughMiddle);
            keyInBone = Quaternion.LookRotation(rotation.GetColumn(2), rotation.GetColumn(1));
        }

        // The key's blade from its meshes' local boxes, in the prop's frame: the long way, from the grip point to the far end
        private static void MeasureKey(KeyHold keyHold, Transform pivot)
        {
            Transform propTransform = keyHold.prop.transform;
            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = -min;
            bool any = false;
            foreach (Renderer renderer in propTransform.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer)
                    continue;
                Bounds local = renderer.localBounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = propTransform.InverseTransformPoint(renderer.transform.TransformPoint(BoundsCorners.Corner(local, corner)));
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                    any = true;
                }
            }
            if (!any)
            {
                min = new Vector3(-0.03f, -0.01f, -0.002f);
                max = -min;
            }
            Vector3 size = max - min;
            Vector3 center = (min + max) * 0.5f;
            Vector3 grip = pivot != null ? propTransform.InverseTransformPoint(pivot.position) : center;
            int longest = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            // Of the other two the thinner is the blade's flat, the other across it
            int first = (longest + 1) % 3, second = (longest + 2) % 3;
            int thinnest = size[first] <= size[second] ? first : second;
            int across = first + second - thinnest;
            bool towardMax = max[longest] - grip[longest] >= grip[longest] - min[longest];
            if (keyTipFlip)
                towardMax = !towardMax;
            Vector3 tip = center;
            tip[longest] = towardMax ? max[longest] : min[longest];
            Vector3 axis = Vector3.zero;
            axis[longest] = towardMax ? 1f : -1f;
            Vector3 width = Vector3.zero;
            width[across] = 1f;
            keyHold.tipLocal = tip;
            keyHold.axisLocal = axis;
            keyHold.widthLocal = width;
            keyHold.gripLocal = grip;
            keyHold.centerLocal = center;
            keyHold.flatAxis = thinnest;
            if (logGrabs && measuredKeys.Add(keyHold.prop.name))
            {
                Vector3 scaled = Vector3.Scale(size, propTransform.lossyScale) * 100f;
                Plugin.MyLog.LogInfo($"[Doors] key '{keyHold.prop.name}': {scaled.x:0.#} x {scaled.y:0.#} x {scaled.z:0.#} cm, "
                    + $"{(pivot != null ? "pivot" : "no pivot (held by its middle)")}, blade along {(towardMax ? "+" : "-")}{"xyz"[longest]}, "
                    + $"{Vector3.Distance(grip, tip) * propTransform.lossyScale.x * 100f:0.#} cm from the grip to the tip");
            }
        }

        private static Transform FindDescendant(Transform root, string name, bool suffix)
        {
            if (root == null)
                return null;
            bool match = suffix ? root.name.EndsWith(name, StringComparison.OrdinalIgnoreCase) : string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase);
            if (match)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name, suffix);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void StowKey(KeyHold keyHold)
        {
            if (keyHold.prop != null)
            {
                PlayKeySound(keyHold, keySoundVolume * 0.6f);
                ReturnKeyProp(keyHold);
            }
            if (keyHold.state == KeyState.Seated || keyHold.state == KeyState.Unlocked)
                BeginKeyEaseBack(keyHold);
            SetKeyState(keyHold, KeyState.None);
            keyHold.door = null;
            keyHold.key = null;
        }

        private static void ReturnKeyProp(KeyHold keyHold)
        {
            GameObject prop = keyHold.prop;
            keyHold.prop = null;
            if (prop != null)
            {
                foreach (Collider collider in keyHold.disabledColliders)
                    if (collider != null)
                        collider.enabled = true;
                foreach (KeyValuePair<Transform, int> originalLayer in keyHold.originalLayers)
                    if (originalLayer.Key != null)
                        originalLayer.Key.gameObject.layer = originalLayer.Value;
                prop.transform.SetParent(null, worldPositionStays: false);
                AssetPoolObject.ReturnToPool(prop);
            }
            keyHold.disabledColliders.Clear();
            keyHold.originalLayers.Clear();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // In the lock

        private static void TrySeat(KeyHold keyHold)
        {
            bool canSeat = false;
            Vector3 tip = keyHold.prop.transform.TransformPoint(keyHold.tipLocal);
            if (Keyhole(keyHold.door, out Vector3 lockPoint, out Vector3 keyway))
            {
                Vector3 keyAxis = keyHold.prop.transform.TransformDirection(keyHold.axisLocal).normalized;
                Vector3 offset = tip - lockPoint;
                float along = Vector3.Dot(offset, keyway);
                Vector3 inward = keyway * Mathf.Sign(Vector3.Dot(keyAxis, keyway));
                canSeat = Mathf.Abs(along) <= keyholeAxialReach && (offset - along * keyway).magnitude <= keyholeRadius
                    && Vector3.Dot(keyAxis, inward) >= Mathf.Cos(keyInsertAngle * Mathf.Deg2Rad);
                if (keyDebugViz)
                {
                    DebugShapes.Sphere(ref keyHold.keyholeSphere, "VRKeyholeViz", true, lockPoint, keyholeRadius, new Color(0.2f, 0.4f, 1f, 0.35f));
                    DebugShapes.Sphere(ref keyHold.tipSphere, "VRKeyTipViz", true, tip, 0.006f, canSeat ? Color.green : Color.yellow);
                }
                if (canSeat)
                    Seat(keyHold, lockPoint, inward);
            }
        }

        // The lock: its handle's pivot (the cylinder, a thumb-turn), else the door handle's. The keyway runs through the door along the
        // lock's own turning axis when that's across the door, else the door's face normal there.
        private static bool Keyhole(Door door, out Vector3 lockPoint, out Vector3 keyway)
        {
            lockPoint = keyway = Vector3.zero;
            DoorHandle lockHandle = door.LockHandle != null ? door.LockHandle : door._handle;
            if (lockHandle == null)
                return false;
            lockPoint = lockHandle.transform.position;
            Vector3 hingeAxis = HingeAxis(door);
            Vector3 lever = Vector3.ProjectOnPlane(lockPoint - door.transform.position, hingeAxis);
            keyway = lever.sqrMagnitude > 1e-4f ? Vector3.Cross(hingeAxis, lever).normalized : door.transform.forward;
            if (door.LockHandle != null && TurnOf(door.LockHandle, out _, out Vector3 axisLocal))
            {
                Transform parent = door.LockHandle.transform.parent;
                Vector3 lockAxis = (parent != null ? parent.TransformDirection(axisLocal) : axisLocal).normalized;
                if (Mathf.Abs(Vector3.Dot(lockAxis, keyway)) > 0.8f)
                    keyway = lockAxis;
            }
            return true;
        }

        private static void Seat(KeyHold keyHold, Vector3 lockPoint, Vector3 inward)
        {
            LimbIK armIk = keyHold.dominant ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk;
            GameObject rigHand = keyHold.dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand;
            if (armIk == null || armIk.solver == null || armIk.solver.bone3 == null || rigHand == null)
                return;
            Transform rig = rigHand.transform;
            Transform propTransform = keyHold.prop.transform;
            Door door = keyHold.door;

            // In at the door's face along the keyway, the blade on edge: its flat across the hinge's axis, the nearer way up
            bool logSeat = logGrabs && seatLogged.Add(door.Id ?? door.name);
            bool faceFound = DoorFaceAlong(door, lockPoint, inward, logSeat, out Vector3 face, out string surfaces);
            keyHold.seatedTip = face + inward * keyInsertDepth;
            if (logSeat)
            {
                Plugin.MyLog.LogInfo($"[Doors] key seated in '{door.name}': the face {Vector3.Dot(lockPoint - face, inward) * 100f:0.0} cm before the lock's pivot "
                    + $"{(faceFound ? "" : "(none found: the fallback) ")}along the keyway, tip {keyInsertDepth * 100f:0.0} cm in; the door's surfaces on the way "
                    + $"(cm before the pivot): {surfaces}");
            }
            keyHold.inward = inward;
            Vector3 widthNow = propTransform.TransformDirection(keyHold.widthLocal);
            Vector3 up = Vector3.ProjectOnPlane(HingeAxis(door), inward);
            if (up.sqrMagnitude < 1e-4f)
                up = Vector3.ProjectOnPlane(widthNow, inward);
            up = up.normalized * (Vector3.Dot(up, widthNow) >= 0f ? 1f : -1f);
            keyHold.seatedRotation = Quaternion.LookRotation(inward, up) * Quaternion.Inverse(Quaternion.LookRotation(keyHold.axisLocal, keyHold.widthLocal));

            keyHold.rig = rig;
            keyHold.armIk = armIk;
            keyHold.rigRotationAtSeat = rig.rotation;
            keyHold.gripInRig = rig.InverseTransformPoint(propTransform.TransformPoint(keyHold.gripLocal));
            keyHold.gripAtSeat = ControllerGrip(keyHold);
            keyHold.turn = 0f;
            keyHold.lastTickTurn = 0f;

            // The arm's target from where the hand is now (its last bone) onto the lock
            if (keyHold.latch == null)
                keyHold.latch = new GameObject("VRDoorKeyLatch").transform;
            Transform bone = armIk.solver.bone3.transform;
            Transform frame = rig.parent;
            keyHold.latchFromPosition = frame != null ? frame.InverseTransformPoint(bone.position) : bone.position;
            keyHold.latchFromRotation = frame != null ? Quaternion.Inverse(frame.rotation) * bone.rotation : bone.rotation;
            keyHold.latch.SetPositionAndRotation(bone.position, bone.rotation);
            if (!keyHold.easingBack)
                keyHold.savedTarget = armIk.solver.target;
            keyHold.easingBack = false;
            armIk.solver.target = keyHold.latch;
            keyHold.latchTime = Time.time;
            SetKeyState(keyHold, KeyState.Seated);
            PulseHand(keyHold.dominant, 0.35f, 0.03f, 140f, 0f);
            PlayKeySound(keyHold, keySoundVolume * 0.5f);
        }

        // The door's face the key goes in at: its first surface along the keyway, coming in from outside, within keyFaceBand of the lock's
        // pivot: the first surface of any of its colliders can be 9-12 cm out from it on a door ~4 cm thick (a handle, a collider
        // thicker than the door).
        private static bool DoorFaceAlong(Door door, Vector3 lockPoint, Vector3 inward, bool describe, out Vector3 face, out string surfaces)
        {
            face = lockPoint - inward * keyFaceFallback;
            surfaces = "";
            int count = Physics.RaycastNonAlloc(lockPoint - inward * 0.2f, inward, keyholeHits, 0.25f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            System.Text.StringBuilder described = describe ? new System.Text.StringBuilder() : null;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = keyholeHits[i];
                if (hit.collider == null || hit.collider.GetComponentInParent<Door>() != door)
                    continue;
                float before = Vector3.Dot(lockPoint - hit.point, inward);
                described?.Append($"{before * 100f:0.0} '{hit.collider.name}' ({LayerMask.LayerToName(hit.collider.gameObject.layer)}); ");
                if (before < -0.01f || before > keyFaceBand || hit.distance >= best)
                    continue;
                best = hit.distance;
                face = hit.point;
            }
            if (described != null)
                surfaces = described.Length > 0 ? described.ToString().TrimEnd(' ', ';') : "none";
            return best < float.MaxValue;
        }

        // The key's grip as the controller has it (HandCollision's clamp left out)
        private static Vector3 ControllerGrip(KeyHold keyHold)
        {
            Transform rig = keyHold.rig;
            return rig.TransformPoint(keyHold.gripInRig) - rig.position + HandCollision.RawPosition(rig);
        }

        private static bool PulledOut(KeyHold keyHold)
        {
            return keyHold.rig == null || (ControllerGrip(keyHold) - keyHold.gripAtSeat).sqrMagnitude > keyPullOutDistance * keyPullOutDistance;
        }

        // The wrist's turn about the keyway since it seated turns the key; far enough, the lock gives
        private static void UpdateSeated(KeyHold keyHold)
        {
            if (keyHold.armIk == null || keyHold.prop == null || PulledOut(keyHold))
            {
                Unseat(keyHold);
                return;
            }
            float twist = TwistDegrees(keyHold.rig.rotation * Quaternion.Inverse(keyHold.rigRotationAtSeat), keyHold.inward);
            keyHold.turn = Mathf.Clamp(twist, -keyMaxTurn, keyMaxTurn);
            if (Mathf.Abs(keyHold.turn - keyHold.lastTickTurn) >= keyTurnTickDegrees)
            {
                keyHold.lastTickTurn = keyHold.turn;
                PulseHand(keyHold.dominant, 0.12f, 0.015f, 180f, 0f);
            }
            if (Mathf.Abs(keyHold.turn) < keyUnlockTurn)
                return;
            if (Unlock(keyHold))
            {
                SetKeyState(keyHold, KeyState.Unlocked);
                PulseHand(keyHold.dominant, 0.6f, 0.06f, 70f, 0f);
            }
            else
                StowKey(keyHold);
        }

        private static void Unseat(KeyHold keyHold)
        {
            BeginKeyEaseBack(keyHold);
            SetKeyState(keyHold, KeyState.InHand);
            keyHold.unseatTime = Time.time;
        }

        // Tarkov's unlock (InteractionContextHelper's Unlock action): the key's use, its inventory events, the Start packet FIKA sends with the
        // key; DoorKeyPatches skips the walk-up and the animation
        private static bool Unlock(KeyHold keyHold)
        {
            EFT.Player player = VRGlobals.player;
            Door door = keyHold.door;
            if (player == null || door == null || door.DoorState != EDoorState.Locked || keyHold.key == null)
                return false;
            player.MovementContext.ResetCanUsePropState();
            Option<UnlockResult> option = door.UnlockOperation(keyHold.key, player, door);
            if (!option.Succeeded)
            {
                Plugin.MyLog.LogWarning($"[Doors] the key didn't unlock '{door.name}': {option.Error}");
                return false;
            }
            UnlockResult unlock = option.Value;
            InventoryController inventory = player.InventoryController;
            unlock.RaiseEvents(inventory, CommandStatus.Begin);
            unlockingByHand = door;
            try
            {
                player.StartInteraction(door, unlock, () => unlock.RaiseEvents(inventory, CommandStatus.Succeed));
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[Doors] unlocking '{door.name}': {exception}");
            }
            finally
            {
                unlockingByHand = null;
            }
            bool unlocked = door.DoorState != EDoorState.Locked;
            if (logGrabs)
                Plugin.MyLog.LogInfo($"[Doors] key turned in '{door.name}' ({door.Id}): {(unlocked ? "unlocked" : "still locked")}, door {door.DoorState}");
            return unlocked;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The hand on the lock (after the doors' UpdateLatch, every frame)

        private static void UpdateKeyLatch(KeyHold keyHold)
        {
            if (keyHold.state == KeyState.Seated || keyHold.state == KeyState.Unlocked)
            {
                if (keyHold.armIk == null || keyHold.latch == null || keyHold.prop == null || keyHold.rig == null)
                    return;
                // (the two-handing edges and the equip paths set the arm's target: ours while the key is in)
                if (keyHold.armIk.solver.target != keyHold.latch)
                    keyHold.armIk.solver.target = keyHold.latch;
                if (!SeatedLatchPose(keyHold, out Vector3 position, out Quaternion rotation))
                    return;
                float ease = Mathf.SmoothStep(0f, 1f, (Time.time - keyHold.latchTime) / Mathf.Max(keyInsertTime, 0.001f));
                Transform frame = keyHold.rig.parent;
                Vector3 from = frame != null ? frame.TransformPoint(keyHold.latchFromPosition) : keyHold.latchFromPosition;
                Quaternion fromRotation = frame != null ? frame.rotation * keyHold.latchFromRotation : keyHold.latchFromRotation;
                keyHold.latch.SetPositionAndRotation(Vector3.Lerp(from, position, ease), Quaternion.Slerp(fromRotation, rotation, ease));
                return;
            }
            if (!keyHold.easingBack)
                return;
            float progress = (Time.time - keyHold.latchTime) / Mathf.Max(handReleaseTime, 0.001f);
            // Something else took the arm while it eased back: it's theirs
            if (progress >= 1f || keyHold.rig == null || keyHold.latch == null || keyHold.armIk == null || keyHold.armIk.solver.target != keyHold.latch)
            {
                EndKeyLatch(keyHold);
                return;
            }
            Transform rigFrame = keyHold.rig.parent;
            Vector3 start = rigFrame != null ? rigFrame.TransformPoint(keyHold.latchFromPosition) : keyHold.latchFromPosition;
            Quaternion startRotation = rigFrame != null ? rigFrame.rotation * keyHold.latchFromRotation : keyHold.latchFromRotation;
            float eased = Mathf.SmoothStep(0f, 1f, progress);
            keyHold.latch.SetPositionAndRotation(Vector3.Lerp(start, keyHold.rig.position, eased), Quaternion.Slerp(startRotation, keyHold.rig.rotation, eased));
        }

        // Where the arm's last bone goes for the key to sit turned in the lock: the key rides the palm, the palm rides that bone
        private static bool SeatedLatchPose(KeyHold keyHold, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            Transform propTransform = keyHold.prop.transform;
            Transform palm = propTransform.parent;
            Transform bone = keyHold.armIk.solver.bone3 != null ? keyHold.armIk.solver.bone3.transform : null;
            if (palm == null || bone == null)
                return false;
            Quaternion keyRotation = Quaternion.AngleAxis(keyHold.turn, keyHold.inward) * keyHold.seatedRotation;
            Vector3 keyPosition = keyHold.seatedTip - keyRotation * Vector3.Scale(keyHold.tipLocal, propTransform.lossyScale);
            Quaternion palmRotation = keyRotation * Quaternion.Inverse(propTransform.localRotation);
            Vector3 palmPosition = keyPosition - palmRotation * Vector3.Scale(propTransform.localPosition, palm.lossyScale);
            Quaternion boneInverse = Quaternion.Inverse(bone.rotation);
            Quaternion palmInBone = boneInverse * palm.rotation;
            Vector3 palmInBonePosition = boneInverse * (palm.position - bone.position);
            rotation = palmRotation * Quaternion.Inverse(palmInBone);
            position = palmPosition - rotation * palmInBonePosition;
            return true;
        }

        private static void BeginKeyEaseBack(KeyHold keyHold)
        {
            if (keyHold.latch == null || keyHold.rig == null || keyHold.armIk == null || keyHold.armIk.solver.target != keyHold.latch)
            {
                EndKeyLatch(keyHold);
                return;
            }
            Transform frame = keyHold.rig.parent;
            keyHold.latchFromPosition = frame != null ? frame.InverseTransformPoint(keyHold.latch.position) : keyHold.latch.position;
            keyHold.latchFromRotation = frame != null ? Quaternion.Inverse(frame.rotation) * keyHold.latch.rotation : keyHold.latch.rotation;
            keyHold.latchTime = Time.time;
            keyHold.easingBack = true;
        }

        private static void EndKeyLatch(KeyHold keyHold)
        {
            if (keyHold.armIk != null && keyHold.latch != null && keyHold.armIk.solver.target == keyHold.latch)
                keyHold.armIk.solver.target = keyHold.savedTarget;
            keyHold.easingBack = false;
            keyHold.savedTarget = null;
            keyHold.armIk = null;
            keyHold.rig = null;
        }

        private static void ResetKeys()
        {
            for (int i = 0; i < keyHolds.Length; i++)
            {
                KeyHold keyHold = keyHolds[i];
                ReturnKeyProp(keyHold);
                EndKeyLatch(keyHold);
                keyHold.state = KeyState.None;
                keyHold.door = null;
                keyHold.key = null;
                if (keyHold.keyholeSphere != null)
                    UnityEngine.Object.Destroy(keyHold.keyholeSphere);
                if (keyHold.tipSphere != null)
                    UnityEngine.Object.Destroy(keyHold.tipSphere);
            }
            unlockingByHand = null;
            playerOwnerOf = null;
            playerOwner = null;
        }

        // The debug spheres off when the key isn't looking for the lock
        private static void HideKeyViz(KeyHold keyHold)
        {
            if (keyHold.keyholeSphere != null && keyHold.keyholeSphere.activeSelf)
                keyHold.keyholeSphere.SetActive(false);
            if (keyHold.tipSphere != null && keyHold.tipSphere.activeSelf)
                keyHold.tipSphere.SetActive(false);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The key's own pickup clip (what Tarkov plays as its unlock brings the key out), at the key
        private static void PlayKeySound(KeyHold keyHold, float volume)
        {
            GUISounds guiSounds = Singleton<GUISounds>.Instance;
            BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            Item item = keyHold.key != null ? keyHold.key.Item : null;
            if (guiSounds == null || betterAudio == null || item == null)
                return;
            AudioClip clip = guiSounds.GetItemClip(item.ItemSound, EInventorySoundType.pickup);
            Vector3 position = keyHold.prop != null ? keyHold.prop.transform.position : VRGlobals.player.Position;
            if (clip != null)
                betterAudio.TryPlayAtPoint(out _, position, clip, BetterAudio.AudioSourceGroupType.Character, 25, volume);
        }

        private static void PulseHand(bool dominant, float amplitude, float duration, float frequency, float delay)
        {
            HapticMixer.PlayPulse(dominant ? Handedness.GunHandSource : Handedness.OffHandSource, amplitude, duration, frequency, delay);
        }
    }
}
