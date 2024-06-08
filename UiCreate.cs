using System.IO;
using TarkovVR;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;

public class CircularSegmentUI : MonoBehaviour
{
    public int numberOfSegments = 5;  // Number of segments in the circle
    public float radius = 100f;       // Radius of the circle
    public float segmentSpacing = 2f; // Spacing between segments in degrees

    public string spritePath = Path.Combine(Application.dataPath, "Assets"); // Path to the image in the Resources folder
    //public string[] menuSprites = { "reload.png", "checkAmmo.png", "inspect.png", "fixMalfunction.png", "fireMode_burst.png" };
    public Sprite[] menuSprites;
    public Image[] menuSegments;
    public Sprite defaultMenuSprite = null;
    public Sprite selectedMenuSprite = null;
    public bool leftHand = false;

    public void Init()
    {
        Plugin.MyLog.LogWarning("Start");
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

        transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
        transform.localPosition = new Vector3(-0.0728f, -0.1343f, 0);
        transform.localEulerAngles = new Vector3(290, 252, 80);
    }

    private int lastSelectedSegment = -1;


    public void CreateGunUi(string[] menuSpriteNames) {
        Plugin.MyLog.LogWarning("Create gun ui");
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
    }

    public void CreateQuickSlotUi(Sprite[] menuSprites)
    {
        
        menuSegments = new Image[menuSprites.Length];
        numberOfSegments = menuSprites.Length;
        this.menuSprites = menuSprites;
        leftHand = true;
        CreateSegments();
    }

    void Update()
    {
        if (numberOfSegments == 0)
            return;
        Vector2 joystickInput = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.RightHand);
        if (leftHand)
            joystickInput = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.LeftHand);

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

            // Determine the selected segment
            int selectedSegment = Mathf.FloorToInt(angle / (360f / numberOfSegments));
            menuSegments[selectedSegment].color = new Color(1f, 1f, 1f, 0.5f); // Change to blue with desired opacity
            menuSegments[selectedSegment].sprite = selectedMenuSprite;
            lastSelectedSegment = selectedSegment;
        }

        if (!leftHand && lastSelectedSegment != -1 && !SteamVR_Actions._default.RightGrip.GetState(SteamVR_Input_Sources.RightHand))
        {
            if (lastSelectedSegment == 1)
                VRGlobals.checkMagazine = true;
            else if (lastSelectedSegment == 2)
                VRGlobals.inspectWeapon = true;
            else if (lastSelectedSegment == 4)
                VRGlobals.changeFireMode = true;
            gameObject.active = false;
            // Add additional actions as necessary
        }
        if (leftHand && VRGlobals.quickSlot == -1 && lastSelectedSegment != -1 && !SteamVR_Actions._default.LeftGrip.GetState(SteamVR_Input_Sources.LeftHand))
        {
            Plugin.MyLog.LogWarning("Selected item " + lastSelectedSegment);
            VRGlobals.quickSlot = lastSelectedSegment;
            gameObject.active = false;
        }
    }

    void CreateSegments()
    {
        // Calculate the angle between each segment, including spacing
        float angleStep = 360f / numberOfSegments;
        float adjustedAngleStep = angleStep - segmentSpacing;

        // Calculate the fill amount considering the spacing
        float fillAmount = adjustedAngleStep / 360f;

        for (int i = 0; i < numberOfSegments; i++)
        {
            // Instantiate a new segment from the prefab
            GameObject newSegment = new GameObject("radialSegment_" + i);

            newSegment.transform.parent = transform;
            newSegment.transform.localScale = Vector3.one;
            Image segmentImage = newSegment.AddComponent<Image>();
            menuSegments[i] = segmentImage;

            // Configure the segment's image properties
            segmentImage.sprite = defaultMenuSprite;
            segmentImage.type = Image.Type.Filled;
            segmentImage.fillMethod = Image.FillMethod.Radial360;
            segmentImage.fillOrigin = (int)Image.Origin360.Bottom;
            segmentImage.fillAmount = fillAmount;

            // Set the size and position of the segment
            RectTransform rectTransform = newSegment.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(radius * 2, radius * 2);
            rectTransform.localPosition = Vector3.zero;
            segmentImage.color = new Color(1f, 1f, 1f, 0.3f);

            // Rotate the segment to the correct angle
            float angle = (i * angleStep) + (angleStep / 2);
            rectTransform.localRotation = Quaternion.Euler(0, 0, -angle);

            // Create and configure the menu option icon
            GameObject menuOption = new GameObject("menuOption_" + i);
            menuOption.transform.parent = newSegment.transform;
            menuOption.transform.localPosition = Vector3.zero;
            Vector3 newRot = rectTransform.localEulerAngles * -1;
            newRot.z += 90;
            if (leftHand)
                newRot.z += 90;
            menuOption.transform.localEulerAngles = newRot;
            
            menuOption.transform.localScale = Vector3.one;
            if (leftHand)
                menuOption.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f);

            Image optionIcon = menuOption.AddComponent<Image>();


           

            // Assign the loaded sprite to the target image
            optionIcon.sprite = menuSprites[i];
            optionIcon.color = new Color(1f, 1f, 1f, 1f);

            RectTransform iconRectTransform = menuOption.GetComponent<RectTransform>();
            iconRectTransform.sizeDelta = new Vector2(radius * 0.5f, radius * 0.5f); // Adjust size as needed
            float x = -0.45f + ((numberOfSegments - 4) * 0.06f);
            float y = -0.5f - ((numberOfSegments - 4) * 0.06f);
            iconRectTransform.localPosition = new Vector3(radius * x, radius * y, 0); // Adjust position as needed
            //if (numberOfSegments > 4)
            //{
            //}
            //else if (numberOfSegments == 4) {
            //    float x = -0.45f + ((numberOfSegments - 4) * 0.06f);
            //    float y = -0.5f - ((numberOfSegments - 4) * 0.06f);
            //    iconRectTransform.localPosition = new Vector3(radius * x, radius * y, 0); // Adjust position as needed
            //}
        }
    }
}
