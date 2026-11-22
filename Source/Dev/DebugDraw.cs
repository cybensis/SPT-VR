using UnityEngine;

namespace TarkovVR.Source.Dev
{
    // Shapes for the mod's debug views (debugZones, debugViz ...). Immediate mode: call every frame the shape should show, there is
    // nothing to keep, hide or destroy. They draw only with SPT-VR-DevTools installed (it sets Drawer); without it each call is a null
    // check. See-through; onTop draws over everything.
    public static class DebugDraw
    {
        public static IDebugDrawer Drawer;

        public static void Sphere(Vector3 position, float radius, Color color, bool onTop = false) => Drawer?.Sphere(position, radius, color, onTop);

        public static void Capsule(Vector3 start, Vector3 end, float radius, Color color, bool onTop = false) => Drawer?.Capsule(start, end, radius, color, onTop);

        public static void Line(Vector3 start, Vector3 end, float width, Color color, bool onTop = false) => Drawer?.Line(start, end, width, color, onTop);
    }

    public interface IDebugDrawer
    {
        void Sphere(Vector3 position, float radius, Color color, bool onTop);
        void Capsule(Vector3 start, Vector3 end, float radius, Color color, bool onTop);
        void Line(Vector3 start, Vector3 end, float width, Color color, bool onTop);
    }
}
