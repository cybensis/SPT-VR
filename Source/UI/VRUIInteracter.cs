using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Health;
using EFT.UI.Ragfair;
using EFT.UI.Utilities.LightScroller;
using System;
using System.Collections.Generic;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valve.VR;
using TarkovVR.Source.Controls;
using static RootMotion.FinalIK.HitReaction;

namespace TarkovVR.Source.UI
{
    internal class VRUIInteracter : MonoBehaviour
    {

        private float rayDistance = 100f;

        // The trigger holding an item drag: one state for this pointer and the item views (FixDragAndDropButton). They used to read
        // 0.5 and 0.7, so a light press in between began the drag here and ended it there, every frame (headset: flickers between
        // grabbing and not). The gap between press and release keeps a trigger resting at one value from flickering it too.
        public static float dragTriggerPress = 0.5f;
        public static float dragTriggerRelease = 0.3f;
        private static bool dragTriggerHeld;
        private static int dragTriggerFrame = -1;

        public static bool DragTriggerHeld()
        {
            if (dragTriggerFrame != Time.frameCount)
            {
                dragTriggerFrame = Time.frameCount;
                float axis = VRSettings.GetLeftHandedMode() ? VRInput.LeftTrigger.axis : VRInput.RightTrigger.axis;
                dragTriggerHeld = dragTriggerHeld ? axis > dragTriggerRelease : axis >= dragTriggerPress;
            }
            return dragTriggerHeld;
        }

        // Drag an item off the inventory panel and release to drop it on the ground. RAID ONLY
        // (gated on RaidVRPlayerManager in DropDraggedItemToGround) — there is no world to drop into
        // in the hideout/stash/loadout, and those use HideoutVRPlayerManager / no local Player, so
        // the gesture is a no-op there. Live-tunable for A/B; promote to a VR-settings toggle later.
        public static bool enableDragToDrop = true;

        // A release over dead space only counts as a drop once the aim has been dragged this far
        // (world metres) from the item's origin slot — measured on the inventory panel's plane. A
        // release closer than this just cancels, so a fumbled near-miss move doesn't drop the item.
        // Releasing over an actual grid/slot is always a move regardless of distance, so this gate
        // only governs dead-space/off-panel releases. The panel is ~1.5 m wide in world space
        // (canvas * 0.0006 scale), so ~0.2 m ≈ "clearly dragged off the grid". Tune in-headset.
        public static float dropDistance = 0.5f;
        // Where the drag started (origin slot) + the last point/normal the ray touched the panel,
        // used to measure how far the aim has been dragged for the drop gate.
        private Vector3 dragStartPanelPoint;
        private Vector3 lastUiHitPoint;
        private Vector3 lastUiHitNormal;

        private PointerEventData eventData;
        public GameObject lastHighlightedObject;
        private GameObject dragObject;
        public GameObject hitObject;
        public Vector3 uiPointerPos;
        public float timeHeld = 0f;
        public Vector3 pressPosition;
        private GameObject pressedObject;

        private float lastClickTime = 0f;
        private int clickCount = 0;

        public bool rotated = false;
        public float rightJoyTimeHeld = 0f;

        private bool rightClickTriggered = false;
        private float timeAButtonHeld = 0f;

        //private Transform raycastSource;

        //private void Awake()
        //{
        //    if (this.transform.GetChild(0) != null)
        //        raycastSource = this.transform.GetChild(0);
        //    else
        //        raycastSource = this.transform;

        //}
        public void Update()
        {
            float primaryHandJoystickAxis = VRSettings.GetLeftHandedMode() ? VRInput.LeftJoystick.axis.x : VRInput.RightJoystick.axis.x;

            if (dragObject != null && Mathf.Abs(primaryHandJoystickAxis) > 0.7)
            {
                rightJoyTimeHeld += Time.deltaTime;
            }
            else if (Mathf.Abs(primaryHandJoystickAxis) < 0.7)
            {
                rightJoyTimeHeld = 0;
                rotated = false;
            }

            bool isHit = Physics.Raycast(
                transform.position,
                transform.forward,
                out var hit,
                rayDistance,
                LayerMask.GetMask("UI"));
            if (isHit)
            {
                //i++;
                eventData = new PointerEventData(EventSystem.current);
                hitObject = RaycastFindHit(hit, ref eventData);
                //if (hitObject)
                //    Plugin.MyLog.LogWarning("HIT:    " + hit.collider + "    |   LAST HIT:    " + hitObject.name);
                eventData.worldPosition = hit.point;
                // Remember the panel surface the ray is on, so the off-panel drop gate can measure
                // how far the aim later slides past it.
                lastUiHitPoint = hit.point;
                lastUiHitNormal = hit.normal;

                if (hitObject)
                {
                    eventData.pointerEnter = hitObject;
                    handleOnEnterExit();
                    handleButtonClick(hit.point);
                    handleUIScrollwheel();
                }
                // Handle on exit if applicable
                else if (lastHighlightedObject != null)
                {
                    ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);
                    lastHighlightedObject = null;
                }
                if (hitObject || dragObject)
                    handleDragging(hit.point);

            }
            // Ray has left every UI collider while a drag is in progress: the user has pulled the
            // item off the inventory panel into open space. Keep the drag alive while the trigger is
            // held; releasing here drops the item to the ground (raid only). Without this branch an
            // off-panel release does nothing because the whole drag pipeline lives under `if (isHit)`.
            else if (enableDragToDrop && dragObject != null)
            {
                HandleOffPanelDrag();
            }
            //else
            //{
            //    Plugin.MyLog.LogError("NO HIT");
            //}
        }

        public void EndDrop()
        {
            dragObject = null;
        }

        // Handle OnEnter and OnExit events
        private void handleOnEnterExit()
        {
            if (lastHighlightedObject != hitObject)
            {
                if (lastHighlightedObject != null)
                    ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);

                if (dragObject == null || hitObject.GetComponent<HealthBarButton>())
                {
                    PointerEventData newEnterData = new PointerEventData(EventSystem.current);
                    newEnterData.pointerDrag = dragObject;
                    ExecuteEvents.Execute(hitObject, newEnterData, ExecuteEvents.pointerEnterHandler);
                    lastHighlightedObject = hitObject;
                }
            }
        }

        private void handleButtonClick(Vector2 hitPoint)
        {
            VRBoolean actionButton = VRSettings.GetLeftHandedMode()
                ? VRInput.ButtonX
                : VRInput.ButtonA;

            bool isPointerClick = !hitObject.GetComponent<EmptyItemView>();

            // Normalize object targeting for specific UI cases
            if (hitObject.transform.parent)
            {
                if (hitObject.name == "Toggle" && hitObject.transform.parent.parent.TryGetComponent(out CategoryView toggleCategory))
                    hitObject = toggleCategory._toggle.gameObject;
                else if (hitObject.transform.parent.TryGetComponent(out SubcategoryView _))
                    hitObject = hitObject.transform.parent.gameObject;
                else if (hitObject.name == "Main" && hitObject.transform.parent.TryGetComponent(out CategoryView _))
                    hitObject = hitObject.transform.parent.gameObject;
            }

            // Track button down and send OnPointerDown event
            if (actionButton.stateDown)
            {
                pressedObject = hitObject;
                eventData.button = PointerEventData.InputButton.Left;
                ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerDownHandler);
            }

            // Handle right-click (hold)
            if (actionButton.state)
            {
                timeAButtonHeld += Time.deltaTime;

                if (!rightClickTriggered && timeAButtonHeld > 0.25f)
                {
                    rightClickTriggered = true;
                    eventData.pressPosition = hitPoint;
                    pressPosition = eventData.worldPosition;
                    eventData.button = PointerEventData.InputButton.Right;
                    eventData.clickCount = 1;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                }
            }
            else
            {
                timeAButtonHeld = 0f;
                rightClickTriggered = false;
            }

            // Handle button release - send OnPointerUp event
            if (dragObject == null && !rightClickTriggered && actionButton.stateUp && pressedObject == hitObject)
            {
                eventData.button = PointerEventData.InputButton.Left;

                // Send OnPointerUp first (this triggers the toggle)
                ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerUpHandler);

                // Then send click event for other UI elements that need it
                if (isPointerClick)
                {
                    clickCount = (Time.time - lastClickTime <= 0.25f) ? clickCount + 1 : 1;
                    lastClickTime = Time.time;
                    eventData.clickCount = clickCount;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                }
            }
        }
        private void handleUIScrollwheel()
        {
            float primaryHandJoystickAxis = VRSettings.GetLeftHandedMode() ? VRInput.LeftJoystick.axis.y : VRInput.RightJoystick.axis.y;
            if (Mathf.Abs(primaryHandJoystickAxis) > 0.4 && hitObject.GetComponentInParent<ScrollRectNoDrag>() != null)
            {
                eventData.scrollDelta = new Vector2(0, primaryHandJoystickAxis / 1.5f);
                ExecuteEvents.Execute(hitObject.GetComponentInParent<ScrollRectNoDrag>().gameObject, eventData, ExecuteEvents.scrollHandler);
            }
            if (Mathf.Abs(primaryHandJoystickAxis) > 0.4 && hitObject.GetComponentInParent<ScrollRect>() != null)
            {
                eventData.scrollDelta = new Vector2(0, primaryHandJoystickAxis / 1.5f);
                ExecuteEvents.Execute(hitObject.GetComponentInParent<ScrollRect>().gameObject, eventData, ExecuteEvents.scrollHandler);
            }
            if (Mathf.Abs(primaryHandJoystickAxis) > 0.4 && hitObject.GetComponentInParent<LightScroller>() != null)
            {
                eventData.scrollDelta = new Vector2(0, primaryHandJoystickAxis / 1.5f);
                ExecuteEvents.Execute(hitObject.GetComponentInParent<LightScroller>().gameObject, eventData, ExecuteEvents.scrollHandler);
            }
        }

        private void handleDragging(Vector2 hitPoint)
        {
            // Use pressedObject to ensure that the object the user is trying to drag is the one they have selected,
            // not an object they selected then moved off from.
            if (DragTriggerHeld())
            {
                VRBoolean cancelButton = (VRSettings.GetLeftHandedMode()) ? VRInput.ButtonY : VRInput.ButtonB;

                if (dragObject == null)
                {
                    pressedObject = hitObject;
                    pressPosition = eventData.worldPosition;
                    dragStartPanelPoint = eventData.worldPosition; // origin slot, for the drop-distance gate
                    eventData.dragging = true;
                    eventData.pressPosition = hitPoint;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.beginDragHandler);
                    dragObject = hitObject;
                }
                else
                {
                    eventData.button = PointerEventData.InputButton.Left;
                    eventData.position = uiPointerPos;
                    eventData.pressPosition = pressPosition;
                    eventData.dragging = true;
                    ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.dragHandler);
                }
                if (dragObject && cancelButton.stateUp)
                    CancelDrag();

            }
            else if (dragObject)
                ReleaseDrag();
        }

        private void ReleaseDrag()
        {
            bool overDropTarget = hitObject != null &&
                (hitObject.GetComponentInParent<IContainerView>() != null ||
                 hitObject.GetComponentInParent<ItemView>() != null ||
                 hitObject.GetComponentInParent<IDropHandler>() != null);

            if (overDropTarget)
                DropItem();
            else if (enableDragToDrop && DragAimDistanceFromStart() >= dropDistance)
                DropDraggedItemToGround();
            else
                CancelDrag();
        }

        private void DropItem()
        {
            eventData.dragging = false;
            eventData.pointerDrag = dragObject;
            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.dropHandler);
            ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.endDragHandler);
            dragObject = null;
        }

        private void CancelDrag()
        {
            eventData.pointerDrag = null;
            eventData.dragging = false;
            ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.endDragHandler);
            dragObject = null;
        }

        // Mid-drag, but the pointer ray has left the inventory panel entirely (pointing off into the
        // world). Nothing valid is under the aim, so this is the same as a dead-space release: B/Y
        // cancels; releasing the trigger drops the item if it's been dragged far enough from origin.
        private void HandleOffPanelDrag()
        {
            bool triggerHeld = DragTriggerHeld();
            VRBoolean cancelButton = VRSettings.GetLeftHandedMode()
                ? VRInput.ButtonY
                : VRInput.ButtonB;

            if (cancelButton.stateUp)
            {
                CancelDrag();
                return;
            }
            if (triggerHeld)
                return; // still dragging

            if (DragAimDistanceFromStart() >= dropDistance)
                DropDraggedItemToGround();
            else
                CancelDrag();
        }

        // Distance (world metres) from where the drag started (the item's origin slot) to the current
        // aim, projected onto the panel plane. Same metric on-panel and off, so the drop gate behaves
        // consistently across the panel edge.
        private float DragAimDistanceFromStart()
        {
            return Vector3.Distance(ProjectAimOntoPanel(), dragStartPanelPoint);
        }

        // Intersect the hand ray with the inventory panel's plane (through the last on-panel hit
        // point, using its surface normal). On-panel this returns ~the current aim point; off-panel
        // it's the extrapolated point on the panel plane. Degenerate cases (ray parallel to / behind
        // the panel, or the panel was never touched) return a far-away point so the release drops.
        private Vector3 ProjectAimOntoPanel()
        {
            Vector3 origin = transform.position;
            Vector3 dir = transform.forward;
            if (lastUiHitNormal != Vector3.zero)
            {
                float denom = Vector3.Dot(dir, lastUiHitNormal);
                if (Mathf.Abs(denom) >= 1e-5f)
                {
                    float t = Vector3.Dot(lastUiHitPoint - origin, lastUiHitNormal) / denom;
                    if (t > 0f)
                        return origin + dir * t;
                }
            }
            return dragStartPanelPoint + dir * 9999f; // "way off" — always exceeds dropDistance
        }

        // Drop the currently-dragged inventory item onto the ground via the EXACT same path as
        // right-click -> Discard: the item's context-menu Discard interaction. In raid that throws
        // the item to the floor as a lootable world item (and FIKA-syncs); it also respects discard
        // limits, so quest/locked items can't be dropped (IsInteractionAvailable is false -> no-op).
        // (The earlier InteractionsHandlerClass.Discard call was the wrong, lower-level op — it
        // removed the item from inventory WITHOUT spawning it in the world, i.e. "it disappeared".)
        private void DropDraggedItemToGround()
        {
            GameObject dragged = dragObject;
            ItemView itemView = dragged.GetComponent<ItemView>() ?? dragged.GetComponentInParent<ItemView>();

            // Clean up the native drag ghost without moving the item (null target => no AcceptItem).
            if (eventData == null)
                eventData = new PointerEventData(EventSystem.current);
            eventData.button = PointerEventData.InputButton.Left;
            eventData.pointerEnter = null;
            eventData.pointerDrag = null;
            eventData.dragging = false;
            ExecuteEvents.Execute(dragged, eventData, ExecuteEvents.endDragHandler);
            dragObject = null;

            // RAID ONLY. Hideout/stash/loadout use HideoutVRPlayerManager or have no local Player —
            // never discard there. This is the single gate that keeps the gesture out of the hideout.
            if (!(VRGlobals.vrPlayer is RaidVRPlayerManager))
                return;
            if (itemView == null || itemView.Item == null)
                return;

            try
            {
                // NewContextInteractions == ItemUiContext.GetItemContextInteractions(ItemContext, null).
                var interactions = itemView.NewContextInteractions;
                if (interactions != null && interactions.IsInteractionAvailable(EItemInfoButton.Discard).Succeed)
                    interactions.ExecuteInteraction(EItemInfoButton.Discard);
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[VRDrop] drop-to-ground failed: {ex}");
            }
        }
        private GameObject RaycastFindHit(RaycastHit hit, ref PointerEventData eventData)
        {
            Camera mainCam = Camera.main;
            if (mainCam == null || EventSystem.current == null)
                return null;

            var pointerPosition = mainCam.WorldToScreenPoint(hit.point);
            //Plugin.MyLog.LogWarning("WDWD: " + pointerPosition + " " + hit.point);

            eventData.position = pointerPosition;
            //eventData.pressPosition = pointerPosition;
            uiPointerPos = hit.point;
            // Send a pointer enter event to the UI element
            List<RaycastResult> uiRaycasts = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, uiRaycasts);
            bool foundValidObject = false;
            GameObject hitObject = null;
            if (hit.collider.gameObject.name == "Cube")
            {
                return hit.collider.gameObject;
            }
            if (uiRaycasts.Count > 0)
            {
                hitObject = uiRaycasts[0].gameObject;
                //uiPointerPos = new Vector2(uiRaycasts[0].worldPosition.x, uiRaycasts[0].worldPosition.y);
                int i = 0;
                // Work up the parents to see if any of them are interactable
                while (i < 5)
                {

                    if (hitObject.GetComponent<IPointerEnterHandler>() != null || hitObject.GetComponent<IBeginDragHandler>() != null || hitObject.GetComponent<IDragHandler>() != null || hitObject.GetComponent<IContainerView>() != null)
                    {
                        foundValidObject = true;
                        break;
                    }
                    else if (hitObject.name == "Background Tile" || hitObject.name == "HighlightPanel")
                    {
                        hitObject = hitObject.transform.parent.gameObject;
                        foundValidObject = true;
                        break;
                    }
                    if (hitObject.transform.parent != null)
                        hitObject = hitObject.transform.parent.gameObject;
                    else
                    {
                        break;
                    }
                    i++;
                }
            }
            if (foundValidObject)
            {
                CameraViewporter vp = hitObject.GetComponentInParent<CameraViewporter>();
                if (vp != null && vp.TargetCamera != null)
                {
                    eventData.position = vp.TargetCamera.WorldToScreenPoint(hit.point);
                }
                return hitObject;
            }
            else
                return null;
        }

    }
}