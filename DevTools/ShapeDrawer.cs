using TarkovVR.Source.Dev;
using UnityEngine;
using UnityEngine.Rendering;

namespace SptVrDevTools
{
    // Draws SPT-VR's DebugDraw shapes for the frame they're asked for (Graphics.DrawMesh: nothing to keep or hide). The material is
    // the one the loot markers use (Hidden/Internal-Colored: alpha blended, both faces, no depth write); onTop draws over everything.
    internal sealed class ShapeDrawer : IDebugDrawer
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private bool ready;
        private Mesh sphere, capsule, cube;
        private Material seeThrough, overEverything;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public void Sphere(Vector3 position, float radius, Color color, bool onTop)
        {
            if (!Ready())
                return;
            Draw(sphere, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * (radius * 2f)), color, onTop);
        }

        public void Capsule(Vector3 start, Vector3 end, float radius, Color color, bool onTop)
        {
            if (!Ready())
                return;
            Vector3 segment = end - start;
            Quaternion rotation = segment.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, segment) : Quaternion.identity;
            // Unity's capsule is 1 wide and 2 tall
            Vector3 scale = new Vector3(radius * 2f, (segment.magnitude + radius * 2f) * 0.5f, radius * 2f);
            Draw(capsule, Matrix4x4.TRS((start + end) * 0.5f, rotation, scale), color, onTop);
        }

        public void Line(Vector3 start, Vector3 end, float width, Color color, bool onTop)
        {
            if (!Ready())
                return;
            Vector3 segment = end - start;
            float length = segment.magnitude;
            if (length < 1e-5f)
                return;
            Draw(cube, Matrix4x4.TRS((start + end) * 0.5f, Quaternion.LookRotation(segment), new Vector3(width, width, length)), color, onTop);
        }

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color, bool onTop)
        {
            block.SetColor(ColorId, color);
            Graphics.DrawMesh(mesh, matrix, onTop ? overEverything : seeThrough, 0, null, 0, block, ShadowCastingMode.Off, false);
        }

        private bool Ready()
        {
            if (ready)
                return sphere != null;
            ready = true;
            try
            {
                sphere = WhiteCopy(PrimitiveType.Sphere);
                capsule = WhiteCopy(PrimitiveType.Capsule);
                cube = WhiteCopy(PrimitiveType.Cube);
                Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                seeThrough = MakeMaterial(shader, onTop: false);
                overEverything = MakeMaterial(shader, onTop: true);
            }
            catch (System.Exception exception)
            {
                sphere = null;
                DevToolsPlugin.Log.LogWarning("[DevTools] debug shapes unavailable: " + exception.Message);
            }
            return sphere != null;
        }

        // Internal-Colored multiplies the vertex colour, and the primitives have none (black): a copy painted white
        private static Mesh WhiteCopy(PrimitiveType type)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            Mesh mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            Object.Destroy(primitive);
            Color[] colors = new Color[mesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = Color.white;
            mesh.colors = colors;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            return mesh;
        }

        private static Material MakeMaterial(Shader shader, bool onTop)
        {
            Material material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetInt("_ZTest", (int)(onTop ? CompareFunction.Always : CompareFunction.LessEqual));
            material.SetInt("_ZWrite", 0);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.renderQueue = onTop ? 5000 : 3000;
            return material;
        }
    }
}
