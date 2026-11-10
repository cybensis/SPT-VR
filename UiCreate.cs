using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using System;
using System.Collections.Generic;
using System.IO;
using TarkovVR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static TarkovVR.Source.Controls.InputHandlers;

public class CircularSegmentUI : MonoBehaviour
{
    public int numberOfSegments = 5;  // Number of segments in the circle
    public float radius = 100f;       // Radius of the circle
    public float segmentSpacing = 2f; // Spacing between segments in degrees

    public string spritePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\Assets"); // Path to the image in the Resources folder
    //public string[] menuSprites = { "reload.png", "checkAmmo.png", "inspect.png", "fixMalfunction.png", "fireMode_burst.png" };
    public Sprite[] menuSprites;
    public Image[] menuSegments;
    public Sprite defaultMenuSprite = null;
    public Sprite selectedMenuSprite = null;
    public bool leftHand = false;
    private QuickSlotHandler quickSlotHandler;
    private SelectWeaponHandler selectWeaponHandler;
    private List<EBoundItem> quickSlotOrder;
    private List<ItemIcon> iconsList;
    public void Init()
    {

        string fullPath = Path.Combine(spritePath, "radialMenu.png");
        byte[] fileData = File.ReadAllBytes(fullPath);
        Texture2D texture = new Texture2D(2, 2);
        texture.LoadImage(fileData);
        defaultMenuSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));

        fullPath = Path.Combine(spritePath, "radialMenu_Selected.png");
        fileData = File.ReadAllBytes(fullPath);
        texture = new Texture2D(2, 2);
        texture.LoadImage(fileData);
        selectedMenuSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));

        gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        transform.localScale = new Vector3(0.001f,0.001f, 0.001f);
    }

    private int lastSelectedSegment = -1;

    // The grenade picker (GrenadePicker): drawn like the quick slots, but its stick and its closing are the picker's - it reads back the
    // segment the stick points at
    public bool pickerMode;
    public bool pickerOnLeftController;
    public Vector2 pickerStick;
    public int SelectedSegment => lastSelectedSegment;

    public void CreatePickerUi(Sprite[] sprites)
    {
        pickerMode = true;
        leftHand = true;    // the quick slots' icon layout
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
        lastSelectedSegment = -1;
        menuSprites = sprites;
        numberOfSegments = sprites.Length;
        menuSegments = new Image[numberOfSegments];
        if (numberOfSegments > 0)
            CreateSegments();
    }

    //private void OnEnable() {
    //    if (leftHand)
    //        CreateQuickSlotUi();
    //}

    public void CreateGunUi(string[] menuSpriteNames) {
        menuSegments = new Image[menuSpriteNames.Length];
        this.menuSprites = new Sprite[menuSpriteNames.Length];
        numberOfSegments = menuSpriteNames.Length;
        for (int i = 0; i < numberOfSegments; i++) {
            byte[] fileData = File.ReadAllBytes(Path.Combine(spritePath, menuSpriteNames[i]));
            Texture2D texture = new Texture2D(2, 2);
            texture.LoadImage(fileData);
            menuSprites[i] = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
        CreateSegments();
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.SelectFirstPrimaryWeapon, out baseHandler);
        if (baseHandler != null)
            selectWeaponHandler = baseHandler as SelectWeaponHandler;

        gameObject.active = false;
        if (VRSettings.GetLeftHandedMode())
        {
            transform.localPosition = new Vector3(0.0172f, -0.1143f, -0.03f);
            transform.localEulerAngles = new Vector3(270, 127, 80);
        }
        else
        {
            transform.localPosition = new Vector3(-0.0728f, -0.1343f, 0);
            transform.localEulerAngles = new Vector3(290, 252, 80);
        }
    }

    public void CreateQuickSlotUi()
    {
        leftHand = true;
        int children = transform.childCount;
        for (int i = 0; i < children; i++)
        {
            Destroy(transform.GetChild(i).gameObject);
        }
        Dictionary<EBoundItem, Item> quickSlots = VRGlobals.player.Inventory.FastAccess.BoundItems;
        List<Sprite> newMenuSprites = new List<Sprite>();
        quickSlotOrder = new List<EBoundItem>();
        iconsList = new List<ItemIcon>();
        foreach (KeyValuePair<EBoundItem, Item> entry in quickSlots)
        {
            // Grenades aren't in this menu: they come off the chest slot (GrenadeSlot shows the quick-access one), held by the grip that took them
            if (entry.Value is ThrowWeap)
                continue;
            ItemIcon itemIcon = ItemViewFactory.LoadItemIcon(entry.Value);
            if (itemIcon.Sprite != null)
            {
                iconsList.Add(itemIcon);    
                newMenuSprites.Add(itemIcon.Sprite);
                quickSlotOrder.Add(entry.Key);
            }

        }
        this.menuSprites = newMenuSprites.ToArray();
        // One segment per entry shown (a slot without an icon, or a grenade, has none)
        numberOfSegments = this.menuSprites.Length;
        menuSegments = new Image[numberOfSegments];
        if (this.menuSprites.Length > 0)
            CreateSegments();
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.SelectFastSlot4, out baseHandler);
        if (baseHandler != null)
            quickSlotHandler = baseHandler as QuickSlotHandler;
        if (VRSettings.GetLeftHandedMode())
        {
            transform.localPosition = new Vector3(0.0472f, -0.1043f, 0.01f);
            transform.localEulerAngles = new Vector3(272, 116, 27);
        }
        else {
            transform.localPosition = new Vector3(-0.0728f, -0.1343f, 0);
            transform.localEulerAngles = new Vector3(290, 252, 80);
        }

    }

    // The menu is picked with the stick of the controller it sits on, and that stick's other use is blocked while it's open (with
    // Swap Sticks the right controller's stick is the one moving you: VRInput). Kept from the open so the close clears the same flag.
    private bool blockedLeftStick;

    private bool OnLeftController => pickerMode ? pickerOnLeftController : leftHand != VRSettings.GetLeftHandedMode();

    private void OnEnable() {
        blockedLeftStick = OnLeftController != VRSettings.GetSwapSticks();
        if (blockedLeftStick)
            VRGlobals.blockLeftJoystick = true;
        else
            VRGlobals.blockRightJoystick = true;
    }
    private void OnDisable()
    {
        if (blockedLeftStick)
            VRGlobals.blockLeftJoystick = false;
        else
            VRGlobals.blockRightJoystick = false;
        // The stick that picked in it reads centred for everything else until it's let go, or the push that picked carries on as a
        // crouch or a move. Only a menu that was up: they're also made and switched off at a raid's start.
        if (shown)
            VRInput.HoldStickUntilLetGo(OnLeftController ? OpenXRInput.LEFT_HAND : OpenXRInput.RIGHT_HAND);
        shown = false;
    }
    private bool shown;
    void Update()
    {
        shown = true;
        bool leftHandedMode = VRSettings.GetLeftHandedMode();
        bool secondaryGripState = leftHandedMode ? VRInput.RightGrip.GetState(SteamVR_Input_Sources.RightHand) : VRInput.LeftGrip.GetState(SteamVR_Input_Sources.LeftHand);
        bool primaryGripState = leftHandedMode ? VRInput.LeftGrip.GetState(SteamVR_Input_Sources.LeftHand) : VRInput.RightGrip.GetState(SteamVR_Input_Sources.RightHand);
        // Nothing in it (no quick slots, or grenades only): it still closes with its grip, else it stays open holding a stick
        if (numberOfSegments == 0)
        {
            if (!pickerMode && (leftHand ? !secondaryGripState : !primaryGripState))
                gameObject.active = false;
            return;
        }
        Vector2 joystickInput = pickerMode ? pickerStick : OnLeftController ? VRInput.RawLeftJoystick.axis : VRInput.RawRightJoystick.axis;


        if (lastSelectedSegment != -1)
        {
            menuSegments[lastSelectedSegment].color = new Color(1f, 1f, 1f, 0.3f);
            menuSegments[lastSelectedSegment].sprite = defaultMenuSprite;
            lastSelectedSegment = -1;
        }

        if (joystickInput.x != 0 || joystickInput.y != 0)
        {
            float angle = Mathf.Atan2(joystickInput.x, joystickInput.y) * Mathf.Rad2Deg;
            angle += 15;
            if (angle < 0) angle += 360;

            int selectedSegment = Mathf.FloorToInt(angle / (360f / numberOfSegments));
            menuSegments[selectedSegment].color = new Color(1f, 1f, 1f, 0.5f);
            menuSegments[selectedSegment].sprite = selectedMenuSprite;
            lastSelectedSegment = selectedSegment;
        }

        // The picker commits and closes itself
        if (pickerMode)
            return;

        if (!leftHand && !primaryGripState)
        {
            if (lastSelectedSegment == 0)
                selectWeaponHandler.TriggerSwapPrimaryWeapon();
            else if (lastSelectedSegment == 1)
                selectWeaponHandler.TriggerSwapSecondaryWeapon();
            else if (lastSelectedSegment == 2)
                selectWeaponHandler.TriggerSwapSidearm();
            else if (lastSelectedSegment == 3)
                selectWeaponHandler.TriggerSwapToMelee();
            
            
            gameObject.active = false;
        }

        if (leftHand && !secondaryGripState)
        {
            if (quickSlotHandler.GetQuickUseSlot() == 0 && lastSelectedSegment != -1) {
                quickSlotHandler.TriggerUseQuickSlot(quickSlotOrder[lastSelectedSegment]);
                menuSegments[lastSelectedSegment].color = new Color(1f, 1f, 1f, 0.3f);
                menuSegments[lastSelectedSegment].sprite = defaultMenuSprite;
                lastSelectedSegment = -1;
            }


            gameObject.active = false;
        }
    }

    // parent is HumanLForearm3

    // Timer panel localpos: 0.047 0.08 0.025
    // local rot = 88.5784 83.1275 174.7802
    // child(0).localeuler = 0 342.1273 0

    // leftwristui localpos = -0.1 0.04 0.035
    // localrot = 304.3265 181 180
    void CreateSegments()
    {
        if (menuSegments == null || menuSegments.Length < numberOfSegments)
            menuSegments = new Image[numberOfSegments];

        if (menuSprites == null || menuSprites.Length < numberOfSegments)
        {
            Debug.LogError($"menuSprites is null or too short! Expected at least {numberOfSegments}, got {(menuSprites == null ? "null" : menuSprites.Length.ToString())}");
            return;
        }

        float angleStep = 360f / numberOfSegments;
        float adjustedAngleStep = angleStep - segmentSpacing;
        float fillAmount = adjustedAngleStep / 360f;

        for (int i = 0; i < numberOfSegments; i++)
        {
            GameObject newSegment = new GameObject("radialSegment_" + i);
            newSegment.transform.parent = transform;
            newSegment.transform.localScale = Vector3.one;

            Image segmentImage = newSegment.AddComponent<Image>();
            menuSegments[i] = segmentImage;

            segmentImage.sprite = defaultMenuSprite;
            segmentImage.type = Image.Type.Filled;
            segmentImage.fillMethod = Image.FillMethod.Radial360;
            segmentImage.fillOrigin = (int)Image.Origin360.Bottom;
            segmentImage.fillAmount = fillAmount;

            RectTransform rectTransform = newSegment.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(radius * 2, radius * 2);
            rectTransform.localPosition = Vector3.zero;
            segmentImage.color = new Color(1f, 1f, 1f, 0.3f);

            float angle = (i * angleStep) + (angleStep / 2);
            rectTransform.localRotation = Quaternion.Euler(0, 0, -angle);

            GameObject menuOption = new GameObject("menuOption_" + i);
            menuOption.transform.parent = newSegment.transform;
            menuOption.transform.localPosition = Vector3.zero;
            Vector3 newRot = rectTransform.localEulerAngles * -1;
            newRot.z += 90;
            if (leftHand)
                newRot.z += 90;
            menuOption.transform.localEulerAngles = newRot;
            menuOption.transform.localScale = leftHand ? new Vector3(1.25f, 1.25f, 1.25f) : Vector3.one;

            Image optionIcon = menuOption.AddComponent<Image>();
            optionIcon.sprite = i < menuSprites.Length ? menuSprites[i] : null;
            optionIcon.color = new Color(1f, 1f, 1f, 1f);

            RectTransform iconRectTransform = menuOption.GetComponent<RectTransform>();
            iconRectTransform.sizeDelta = new Vector2(radius * 0.5f, radius * 0.5f);
            float x = -0.45f + ((numberOfSegments - 4) * 0.06f);
            float y = -0.5f - ((numberOfSegments - 4) * 0.06f);
            iconRectTransform.localPosition = new Vector3(radius * x, radius * y, 0);
        }
    }
}
