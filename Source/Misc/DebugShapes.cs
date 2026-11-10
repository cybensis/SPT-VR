using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Misc
{
    // See-through debug shapes (the debugZones / debugViz spheres and capsules): made on first use, kept in the caller's field,
    // recoloured every call.
    internal static class DebugShapes
    {
        internal static void Sphere(ref GameObject sphere, string name, bool show, Vector3 position, float radius, Color color)
        {
            if (!show)
            {
                if (sphere != null)
                    sphere.SetActive(false);
                return;
            }
            if (sphere == null)
                sphere = Create(PrimitiveType.Sphere, name);
            sphere.SetActive(true);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * (radius * 2f);
            sphere.GetComponent<Renderer>().material.color = color;
        }

        internal static void Capsule(ref GameObject capsule, string name, Vector3 start, Vector3 end, float radius, Color color)
        {
            if (capsule == null)
                capsule = Create(PrimitiveType.Capsule, name);
            capsule.SetActive(true);
            Vector3 segment = end - start;
            capsule.transform.position = (start + end) * 0.5f;
            if (segment.sqrMagnitude > 1e-8f)
                capsule.transform.rotation = Quaternion.FromToRotation(Vector3.up, segment);
            // Unity's capsule is 1 wide and 2 tall
            capsule.transform.localScale = new Vector3(radius * 2f, (segment.magnitude + radius * 2f) * 0.5f, radius * 2f);
            capsule.GetComponent<Renderer>().material.color = color;
        }

        private static GameObject Create(PrimitiveType type, string name)
        {
            GameObject shape = GameObject.CreatePrimitive(type);
            shape.name = name;
            Object.Destroy(shape.GetComponent<Collider>());
            Renderer shapeRenderer = shape.GetComponent<Renderer>();
            shapeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            shapeRenderer.receiveShadows = false;
            // Sprites/Default takes alpha
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Standard");
            shapeRenderer.material = new Material(shader);
            return shape;
        }
    }
}
