using UnityEngine;
using UnityEngine.EventSystems;

namespace TarkovVR.Source.UI
{
    // A BaseInput override that makes the EventSystem's input module believe there is no mouse at all.
    //
    // EFT's UI runs on Unity's StandaloneInputModule, which fires pointer events on whatever sits under the desktop cursor;
    // in VR the laser drives the UI (VRUIInteracter dispatches its own events through ExecuteEvents), and those mouse
    // highlights fight it. StandaloneInputModule.Process() only calls ProcessMouseEvent() when mousePresent is true, so
    // false here disables all mouse handling. Keyboard, IME and navigation fall through to BaseInput's defaults.
    //
    // Assigned to the input module via MouseInputBlockPatches (currentInputModule.inputOverride).
    internal class VRNoMouseInput : BaseInput
    {
        public override bool mousePresent => false;
        public override Vector2 mousePosition => new Vector2(-1f, -1f);
        public override Vector2 mouseScrollDelta => Vector2.zero;
        public override bool GetMouseButton(int button) => false;
        public override bool GetMouseButtonDown(int button) => false;
        public override bool GetMouseButtonUp(int button) => false;
    }
}
