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
