using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Linq;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Controls;
using Comfort.Common;
using EFT;
using EFT.UI;
using Valve.VR;

namespace TarkovVR.Source.UI
{
    public class VRKeyboardKey : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public string character;
        public string shiftCharacter;
        public KeyType type;
        public VRKeyboardController controller;

        private Image background;
        private Color normalColor = new Color(0.1f, 0.1f, 0.1f, 1f);
        private Color hoverColor = new Color(0.3f, 0.3f, 0.3f, 1f);
        private Color pressedColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        private bool isHovering = false;

        private BoxCollider boxCollider;
        private RectTransform rectTransform;
        private Vector3[] fourCornersArray = new Vector3[4];

        public enum KeyType { Character, Shift, Backspace, Enter, Space, Close, Clear }

        public void Init(string charKey, string shiftKey, KeyType kType, VRKeyboardController ctrl)
        {
            character = charKey;
            shiftCharacter = shiftKey;
            type = kType;
            controller = ctrl;
            background = GetComponent<Image>();
            if (background) background.color = normalColor;

            boxCollider = GetComponent<BoxCollider>();
            rectTransform = GetComponent<RectTransform>();
        }

        private void LateUpdate()
        {
            if (boxCollider != null && rectTransform != null)
            {
                // get rect coords after render to properly adhere to any transformations
                rectTransform.GetLocalCorners(fourCornersArray);

                float width = fourCornersArray[2].x - fourCornersArray[0].x;
                float height = fourCornersArray[1].y - fourCornersArray[0].y;

                Vector3 visualCenter = (fourCornersArray[0] + fourCornersArray[2]) * 0.5f;

                // Only resize when not resized yet, basically only do it once
                if (Mathf.Abs(boxCollider.size.x - width) > 0.1f || Mathf.Abs(boxCollider.size.y - height) > 0.1f)
                {
                    boxCollider.size = new Vector3(width, height, 0.001f);
                    boxCollider.center = new Vector3(visualCenter.x, visualCenter.y, 0f);
                }
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (background) background.color = pressedColor;
            controller.OnKeyPressed(this);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (background) background.color = isHovering ? hoverColor : normalColor;
            controller.OnKeyReleased(this);
        }

        public void OnPointerEnter(PointerEventData eventData) {
            isHovering = true;
            if (background) {
                background.color = hoverColor;
            }
            TriggerHoverHaptic();
        }
        
        public void OnPointerExit(PointerEventData eventData) 
        {
            isHovering = false;
            if (background) background.color = normalColor;
            controller.OnKeyReleased(this);
        }

        private void TriggerHoverHaptic()
        {
            float duration = 0.05f;
            float frequency = 150f;
            float amplitude = 0.15f;

            var haptic = SteamVR_Actions._default.Haptic;

            haptic?.Execute(0f, duration, frequency, amplitude, SteamVR_Input_Sources.RightHand);
        }
    }

    public class VRKeyboardController : MonoBehaviour
    {
        public static VRKeyboardController Instance;
        private TMP_InputField activeInputField;
        private GameObject keyboardRoot;
        private bool isShifted = false;
        private List<TextMeshProUGUI> keyLabels = new List<TextMeshProUGUI>();
        private List<VRKeyboardKey> keyLogics = new List<VRKeyboardKey>();

        private VRKeyboardKey currentPressedKey;
        private float nextRepeatTime;
        private const float REPEAT_DELAY = 0.5f;
        private const float REPEAT_RATE = 0.1f;

        // US QWERTY Layout
        private readonly string[] row1 = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };
        private readonly string[] row1s = { "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "_", "+" };
        private readonly string[] row2 = { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "[", "]" };
        private readonly string[] row2s = { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "{", "}" };
        private readonly string[] row3 = { "a", "s", "d", "f", "g", "h", "j", "k", "l", ";", "'" };
        private readonly string[] row3s = { "A", "S", "D", "F", "G", "H", "J", "K", "L", ":", "\"" };
        private readonly string[] row4 = { "z", "x", "c", "v", "b", "n", "m", ",", ".", "/" };
        private readonly string[] row4s = { "Z", "X", "C", "V", "B", "N", "M", "<", ">", "?" };

        private void Awake()
        {
            Instance = this;
            GenerateKeyboard();
            keyboardRoot.SetActive(false);
        }

        private void Update()
        {
            if (activeInputField == null || !activeInputField.gameObject.activeInHierarchy)
            {
                CloseKeyboard();
                return;
            }

            if (currentPressedKey != null && Time.time >= nextRepeatTime)
            {
                HandleKeyPress(currentPressedKey);
                nextRepeatTime = Time.time + REPEAT_RATE;
                Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
            }

            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) ||
                (VRInputManager.inputHandlers != null && (Valve.VR.SteamVR_Actions._default.LeftTrigger.axis > 0.5f || Valve.VR.SteamVR_Actions._default.RightTrigger.axis > 0.5f)))
            {
                if (TarkovVR.Patches.Misc.MenuPatches.vrUiInteracter != null)
                {
                    GameObject hit = TarkovVR.Patches.Misc.MenuPatches.vrUiInteracter.hitObject;
                    if (hit == null || (!hit.transform.IsChildOf(keyboardRoot.transform) && hit != activeInputField.gameObject))
                        if (hit != null && !hit.GetComponent<VRKeyboardKey>()) CloseKeyboard();
                }
            }
        }

        public void OpenKeyboard(TMP_InputField inputField)
        {
            activeInputField = inputField;
            keyboardRoot.SetActive(true);
            LayoutElement layout = keyboardRoot.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            RectTransform kbRect = keyboardRoot.GetComponent<RectTransform>();

            if (VRGlobals.commonUi != null)
            {
                keyboardRoot.transform.SetParent(VRGlobals.commonUi, false);
                kbRect.localScale = Vector3.one;
                kbRect.localRotation = Quaternion.Euler(30, 0, 0);

                // Lots of random stuff to try and position keyboard correctly
                // The canvas size seems to be 2560 but the ui itself is like 1920? not sure
                kbRect.anchorMin = Vector2.zero;
                kbRect.anchorMax = Vector2.zero;

                kbRect.pivot = new Vector2(0.5f, 1f);
                kbRect.anchoredPosition = new Vector2(1280f, -200f);
                kbRect.sizeDelta = new Vector2(1536f, 450f);
            }
            else
            {
                // if no ui found, it creates floating keyboard. looks ugly tho, hope it doesn't happen
                Transform head = Camera.main.transform;
                Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                keyboardRoot.transform.position = head.position + (flatForward * 0.5f) + (Vector3.down * 0.4f);
                keyboardRoot.transform.rotation = Quaternion.LookRotation(flatForward) * Quaternion.Euler(30, 0, 0);
            }
            //fix hitbox after first render
            LayoutRebuilder.ForceRebuildLayoutImmediate(kbRect);
        }

        public void CloseKeyboard()
        {
            keyboardRoot.SetActive(false);
            activeInputField = null;
            currentPressedKey = null;
        }

        public void OnKeyPressed(VRKeyboardKey key)
        {
            currentPressedKey = key;
            nextRepeatTime = Time.time + REPEAT_DELAY;
            HandleKeyPress(key);
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
        }

        public void OnKeyReleased(VRKeyboardKey key)
        {
            if (currentPressedKey == key)
                currentPressedKey = null;
        }

        public void HandleKeyPress(VRKeyboardKey key)
        {
            if (activeInputField == null || key == null) return;
            activeInputField.Select();
            activeInputField.ActivateInputField();

            int start = Mathf.Min(activeInputField.selectionAnchorPosition, activeInputField.selectionFocusPosition);
            int end = Mathf.Max(activeInputField.selectionAnchorPosition, activeInputField.selectionFocusPosition);

            switch (key.type)
            {
                // Adjusted to just simulate keypresses, this is less error prone.
                case VRKeyboardKey.KeyType.Character:
                    string charToAdd = isShifted ? key.shiftCharacter : key.character;
                    foreach (char c in charToAdd)
                    {
                        Event e = Event.KeyboardEvent(c.ToString());
                        e.character = c;
                        activeInputField.ProcessEvent(e);
                    }
                    if (isShifted) ToggleShift();
                    break;

                case VRKeyboardKey.KeyType.Space:
                    Event spaceEvent = Event.KeyboardEvent("space");
                    spaceEvent.character = ' ';
                    activeInputField.ProcessEvent(spaceEvent);
                    break;

                case VRKeyboardKey.KeyType.Backspace:
                    activeInputField.ProcessEvent(Event.KeyboardEvent("backspace"));
                    break;

                case VRKeyboardKey.KeyType.Enter:
                    activeInputField.ProcessEvent(Event.KeyboardEvent("return"));
                    //activeInputField.onSubmit?.Invoke(activeInputField.text);
                    CloseKeyboard();
                    return;

                case VRKeyboardKey.KeyType.Shift:
                    ToggleShift();
                    break;

                case VRKeyboardKey.KeyType.Clear:
                    activeInputField.text = "";
                    activeInputField.caretPosition = 0;
                    break;

                case VRKeyboardKey.KeyType.Close:
                    CloseKeyboard();
                    return;
            }
            activeInputField.ForceLabelUpdate();
        }

        private void ToggleShift()
        {
            isShifted = !isShifted;
            for (int i = 0; i < keyLogics.Count; i++)
            {
                if (keyLogics[i].type == VRKeyboardKey.KeyType.Character)
                    keyLabels[i].text = isShifted ? keyLogics[i].shiftCharacter : keyLogics[i].character;
            }
        }

        private void GenerateKeyboard()
        {
            keyboardRoot = new GameObject("VRKeyboard");
            keyboardRoot.transform.SetParent(this.transform);
            keyboardRoot.layer = 5;

            Canvas canvas = keyboardRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            keyboardRoot.AddComponent<GraphicRaycaster>();

            GameObject bgObj = new GameObject("Background");
            bgObj.transform.SetParent(keyboardRoot.transform, false);
            bgObj.layer = 5;
            Image bg = bgObj.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
            RectTransform bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero; bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero; bgRect.offsetMax = Vector2.zero;

            GameObject container = new GameObject("Container");
            container.transform.SetParent(keyboardRoot.transform, false);
            container.layer = 5;
            VerticalLayoutGroup vLayout = container.AddComponent<VerticalLayoutGroup>();
            vLayout.spacing = 5;
            vLayout.childControlHeight = true; vLayout.childControlWidth = true;
            vLayout.childForceExpandHeight = true; vLayout.childForceExpandWidth = true;
            vLayout.padding = new RectOffset(15, 15, 15, 15);

            RectTransform containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = Vector2.zero; containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = Vector2.zero; containerRect.offsetMax = Vector2.zero;

            CreateRow(container, row1, row1s);
            CreateRow(container, row2, row2s);
            CreateRow(container, row3, row3s);
            CreateRow(container, row4, row4s);
            CreateSpecialRow(container);
        }

        private void CreateRow(GameObject parent, string[] keys, string[] shiftKeys)
        {
            GameObject rowObj = new GameObject("Row");
            rowObj.transform.SetParent(parent.transform, false);
            rowObj.layer = 5;
            HorizontalLayoutGroup hLayout = rowObj.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 8;
            hLayout.childControlHeight = true; hLayout.childControlWidth = true;
            hLayout.childForceExpandHeight = false;
            hLayout.childForceExpandWidth = true;

            for (int i = 0; i < keys.Length; i++)
                CreateKey(rowObj, keys[i], shiftKeys[i], VRKeyboardKey.KeyType.Character);
        }

        private void CreateSpecialRow(GameObject parent)
        {
            GameObject rowObj = new GameObject("SpecialRow");
            rowObj.transform.SetParent(parent.transform, false);
            rowObj.layer = 5;
            HorizontalLayoutGroup hLayout = rowObj.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 8;
            hLayout.childControlHeight = true; hLayout.childControlWidth = true;
            hLayout.childForceExpandHeight = false;
            hLayout.childForceExpandWidth = true;

            CreateKey(rowObj, "SHIFT", "SHIFT", VRKeyboardKey.KeyType.Shift, 2.5f);
            CreateKey(rowObj, "SPACE", "SPACE", VRKeyboardKey.KeyType.Space, 6f);
            CreateKey(rowObj, "BACK", "BACK", VRKeyboardKey.KeyType.Backspace, 2.5f);
            CreateKey(rowObj, "ENTER", "ENTER", VRKeyboardKey.KeyType.Enter, 2.5f);
            CreateKey(rowObj, "X", "X", VRKeyboardKey.KeyType.Close, 1.5f);
        }

        private void CreateKey(GameObject parent, string normal, string shifted, VRKeyboardKey.KeyType type, float weight = 1f)
        {
            GameObject keyObj = new GameObject(normal);
            keyObj.transform.SetParent(parent.transform, false);
            keyObj.layer = 5;

            Image img = keyObj.AddComponent<Image>();
            img.color = new Color(0.2f, 0.2f, 0.2f);
            keyObj.AddComponent<BoxCollider>();

            VRKeyboardKey keyScript = keyObj.AddComponent<VRKeyboardKey>();
            keyScript.Init(normal, shifted, type, this);
            keyLogics.Add(keyScript);

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(keyObj.transform, false);
            textObj.layer = 5;
            TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = normal;
            text.fontSize = 28;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero; textRect.offsetMax = Vector2.zero;

            LayoutElement layout = keyObj.AddComponent<LayoutElement>();
            layout.flexibleWidth = weight;
            layout.preferredHeight = 70;

            keyLabels.Add(text);
        }
    }
}