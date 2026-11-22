using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // A held loot item's surface as the closing fingers see it (HandsInteractionController.Grasp), in the item's own frame (its root's
    // position and rotation, unscaled). Every visible part starts as its mesh-bounds box, then gets a convex hull from the mesh's vertices:
    // read on the CPU when the mesh is readable, else copied back from the GPU (most of Tarkov's item meshes aren't readable). Each hull
    // replaces its part's box as it arrives (Version goes up and the grip re-solves). Hulls are convex MeshColliders in their own physics
    // scene (LocalPhysicsMode), so nothing in the game can hit them; Physics.ClosestPoint measures them at a given pose.
    internal sealed class GraspShape
    {
        public static int maxParts = 10;                 // the biggest parts by volume; the rest (screws, decals) don't shape a grip
        // A hull is each part's furthest vertex in this many directions spread over a sphere. A hull of n points has at most 2n-4 faces,
        // and PhysX cooks at most 255 polygons (more gives its "partial hull" warning on every grab), so 100 gives at most 196.
        // The directions are ~20 deg apart: half a millimetre off on a 6 cm bottle.
        [Tunable("Loot & hands", TunableKind.Tune, "Points per item shape (keep it 127 or under: PhysX's face limit).")]
        public static int hullDirections = 100;
        public static int preThinPoints = 4000;          // points the grid thinning cuts down to first, so the direction search stays cheap
        public static float hullCellSize = 0.003f;       // m: the thinning grid's first cell
        public static float minHullThickness = 0.002f;   // m: a part flatter than this keeps its box (a flat point set can't be cooked)

        private sealed class Part
        {
            public Vector3 center;                       // box, item frame
            public Quaternion rotation = Quaternion.identity;
            public Vector3 halfSize;
            public float reach;                          // bounding radius about the center
            public float volume;
            public Matrix4x4 meshToItem;
            public MeshCollider hull;                    // null until built, or never (skinned, flat, failed)
            public Mesh hullMesh;
        }

        private readonly List<Part> parts = new List<Part>();
        private readonly string itemName;
        private GameObject holder;
        private bool disposed;
        private int hullsFromCpu, hullsFromGpu, hullsFailed, hullsSkipped;
        private Vector3 boundsMin, boundsMax;            // item frame, all the parts' boxes

        public int Version { get; private set; }
        public int PartCount => parts.Count;

        private static Scene sandbox;
        private static bool? hullQueriesWork;            // Physics.ClosestPoint on a sandbox hull, checked on the first one built

        private GraspShape(string itemName)
        {
            this.itemName = itemName;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // A held thing's shape, in its own frame: a loot item, a door's handle
        public static GraspShape Build(Component item)
        {
            GraspShape shape = new GraspShape(item.name);
            Transform root = item.transform;
            Matrix4x4 worldToItem = Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;
            Quaternion worldToItemRotation = Quaternion.Inverse(root.rotation);
            var candidates = new List<(Part part, Mesh mesh)>();
            foreach (Renderer renderer in VisibleRenderers(item))
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    // Its mesh is in bind pose, not where the bones put it: use the world box it's drawn in, and no hull
                    Bounds drawn = skinned.bounds;
                    Part skinnedPart = new Part
                    {
                        center = worldToItem.MultiplyPoint3x4(drawn.center),
                        rotation = worldToItemRotation,
                        halfSize = drawn.extents,
                    };
                    candidates.Add((skinnedPart, null));
                    continue;
                }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0)
                    continue;
                Matrix4x4 meshToItem = worldToItem * renderer.transform.localToWorldMatrix;
                Bounds bounds = mesh.bounds;
                Vector3 directionX = meshToItem.MultiplyVector(Vector3.right);
                Vector3 directionY = meshToItem.MultiplyVector(Vector3.up);
                Vector3 directionZ = meshToItem.MultiplyVector(Vector3.forward);
                Part part = new Part
                {
                    center = meshToItem.MultiplyPoint3x4(bounds.center),
                    halfSize = new Vector3(bounds.extents.x * directionX.magnitude, bounds.extents.y * directionY.magnitude, bounds.extents.z * directionZ.magnitude),
                    meshToItem = meshToItem,
                };
                if (directionZ.sqrMagnitude > 1e-12f && directionY.sqrMagnitude > 1e-12f)
                    part.rotation = Quaternion.LookRotation(directionZ, directionY);
                candidates.Add((part, mesh));
            }
            foreach (var (part, _) in candidates)
            {
                part.reach = part.halfSize.magnitude;
                part.volume = Mathf.Max(part.halfSize.x, 0.001f) * Mathf.Max(part.halfSize.y, 0.001f) * Mathf.Max(part.halfSize.z, 0.001f);
            }
            candidates.Sort((first, second) => second.part.volume.CompareTo(first.part.volume));
            if (candidates.Count > maxParts)
                candidates.RemoveRange(maxParts, candidates.Count - maxParts);

            shape.boundsMin = Vector3.positiveInfinity;
            shape.boundsMax = Vector3.negativeInfinity;
            foreach ((Part part, Mesh mesh) in candidates)
            {
                shape.parts.Add(part);
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = Corner(part, corner);
                    shape.boundsMin = Vector3.Min(shape.boundsMin, point);
                    shape.boundsMax = Vector3.Max(shape.boundsMax, point);
                }
            }
            foreach ((Part part, Mesh mesh) in candidates)
            {
                if (mesh == null || Mathf.Min(part.halfSize.x, Mathf.Min(part.halfSize.y, part.halfSize.z)) * 2f < minHullThickness)
                    shape.hullsSkipped++;
                else if (mesh.isReadable)
                    shape.BuildHull(part, mesh.vertices, fromGpu: false);
                else
                    shape.RequestGpuVertices(part, mesh);
            }
            return shape;
        }

        // What's drawn of the item: its renderers, less a LOD group's lower LODs (Unity culls those without disabling them)
        private static List<Renderer> VisibleRenderers(Component item)
        {
            var lowerLods = new HashSet<Renderer>();
            foreach (LODGroup group in item.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = group.GetLODs();
                var firstLod = lods.Length > 0 && lods[0].renderers != null ? new HashSet<Renderer>(lods[0].renderers) : new HashSet<Renderer>();
                for (int level = 1; level < lods.Length; level++)
                {
                    if (lods[level].renderers == null)
                        continue;
                    foreach (Renderer renderer in lods[level].renderers)
                    {
                        if (renderer != null && !firstLod.Contains(renderer))
                            lowerLods.Add(renderer);
                    }
                }
            }
            var visible = new List<Renderer>();
            foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || !renderer.enabled || lowerLods.Contains(renderer))
                    continue;
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                    visible.Add(renderer);
            }
            return visible;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Hulls

        private void BuildHull(Part part, Vector3[] meshVertices, bool fromGpu)
        {
            if (disposed)
                return;
            List<Vector3> points = HullPoints(part.meshToItem, meshVertices);
            if (points == null || points.Count < 8)
            {
                hullsFailed++;
                return;
            }
            try
            {
                EnsureHolder();
                Mesh hullMesh = new Mesh { name = "SPTVR grasp hull" };
                hullMesh.SetVertices(points);
                // Convex cooking reads only the vertices; a triangle list just has to be there and valid
                int[] triangles = new int[(points.Count + 2) / 3 * 3];
                for (int index = 0; index < triangles.Length; index++)
                    triangles[index] = Mathf.Min(index, points.Count - 1);
                hullMesh.triangles = triangles;
                MeshCollider collider = holder.AddComponent<MeshCollider>();
                collider.convex = true;
                collider.sharedMesh = hullMesh;
                if (collider.sharedMesh == null || !CheckHullQueries(collider))
                {
                    UnityEngine.Object.Destroy(collider);
                    UnityEngine.Object.Destroy(hullMesh);
                    hullsFailed++;
                    return;
                }
                part.hull = collider;
                part.hullMesh = hullMesh;
                if (fromGpu) hullsFromGpu++;
                else hullsFromCpu++;
                Version++;
            }
            catch (Exception exception)
            {
                hullsFailed++;
                if (HandsInteractionController.graspDebug)
                    Plugin.MyLog.LogWarning($"[Grasp] {itemName}: hull failed: {exception.Message}");
            }
        }

        // The mesh's vertices in the item's frame, cut down to what a hull needs. First they're thinned on a grid (one vertex per cell, the
        // first one in it; the cell size doubles until preThinPoints fit). Then only the vertex furthest along each of hullDirections
        // directions over a sphere is kept: all on the hull, spread around it, and few enough for PhysX's 255-polygon limit.
        private static List<Vector3> HullPoints(Matrix4x4 meshToItem, Vector3[] meshVertices)
        {
            if (meshVertices == null || meshVertices.Length == 0)
                return null;
            int step = Mathf.Max(1, meshVertices.Length / 40000);
            var all = new List<Vector3>(meshVertices.Length / step + 1);
            for (int index = 0; index < meshVertices.Length; index += step)
                all.Add(meshToItem.MultiplyPoint3x4(meshVertices[index]));
            List<Vector3> kept = all;
            if (all.Count > preThinPoints)
            {
                var cells = new HashSet<Vector3Int>();
                kept = new List<Vector3>();
                for (float cell = hullCellSize; cell < 1f; cell *= 2f)
                {
                    cells.Clear();
                    kept.Clear();
                    foreach (Vector3 point in all)
                    {
                        if (cells.Add(new Vector3Int(Mathf.FloorToInt(point.x / cell), Mathf.FloorToInt(point.y / cell), Mathf.FloorToInt(point.z / cell))))
                            kept.Add(point);
                    }
                    if (kept.Count <= preThinPoints)
                        break;
                }
            }
            var furthest = new HashSet<int>();
            foreach (Vector3 direction in SphereDirections(Mathf.Clamp(hullDirections, 8, 127)))
            {
                int best = 0;
                float bestReach = float.MinValue;
                for (int index = 0; index < kept.Count; index++)
                {
                    float reach = Vector3.Dot(kept[index], direction);
                    if (reach > bestReach)
                    {
                        bestReach = reach;
                        best = index;
                    }
                }
                furthest.Add(best);
            }
            var points = new List<Vector3>(furthest.Count);
            foreach (int index in furthest)
                points.Add(kept[index]);
            return points;
        }

        // Evenly spread unit directions (a Fibonacci sphere)
        private static Vector3[] sphereDirections;
        private static Vector3[] SphereDirections(int count)
        {
            if (sphereDirections != null && sphereDirections.Length == count)
                return sphereDirections;
            var directions = new Vector3[count];
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int index = 0; index < count; index++)
            {
                float height = 1f - 2f * (index + 0.5f) / count;
                float ring = Mathf.Sqrt(1f - height * height);
                float angle = goldenAngle * index;
                directions[index] = new Vector3(Mathf.Cos(angle) * ring, height, Mathf.Sin(angle) * ring);
            }
            return sphereDirections = directions;
        }

        private void EnsureHolder()
        {
            if (holder != null)
                return;
            if (!sandbox.IsValid() || !sandbox.isLoaded)
                sandbox = SceneManager.CreateScene("SPTVR_GraspShapes", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            holder = new GameObject("SPTVR grasp shape " + itemName);
            // Into the sandbox before any collider goes on it
            SceneManager.MoveGameObjectToScene(holder, sandbox);
        }

        // Physics.ClosestPoint on a collider in its own physics scene, at a pose we pass, should work (it's a geometry query on the
        // shape). It's checked once: a point 10 m off has to come back to the hull's surface.
        private static bool CheckHullQueries(MeshCollider collider)
        {
            if (hullQueriesWork.HasValue)
                return hullQueriesWork.Value;
            Vector3 far = collider.sharedMesh.bounds.center + Vector3.right * 10f;
            Vector3 closest = Physics.ClosestPoint(far, collider, Vector3.zero, Quaternion.identity);
            hullQueriesWork = (closest - far).sqrMagnitude > 1f && closest.x < far.x;
            if (hullQueriesWork != true)
                Plugin.MyLog.LogWarning("[Grasp] closest-point queries on the hull colliders don't work here: grips use the items' boxes only");
            return hullQueriesWork.Value;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // A non-readable mesh's vertices back from its GPU buffer

        private void RequestGpuVertices(Part part, Mesh mesh)
        {
            if (!SystemInfo.supportsAsyncGPUReadback || !mesh.HasVertexAttribute(VertexAttribute.Position))
            {
                hullsSkipped++;
                return;
            }
            VertexAttributeFormat format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            if (mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3 || (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16))
            {
                hullsSkipped++;
                return;
            }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            int stride = mesh.GetVertexBufferStride(stream);
            int vertexCount = mesh.vertexCount;
            GraphicsBuffer buffer;
            try
            {
                buffer = mesh.GetVertexBuffer(stream);
            }
            catch (Exception exception)
            {
                hullsFailed++;
                if (HandsInteractionController.graspDebug)
                    Plugin.MyLog.LogWarning($"[Grasp] {itemName}: no vertex buffer: {exception.Message}");
                return;
            }
            if (buffer == null)
            {
                hullsFailed++;
                return;
            }
            try
            {
                AsyncGPUReadback.Request(buffer, request =>
                {
                    // The readback owned the buffer until now
                    buffer.Dispose();
                    if (disposed)
                        return;
                    if (request.hasError)
                    {
                        hullsFailed++;
                        return;
                    }
                    try
                    {
                        Vector3[] vertices = ReadPositions(request.GetData<byte>().ToArray(), vertexCount, offset, stride, format);
                        if (vertices == null)
                            hullsFailed++;
                        else
                            BuildHull(part, vertices, fromGpu: true);
                    }
                    catch (Exception exception)
                    {
                        hullsFailed++;
                        if (HandsInteractionController.graspDebug)
                            Plugin.MyLog.LogWarning($"[Grasp] {itemName}: readback unreadable: {exception.Message}");
                    }
                });
            }
            catch (Exception exception)
            {
                buffer.Dispose();
                hullsFailed++;
                if (HandsInteractionController.graspDebug)
                    Plugin.MyLog.LogWarning($"[Grasp] {itemName}: readback refused: {exception.Message}");
            }
        }

        private static Vector3[] ReadPositions(byte[] bytes, int vertexCount, int offset, int stride, VertexAttributeFormat format)
        {
            int size = format == VertexAttributeFormat.Float32 ? 12 : 6;
            if (vertexCount <= 0 || stride < size || bytes.Length < (vertexCount - 1) * stride + offset + size)
                return null;
            var positions = new Vector3[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int start = vertex * stride + offset;
                positions[vertex] = format == VertexAttributeFormat.Float32
                    ? new Vector3(BitConverter.ToSingle(bytes, start), BitConverter.ToSingle(bytes, start + 4), BitConverter.ToSingle(bytes, start + 8))
                    : new Vector3(HalfToFloat(BitConverter.ToUInt16(bytes, start)), HalfToFloat(BitConverter.ToUInt16(bytes, start + 2)), HalfToFloat(BitConverter.ToUInt16(bytes, start + 4)));
            }
            return positions;
        }

        private static float HalfToFloat(ushort half)
        {
            int exponent = (half >> 10) & 0x1f;
            int mantissa = half & 0x3ff;
            float value = exponent == 0 ? mantissa * (1f / 16777216f)
                : exponent == 31 ? float.PositiveInfinity
                : (1f + mantissa / 1024f) * Mathf.Pow(2f, exponent - 15);
            return (half & 0x8000) != 0 ? -value : value;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Queries: world points against the item at the given pose

        // A finger's sphere here touches the item or is inside it
        public bool Touches(Vector3 worldPoint, float radius, Vector3 itemPosition, Quaternion itemRotation)
        {
            Vector3 inItem = Quaternion.Inverse(itemRotation) * (worldPoint - itemPosition);
            foreach (Part part in parts)
            {
                float reach = part.reach + radius;
                if ((inItem - part.center).sqrMagnitude > reach * reach)
                    continue;
                float distance;
                if (part.hull != null && hullQueriesWork == true)
                    distance = (Physics.ClosestPoint(worldPoint, part.hull, itemPosition, itemRotation) - worldPoint).magnitude;
                else
                    distance = (BoxClosest(part, inItem, out _) - inItem).magnitude;
                if (distance < radius)
                    return true;
            }
            return false;
        }

        // The item's surface nearest a world point, and the way out of the item there. From inside, it's the nearest way out of the part
        // the point is in, using that part's box (a hull gives no direction from inside).
        public bool NearestSurface(Vector3 worldPoint, Vector3 itemPosition, Quaternion itemRotation, out Vector3 surface, out Vector3 outward, out bool inside)
        {
            surface = worldPoint;
            outward = Vector3.up;
            inside = false;
            Vector3 inItem = Quaternion.Inverse(itemRotation) * (worldPoint - itemPosition);
            float nearestOutside = float.MaxValue, shallowestInside = float.MaxValue;
            Vector3 outsideSurface = Vector3.zero, outsideOutward = Vector3.up, insideSurface = Vector3.zero, insideOutward = Vector3.up;
            bool foundOutside = false, foundInside = false;
            foreach (Part part in parts)
            {
                bool inPart = false;
                Vector3 partSurface = Vector3.zero;     // item frame
                if (part.hull != null && hullQueriesWork == true)
                {
                    Vector3 closestWorld = Physics.ClosestPoint(worldPoint, part.hull, itemPosition, itemRotation);
                    if ((closestWorld - worldPoint).sqrMagnitude > 1e-10f)
                        partSurface = Quaternion.Inverse(itemRotation) * (closestWorld - itemPosition);
                    else
                        inPart = true;
                }
                else
                {
                    partSurface = BoxClosest(part, inItem, out inPart);
                }
                if (inPart)
                {
                    Vector3 exit = BoxExit(part, inItem, out float depth);
                    if (depth < shallowestInside)
                    {
                        shallowestInside = depth;
                        insideSurface = exit;
                        insideOutward = (exit - inItem).sqrMagnitude > 1e-12f ? (exit - inItem).normalized : part.rotation * Vector3.up;
                        foundInside = true;
                    }
                    continue;
                }
                float distance = (partSurface - inItem).magnitude;
                if (distance < nearestOutside)
                {
                    nearestOutside = distance;
                    outsideSurface = partSurface;
                    outsideOutward = (inItem - partSurface) / Mathf.Max(distance, 1e-6f);
                    foundOutside = true;
                }
            }
            if (foundInside)
            {
                inside = true;
                surface = itemPosition + itemRotation * insideSurface;
                outward = itemRotation * insideOutward;
                return true;
            }
            if (!foundOutside)
                return false;
            surface = itemPosition + itemRotation * outsideSurface;
            outward = itemRotation * outsideOutward;
            return true;
        }

        // Small enough for a pinch: checks its longest and middle sides, from the box around all the parts
        public bool FitsAPinch(float maxLength, float maxWidth)
        {
            if (parts.Count == 0)
                return false;
            Vector3 size = boundsMax - boundsMin;
            float[] sides = { size.x, size.y, size.z };
            Array.Sort(sides);
            return sides[2] <= maxLength && sides[1] <= maxWidth;
        }

        public string Describe()
        {
            Vector3 size = (boundsMax - boundsMin) * 100f;
            return $"{parts.Count} part(s), {size.x:0.0}x{size.y:0.0}x{size.z:0.0} cm, hulls: {hullsFromCpu} readable + {hullsFromGpu} from the GPU, {hullsFailed} failed, {hullsSkipped} boxes kept";
        }

        public void Dispose()
        {
            disposed = true;
            foreach (Part part in parts)
            {
                if (part.hullMesh != null)
                    UnityEngine.Object.Destroy(part.hullMesh);
                part.hull = null;
            }
            if (holder != null)
                UnityEngine.Object.Destroy(holder);
            holder = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static Vector3 Corner(Part part, int corner)
        {
            Vector3 local = new Vector3((corner & 1) == 0 ? -part.halfSize.x : part.halfSize.x, (corner & 2) == 0 ? -part.halfSize.y : part.halfSize.y,
                (corner & 4) == 0 ? -part.halfSize.z : part.halfSize.z);
            return part.center + part.rotation * local;
        }

        // The box's point nearest a point (item frame); the point itself from inside
        private static Vector3 BoxClosest(Part part, Vector3 inItem, out bool inside)
        {
            Vector3 local = Quaternion.Inverse(part.rotation) * (inItem - part.center);
            Vector3 clamped = new Vector3(Mathf.Clamp(local.x, -part.halfSize.x, part.halfSize.x), Mathf.Clamp(local.y, -part.halfSize.y, part.halfSize.y),
                Mathf.Clamp(local.z, -part.halfSize.z, part.halfSize.z));
            inside = clamped == local;
            return part.center + part.rotation * clamped;
        }

        // From inside the box: its face nearest the point (the least-penetrated axis), and how deep the point is
        private static Vector3 BoxExit(Part part, Vector3 inItem, out float depth)
        {
            Vector3 local = Quaternion.Inverse(part.rotation) * (inItem - part.center);
            float throughX = part.halfSize.x - Mathf.Abs(local.x);
            float throughY = part.halfSize.y - Mathf.Abs(local.y);
            float throughZ = part.halfSize.z - Mathf.Abs(local.z);
            Vector3 exit = local;
            if (throughX <= throughY && throughX <= throughZ)
            {
                exit.x = local.x >= 0f ? part.halfSize.x : -part.halfSize.x;
                depth = throughX;
            }
            else if (throughY <= throughZ)
            {
                exit.y = local.y >= 0f ? part.halfSize.y : -part.halfSize.y;
                depth = throughY;
            }
            else
            {
                exit.z = local.z >= 0f ? part.halfSize.z : -part.halfSize.z;
                depth = throughZ;
            }
            return part.center + part.rotation * exit;
        }
    }
}
