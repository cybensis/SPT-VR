using EFT;
using EFT.UI;
using System.Collections.Generic;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using static HighLightMesh;
using static TarkovVR.Source.Controls.InputHandlers;


public class GunInteractionController : MonoBehaviour
{
    public Transform magazine;
    private static LayerMask WEAPON_COLLIDER_LAYER = 28;
    private Transform internalMag;
    private Transform fireModeSwitch;
    private List<Transform> tacDevices;
    private List<Transform> interactables;
    private GameObject gunRaycastReciever;
    public bool initialized = false;
    public int lastHitCompIndex = -1;

    // --- off-hand weapon interact ---
    // Reaching the support hand to a weapon part selects it; the off-hand trigger executes it (InteractHandler).
    // Selecting by dominant grip + gaze can't work with the sling: the grip is always held while holding the gun.
    public static float maxAnchorDistance = 5f;       // m: a right-hand marker further than this from the holder is not anchored to
    public static float interactReachRadius = 0.14f;  // m from the palm to a part to select it (the nearest part within it wins)
    public static float interactProbeForward = 0.08f; // m from wrist to palm along the off controller's forward
    private GamePlayerOwner playerOwner;
    private List<AvailableInteractionState> weaponUiLists;
    private List<Part> meshList;
    private List<Part> malfunctionMeshList;
    private HighLightMesh meshHighlighter;
    public bool highlightingMesh = false;
    private int boltIndex = -1;
    private bool initMalfunction = false;
    public bool hasExaminedAfterMalfunction = false;
    public Vector3 armsOffset = new Vector3(-0.05f, -0.075f, -0.075f);

    private Dictionary<Transform, AvailableInteractionState> interactablesDictionary;
    private Dictionary<Transform, AvailableInteractionState> tacDeviceDictionary;
    private Dictionary<Transform, AvailableInteractionState> malfunctionMeshDictionary;

    public void Init()
    {
        if (interactables == null)
            interactables = new List<Transform>();
        if (tacDevices == null)
            tacDevices = new List<Transform>();
        if (weaponUiLists == null)
            weaponUiLists = new List<AvailableInteractionState>();
        if (meshList == null)
            meshList = new List<Part>();
        if (malfunctionMeshList == null)
            malfunctionMeshList = new List<Part>();
        if (interactablesDictionary == null)
            interactablesDictionary = new Dictionary<Transform, AvailableInteractionState>();
        if (tacDeviceDictionary == null)
            tacDeviceDictionary = new Dictionary<Transform, AvailableInteractionState>();
        if (malfunctionMeshDictionary == null)
            malfunctionMeshDictionary = new Dictionary<Transform, AvailableInteractionState>();
         transform.localEulerAngles = new Vector3(340, 340, 0);

    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void FinishInit() {
        if (Camera.main != null)
        {
            if (!Camera.main.stereoEnabled)
                return;
            Camera.main.gameObject.GetComponent<HighLightMesh>().enabled = true;
        }
        if (meshHighlighter == null)
            meshHighlighter = GetComponentInChildren<HighLightMesh>();
        else
            meshHighlighter.Awake();
        initialized = true;
    }

    private void OnEnable() {
        
        if (initialized && gunRaycastReciever != null) {
            gunRaycastReciever.gameObject.layer = WEAPON_COLLIDER_LAYER;
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = true;
        }
        if (VRGlobals.VRCam != null && meshHighlighter != null && meshHighlighter._cmb != null)
        {
            VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
        }
        transform.localEulerAngles = new Vector3(340, 340, 0);

        Transform rightHandsPositioner = transform.Find("RightHandPositioner");
        if (rightHandsPositioner && rightHandsPositioner.GetComponent<HandsPositioner>())
        {
            rightHandsPositioner.GetComponent<HandsPositioner>().enabled = true;
            rightHandsPositioner.gameObject.active = true; 
        }
        
        if (Handedness.MirrorLeftHanded) { 
            this.transform.parent.localScale = new Vector3(-1, 1, 1);
            
        }
        
        if (VRGlobals.ikManager) {
            // While the gun is slung or support-held (ChestHang.Detached) the right hand is off the gun, so keep that arm free
            // instead of resetting it to hold the gun (menus and interactions disable and re-enable this component).
            // Only while that gun is still in the hands: a grenade drawn with the gun slung enables its own controller here
            // before ChestHang has seen the switch (Tarkov sets HandsController, then calls Spawn).
            if (TarkovVR.Source.Player.Interactions.ChestHang.Detached && VRGlobals.player != null
                && VRGlobals.player.HandsController is EFT.Player.FirearmController)
            {
                TarkovVR.Source.Player.Interactions.ChestHang.KeepGunHandFree();
            }
            else
            {
                VRGlobals.ikManager.rightArmIk.solver.target = null;
                VRGlobals.ikManager.rightArmIk.enabled = false;
            }
            if (!VRGlobals.vrPlayer.isSupporting) {
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
        }
        framesAfterEnabled = 0;
        VRGlobals.blockLeftJoystick = false;
        VRGlobals.blockRightJoystick = false;
    }

    public void OnDisable()
    {
        if (initialized && gunRaycastReciever != null && gunRaycastReciever.GetComponent<BoxCollider>() != null)
        {
            gunRaycastReciever.GetComponent<BoxCollider>().enabled = false;
        }

        if (transform != null)
        {
            Transform rightHandsPositioner = transform.Find("RightHandPositioner");
            if (rightHandsPositioner != null && rightHandsPositioner.GetComponent<HandsPositioner>() != null)
            {
                rightHandsPositioner.GetComponent<HandsPositioner>().enabled = false;
            }
        }

        if (highlightingMesh && meshHighlighter)
        {
            if (VRGlobals.VRCam != null && meshHighlighter._cmb != null)
            {
                VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
            }
            meshHighlighter.enabled = false;
            highlightingMesh = false;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.blockLeftJoystick = false;
            else
                VRGlobals.blockRightJoystick = false;
        }
    }
    public void SetHighlightComponent(HighLightMesh meshHighlighter) {
        this.meshHighlighter = meshHighlighter;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetPlayerOwner(GamePlayerOwner owner) {
        playerOwner = owner;
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------

    public int framesAfterEnabled = 0;


    private void Update()
    {
        if (!initialized)
            return;

        bool isLeftHanded = VRSettings.GetLeftHandedMode();
        bool menuOpen = VRGlobals.menuOpen;      
        bool radialMenuActive = VRGlobals.vrPlayer.radialMenu.active;

        // firearmController can be null here: not aiming then
        bool isAiming = VRGlobals.firearmController?.IsAiming ?? false;

        Transform cameraTransform = VRGlobals.VRCam.transform;
        Vector3 cameraPosition = cameraTransform.position;
        Vector3 cameraForward = cameraTransform.forward;
        Vector3 cameraEulerAngles = cameraTransform.eulerAngles;

        // Clean up the highlight in states where it must not be active
        bool interactCommon = !menuOpen && !radialMenuActive && !isAiming && !WeaponPatches.grenadeEquipped && !ManualReloadReplacesMenu();
        int nearestPartIdx = -1;
        bool shouldShowHighlight;
        nearestPartIdx = interactCommon ? OffHandNearestPart() : -1;
        shouldShowHighlight = nearestPartIdx != -1;
        if (!shouldShowHighlight && highlightingMesh)
            ForceCleanupHighlight();

        bool isSprintEnabled = VRGlobals.player.IsSprintEnabled;
        bool isInPronePose = VRGlobals.player.IsInPronePose;

        if (menuOpen && highlightingMesh)
        {
            DisableHighlighting();
            return;
        }

        if (shouldShowHighlight && isLeftHanded != null)
        {
            HandleWeaponInteraction(cameraPosition, cameraForward, isLeftHanded, nearestPartIdx);
        }
        else if (lastHitCompIndex != -1)
        {
            ClearInteraction();
        }
        else if (highlightingMesh && meshHighlighter)
        {
            DisableHighlighting();
            SetJoystickBlock(isLeftHanded, false);
        }

        if (lastHitCompIndex != -1)
        {
            UpdateInteractionUI(cameraTransform);
        }

        if (ShouldUpdateWeaponPosition())
        {
            UpdateWeaponPosition();
        }

        if (VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(VRPlayerManager.LEFT_HAND_ANIMATOR_HASH) == 0)
        {
            framesAfterEnabled++;
        }
    }
    
    // True when the hands do what this menu would: Manual Reload on a gun with a magazine, or a gun the hands load (tube shotguns,
    // bolt-actions, break-actions). Also a malfunction the hands clear with the handle, slide or bolt (VRReloadController.Malfunctions);
    // any other jam (including a shotgun's) still needs the menu's Fix Malfunction.
    private static bool ManualReloadReplacesMenu()
    {
        if (VRReloadController.MalfunctionClearsByHand(VRGlobals.firearmController))
            return true;
        return (VRReloadController.ManualReloadActive() || VRReloadController.HandsLoadShotgun(VRGlobals.firearmController)
                || VRReloadController.HandsLoadBoltAction(VRGlobals.firearmController) || VRReloadController.HandsLoadBreakAction(VRGlobals.firearmController))
            && VRGlobals.firearmController.Weapon.MalfState.State == EFT.InventoryLogic.Weapon.EMalfunctionState.None;
    }

    private Part[] cachedMalfunctionMeshArray;
    private Part[] cachedMeshArray;
    private bool malfunctionMeshArrayDirty = true;
    private bool meshArrayDirty = true;

    private Transform cachedRightHandPositioner;

    private void HandleWeaponInteraction(Vector3 cameraPosition, Vector3 cameraForward, bool isLeftHanded, int nearestPartIdx)
    {
        if (VRGlobals.firearmController == null)
            return;
        bool hasMalfunction = VRGlobals.firearmController.Weapon.MalfState.State != EFT.InventoryLogic.Weapon.EMalfunctionState.None;

        if (hasMalfunction)
        {
            HandleMalfunctionHighlighting(isLeftHanded);
            SelectPart(boltIndex, hasExaminedAfterMalfunction ? 1 : 0);   // during a jam, any reach selects the bolt
        }
        else
        {
            HandleNormalHighlighting(isLeftHanded);
            SelectPart(nearestPartIdx, 0);
        }
    }

    // Returns the weapon part the off hand is reaching (no button or gaze needed), or -1. Blocked while the off hand is busy:
    // supporting (it's on the foregrip, which the mag and bolt zones overlap), or the gun is off the hand (slung or support-held).
    private int OffHandNearestPart()
    {
        if (interactables == null || interactables.Count == 0 || VRGlobals.vrPlayer == null ||
            VRGlobals.vrPlayer.isSupporting || TarkovVR.Source.Player.Interactions.ChestHang.Detached ||
            VRReloadController.HoldingMagazine || VRReloadController.HoldingChargingHandle || VRReloadController.OffHandAtChargingHandle ||
            VRReloadController.HoldingSeatedMagazine || TarkovVR.Source.Player.Interactions.GunSpots.Busy)
            return -1;
        GameObject leftHandObject = VRGlobals.vrPlayer.LeftHand;
        if (leftHandObject == null)
            return -1;
        Vector3 probe = leftHandObject.transform.position + leftHandObject.transform.forward * interactProbeForward;
        int closestIndex = FindClosestTransform(probe);
        if (closestIndex < 0 || interactables[closestIndex] == null)
            return -1;
        return (interactables[closestIndex].position - probe).sqrMagnitude <= interactReachRadius * interactReachRadius ? closestIndex : -1;
    }

    private void SelectPart(int index, int actionIndex)
    {
        if (index < 0 || index >= weaponUiLists.Count || lastHitCompIndex == index)
            return;
        var actions = weaponUiLists[index].Actions;
        if (actions == null || actions.Count == 0)
            return;
        weaponUiLists[index].SelectedAction = actions[Mathf.Min(actionIndex, actions.Count - 1)];
        playerOwner.AvailableInteractionState.UpdateState(weaponUiLists[index]);
        lastHitCompIndex = index;
        VRGlobals.vrPlayer.interactionUi.localScale = new Vector3(0.5f, 0.5f, 0.5f);
    }

    private void HandleMalfunctionHighlighting(bool isLeftHanded)
    {
        if ((!highlightingMesh || !initMalfunction) && meshHighlighter)
        {
            if (malfunctionMeshArrayDirty)
            {
                cachedMalfunctionMeshArray = malfunctionMeshList.ToArray();
                malfunctionMeshArrayDirty = false;
            }

            meshHighlighter._targetParts = cachedMalfunctionMeshArray;
            EnableHighlighting(Color.red);
            initMalfunction = true;
            SetJoystickBlock(isLeftHanded, true);
        }
    }

    private void HandleNormalHighlighting(bool isLeftHanded)
    {
        if ((!highlightingMesh || initMalfunction) && meshHighlighter)
        {
            if (meshArrayDirty)
            {
                cachedMeshArray = meshList.ToArray();
                meshArrayDirty = false;
            }

            meshHighlighter._targetParts = cachedMeshArray;
            EnableHighlighting(Color.white);
            initMalfunction = false;
            hasExaminedAfterMalfunction = false;
            SetJoystickBlock(isLeftHanded, true);
        }
    }

    private void EnableHighlighting(Color color)
    {
        meshHighlighter.enabled = true;
        highlightingMesh = true;
        meshHighlighter.Color = color;

        VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
        VRGlobals.VRCam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
    }

    private void DisableHighlighting()
    {
        if (meshHighlighter)
        {
            meshHighlighter.enabled = false;
            highlightingMesh = false;
        }
    }

    private void SetJoystickBlock(bool isLeftHanded, bool block)
    {
        if (block && lastHitCompIndex == -1)
            return;
        if (isLeftHanded)
            VRGlobals.blockLeftJoystick = block;
        else
            VRGlobals.blockRightJoystick = block;
    }

    private void ClearInteraction()
    {
        playerOwner.AvailableInteractionState.UpdateState(null);
        lastHitCompIndex = -1;
        VRGlobals.vrPlayer.interactionUi.localScale = Vector3.one;
        VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
    }

    private void UpdateInteractionUI(Transform cameraTransform)
    {
        VRGlobals.vrPlayer.interactionUi.position = interactables[lastHitCompIndex].position;
        VRGlobals.vrPlayer.interactionUi.LookAt(cameraTransform);
        VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
    }

    private bool ShouldUpdateWeaponPosition()
    {
        if (cachedRightHandPositioner == null)
            cachedRightHandPositioner = transform.Find("RightHandPositioner");

        return cachedRightHandPositioner && VRGlobals.player._markers.Length > 1;
    }

    private void UpdateWeaponPosition()
    {
        if (cachedRightHandPositioner == null)
        {
            cachedRightHandPositioner = transform.Find("RightHandPositioner");
            if (cachedRightHandPositioner == null)
                return;
        }

        if (VRGlobals.weaponHolder == null || VRGlobals.player == null)
            return;

        // Tarkov went to empty hands without spawning them, so the holder no longer belongs to this gun (WeaponPatches.RepairUnspawnedEmptyHands)
        if (WeaponPatches.RepairUnspawnedEmptyHands())
            return;

        // Remove last frame's collision offset so the anchor below measures the unmodified pose
        // (WeaponCollision.Apply resolves and applies it again after the anchor).
        TarkovVR.Source.Player.Interactions.WeaponCollision.PreAnchor();
        // Then remove last frame's reload jolt (it was applied before the collision offset)
        TarkovVR.Source.Weapons.GunJolt.PreAnchor();
        // A press check turns the gun in the hand about the thumb's knuckle (VRReloadController.PressCheck). It runs before the anchor,
        // which then puts the hand's marker on the turned gun at your hand.
        TarkovVR.Source.Weapons.VRReloadController.ApplyPressCheckGunTurn(VRGlobals.weaponHolder.transform);

        if (!WeaponPatches.grenadeEquipped && VRGlobals.firearmController != null)
        {
            // Pressing the gun against something makes the animator move GunBaseTransform, so it's reset here
            VRGlobals.firearmController.GunBaseTransform.localPosition = Vector3.zero;
            VRGlobals.firearmController.GunBaseTransform.localEulerAngles = Vector3.zero;
        }

        // Off the dominant hand (chest sling, or held by the support hand alone): skip the marker anchor and collision,
        // and let ChestHang set the holder's world pose.
        // (false = the slung gun was switched away from; this is the next gun, placed below as usual)
        if (TarkovVR.Source.Player.Interactions.ChestHang.Hanging && TarkovVR.Source.Player.Interactions.ChestHang.DriveHangPose())
            return;
        if (TarkovVR.Source.Player.Interactions.ChestHang.SupportOnly && TarkovVR.Source.Player.Interactions.ChestHang.DriveSupportPose())
            return;
        // Held by the gun hand on the charging handle alone (an "Iraqi" reload)
        if (TarkovVR.Source.Player.Interactions.ChestHang.HandleHeld && TarkovVR.Source.Player.Interactions.ChestHang.DriveHandleHoldPose())
            return;

        // A grenade with its pin out is turned so the hand keeps its rotation (GrenadeHold.KeepHandTurn). This runs before the anchor
        // below, which then puts the turned grip on your hand.
        if (WeaponPatches.grenadeEquipped)
            GrenadeHold.KeepHandTurn(VRGlobals.weaponHolder.transform);

        // _markers[1] is weapon_R_IK_marker, a bone on the gun that reload and malfunction clips animate. While one plays,
        // VRGunController keeps the gun where it was in your hand instead of following that marker.
        if (!VRGunController.TryHoldGunInHand(VRGlobals.weaponHolder.transform))
        {
            // The grip position without the recoil on the hand, so the recoil shows on top of it (RecoilPose.kickShare)
            Vector3 anchor = !WeaponPatches.grenadeEquipped && VRGlobals.firearmController != null
                ? TarkovVR.Source.Weapons.RecoilPose.GripAnchor(VRGlobals.player._markers[1].transform)
                : VRGlobals.player._markers[1].transform.position;
            // A grenade in the left hand is anchored at the right grip mirrored across the grenade's centre plane
            // (RealLeftHandedPatches.GunPlane), so the mirrored grip lands on the controller. A gun's grip sits on its centre plane;
            // a grenade's is 11-12 cm off it.
            // When the whole grenade is mirrored (GrenadeHold.mirrorInLeftHand: the positioner scaled -1 on x), the grip marker already
            // is the left grip. It's anchored as is, through the positioner's scale (InverseTransformVector), so the holder's local
            // position is mirrored too.
            bool mirroredGrenade = GrenadeHold.MirroredInLeftHand;
            if (!mirroredGrenade && WeaponPatches.grenadeEquipped && Handedness.GunInLeftHand && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                anchor = RealLeftHandedPatches.ReflectPoint(anchor, planePoint, planeNormal);
            Vector3 differenceBetweenHands = anchor - VRGlobals.weaponHolder.transform.position;
            // A marker metres away belongs to a gun model returned to the pool, not one in the hand; the holder would follow it there
            if (differenceBetweenHands.sqrMagnitude < maxAnchorDistance * maxAnchorDistance)
            {
                differenceBetweenHands = (mirroredGrenade ? cachedRightHandPositioner.InverseTransformVector(differenceBetweenHands)
                    : cachedRightHandPositioner.InverseTransformDirection(differenceBetweenHands)) * -1;
                VRGlobals.weaponHolder.transform.localPosition = differenceBetweenHands + CalculateRightHandPosOffset();
                if (!VRGunController.IsTarkovAnimatingTheGun())
                    VRGunController.RememberGunPosition(VRGlobals.weaponHolder.transform);
            }
        }

        // A press check with the gun in the left hand moves the gun so the left hand (the marker's mirror image) stays on the left controller
        TarkovVR.Source.Weapons.VRReloadController.HoldPressCheckLeftHand(VRGlobals.weaponHolder.transform);

        // A reload jolt (a magazine seated, a bolt slammed home) on top of the anchor. It's applied before the collision so the
        // collision physics includes it.
        TarkovVR.Source.Weapons.GunJolt.Apply(VRGlobals.weaponHolder.transform);

        // Physical weapon collision runs after the marker anchor as the last holder write of the frame, so the anchor can't cancel it.
        // It's done at the gun's final placement this frame (WeaponCollision.atFinalPlacement).
        TarkovVR.Source.Player.Interactions.WeaponCollision.Request();
    }

    // Call this when mesh lists change to invalidate cache
    public void InvalidateMeshCache()
    {
        malfunctionMeshArrayDirty = true;
        meshArrayDirty = true;
    }

    private void ForceCleanupHighlight()
    {
        if (meshHighlighter?._cmb != null && VRGlobals.VRCam != null)
        {
            VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, meshHighlighter._cmb);
        }

        if (meshHighlighter != null)
            meshHighlighter.enabled = false;

        highlightingMesh = false;

        // Unblock the joysticks whatever the highlighter's state
        VRGlobals.blockLeftJoystick = false;
        VRGlobals.blockRightJoystick = false;
    }

    // The right-hand rotation settings move the right hand's position, so this compensates for them
    public Vector3 CalculateRightHandPosOffset()
    {
        // The vertical offset setting is 0-100. The horizontal one starts at 0; raising the slider makes it negative (down to -50),
        // and the slider fully to the left gives +50.
        float normalizedVertical = VRSettings.GetPrimaryHandVertOffset();
        // Integer division on purpose: the compensation only steps at multiples of 50. "/ 50f" would track fractional offsets.
        float normalizedHorizontal = (int)VRSettings.GetPrimaryHandHorOffset() / 50;
        if (normalizedVertical >= 50)
            // Map values from 50 to 100 to the range 0 to 1
            normalizedVertical = (normalizedVertical - 50) / 50;
        else
            // Map values from 0 to 50 to the range -1 to 0
            normalizedVertical = (normalizedVertical - 50) / 50;

        Vector3 baseOffset = (VRSettings.GetLeftHandedMode() ? new Vector3(0.03f, 0.05f, -0.045f) : new Vector3(0.025f, 0.04f, -0.02f));
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

        return baseOffset + adjustedOffset + new Vector3(0f, 0.01f, VRSettings.GetHandPosOffset());
    }

    public void SetScopeHighlight(Transform scopeTransform)
    {
        if (meshHighlighter == null)
        {
            meshHighlighter = VRGlobals.VRCam?.gameObject.GetComponent<HighLightMesh>();

            if (meshHighlighter == null)
            {
                UnityEngine.Debug.LogWarning("SetScopeHighlight: meshHighlighter is null");
                return;
            }
        }

        List<Part> scopeMeshList = new List<Part>();
        Renderer[] componentsInChildren = scopeTransform?.GetComponentsInChildren<Renderer>(includeInactive: false);

        if (componentsInChildren != null)
        {
            foreach (Renderer renderer in componentsInChildren)
            {
                if (renderer == null) continue;

                SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
                if (skinnedMeshRenderer != null && skinnedMeshRenderer.enabled)
                {
                    scopeMeshList.Add(new Part(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
                }
                else if (renderer is MeshRenderer && renderer.enabled &&
                         renderer.GetComponent<MeshFilter>() != null &&
                         renderer.GetComponent<MeshFilter>().sharedMesh != null)
                {
                    scopeMeshList.Add(new Part(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
                }
            }
        }

        if (scopeMeshList.Count > 0 && meshHighlighter != null)
        {
            meshHighlighter._targetParts = scopeMeshList.ToArray();
            meshHighlighter.enabled = true;
        }
    }

    public void RemoveScopeHighlight()
    {
        if (meshHighlighter != null)
        {
            meshHighlighter.enabled = false;
        }
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private int FindClosestTransform(Vector3 hitPoint)
    {
        int index = -1;
        float closestDistanceSqr = Mathf.Infinity;

        for (int i = 0; i < interactables.Count; i++) {
            if (interactables[i] == null) continue;
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
        List<InteractionAction> listComponents = new List<InteractionAction>();
        InteractionAction checkMagazine = new InteractionAction();
        checkMagazine.Name = "Check Magazine";
        InteractionAction reload = new InteractionAction();
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
        AvailableInteractionState magazineList = new AvailableInteractionState();
        magazineList.Actions = listComponents;
        weaponUiLists.Add(magazineList);
        if (magazine && magazine.childCount > 0)
        {
            if (magazine.GetComponent<MeshRenderer>())
                magazine.GetChild(0).position = magazine.GetComponent<MeshRenderer>().bounds.center;
            else if (meshList.Count > meshListLengthBeforeMags)
                interactables[interactables.Count - 1].localPosition = meshList[meshListLengthBeforeMags]._mesh.bounds.center;
        }
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public bool IsMagazineSet() {
        return (magazine || internalMag);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetChargingHandleOrBolt(Transform chargeOrBoltTransform, bool isBolt) {
        CreateInteractableMarker(chargeOrBoltTransform, "boltMarker");
        boltIndex = interactables.Count - 1;
        GetMesh(chargeOrBoltTransform);
        GetMalfunctionMeshes(chargeOrBoltTransform);
        List<InteractionAction> listComponents = new List<InteractionAction>();
        InteractionAction examineWeapon = new InteractionAction();
        examineWeapon.Name = "Examine Weapon";
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ExamineWeapon, out baseHandler);
        if (baseHandler != null)
        {
            ExamineWeaponHandler examineWeaponHandler = baseHandler as ExamineWeaponHandler;
            examineWeapon.Action = examineWeaponHandler.TriggerExamineWeapon;
        }
        listComponents.Add(examineWeapon);


        InteractionAction checkChamber = new InteractionAction();
        checkChamber.Name = "Check Chamber/Fix Malfunction";
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.CheckChamber, out baseHandler);
        if (baseHandler != null)
        {
            CheckChamberHandler checkChamberHandler = baseHandler as CheckChamberHandler;
            checkChamber.Action = checkChamberHandler.TriggerCheckChamber;
        }
        listComponents.Add(checkChamber);


        AvailableInteractionState chamberList = new AvailableInteractionState();
        chamberList.Actions = listComponents;
        weaponUiLists.Add(chamberList);
    }
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void SetFireModeSwitch(Transform fireModeSwitch) {
        CreateInteractableMarker(fireModeSwitch, "fireModeMarker");
        GetMesh(fireModeSwitch);
        List<InteractionAction> listComponents = new List<InteractionAction>();
        InteractionAction toggleFireMode = new InteractionAction();
        toggleFireMode.Name = "Toggle Fire Mode";
        IInputHandler baseHandler;
        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ChangeWeaponMode, out baseHandler);
        if (baseHandler != null)
        {
            FireModeHandler checkChamberHandler = baseHandler as FireModeHandler;
            toggleFireMode.Action = checkChamberHandler.TriggerChangeFireMode;
        }

        listComponents.Add(toggleFireMode);


        AvailableInteractionState fireModList = new AvailableInteractionState();
        fireModList.Actions = listComponents;
        weaponUiLists.Add(fireModList);
        this.fireModeSwitch = fireModeSwitch;
    }

    public Transform GetFireModeSwitch() { return this.fireModeSwitch; }

    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private void CreateInteractableMarker(Transform parent, string name)
    {
        GameObject marker = new GameObject(name);
        marker.transform.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
        marker.transform.parent = parent;
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localEulerAngles = new Vector3(0, 270, 0);
        // A reload removes the existing mag marker. Adding the new one at the end would put the list indexes out of line,
        // so it takes the removed marker's slot instead.
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
                meshList.Add(new Part(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                meshList.Add(new Part(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
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
                malfunctionMeshList.Add(new Part(null, skinnedMeshRenderer.transform, skinnedMeshRenderer));
            }
            else if (renderer is MeshRenderer && renderer.enabled)
            {
                malfunctionMeshList.Add(new Part(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform));
            }
        }
    }

}
