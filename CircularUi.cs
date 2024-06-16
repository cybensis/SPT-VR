using System.Text;
using TarkovVR;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static System.Net.Mime.MediaTypeNames;

public class EnsureCanvasOnTop : MonoBehaviour
{
    public int sortingOrder = 1000;
    public string sortingLayerName = "UI";
    private static SteamVR_Overlay overlay;
    private static uint overlayhandle;
    private string text = "";
    void Start()
    {
        if (!overlay)
            overlay = new SteamVR_Overlay();
        SteamVR.instance.overlay.ShowKeyboard(
     (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
     (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
     (uint)EKeyboardFlags.KeyboardFlag_Modal, "Description", 256, "", 0);


        SteamVR_Utils.Event.Listen("KeyboardCharInput", OnKeyboardCharInput);
        SteamVR_Utils.Event.Listen("KeyboardClosed", OnKeyboardClosed);
        //var t = SteamVR.instance.overlay.GetTransformForOverlayCoordinates(overlayhandle);
        //Vector3 position = new Vector3(0, 1, 1); // Adjust the position as needed
        //Quaternion rotation = Quaternion.Euler(0, 180, 0); // Adjust the rotation as needed
        //SetKeyboardTransformAbsolute(position, rotation);
        //StringBuilder stringBuilder = new StringBuilder(256);
        //SteamVR.instance.overlay.GetKeyboardText(stringBuilder, 256);
        //value = stringBuilder.ToString();
        var keyboardCharInputAction =
    SteamVR_Events.SystemAction(EVREventType.VREvent_KeyboardCharInput, ev => {
            StringBuilder stringBuilder = new StringBuilder(256);
            SteamVR.instance.overlay.GetKeyboardText(stringBuilder, 256);
            string value = stringBuilder.ToString();
            Plugin.MyLog.LogWarning("String  " + value);
        });
        keyboardCharInputAction.enabled = true;
        var keyboardClosedAction =
            SteamVR_Events.SystemAction(EVREventType.VREvent_KeyboardClosed, ev => Plugin.MyLog.LogError("Closed"));
        keyboardClosedAction.enabled = true;
        var keyboardDoneAction =
            SteamVR_Events.SystemAction(EVREventType.VREvent_KeyboardDone, ev => Plugin.MyLog.LogError("Done"));
        keyboardDoneAction.enabled = true;

    }

    //private void SetKeyboardTransformAbsolute(Vector3 position, Quaternion rotation)
    //{
    //    // Convert position and rotation to SteamVR's matrix format
    //    var transform = new SteamVR_Utils.RigidTransform(position, rotation);
    //    SteamVR.instance.overlay.SetKeyboardTransformAbsolute(ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform);
    //}

    //private void OnKeyboardShown(bool success)
    //{
    //    if (success)
    //    {
    //        SteamVR_Overlay.instance.keyboardCharInput += OnKeyboardCharInput;
    //        SteamVR_Overlay.instance.keyboardClosed += OnKeyboardClosed;
    //    }
    //}

    private void OnKeyboardCharInput(object[] args)
    {
        VREvent_t evt = (VREvent_t)args[0];

        if (evt.data.keyboard.cNewInput == "\b")
        { // User hit backspace
            if (text.Length > 0)
            {
                text = text.Substring(0, text.Length - 1);
            }
        }
        else if (evt.data.keyboard.cNewInput == "\x1b")
        {
            // Close the keyboard
            SteamVR.instance.overlay.HideKeyboard();
        }
        else
        {
            text += evt.data.keyboard.cNewInput;
        }
        Plugin.MyLog.LogWarning(text);
    }

    private void OnKeyboardClosed(object[] args)
    {
        
    }

    private void ProcessInput(string input)
    {
        // Implement your input processing logic here
        Debug.Log("Processing Input: " + input);
        // Example: Update a UI text element or send the input to a game system
    }

}
