using UnityEngine;

namespace TarkovVR.Source.Controls
{
    // B (Y left-handed) is Escape in a menu and the gun's out of one: the mag release with Manual Reload on, Tarkov's reload and the
    // double tap's drop with it off. Backing out of menus it gets pressed fast, and the presses after the one that closed the menu
    // would go to the gun. So for a moment after a menu closes the button does nothing to the gun: each press in that moment starts
    // it over, and one still held when it ends counts only once it's let go.
    public static class MenuExitButtonWait
    {
        public static float waitTime = 0.6f;    // s after a menu closes, or after the last press while waiting

        private static int evaluatedFrame = -1;
        private static bool blocks;
        private static float blockedUntil = -10f;
        private static bool downWhileBlocked;

        // True while the button must not act on the gun. Worked out once a frame; VRInputManager.UpdateCommands calls Tick every frame,
        // menus included, so the moment a menu closes is never missed.
        public static bool Blocks
        {
            get
            {
                Tick();
                return blocks;
            }
        }

        public static void Tick()
        {
            if (evaluatedFrame == Time.frameCount)
                return;
            evaluatedFrame = Time.frameCount;

            // Either upper button: the off hand's is Escape in the game too
            bool buttonDown = Handedness.GunHandUpperButton.state || Handedness.OffHandUpperButton.state;
            bool pressedNow = buttonDown && !downWhileBlocked;
            if (VRGlobals.menuOpen || (Time.time < blockedUntil && pressedNow))
                blockedUntil = Time.time + waitTime;
            blocks = Time.time < blockedUntil || (downWhileBlocked && buttonDown);
            downWhileBlocked = blocks && buttonDown;
        }
    }
}
