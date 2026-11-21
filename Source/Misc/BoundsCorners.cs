using UnityEngine;

namespace TarkovVR.Source.Misc
{
    internal static class BoundsCorners
    {
        // One of a bounding box's eight corners, corner = 0..7 (bit 0: +x, bit 1: +y, bit 2: +z). Measuring a mesh by its
        // mesh-local bounds corners (each transformed to world) keeps the measurement independent of how the object is turned.
        // Renderer.bounds is a world-aligned box that grows when the object is turned diagonally.
        internal static Vector3 Corner(Bounds bounds, int corner)
        {
            Vector3 sign = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
            return bounds.center + Vector3.Scale(bounds.extents, sign);
        }
    }
}
