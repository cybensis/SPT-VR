using EFT;
using EFT.UI.Ragfair;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Valve.VR;
using static HighLightMesh;
using static System.Net.Mime.MediaTypeNames;
using static TarkovVR.Source.Controls.InputHandlers;


public class GunInteractionController : MonoBehaviour
{
    public Transform magazine;
    private static LayerMask WEAPON_COLLIDER_LAYER = 28;
    private Transform internalMag;
    private Transform chargingHandle;
    private Transform bolt;
    private Transform fireModeSwitch;
    private List<Transform> tacDevices;
    private List<Transform> interactables;
    private GameObject gunRaycastReciever;
    private Vector3 rotOffset = new Vector3(-0.02f, 0, 0.1f);
    private Vector3 rotOffset2 = new Vector3(0.06f, 0.03f, -0.06f);
    public bool initialized = false;
    private int lastHitCompIndex = -1;
    private GamePlayerOwner playerOwner;
    private List<ActionsReturnClass> weaponUiLists;
    private List<Class614> meshList;
    private List<Class614> malfunctionMeshList;
    private HighLightMesh meshHighlighter;
    public bool hightlightingMesh = false;
    private int boltIndex = -1;
    private bool initMalfunction = false;
    public bool hasExaminedAfterMalfunction = false;
    public Vector3 armsOffset = new Vector3(-0.05f, -0.075f, -0.075f);

    private Dictionary<Transform, ActionsReturnClass> interactablesDictionary;
    private Dictionary<Transform, ActionsReturnClass> tacDeviceDictionary;
    private Dictionary<Transform, ActionsReturnClass> malfunctionMeshDictionary;

    //private bool
    public void Init()
    {
        //gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        if (interactables == null)
            interactables = new List<Transform>();
        if (tacDevices == null)
            tacDevices = new List<Transform>();
        if (weaponUiLists == null)
            weaponUiLists = new List<ActionsReturnClass>();
        if (meshList == null)
            meshList = new List<Class614>();
        if (malfunctionMeshList == null)
            malfunctionMeshList = new List<Class614>();

        if (interactablesDictionary == null)
            interactablesDictionary = new Dictionary<Transform, ActionsReturnClass>();
        if (tacDeviceDictionary == null)
            tacDeviceDictionary = new Dictionary<Transform, ActionsReturnClass>();
        if (malfunctionMeshDictionary == null)
            malfunctionMeshDictionary = new Dictionary<Transform, ActionsReturnClass>();

         transform.localEulerAngles = new Vector3(340, 340, 0);

    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void FinishInit() {
        Camera.main.gameObject.GetComponent<HighLightMesh>().enabled = true;
        meshHighlighter.Awake();
        initialized = true;
    }

    private void OnEnable() {
        if (initialized) {
            gunRaycastReciever.gameObject.layer = WEAPON_COLLIDER_LAYER;
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = true;
        }
        transform.localEulerAngles = new Vector3(340, 340, 0);

        //if (VRSettings.GetLeftHandedMode())
        //    transform.localEulerAngles = new Vector3(340, 40, 0);
        //else
        //    transform.localEulerAngles = new Vector3(340, 340, 0);
        prevRot = Vector3.zero;
        prevPos = Vector3.zero;
        prevForward = Vector3.zero;
        Transform rightHandsPositioner = transform.FindChild("RightHandPositioner");
        if (rightHandsPositioner && rightHandsPositioner.GetComponent<HandsPositioner>())
        {
            rightHandsPositioner.GetComponent<HandsPositioner>().enabled = true;
            rightHandsPositioner.gameObject.active = true; 
        }

        if (VRSettings.GetLeftHandedMode()) { 
            this.transform.parent.localScale = new Vector3(-1, 1, 1);
            
        }

        if (VRGlobals.ikManager) {
            VRGlobals.ikManager.rightArmIk.solver.target = null;
            VRGlobals.ikManager.rightArmIk.enabled = false;
            if (!VRGlobals.vrPlayer.isSupporting) { 
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
        }
        framesAfterEnabled = 0;

    }
    public void OnDisable()
    {
        if (initialized)
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = false;

        Transform rightHandsPositioner = transform.FindChild("RightHandPositioner");

        if (rightHandsPositioner && rightHandsPositioner.GetComponent<HandsPositioner>())
        {
            rightHandsPositioner.GetComponent<HandsPositioner>().enabled = false;
        }

        prevRot = Vector3.zero;
        prevPos = Vector3.zero;
        prevForward = Vector3.zero;



    }
    public void SetHighlightComponent(HighLightMesh meshHighlighter) { 
        this.meshHighlighter = meshHighlighter;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetPlayerOwner(GamePlayerOwner owner) {
        playerOwner = owner;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------

    private Vector3 prevRot;
    private Vector3 prevPos;
    private Vector3 prevForward;
    private Vector3 prevBodyPos;
    public int framesAfterEnabled = 0;
    private void Update()
    {
        if (!initialized)
            return;

        //if (!test)
        //{
        //    VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
        //    VRGlobals.ikManager.MatchLegsToArms();
        //}

        //if (prevBodyPos == Vector3.zero)
        //{
        //    prevBodyPos = VRGlobals.camRoot.transform.position;
        //}
        //else { 
        //    VRGlobals.camRoot.transform.position = VRGlobals.emptyHands.position;
        //    VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.camRoot.transform.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.camRoot.transform.position.z);
        //}
        // Use this to keep the upper arms positioned under the players camera if they're not prone or sprinting
        if (!VRGlobals.player.IsSprintEnabled && !VRGlobals.player.IsInPronePose)
        {
            transform.position = Camera.main.transform.position + new Vector3(0, -0.12f, 0) + (Camera.main.transform.forward * -0.175f);
        }
        else
        {
            transform.localPosition = Vector3.zero;
            transform.localEulerAngles = new Vector3(340, 340, 0);
            //if (VRSettings.GetLeftHandedMode())
            //    transform.localEulerAngles = new Vector3(340, 40, 0);
            //else
        }
        prevRot = Camera.main.transform.eulerAngles;
        prevForward = Camera.main.transform.forward;
        prevPos = Camera.main.transform.position;

        if (VRGlobals.menuOpen && hightlightingMesh) {
            meshHighlighter.enabled = false;
            hightlightingMesh = false;
        }


        if (!VRGlobals.menuOpen && (VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftGrip.state : SteamVR_Actions._default.RightGrip.state) && (!VRGlobals.vrPlayer.radialMenu || !VRGlobals.vrPlayer.radialMenu.active) && !VRGlobals.firearmController.IsAiming)
        {
            if (VRGlobals.firearmController.Weapon.MalfState.State != EFT.InventoryLogic.Weapon.EMalfunctionState.None) {
                if ((!hightlightingMesh || !initMalfunction) && meshHighlighter)
                {
                    meshHighlighter.class614_0 = malfunctionMeshList.ToArray();
                    meshHighlighter.enabled = true;
                    hightlightingMesh = true;
                    meshHighlighter.Color = Color.red;
                    initMalfunction = true;
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = true;
                    else
                        VRGlobals.blockRightJoystick = true;
                    Camera.main.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter.commandBuffer_0);
                }

                RaycastHit hit;
                LayerMask mask = 1 << WEAPON_COLLIDER_LAYER;
                if (Physics.Raycast(Camera.main.transform.position, Quaternion.Euler(5, 0, 0) * Camera.main.transform.forward, out hit, 2, mask)) {
                    if (lastHitCompIndex != boltIndex)
                    {
                        //if (lastHitCompIndex != -1)
                        //    interactables[lastHitCompIndex].gameObject.active = true;
                        if (hasExaminedAfterMalfunction) {
                            weaponUiLists[boltIndex].SelectedAction = weaponUiLists[boltIndex].Actions[1];
                            playerOwner.AvailableInteractionState.method_0(weaponUiLists[boltIndex]);
                        }
                        else { 
                            weaponUiLists[boltIndex].SelectedAction = weaponUiLists[boltIndex].Actions[0];
                            playerOwner.AvailableInteractionState.method_0(weaponUiLists[boltIndex]);
                        }
                        //interactables[index].gameObject.active = false;
                        lastHitCompIndex = boltIndex;
                        VRGlobals.vrPlayer.interactionUi.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                    }
                }

            }
            else
            {
                if ((!hightlightingMesh || initMalfunction) && meshHighlighter) {

                    meshHighlighter.class614_0 = meshList.ToArray();
                    meshHighlighter.enabled = true;
                    hightlightingMesh = true;
                    meshHighlighter.Color = Color.white;
                    initMalfunction = false;
                    hasExaminedAfterMalfunction = false;
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = true;
                    else
                        VRGlobals.blockRightJoystick = true;
                    Camera.main.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter.commandBuffer_0);

                }

                RaycastHit hit;
                LayerMask mask = 1 << WEAPON_COLLIDER_LAYER;


                if (Physics.Raycast(Camera.main.transform.position, Quaternion.Euler(5, 0, 0) * Camera.main.transform.forward, out hit, 2, mask))
                {
                    int index = FindClosestTransform(hit.point);
                    if (lastHitCompIndex != index)
                    {
                        //if (lastHitCompIndex != -1)
                        //    interactables[lastHitCompIndex].gameObject.active = true;

                        weaponUiLists[index].SelectedAction = weaponUiLists[index].Actions[0];
                        playerOwner.AvailableInteractionState.method_0(weaponUiLists[index]);
                        //interactables[index].gameObject.active = false;
                        lastHitCompIndex = index;
                        VRGlobals.vrPlayer.interactionUi.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                    }
                }
                else if (lastHitCompIndex != -1)
                {
                    playerOwner.AvailableInteractionState.method_0(null);
                    //interactables[lastHitCompIndex].gameObject.active = true;
                    lastHitCompIndex = -1;
                    VRGlobals.vrPlayer.interactionUi.localScale = Vector3.one;
                }

            }
        }
        else if (lastHitCompIndex != -1) {
            playerOwner.AvailableInteractionState.method_0(null);
            //interactables[lastHitCompIndex].gameObject.active = true;
            lastHitCompIndex = -1;
            VRGlobals.vrPlayer.interactionUi.localScale = Vector3.one;
            Camera.main.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter.commandBuffer_0);
        }
        else if (hightlightingMesh && meshHighlighter) {
            meshHighlighter.enabled = false;
            hightlightingMesh = false;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.blockLeftJoystick = false;
            else
                VRGlobals.blockRightJoystick = false;
        }

        if (lastHitCompIndex != -1)
        {
            VRGlobals.vrPlayer.interactionUi.position = interactables[lastHitCompIndex].position;
            VRGlobals.vrPlayer.interactionUi.LookAt(Camera.main.transform);
            VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
        }
        if (framesAfterEnabled == 1 && transform.FindChild("RightHandPositioner") && VRGlobals.player._markers.Length > 1)
        {
            if (!WeaponPatches.grenadeEquipped) { 
                // If the gun is pressed up against something that moves the animator around which will throw off the calculations
                // when the players stops pressing it against something, so remove any localRotation or localPosition for this frame
                VRGlobals.firearmController.GunBaseTransform.localPosition = Vector3.zero;
                VRGlobals.firearmController.GunBaseTransform.localEulerAngles = Vector3.zero;
            }
            //if (VRSettings.GetLeftHandedMode())
            //    VRGlobals.player._markers[1].transform.localPosition += new Vector3(0,0.04f,0.04f);
            Vector3 differenceBetweenHands = VRGlobals.player._markers[1].transform.position - VRGlobals.weaponHolder.transform.position;
            // Get the difference between the weapon holder and the right hand IK marker then multiply by 1 for some reason
            differenceBetweenHands = (transform.FindChild("RightHandPositioner").InverseTransformDirection(differenceBetweenHands) * -1);
            // Add some extra offset values so it matches up better with the hand
            //VRGlobals.weaponHolder.transform.localPosition = differenceBetweenHands + new Vector3(0.05f,0.04f,-0.05f);
            VRGlobals.weaponHolder.transform.localPosition = differenceBetweenHands + CalculateRightHandPosOffset();

            // 50,50 new Vector3(0.05f, 0.02f, -0.02f);
            // 30,30 new Vector3(0.05f,0.04f,-0.05f)
            // 40,50 new Vector3(0.07f,0.03f,-0.06f)
            // 20,50 new Vector3(0.07f,0.03f,-0.06f)
            // 0,50 new Vector3(0.12f,0.06f,-0.06f)

            // 100,30 -0.02 0 +0.14
            // 80,30 -0.02 0 +0.1
            // 60,30 -0.01 0 +0.06
            // 40,30 0 0 +0.03
            // 30,30 new Vector3(0.05f,0.04f,-0.05f)
            // 10,30 +0.05 +0.02 -0.02
            // 0,30 +0.06 +0.03 -0.03


            // 30,100 0 -0.03 0
            // 30,90 0 -0.02 0
            // 30,70 0 -0.01 0
            // 30,50 0 0 0
            // 30,30 new Vector3(0.05f,0.04f,-0.05f)
            // 30,20 0 +0.03 0
            // 30,10 0 +0.045 0
            // 30,0 0 +0.06 0

        }
        if (VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(VRPlayerManager.LEFT_HAND_ANIMATOR_HASH) == 0)
        {
            framesAfterEnabled++;

        }
    }

    //private void LateUpdate()
    //{
    //    //VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
    //    VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
    //    VRGlobals.ikManager.MatchLegsToArms();
    //}

    // Different right handed rotation values in the VR settings can cause the position of the right hand to change, so this helps adjust that
    public Vector3 CalculateRightHandPosOffset()
    {
        // Vertical offset setting is a value from 0-100, horizontal starts at 0, the higher the slider, it goes into negative with the max being
        // -50, or slider max to the left is +50, stupid I know, can't possibly explain why I did it this way.
        float normalizedVertical = VRSettings.GetPrimaryHandVertOffset();
        float normalizedHorizontal = VRSettings.GetPrimaryHandHorOffset() / 50;
        if (normalizedVertical >= 50)
            // Map values from 50 to 100 to the range 0 to 1
            normalizedVertical = (normalizedVertical - 50) / 50;
        else
            // Map values from 0 to 50 to the range -1 to 0
            normalizedVertical = (normalizedVertical - 50) / 50;

        Vector3 baseOffset = (VRSettings.GetLeftHandedMode() ? new Vector3(0.03f, 0.05f, -0.045f) : new Vector3(0.035f, 0.04f, -0.02f));
        Vector3 vertPositiveOffsetScale = (VRSettings.GetLeftHandedMode() ? new Vector3(0.09f, 0f, -0.1f) : new Vector3(-0.02f, 0, 0.1f));
        Vector3 vertNeggativeOffsetScale = (VRSettings.GetLeftHandedMode() ? new Vector3(-0.02f, -0.03f, 0.08f) : new Vector3(0.06f, 0.03f, -0.06f));
        Vector3 adjustedOffset = Vector3.zero;
        float horizontalY = 0;
        if (VRSettings.GetLeftHandedMode())
            normalizedHorizontal *= -1;
        if (normalizedHorizontal < 0)
            horizontalY = 0.03f * normalizedHorizontal;
        else if (normalizedVertical > 0)
            horizontalY = 0.06f * normalizedHorizontal;

        if (normalizedVertical > 0)
            adjustedOffset = vertPositiveOffsetScale * normalizedVertical;
        else if (normalizedVertical < 0)
            adjustedOffset = vertNeggativeOffsetScale * -normalizedVertical;

        adjustedOffset = new Vector3(adjustedOffset.x, adjustedOffset.y + horizontalY, adjustedOffset.z);
;

        return baseOffset + adjustedOffset;
    }

    public void SetScopeHighlight(Transform scopeTransform)
    {

        List<Class614> scopeMeshList = new List<Class614>();
        Renderer[] componentsInChildren = scopeTransform.GetComponentsInChildren<Renderer>(includeInactive: false);
        Renderer[] array = componentsInChildren;
        foreach (Renderer renderer in array)
        {
            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.enabled)
            {
                scopeMeshList.Add(new Class614(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                scopeMeshList.Add(new Class614(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
        meshHighlighter.class614_0 = scopeMeshList.ToArray();
        meshHighlighter.enabled = true;
    }
    public void RemoveScopeHighlight()
    {
        meshHighlighter.enabled = false;
    }

    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private int FindClosestTransform(Vector3 hitPoint)
    {
        int index = -1;
        float closestDistanceSqr = Mathf.Infinity;

        for (int i = 0; i < interactables.Count; i++) { 
            Vector3 directionToTarget = interactables[i].position - hitPoint;
            float dSqrToTarget = directionToTarget.sqrMagnitude; // Avoids the cost of calculating the square root

            if (dSqrToTarget < closestDistanceSqr)
            {
                closestDistanceSqr = dSqrToTarget;
                index = i;
            }
            
        }

        return index;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetMagazine(Transform magTransform, bool isInternal) {
        if (isInternal)
            internalMag = magTransform.transform;
        else 
            magazine = magTransform.transform;


        CreateInteractableMarker(magTransform.transform, "magMarker");
        int meshListLengthBeforeMags = meshList.Count;
        GetMesh(magTransform.transform);
        List<ActionsTypesClass> listComponents = new List<ActionsTypesClass>();
        ActionsTypesClass checkMagazine = new ActionsTypesClass();
        checkMagazine.Name = "Check Magazine";
        ActionsTypesClass reload = new ActionsTypesClass();
        reload.Name = "Reload";
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ReloadWeapon, out baseHandler);
        if (baseHandler != null)
        {
            ReloadInputHandler reloadHandler = baseHandler as ReloadInputHandler;
            reload.Action = reloadHandler.TriggerReload;

        }
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.CheckAmmo, out baseHandler);
        if (baseHandler != null)
        {
            CheckAmmoHandler checkAmmoHandler =  baseHandler as CheckAmmoHandler;
            checkMagazine.Action = checkAmmoHandler.TriggerCheckAmmo;

        }
        listComponents.Add(checkMagazine);
        listComponents.Add(reload);
        ActionsReturnClass magazineList = new ActionsReturnClass();
        magazineList.Actions = listComponents;
        weaponUiLists.Add(magazineList);
        if (magazine && magazine.childCount > 0)
        {
            if (magazine.GetComponent<MeshRenderer>())
                magazine.GetChild(0).position = magazine.GetComponent<MeshRenderer>().bounds.center;
            else if (meshList.Count > meshListLengthBeforeMags)
                interactables[interactables.Count - 1].localPosition = meshList[meshListLengthBeforeMags].mesh_0.bounds.center;
        }
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public bool IsMagazineSet() {
        return (magazine || internalMag);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetChargingHandleOrBolt(Transform chargeOrBoltTransform, bool isBolt) {
        if (chargingHandle || bolt)
            return;
        if (isBolt)
            bolt = chargeOrBoltTransform;
        else
            chargingHandle = chargeOrBoltTransform;

        CreateInteractableMarker(chargeOrBoltTransform, "boltMarker");
        boltIndex = interactables.Count - 1;
        GetMesh(chargeOrBoltTransform);
        GetMalfunctionMeshes(chargeOrBoltTransform);
        List<ActionsTypesClass> listComponents = new List<ActionsTypesClass>();
        ActionsTypesClass examineWeapon = new ActionsTypesClass();
        examineWeapon.Name = "Examine Weapon";
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ExamineWeapon, out baseHandler);
        if (baseHandler != null)
        {
            ExamineWeaponHandler examineWeaponHandler = baseHandler as ExamineWeaponHandler;
            examineWeapon.Action = examineWeaponHandler.TriggerExamineWeapon;
        }
        listComponents.Add(examineWeapon);


        ActionsTypesClass checkChamber = new ActionsTypesClass();
        checkChamber.Name = "Check Chamber/Fix Malfunction";
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.CheckChamber, out baseHandler);
        if (baseHandler != null)
        {
            CheckChamberHandler checkChamberHandler = baseHandler as CheckChamberHandler;
            checkChamber.Action = checkChamberHandler.TriggerCheckChamber;
        }
        listComponents.Add(checkChamber);


        ActionsReturnClass chamberList = new ActionsReturnClass();
        chamberList.Actions = listComponents;
        weaponUiLists.Add(chamberList);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetFireModeSwitch(Transform fireModeSwitch) {
        CreateInteractableMarker(fireModeSwitch, "fireModeMarker");
        GetMesh(fireModeSwitch);
        List<ActionsTypesClass> listComponents = new List<ActionsTypesClass>();
        ActionsTypesClass toggleFireMode = new ActionsTypesClass();
        toggleFireMode.Name = "Toggle Fire Mode";
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ChangeWeaponMode, out baseHandler);
        if (baseHandler != null)
        {
            FireModeHandler checkChamberHandler = baseHandler as FireModeHandler;
            toggleFireMode.Action = checkChamberHandler.TriggerChangeFireMode;
        }

        listComponents.Add(toggleFireMode);


        ActionsReturnClass fireModList = new ActionsReturnClass();
        fireModList.Actions = listComponents;
        weaponUiLists.Add(fireModList);
        this.fireModeSwitch = fireModeSwitch;
    }
    public Transform GetFireModeSwitch() { return this.fireModeSwitch; }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void AddTacticalDevice(Transform tacDevice, FirearmsAnimator animator) {
        CreateInteractableMarker(tacDevice, "tacDeviceMarker");
        GetMesh(tacDevice);
        List<ActionsTypesClass> listComponents = new List<ActionsTypesClass>();
        ActionsTypesClass changeMode = new ActionsTypesClass();
        ActionsTypesClass toggleTacDevice = new ActionsTypesClass();
        changeMode.Name = "Change Mode";
        toggleTacDevice.Name = "Turn On/Off";
        TacticalDeviceController tacDeviceController = new TacticalDeviceController(tacDevice.GetComponent<TacticalComboVisualController>(), animator);
        changeMode.Action = tacDeviceController.ChangeTacDeviceSetting;
        toggleTacDevice.Action = tacDeviceController.ToggleTacDevice;
        listComponents.Add(changeMode);
        listComponents.Add(toggleTacDevice);
        ActionsReturnClass fireModList = new ActionsReturnClass();
        fireModList.Actions = listComponents;
        weaponUiLists.Add(fireModList);
        this.tacDevices.Add(tacDevice);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void CreateInteractableMarker(Transform parent, string name)
    {
        GameObject marker = new GameObject(name);
        //marker.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        //marker.AddComponent<UnityEngine.UI.Image>().color = new Color(0, 0.7174f, 1, 1);
        marker.transform.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
        marker.transform.parent = parent;
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localEulerAngles = new Vector3(0, 270, 0);
        //marker.layer = 5;
        // When a gun is reloaded the existing mag marker is removed, and placing
        // the new one on the end causes the different array indexes to not line up
        // so we need to replace it instead
        bool replaceInteractable = false;
        for (int i = 0; i < interactables.Count; i++)
        {
            if (interactables[i] == null)
            {
                interactables.RemoveAt(i);
                interactables.Insert(i, marker.transform);
                replaceInteractable = true;
            }
        }
        if (!replaceInteractable)
            interactables.Add(marker.transform);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public bool TacDeviceAlreadyRegistered(Transform tacDevice)
    {
        for (int i = 0; i < tacDevices.Count; i++) {
            if (tacDevices[i] == tacDevice)
                return true;
        }
        return false;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void CreateRaycastReceiver(Transform parent, float weaponLength) {

        gunRaycastReciever = new GameObject("weaponInteractReceiver");
        gunRaycastReciever.AddComponent<BoxCollider>().isTrigger = true;
        gunRaycastReciever.transform.parent = parent;
        gunRaycastReciever.transform.localScale = new Vector3(0.175f, weaponLength * 1.25f, 0.4f);
        gunRaycastReciever.transform.localRotation = Quaternion.identity;
        gunRaycastReciever.transform.localPosition = new Vector3(0, (weaponLength / 2) * -1, 0);
        gunRaycastReciever.layer = WEAPON_COLLIDER_LAYER;
        FinishInit();
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void GetMesh(Transform transform) {
        Renderer[] componentsInChildren = transform.GetComponentsInChildren<Renderer>(includeInactive: false);
        Renderer[] array = componentsInChildren;
        foreach (Renderer renderer in array)
        {
            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.enabled)
            {
                meshList.Add(new Class614(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                meshList.Add(new Class614(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
    }

    public void GetMalfunctionMeshes(Transform transform)
    {
        Renderer[] componentsInChildren = transform.GetComponentsInChildren<Renderer>(includeInactive: false);
        Renderer[] array = componentsInChildren;
        foreach (Renderer renderer in array)
        {
            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.enabled)
            {
                malfunctionMeshList.Add(new Class614(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                malfunctionMeshList.Add(new Class614(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
    }

}
