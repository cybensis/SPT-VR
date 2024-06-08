using EFT.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

namespace TarkovVR.Source.UI
{
    internal class VRUIInteracter : MonoBehaviour
    {

        private float rayDistance = 100f;

        private PointerEventData eventData;
        private GameObject lastHighlightedObject;
        private GameObject dragObject;
        private GameObject hitObject;
        public Vector3 pos;
        public Vector3 uiPointerPos;
        public float timeHeld = 0f;
        private Vector3 pressPosition;
        private GameObject pressedObject;


        public bool rotated = false;
        public float rightJoyTimeHeld = 0f;

        private bool rightClickTriggered = false;
        private float timeAButtonHeld = 0f;

        public void Update()
        {
            if (dragObject != null && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > 0.7)
            {
                rightJoyTimeHeld += Time.deltaTime;
            }
            else if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) < 0.7)
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
                //Plugin.MyLog.LogWarning("WDWD: " + hit.collider.gameObject.name + " " + hitObject?.name + " " + hit.point);
                eventData.worldPosition = hit.point;

                if (hitObject)
                {
                    eventData.pointerEnter = hitObject;
                    handleOnEnterExit();
                    handleButtonClick(hit.point);
                    handleUIScrollwheel();
                    handleNewHitDragging();
                }
                // Handle on exit if applicable
                else if (lastHighlightedObject != null)
                {
                    ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);
                    lastHighlightedObject = null;
                }
                handleMainDragging(hit.point);

            }

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

                if (dragObject == null)
                {
                    ExecuteEvents.Execute(hitObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                    lastHighlightedObject = hitObject;
                }
            }
        }

        private void handleButtonClick(Vector2 hitPoint)
        {
            if (SteamVR_Actions._default.ButtonA.stateDown)
            {
                pressedObject = hitObject;
            }

            if (SteamVR_Actions._default.ButtonA.state)
            {
                timeAButtonHeld += Time.deltaTime;
                if (!rightClickTriggered && timeAButtonHeld > 0.25)
                {
                    rightClickTriggered = true;
                    eventData.pressPosition = hitPoint;
                    eventData.button = PointerEventData.InputButton.Right;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                }
            }
            else
            {
                timeAButtonHeld = 0f;
                rightClickTriggered = false;
            }
            if (dragObject == null && !rightClickTriggered && SteamVR_Actions._default.ButtonA.stateUp && pressedObject == hitObject)
            {
                ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
            }
        }

        private void handleUIScrollwheel()
        {
            if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) > 0.7 && hitObject.GetComponentInParent<ScrollRectNoDrag>() != null)
            {
                eventData.scrollDelta = new Vector2(0, SteamVR_Actions._default.RightJoystick.axis.y);
                ExecuteEvents.Execute(hitObject.GetComponentInParent<ScrollRectNoDrag>().gameObject, eventData, ExecuteEvents.scrollHandler);
            }
        }

        private void handleNewHitDragging()
        {
            // Use pressedObject to ensure that the object the user is trying to drag is the one they have selected,
            // not an object they selected then moved off from.
            if (SteamVR_Actions._default.RightTrigger.axis > 0.7)
            {
                pressedObject = hitObject;
            }
            else
            {
                timeHeld = 0;
                if (dragObject)
                {
                    cancelDrag();
                }
            }

            if (dragObject && SteamVR_Actions._default.ButtonB.stateUp)
            {
                timeHeld = 0;
                cancelDrag();
            }
        }

        private void cancelDrag()
        {
            eventData.pointerDrag = null;
            eventData.dragging = false;
            ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.endDragHandler);
            dragObject = null;
        }

        private void handleMainDragging(Vector2 hitPoint)
        {
            if (SteamVR_Actions._default.RightTrigger.axis > 0.7 && (dragObject || pressedObject == hitObject))
            {
                //Plugin.MyLog.LogWarning("state down");
                timeHeld += Time.deltaTime;
                if (dragObject == null && timeHeld > 0.2)
                {
                    pressPosition = hitPoint;
                    eventData.dragging = true;
                    eventData.pressPosition = pressPosition;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.beginDragHandler);
                    dragObject = hitObject;
                }
            }
            else if (SteamVR_Actions._default.RightTrigger.axis < 0.7 && dragObject)
            {
                //eventData.dragging = true;
                //eventData.pressPosition = hitPoint;
                //ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.endDragHandler);
                cancelDrag();
            }
            if (dragObject)
            {
                eventData.button = PointerEventData.InputButton.Left;
                eventData.position = uiPointerPos;
                eventData.pressPosition = pressPosition;
                eventData.dragging = true;
                ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.dragHandler);
            }
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
                    if (hitObject.GetComponent<IPointerEnterHandler>() != null || hitObject.GetComponent<IBeginDragHandler>() != null || hitObject.GetComponent<IDragHandler>() != null || hitObject.GetComponent<GInterface322>() != null)
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