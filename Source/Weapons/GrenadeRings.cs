using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    // Grenade pins and rings, taken from the game's own models and their pin-pull clips (GrenadeRings.Generated.cs, written by
    // Tools/gen_reload_holds/gen_grenades.py). Eleven of the game's twelve grenade models have a ring; the RDG-2 smoke has pull caps.
    internal static partial class GrenadeRings
    {
        internal sealed class RingData
        {
            public readonly string ringName;
            public readonly Vector3 pullAxis;           // the pin's pull in its parent's frame (the clip's yank off the grenade)
            public readonly Vector3 latchPosition;      // the arm's IK target (the clip's weapon_L_IK_marker) in the ring's frame
            public readonly Quaternion latchRotation;
            public readonly Vector3 hookPoint;          // the index finger's middle joint in the ring's frame: the finger through the ring
            public readonly Vector3[] fingers;          // the clip's left fingers on the ring (palm children, like the reload tables)

            public RingData(string ringName, Vector3 pullAxis, Vector3 latchPosition, Quaternion latchRotation, Vector3 hookPoint, Vector3[] fingers)
            {
                this.ringName = ringName;
                this.pullAxis = pullAxis.normalized;
                this.latchPosition = latchPosition;
                this.latchRotation = Quaternion.Normalize(latchRotation);
                this.hookPoint = hookPoint;
                this.fingers = fingers;
            }
        }

        // Finds the grenade model's pin and ring, with the clip's data. Returns false when there is no ring to pull (the RDG-2) or the
        // table doesn't know the model (logged once per model name, so a game update renaming a pin shows up).
        internal static bool TryFind(Transform weaponRoot, out Transform pin, out Transform ring, out RingData data)
        {
            pin = null;
            ring = null;
            data = null;
            if (weaponRoot == null)
                return false;
            foreach (Transform node in weaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!generated.TryGetValue(node.name, out RingData found))
                    continue;
                Transform ringNode = node.Find(found.ringName);
                if (ringNode == null)
                    continue;
                pin = node;
                ring = ringNode;
                data = found;
                return true;
            }
            return false;
        }

        // Every pin-like bone name on a model, for the log when it isn't in the table
        internal static string PinLikeNames(Transform weaponRoot)
        {
            if (weaponRoot == null)
                return "-";
            var names = new System.Text.StringBuilder();
            foreach (Transform node in weaponRoot.GetComponentsInChildren<Transform>(true))
            {
                string lower = node.name.ToLowerInvariant();
                if (lower.Contains("pin") || lower.Contains("ring") || lower.Contains("cap"))
                    names.Append(names.Length > 0 ? ", " : "").Append(node.name);
            }
            return names.Length > 0 ? names.ToString() : "none";
        }
    }
}
