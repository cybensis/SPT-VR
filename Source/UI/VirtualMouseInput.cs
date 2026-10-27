using UnityEngine;
using UnityEngine.EventSystems;

namespace TarkovVR.Source.UI
{
    // The UI's mouse while the virtual screen is on: position from the laser's hit on the panel, clicks from the
    // trigger and A, scrolling from the stick. Installed on the EventSystem by MouseInputBlockPatches.
    internal class VirtualMouseInput : BaseInput
    {
        public override bool mousePresent => true;
        public override Vector2 mousePosition => VirtualScreen.mousePosition;
        public override Vector2 mouseScrollDelta => VirtualScreen.scrollDelta;
        public override bool GetMouseButton(int button) => VirtualScreen.IsButtonHeld(button);
        public override bool GetMouseButtonDown(int button) => VirtualScreen.WasButtonPressed(button);
        public override bool GetMouseButtonUp(int button) => VirtualScreen.WasButtonReleased(button);
    }
}
