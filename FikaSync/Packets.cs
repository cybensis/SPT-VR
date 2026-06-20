using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace SptVrFikaSync
{
    // One VR player's two hand poses in their own chest frame, keyed by NetId. Internal: the VR mod
    // never touches the packet type, it just calls FikaVrSync.SendArmPose(...) with the components.
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

    // One VR player's HELD WEAPON pose (the gun's WeaponRoot transform) in their own chest frame,
    // keyed by NetId. Sent instead of the arm packet while a firearm is held: the remote overrides the
    // observed weapon to this pose and re-IKs the hands onto it, so other players see the gun aimed
    // where the controller actually points (not where FIKA aims it from the head rotation).
    internal struct VRWeaponPacket : INetSerializable
    {
        public int NetId;
        public Vector3 Pos;   public Quaternion Rot;      // WeaponRoot pose (chest-local)
        public bool LeftOnGrip;                            // off hand on the foregrip?
        public Vector3 LeftPos;   public Quaternion LeftRot; // off hand pose (chest-local), used when free

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

    // A VR melee SURFACE hit (wall sparks / glass break). The custom VR melee runs its collider only
    // on the swinger, so observers never spawn the surface effect; this carries the hit so they can
    // replay GameWorld.HackShot at the point. Player/AI hits are NOT sent (damage already syncs).
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

    // One VR player's MANUAL-EATING state: the two rendered wrist poses (so observers drive the
    // arms like empty hands) plus every live food prop's pose + visibility, all chest-local. The
    // props are matched on the observer by a STABLE hash of the prop transform's name (the observed
    // food model is the same prefab -> same bone names), and their world poses overridden after the
    // observed meds animator, so the food rides the synced hands. Active=false is a one-shot STOP
    // (restore hidden renderers + drop the override). Variable prop count like BodyDragPacket.
    internal struct VREatingPacket : INetSerializable
    {
        public int NetId;
        public bool Active;                                   // false = stop (restore + clear)
        public Vector3 LeftPos;   public Quaternion LeftRot;  // rendered left wrist, chest-local
        public Vector3 RightPos;  public Quaternion RightRot; // rendered right wrist, chest-local
        // Food animator (ObjectInHandsAnimator) state PER LAYER, mirrored so the observer's food shows
        // the same skinned deformation (lid bone, bag rip, chew squash) the eater is at — a seek, not a
        // play, so it never loops. All layers (not just 0) because a food's eat can live on any of them.
        public byte AnimLayerCount;
        public int[] AnimHashes;
        public float[] AnimTimes;
        public byte PropCount;
        public int[] NameHashes;        // stable hash of each prop transform's name
        public Vector3[] Positions;     // chest-local
        public Quaternion[] Rotations;  // chest-local
        public bool[] Visible;          // mirror the local renderer-enabled state

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
            AnimHashes = new int[AnimLayerCount];
            AnimTimes = new float[AnimLayerCount];
            for (int i = 0; i < AnimLayerCount; i++) { AnimHashes[i] = reader.GetInt(); AnimTimes[i] = reader.GetFloat(); }
            PropCount = reader.GetByte();
            NameHashes = new int[PropCount];
            Positions = new Vector3[PropCount];
            Rotations = new Quaternion[PropCount];
            Visible = new bool[PropCount];
            for (int i = 0; i < PropCount; i++)
            {
                NameHashes[i] = reader.GetInt();
                Positions[i] = reader.GetUnmanaged<Vector3>();
                Rotations[i] = reader.GetUnmanaged<Quaternion>();
                Visible[i] = reader.GetBool();
            }
        }
    }

    // One eat SOUND event the local eater actually played — forwarded so observers hear the real
    // gesture audio (open/scoop/gulp/bite) instead of the looping vanilla observed-meds sound (which
    // we freeze out). Carried as a STABLE HASH of the event name, NOT the string: FIKA's bundled
    // NetDataWriter has no runtime Put(string) (the publicized stub lies; the real DLL only has
    // Put(string,int)/PutLargeString), so a plain Put(string) throws MissingMethodException. The
    // observer reverse-resolves the hash against the food model's own sound bank (same prefab -> same
    // event names). Fire-and-forget (Unreliable) — a dropped eat blip is harmless.
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

    // A body drag: the FULL ragdoll pose (one world transform per RigidbodySpawner bone), or a release
    // marker. The receiver freezes the corpse's ragdoll kinematic and snaps every bone to this pose, so
    // observers see the EXACT same ragdoll the dragger does — no local-physics divergence (which caused
    // the body to stretch/freak out when only one bone was synced).
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
            Positions = new Vector3[BoneCount];
            Rotations = new Quaternion[BoneCount];
            for (int i = 0; i < BoneCount; i++)
            {
                Positions[i] = reader.GetUnmanaged<Vector3>();
                Rotations[i] = reader.GetUnmanaged<Quaternion>();
            }
        }
    }
}
