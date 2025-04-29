using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Health;
using EFT.UI.Ragfair;
using EFT.UI.Utilities.LightScroller;
using System.Collections.Generic;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valve.VR;
using static RootMotion.FinalIK.HitReaction;

namespace TarkovVR.Source.UI
{
    internal class VRUIInteracter : MonoBehaviour
    {

        private float rayDistance = 100f;
        private static float DRAG_TRIGGER_THRESHOLD = 0.5f;

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
            float primaryHandJoystickAxis = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftJoystick.axis.x : SteamVR_Actions._default.RightJoystick.axis.x;

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
            SteamVR_Action_Boolean actionButton = VRSettings.GetLeftHandedMode()
                ? SteamVR_Actions._default.ButtonX
                : SteamVR_Actions._default.ButtonA;

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

            // Track button down
            if (actionButton.stateDown)
                pressedObject = hitObject;

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

            // Handle click release (left-click, with double-click logic)
            if (dragObject == null && !rightClickTriggered && actionButton.stateUp && pressedObject == hitObject)
            {
                eventData.button = PointerEventData.InputButton.Left;

                if (isPointerClick)
                {
                    clickCount = (Time.time - lastClickTime <= 0.25f) ? clickCount + 1 : 1;
                    lastClickTime = Time.time;
                    eventData.clickCount = clickCount;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                }
                else
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerDownHandler);
            }
        }

        private void handleUIScrollwheel()
        {
            float primaryHandJoystickAxis = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftJoystick.axis.y : SteamVR_Actions._default.RightJoystick.axis.y;
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
            if ((VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftTrigger.axis :  SteamVR_Actions._default.RightTrigger.axis) > DRAG_TRIGGER_THRESHOLD)
            {
                SteamVR_Action_Boolean cancelButton = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.ButtonY : SteamVR_Actions._default.ButtonB;

                if (dragObject == null)
                {
                    pressedObject = hitObject;
                    pressPosition = eventData.worldPosition;
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
            else if (dragObject && hitObject)
                DropItem();
            else if (dragObject)
                CancelDrag();



        }

        private void DropItem()
        {
            eventData.dragging = false;
            eventData.pointerDrag = dragObject;
            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.dropHandler);
            dragObject = null;
        }

        private void CancelDrag()
        {
            eventData.pointerDrag = null;
            eventData.dragging = false;
            ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.endDragHandler);
            dragObject = null;
        }
        private GameObject RaycastFindHit(RaycastHit hit, ref PointerEventData eventData)
        {
            var pointerPosition = Camera.main.WorldToScreenPoint(hit.point);
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

                    if (hitObject.GetComponent<IPointerEnterHandler>() != null || hitObject.GetComponent<IBeginDragHandler>() != null || hitObject.GetComponent<IDragHandler>() != null || hitObject.GetComponent<IContainer>() != null)
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
            if (foundValidObject) { 

                return hitObject;
            }
            else
                return null;
        }

    }
}