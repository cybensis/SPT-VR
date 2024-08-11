using EFT;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Text;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;
using UnityEngine;
using UnityEngine.UI;
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
    private Vector3 rotOffset = Vector3.zero;
    public bool initialized = false;
    private int lastHitCompIndex = -1;
    private GamePlayerOwner playerOwner;
    private List<ActionsReturnClass> weaponUiLists;
    private List<Class559> meshList;
    private List<Class559> malfunctionMeshList;
    private HighLightMesh meshHighlighter;
    public bool hightlightingMesh = false;
    private int boltIndex = -1;
    private bool initMalfunction = false;
    public bool hasExaminedAfterMalfunction = false;
    public Vector3 armsOffset = new Vector3(-0.05f, -0.075f, -0.075f);

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
            meshList = new List<Class559>();
        if (malfunctionMeshList == null)
            malfunctionMeshList = new List<Class559>();


        transform.localEulerAngles = new Vector3(340, 340, 0);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void FinishInit() {
        meshHighlighter.Awake();
        initialized = true;
    }

    private void OnEnable() {
        if (initialized) {
            gunRaycastReciever.gameObject.layer = WEAPON_COLLIDER_LAYER;
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = true;
        }
        transform.localEulerAngles = new Vector3(340, 340, 0);
    }
    public void OnDisable()
    {
        if (initialized)
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = false;
    }
    public void SetHighlightComponent(HighLightMesh meshHighlighter) { 
        this.meshHighlighter = meshHighlighter;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetPlayerOwner(GamePlayerOwner owner) {
        playerOwner = owner;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void Update()
    {
        if (!initialized)
            return;

        if (!VRGlobals.player.IsInPronePose) { 
            transform.position = Camera.main.transform.position;
            transform.localPosition += armsOffset;
        }

        if (SteamVR_Actions._default.RightGrip.state && (!VRGlobals.vrPlayer.radialMenu || !VRGlobals.vrPlayer.radialMenu.active) && !VRGlobals.firearmController.IsAiming)
        {
            if (VRGlobals.firearmController.Weapon.MalfState.State != EFT.InventoryLogic.Weapon.EMalfunctionState.None) {
                if ((!hightlightingMesh || !initMalfunction) && meshHighlighter)
                {
                    meshHighlighter.class559_0 = malfunctionMeshList.ToArray();
                    meshHighlighter.enabled = true;
                    hightlightingMesh = true;
                    meshHighlighter.Color = Color.red;
                    initMalfunction = true;
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

                    meshHighlighter.class559_0 = meshList.ToArray();
                    meshHighlighter.enabled = true;
                    hightlightingMesh = true;
                    meshHighlighter.Color = Color.white;
                    initMalfunction = false;
                    hasExaminedAfterMalfunction = false;
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
        }

        if (lastHitCompIndex != -1)
        {
            VRGlobals.vrPlayer.interactionUi.position = interactables[lastHitCompIndex].position;
            VRGlobals.vrPlayer.interactionUi.LookAt(Camera.main.transform);
            VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
        }
    }


    public void SetScopeHighlight(Transform scopeTransform)
    {

        List<Class559> scopeMeshList = new List<Class559>();
        Renderer[] componentsInChildren = scopeTransform.GetComponentsInChildren<Renderer>(includeInactive: false);
        Renderer[] array = componentsInChildren;
        foreach (Renderer renderer in array)
        {
            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.enabled)
            {
                scopeMeshList.Add(new Class559(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                scopeMeshList.Add(new Class559(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
        meshHighlighter.class559_0 = scopeMeshList.ToArray();
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
                meshList.Add(new Class559(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                meshList.Add(new Class559(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
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
                malfunctionMeshList.Add(new Class559(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                malfunctionMeshList.Add(new Class559(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
    }

}
