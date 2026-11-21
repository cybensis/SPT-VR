using UnityEngine;

namespace TarkovVR.Source.Controls
{
    // Menu modifier keys, held on the hand that isn't pointing. Its trigger is Ctrl (click = quick-move, drag = split a stack) and
    // its lower face button is Alt (click = equip). The grips aren't used: a gun is held by its grip, so a grip modifier would make
    // every click a Ctrl-click.
    internal static class MenuModifiers
    {
        public static float ctrlTriggerPress = 0.5f;
        public static float ctrlTriggerRelease = 0.3f;

        private static bool ctrlHeld;
        private static int ctrlFrame = -1;
        private static int tickedFrame = -1;
        private static bool altWasDown;
        private static bool altUsedForClick;

        private static bool ModifierHandIsLeft => !Handedness.PointerHandIsLeft;

        internal static VRBoolean AltButton => ModifierHandIsLeft ? VRInput.ButtonX : VRInput.ButtonA;

        internal static bool CtrlHeld
        {
            get
            {
                if (ctrlFrame != Time.frameCount)
                {
                    ctrlFrame = Time.frameCount;
                    float axis = (ModifierHandIsLeft ? VRInput.LeftTrigger : VRInput.RightTrigger).axis;
                    ctrlHeld = ctrlHeld ? axis > ctrlTriggerRelease : axis >= ctrlTriggerPress;
                }
                return ctrlHeld;
            }
        }

        internal static bool AltHeld => AltButton.state;

        // A new press of the Alt button starts as not used for a click
        internal static void Tick()
        {
            if (tickedFrame == Time.frameCount)
                return;
            tickedFrame = Time.frameCount;
            bool altDown = AltButton.state;
            if (altDown && !altWasDown)
                altUsedForClick = false;
            altWasDown = altDown;
        }

        // Called when the pointer clicks. If Alt is held, that press of the button counts as a modifier.
        internal static void NoteClick()
        {
            Tick();
            if (altWasDown)
                altUsedForClick = true;
        }

        // True when the released button is the Alt button and a click used it. Its release must not also do the button's normal job.
        internal static bool ReleaseWasModifier(VRBoolean button)
        {
            Tick();
            return ReferenceEquals(button, AltButton) && altUsedForClick;
        }
    }
}
