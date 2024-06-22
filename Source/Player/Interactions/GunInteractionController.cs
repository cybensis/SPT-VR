using EFT;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Text;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static System.Net.Mime.MediaTypeNames;

public class GunInteractionController : MonoBehaviour
{
    private Transform magazine;
    private Transform internalMag;
    private Transform chargingHandle;
    private Transform bolt;
    private Transform fireModeSwitch;
    private List<Transform> tacDevices;
    private List<Transform> interactables;
    private Transform gunRaycastReciever;
    private Vector3 rotOffset = Vector3.zero;
    private bool initialized = false;
    private int lastHitCompIndex = -1;
    private GamePlayerOwner playerOwner;
    private List<GClass2805> weaponUiLists;
    public void Init()
    {
        //gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        if (interactables == null)
            interactables = new List<Transform>();
        if (tacDevices == null)
            tacDevices = new List<Transform>();
        if (weaponUiLists == null)
            weaponUiLists = new List<GClass2805>();


    }
    // 3. Run HideoutPlayerOwner.AvailableInteractionState.set_Value(Gclass2805)
    public void SetPlayerOwner(GamePlayerOwner owner) {
        playerOwner = owner;
    }
    private void Update()
    {
        if (!initialized)
            return;
        for (int i = 0; i < interactables.Count; i++)
        {
            interactables[i].LookAt(Camera.main.transform);
        }

        if (SteamVR_Actions._default.RightGrip.state)
        {
            RaycastHit hit;
            LayerMask mask = 1 << 9;
            if (Physics.Raycast(Camera.main.transform.position, Quaternion.Euler(5, 0, 0) * Camera.main.transform.forward, out hit, 2, mask))
            {
                int index = FindClosestTransform(hit.point);
                if (lastHitCompIndex != index)
                {
                    if (lastHitCompIndex != -1)
                        interactables[lastHitCompIndex].gameObject.active = true;

                    weaponUiLists[index].SelectedAction = weaponUiLists[index].Actions[0];
                    playerOwner.AvailableInteractionState.method_0(weaponUiLists[index]);
                    interactables[index].gameObject.active = false;
                    lastHitCompIndex = index;
                    VRGlobals.vrPlayer.interactionUi.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                }
            }
            else if (lastHitCompIndex != -1)
            {
                playerOwner.AvailableInteractionState.method_0(null);
                interactables[lastHitCompIndex].gameObject.active = true;
                lastHitCompIndex = -1;
                VRGlobals.vrPlayer.interactionUi.localScale = Vector3.one;
            }

        }
        else if (lastHitCompIndex != -1) {
            playerOwner.AvailableInteractionState.method_0(null);
            interactables[lastHitCompIndex].gameObject.active = true;
            lastHitCompIndex = -1;
            VRGlobals.vrPlayer.interactionUi.localScale = Vector3.one;
        }

        if (lastHitCompIndex != -1)
        {
            VRGlobals.vrPlayer.interactionUi.position = interactables[lastHitCompIndex].position;
            VRGlobals.vrPlayer.interactionUi.LookAt(Camera.main.transform);
            VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
        }

    }
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



    public void SetMagazine(Renderer magTransform, bool isInternal) {
        if (isInternal)
            internalMag = magTransform.transform;
        else 
            magazine = magTransform.transform;
        CreateInteractableMarker(magTransform.transform, "magMarker");
        List<GClass2804> listComponents = new List<GClass2804>();
        GClass2804 checkMagazine = new GClass2804();
        checkMagazine.Name = "Check Magazine";
        GClass2804 reload = new GClass2804();
        reload.Name = "Reload";
        listComponents.Add(checkMagazine);
        listComponents.Add(reload);
        GClass2805 magazineList = new GClass2805();
        magazineList.Actions = listComponents;
        weaponUiLists.Add(magazineList);
        interactables[interactables.Count - 1].position = magTransform.bounds.center;
    }

    public bool IsMagazineSet() {
        return (magazine || internalMag);
    }

    public void SetChargingHandleOrBolt(Transform chargeOrBoltTransform, bool isBolt) {
        if (chargingHandle || bolt)
            return;
        if (isBolt)
            bolt = chargeOrBoltTransform;
        else
            chargingHandle = chargeOrBoltTransform;

        CreateInteractableMarker(chargeOrBoltTransform, "boltMarker");
        List<GClass2804> listComponents = new List<GClass2804>();
        GClass2804 fixMalfunction = new GClass2804();
        fixMalfunction.Name = "Check/Fix Malfunction";
        GClass2804 checkChamber = new GClass2804();
        checkChamber.Name = "Check Chamber";
        listComponents.Add(fixMalfunction);
        listComponents.Add(checkChamber);
        GClass2805 chamberList = new GClass2805();
        chamberList.Actions = listComponents;
        weaponUiLists.Add(chamberList);
    }
    public void SetFireModeSwitch(Transform fireModeSwitch) {
        CreateInteractableMarker(fireModeSwitch, "fireModeMarker");
        List<GClass2804> listComponents = new List<GClass2804>();
        GClass2804 toggleFireMode = new GClass2804();
        toggleFireMode.Name = "Toggle Fire Mode";
        listComponents.Add(toggleFireMode);
        GClass2805 fireModList = new GClass2805();
        fireModList.Actions = listComponents;
        weaponUiLists.Add(fireModList);
        this.fireModeSwitch = fireModeSwitch;
    }
    public void AddTacticalDevice(Transform tacDevice) {
        CreateInteractableMarker(tacDevice, "tacDeviceMarker");
        List<GClass2804> listComponents = new List<GClass2804>();
        GClass2804 changeMode = new GClass2804();
        changeMode.Name = "Change Mode";
        listComponents.Add(changeMode);
        GClass2804 toggleTacDevice = new GClass2804();
        toggleTacDevice.Name = "Turn On/Off";
        listComponents.Add(toggleTacDevice);
        GClass2805 fireModList = new GClass2805();
        fireModList.Actions = listComponents;
        weaponUiLists.Add(fireModList);
        this.tacDevices.Add(tacDevice);
    }

    private void CreateInteractableMarker(Transform parent, string name)
    {
        GameObject marker = new GameObject(name);
        marker.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        marker.AddComponent<UnityEngine.UI.Image>().color = new Color(0, 0.7174f, 1, 1);
        marker.transform.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
        marker.transform.parent = parent;
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localEulerAngles = new Vector3(0, 270, 0);
        marker.layer = 5;
        interactables.Add(marker.transform);
    }

    public void CreateRaycastReceiver(Transform parent, float weaponLength) {
        
        Plugin.MyLog.LogWarning("Creating collider " + parent + "    " + weaponLength);
        GameObject weaponInteractCollider = new GameObject("weaponInteractReceiver");
        weaponInteractCollider.AddComponent<BoxCollider>().isTrigger = true;
        weaponInteractCollider.transform.parent = parent;
        weaponInteractCollider.transform.localScale = new Vector3(0.1f, weaponLength, 0.4f);
        weaponInteractCollider.transform.localRotation = Quaternion.identity;
        weaponInteractCollider.transform.localPosition = new Vector3(0, (weaponLength / 2) * -1, 0);
        weaponInteractCollider.layer = 9;
        initialized = true;
    }




}
