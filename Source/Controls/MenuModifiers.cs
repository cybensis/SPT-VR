using UnityEngine;

namespace TarkovVR.Source.Controls
{
    // The modifier keys in menus, held by the hand that isn't pointing: its trigger = Ctrl (click = quick-move, drag = split a
    // stack), its lower face button = Alt (click = equip). Not the grips: a held gun is held by its grip, so a grip as a modifier
    // turned every click into a Ctrl-click.
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

        // A new press of the Alt button starts unused
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

        // The pointer clicked: with Alt held, that press of the button was a modifier
        internal static void NoteClick()
        {
            Tick();
            if (altWasDown)
                altUsedForClick = true;
        }

        // The button let go of was the Alt button and a click used it: its release mustn't also do the button's own job
        internal static bool ReleaseWasModifier(VRBoolean button)
        {
            Tick();
            return ReferenceEquals(button, AltButton) && altUsedForClick;
        }
    }
}
