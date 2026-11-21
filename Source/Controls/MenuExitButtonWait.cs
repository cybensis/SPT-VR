using UnityEngine;

namespace TarkovVR.Source.Controls
{
    // B (Y in left-handed mode) is Escape in a menu and a gun button outside one: the magazine release with Manual Reload on, or Tarkov's
    // reload and the double-tap drop with it off. Players press it quickly to back out of menus, and the presses after the one that closed
    // the menu would reach the gun. So for a short time after a menu closes the button does nothing to the gun. Each press in that time
    // restarts the wait, and a press still held when the wait ends only counts once it is released.
    public static class MenuExitButtonWait
    {
        public static float waitTime = 0.6f;    // s after a menu closes, or after the last press while waiting

        private static int evaluatedFrame = -1;
        private static bool blocks;
        private static float blockedUntil = -10f;
        private static bool downWhileBlocked;

        // True while the button must not act on the gun. Computed once per frame. VRInputManager.UpdateCommands calls Tick every frame,
        // menus included, so the frame a menu closes is never missed.
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

            // Either upper button counts: the off hand's is also Escape in the game
            bool buttonDown = Handedness.GunHandUpperButton.state || Handedness.OffHandUpperButton.state;
            bool pressedNow = buttonDown && !downWhileBlocked;
            if (VRGlobals.menuOpen || (Time.time < blockedUntil && pressedNow))
                blockedUntil = Time.time + waitTime;
            blocks = Time.time < blockedUntil || (downWhileBlocked && buttonDown);
            downWhileBlocked = blocks && buttonDown;
        }
    }
}
