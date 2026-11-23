using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace SptVrFikaSync
{
    // FIKA's RegisterPacket deserializes every packet of a type into ONE kept instance, so a Deserialize reuses its arrays while
    // the count stays the same. A receiver that keeps a packet's data past its handler copies the arrays (the next packet,
    // from any player, overwrites them). The wire format is unchanged.
    internal static class PacketArrays
    {
        public static T[] Sized<T>(T[] array, int count) => array != null && array.Length == count ? array : new T[count];

        // Copies the first count entries into own (resized to count), for a receiver that keeps them
        public static T[] CopyInto<T>(T[] own, T[] source, int count)
        {
            if (source == null)
                return own;
            count = System.Math.Min(count, source.Length);
            own = Sized(own, count);
            System.Array.Copy(source, own, count);
            return own;
        }
    }

    // One VR player's two hand poses in their own body frame (Player.Transform), keyed by NetId. Internal: the VR mod
    // never uses the packet type, it calls FikaVrSync.SendArmPose(...) with the values.
    internal struct VRArmsPacket : INetSerializable
    {
        public int NetId;
        public Vector3 LeftPos;   public Quaternion LeftRot;
        public Vector3 RightPos;  public Quaternion RightRot;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.PutUnmanaged(LeftPos);
            writer.PutUnmanaged(LeftRot);
            writer.PutUnmanaged(RightPos);
            writer.PutUnmanaged(RightRot);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            LeftPos = reader.GetUnmanaged<Vector3>();
            LeftRot = reader.GetUnmanaged<Quaternion>();
            RightPos = reader.GetUnmanaged<Vector3>();
            RightRot = reader.GetUnmanaged<Quaternion>();
        }
    }

    // One VR player's headset pose in their body's frame (Player.Transform), keyed by NetId. Observers turn and bend their copy's
    // upper body toward it and turn its head (HeadSyncApply). Active = false means the head is not tracked right now.
    internal struct VRHeadPacket : INetSerializable
    {
        public int NetId;
        public bool Active;
        public Vector3 Position;
        public Quaternion Rotation;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(Active);
            writer.PutUnmanaged(Position);
            writer.PutUnmanaged(Rotation);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            Active = reader.GetBool();
            Position = reader.GetUnmanaged<Vector3>();
            Rotation = reader.GetUnmanaged<Quaternion>();
        }
    }

    // One VR player's held weapon pose (the gun's WeaponRoot transform) in their own body frame (Player.Transform),
    // keyed by NetId. Sent instead of the arm packet while a firearm is held. The observer moves its copy
    // of the weapon to this pose and runs the hand IK onto it, so the gun aims where the controller
    // points, not where FIKA would aim it from the head rotation.
    internal struct VRWeaponPacket : INetSerializable
    {
        public int NetId;
        public Vector3 Pos;   public Quaternion Rot;      // WeaponRoot pose (body-local)
        public bool LeftOnGrip;                            // off hand on the foregrip?
        public Vector3 LeftPos;   public Quaternion LeftRot; // off hand pose (body-local), used when free

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.PutUnmanaged(Pos);
            writer.PutUnmanaged(Rot);
            writer.Put(LeftOnGrip);
            writer.PutUnmanaged(LeftPos);
            writer.PutUnmanaged(LeftRot);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            Pos = reader.GetUnmanaged<Vector3>();
            Rot = reader.GetUnmanaged<Quaternion>();
            LeftOnGrip = reader.GetBool();
            LeftPos = reader.GetUnmanaged<Vector3>();
            LeftRot = reader.GetUnmanaged<Quaternion>();
        }
    }

    // One VR player's finger pose, keyed by NetId: per hand, the local euler angles of 15 joints (5 fingers x 3 joints)
    // in the palm's child order. Both rigs are the same EFT prefab, so the order matches. Sent while the sender's own
    // code poses the fingers (baked open poses or Index skeletal curls). A packet with both hands inactive is sent
    // once to stop, so observers give the fingers back to the animator.
    internal struct VRFingersPacket : INetSerializable
    {
        public const int JointCount = 15;

        public int NetId;
        public bool LeftActive;
        public bool RightActive;
        public Vector3[] Left;    // joint local eulers; only valid when LeftActive
        public Vector3[] Right;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(LeftActive);
            writer.Put(RightActive);
            if (LeftActive)
                for (int i = 0; i < JointCount; i++)
                    writer.PutUnmanaged(Left[i]);
            if (RightActive)
                for (int i = 0; i < JointCount; i++)
                    writer.PutUnmanaged(Right[i]);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            LeftActive = reader.GetBool();
            RightActive = reader.GetBool();
            if (LeftActive)
            {
                Left = new Vector3[JointCount];
                for (int i = 0; i < JointCount; i++)
                    Left[i] = reader.GetUnmanaged<Vector3>();
            }
            if (RightActive)
            {
                Right = new Vector3[JointCount];
                for (int i = 0; i < JointCount; i++)
                    Right[i] = reader.GetUnmanaged<Vector3>();
            }
        }
    }

    // A VR melee hit on a surface (wall sparks, glass break). The VR melee runs its collider only on the
    // swinging player's machine, so observers never spawn the surface effect. This packet carries the hit so
    // they can replay GameWorld.HackShot at the point. Hits on players and AI are not sent (damage already syncs).
    internal struct VRMeleeHitPacket : INetSerializable
    {
        public Vector3 Point;
        public Vector3 Normal;
        public Vector3 Direction;
        public float Damage;

        public void Serialize(NetDataWriter writer)
        {
            writer.PutUnmanaged(Point);
            writer.PutUnmanaged(Normal);
            writer.PutUnmanaged(Direction);
            writer.Put(Damage);
        }

        public void Deserialize(NetDataReader reader)
        {
            Point = reader.GetUnmanaged<Vector3>();
            Normal = reader.GetUnmanaged<Vector3>();
            Direction = reader.GetUnmanaged<Vector3>();
            Damage = reader.GetFloat();
        }
    }

    // One VR player's manual-eating state, all body-local: the two rendered wrist poses (observers drive
    // the arms as with empty hands) and every food prop's pose and visibility. The observer matches the
    // props by a stable hash of the prop transform's name (the same prefab has the same bone names) and
    // overrides their world poses after the observed meds animator, so the food follows the synced hands.
    // Active = false is sent once to stop: the observer shows hidden renderers again and drops the override.
    internal struct VREatingPacket : INetSerializable
    {
        public int NetId;
        public bool Active;                                   // false = stop (restore + clear)
        public Vector3 LeftPos;   public Quaternion LeftRot;  // rendered left wrist, body-local
        public Vector3 RightPos;  public Quaternion RightRot; // rendered right wrist, body-local
        // Food animator (ObjectInHandsAnimator) state per layer, copied so the observer's food shows the
        // same skinned deformation (lid bone, bag rip, chew squash). The observer seeks to it instead of
        // playing it, so it never loops. All layers are sent, because a food's eat can be on any of them.
        public byte AnimLayerCount;
        public int[] AnimHashes;
        public float[] AnimTimes;
        public byte PropCount;
        public int[] NameHashes;        // stable hash of each prop transform's name
        public Vector3[] Positions;     // body-local
        public Quaternion[] Rotations;  // body-local
        public bool[] Visible;          // the local renderer-enabled state

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(Active);
            if (!Active)
                return;
            writer.PutUnmanaged(LeftPos);  writer.PutUnmanaged(LeftRot);
            writer.PutUnmanaged(RightPos); writer.PutUnmanaged(RightRot);
            writer.Put(AnimLayerCount);
            for (int i = 0; i < AnimLayerCount; i++) { writer.Put(AnimHashes[i]); writer.Put(AnimTimes[i]); }
            writer.Put(PropCount);
            for (int i = 0; i < PropCount; i++)
            {
                writer.Put(NameHashes[i]);
                writer.PutUnmanaged(Positions[i]);
                writer.PutUnmanaged(Rotations[i]);
                writer.Put(Visible[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            Active = reader.GetBool();
            if (!Active)
                return;
            LeftPos = reader.GetUnmanaged<Vector3>();   LeftRot = reader.GetUnmanaged<Quaternion>();
            RightPos = reader.GetUnmanaged<Vector3>();  RightRot = reader.GetUnmanaged<Quaternion>();
            AnimLayerCount = reader.GetByte();
            AnimHashes = PacketArrays.Sized(AnimHashes, AnimLayerCount);
            AnimTimes = PacketArrays.Sized(AnimTimes, AnimLayerCount);
            for (int i = 0; i < AnimLayerCount; i++) { AnimHashes[i] = reader.GetInt(); AnimTimes[i] = reader.GetFloat(); }
            PropCount = reader.GetByte();
            NameHashes = PacketArrays.Sized(NameHashes, PropCount);
            Positions = PacketArrays.Sized(Positions, PropCount);
            Rotations = PacketArrays.Sized(Rotations, PropCount);
            Visible = PacketArrays.Sized(Visible, PropCount);
            for (int i = 0; i < PropCount; i++)
            {
                NameHashes[i] = reader.GetInt();
                Positions[i] = reader.GetUnmanaged<Vector3>();
                Rotations[i] = reader.GetUnmanaged<Quaternion>();
                Visible[i] = reader.GetBool();
            }
        }
    }

    // One eating sound event the local player played, so observers hear the real gesture audio
    // (open, scoop, gulp, bite) instead of the vanilla observed-meds sound, which is frozen out.
    // Sent as a stable hash of the event name, not the string: FIKA's bundled NetDataWriter has no
    // Put(string) at runtime (the publicized stub is wrong; the real DLL only has Put(string,int) and
    // PutLargeString), so a plain Put(string) throws MissingMethodException. The observer looks the
    // hash up in the food model's own sound bank. Sent unreliable, because a dropped one is harmless.
    internal struct VREatingSoundPacket : INetSerializable
    {
        public int NetId;
        public int NameHash;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(NameHash);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            NameHash = reader.GetInt();
        }
    }

    // One VR player's manual reload state, sent with the weapon pose while a firearm is in hands (SPT-VR's VRReloadController.Sync).
    // It holds the rendered wrists and which arms to drive to them, the props in the hands (magazines, a round) by item id, every gun
    // part the reload moves or hides (by path under the gun's root, with local pose and visibility), the bolt catch and bolt-open
    // layer, and event counters. Wrists and props are in the gun's frame (Weapon_root), so they follow the observed gun that
    // ArmSyncApply placed. Ids and paths are hashed with FikaVrSync.StableHash, so no strings are sent at runtime.
    // Active = false is sent once to stop, and the observer puts its copy of the gun back.
    internal struct VRReloadPacket : INetSerializable
    {
        public const byte ManualReload = 1, ManualBolt = 2, DriveLeft = 4, DriveRight = 8, BoltCatch = 16, BoltOpenDriven = 32;

        public int NetId;
        public bool Active;
        public byte Flags;
        public float BoltOpen;
        public int ShellsThrown;        // counters: each change is one event, so a dropped packet can't lose one
        public int RoundsShown;
        public int HiddenGroundItem;    // the ground magazine whose model is in the hand (0 = none)
        public Vector3 LeftPos;   public Quaternion LeftRot;
        public Vector3 RightPos;  public Quaternion RightRot;
        public byte PropCount;
        public int[] PropItems;
        public Vector3[] PropPositions;
        public Quaternion[] PropRotations;
        public byte PartCount;
        public int[] PartPaths;
        public Vector3[] PartPositions;     // local (in the part's parent)
        public Quaternion[] PartRotations;
        public bool[] PartShown;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(Active);
            if (!Active)
                return;
            writer.Put(Flags);
            writer.Put(BoltOpen);
            writer.Put(ShellsThrown);
            writer.Put(RoundsShown);
            writer.Put(HiddenGroundItem);
            writer.PutUnmanaged(LeftPos);  writer.PutUnmanaged(LeftRot);
            writer.PutUnmanaged(RightPos); writer.PutUnmanaged(RightRot);
            writer.Put(PropCount);
            for (int i = 0; i < PropCount; i++)
            {
                writer.Put(PropItems[i]);
                writer.PutUnmanaged(PropPositions[i]);
                writer.PutUnmanaged(PropRotations[i]);
            }
            writer.Put(PartCount);
            for (int i = 0; i < PartCount; i++)
            {
                writer.Put(PartPaths[i]);
                writer.PutUnmanaged(PartPositions[i]);
                writer.PutUnmanaged(PartRotations[i]);
                writer.Put(PartShown[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            Active = reader.GetBool();
            if (!Active)
                return;
            Flags = reader.GetByte();
            BoltOpen = reader.GetFloat();
            ShellsThrown = reader.GetInt();
            RoundsShown = reader.GetInt();
            HiddenGroundItem = reader.GetInt();
            LeftPos = reader.GetUnmanaged<Vector3>();   LeftRot = reader.GetUnmanaged<Quaternion>();
            RightPos = reader.GetUnmanaged<Vector3>();  RightRot = reader.GetUnmanaged<Quaternion>();
            PropCount = reader.GetByte();
            PropItems = PacketArrays.Sized(PropItems, PropCount);
            PropPositions = PacketArrays.Sized(PropPositions, PropCount);
            PropRotations = PacketArrays.Sized(PropRotations, PropCount);
            for (int i = 0; i < PropCount; i++)
            {
                PropItems[i] = reader.GetInt();
                PropPositions[i] = reader.GetUnmanaged<Vector3>();
                PropRotations[i] = reader.GetUnmanaged<Quaternion>();
            }
            PartCount = reader.GetByte();
            PartPaths = PacketArrays.Sized(PartPaths, PartCount);
            PartPositions = PacketArrays.Sized(PartPositions, PartCount);
            PartRotations = PacketArrays.Sized(PartRotations, PartCount);
            PartShown = PacketArrays.Sized(PartShown, PartCount);
            for (int i = 0; i < PartCount; i++)
            {
                PartPaths[i] = reader.GetInt();
                PartPositions[i] = reader.GetUnmanaged<Vector3>();
                PartRotations[i] = reader.GetUnmanaged<Quaternion>();
                PartShown[i] = reader.GetBool();
            }
        }
    }

    // One sound the VR player's manual reload played, by a stable hash of its event name (like VREatingSoundPacket). The observer's
    // copy of the gun skips Tarkov's reload animations, so these replace the animations' sound events.
    internal struct VRReloadSoundPacket : INetSerializable
    {
        public int NetId;
        public int NameHash;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(NameHash);
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            NameHash = reader.GetInt();
        }
    }

    // A body drag: the full ragdoll pose (one world transform per RigidbodySpawner bone), or a release
    // marker. The receiver makes the corpse's ragdoll kinematic and snaps every bone to this pose.
    // Syncing a single bone lets the local physics drift apart and the body stretches.
    internal struct BodyDragPacket : INetSerializable
    {
        public int CorpseId;        // GameWorld.ObservedPlayersCorpses key (dead player's NetId)
        public bool Released;
        public byte BoneCount;
        public Vector3[] Positions; // world-space, per bone
        public Quaternion[] Rotations;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(CorpseId);
            writer.Put(Released);
            if (Released)
                return;
            writer.Put(BoneCount);
            for (int i = 0; i < BoneCount; i++)
            {
                writer.PutUnmanaged(Positions[i]);
                writer.PutUnmanaged(Rotations[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            CorpseId = reader.GetInt();
            Released = reader.GetBool();
            if (Released)
                return;
            BoneCount = reader.GetByte();
            Positions = PacketArrays.Sized(Positions, BoneCount);
            Rotations = PacketArrays.Sized(Rotations, BoneCount);
            for (int i = 0; i < BoneCount; i++)
            {
                Positions[i] = reader.GetUnmanaged<Vector3>();
                Rotations[i] = reader.GetUnmanaged<Quaternion>();
            }
        }
    }

    // A downed teammate (FIKA's downed/revive state) being dragged. Unlike BodyDragPacket (a dead Corpse keyed by
    // GameWorld.ObservedPlayersCorpses), a downed teammate is a live player keyed by NetId. The receiver applies it two ways:
    //   * The downed player's own client teleports itself to RootPos, so a revive puts them where they were dragged.
    //     Their position is owned by their client; nothing else can move it.
    //   * Every other client drives that player's revive-ragdoll bones to the synced world pose (all bones, like
    //     BodyDragPacket: one transform per RigidbodySpawner bone).
    // RootPos is sent in every packet, including the reliable release marker. The release marker carries the final spot,
    // so a dropped drag packet can't strand the teammate. The bone arrays are empty on the release marker.
    internal struct DownedDragPacket : INetSerializable
    {
        public int NetId;            // the downed player's NetId (CoopHandler.Players key)
        public bool Released;
        public Vector3 RootPos;      // where the downed player is moved to (the body's centre, projected onto the ground)
        public byte BoneCount;       // 0 on release
        public Vector3[] Positions;  // world-space, per ragdoll bone
        public Quaternion[] Rotations;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            writer.Put(Released);
            writer.PutUnmanaged(RootPos);
            writer.Put(BoneCount);
            for (int i = 0; i < BoneCount; i++)
            {
                writer.PutUnmanaged(Positions[i]);
                writer.PutUnmanaged(Rotations[i]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            NetId = reader.GetInt();
            Released = reader.GetBool();
            RootPos = reader.GetUnmanaged<Vector3>();
            BoneCount = reader.GetByte();
            Positions = PacketArrays.Sized(Positions, BoneCount);
            Rotations = PacketArrays.Sized(Rotations, BoneCount);
            for (int i = 0; i < BoneCount; i++)
            {
                Positions[i] = reader.GetUnmanaged<Vector3>();
                Rotations[i] = reader.GetUnmanaged<Quaternion>();
            }
        }
    }

    // A corpse a VR player grabbed or let go while dragging it, sent to the host/headless, which runs the bots and the
    // looting AI. SPT-VR.dll needs SteamVR, so it does not run on a headless; only this companion module does.
    // It keeps the AI's idea of where the body is in sync:
    //   * Grab (Released = false): the body is being carried. Its ORBIT loot waypoint is removed so bots can't loot
    //     it at the death spot.
    //   * Settle (Released = true): the body rests at RestPos. The ORBIT waypoint is added there, and the server's
    //     ragdoll copy is moved there so collider scanners like LootingBots find it.
    // Keyed by the dead player's ProfileId, the only stable identity for a Corpse local to the headless (that Corpse is never
    // in GameWorld.ObservedPlayersCorpses, which is client-only). The ProfileId is the same on every machine, resolves via
    // GameWorld.GetEverExistedPlayerByID, and matches ORBIT's corpse-waypoint Target.PlayerProfileID.
    internal struct CorpseLootPacket : INetSerializable
    {
        public string ProfileId;
        public bool Released;   // false = grabbed (being carried), true = settled at RestPos
        public Vector3 RestPos; // resting position (only used when Released)

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId ?? string.Empty);
            writer.Put(Released);
            if (Released)
                writer.PutUnmanaged(RestPos);
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString();
            Released = reader.GetBool();
            if (Released)
                RestPos = reader.GetUnmanaged<Vector3>();
        }
    }

    // A door a VR player moves by hand (SPT-VR's PhysicalDoors), by its Id: its angle and its handle's turn (0 = rest, 1 = down).
    // FIKA itself only syncs the door's state (open or shut). Final marks the last packet of a move, which is sent reliable.
    internal struct VRDoorPacket : INetSerializable
    {
        public string DoorId;
        public float Angle;
        public float HandleTurn;
        public bool Final;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(DoorId ?? string.Empty);
            writer.Put(Angle);
            writer.Put(HandleTurn);
            writer.Put(Final);
        }

        public void Deserialize(NetDataReader reader)
        {
            DoorId = reader.GetString();
            Angle = reader.GetFloat();
            HandleTurn = reader.GetFloat();
            Final = reader.GetBool();
        }
    }
}
