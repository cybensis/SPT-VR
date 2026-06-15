using UnityEngine;
using UnityEngine.EventSystems;

namespace TarkovVR.Source.UI
{
    // A BaseInput override that makes the EventSystem's input module believe there is no mouse at all.
    //
    // EFT's UI runs on Unity's stock StandaloneInputModule. Every frame it raycasts whatever UI element
    // sits under the real desktop cursor and fires pointer enter/exit/click on it - that's what lights up
    // buttons/slots when the mouse drifts over the game screen. In VR we drive the UI ourselves with the
    // laser (VRUIInteracter dispatches its own pointer events through ExecuteEvents and never goes through
    // the input module), so those mouse-driven highlights are pure noise and fight the laser cursor.
    //
    // StandaloneInputModule.Process() only calls ProcessMouseEvent() when input.mousePresent is true, so
    // reporting mousePresent=false here short-circuits ALL mouse handling - no UI element can ever be
    // entered/hovered/clicked by the desktop mouse. The mouse position / buttons / scroll are also forced
    // to a dead state as belt-and-suspenders. Everything else (keyboard, IME composition, navigation axes)
    // falls through to BaseInput's defaults, so typing into focused fields and keyboard nav keep working.
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
