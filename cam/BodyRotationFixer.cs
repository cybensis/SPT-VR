
using EFT;
using EFT.Hideout;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Ragfair;
using System.Collections.Generic;
using System.Reflection;
using TarkovVR.Input;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

namespace TarkovVR.cam
{
    internal class BodyRotationFixer : MonoBehaviour, IPhysicsTrigger
    {
        private float x = 0.5f;
        private float y = 2f;
        private float rayDistance = 100f;
        private int i = 0;
        private PointerEventData pointerData;
        private GameObject lastHighlightedObject;
        private GameObject dragObject;
        public Vector3 pos;
        public Vector2 uiPointerPos;
        public float timeHeld = 0f;
        private Vector3 pressPosition;
        private GameObject pressedObject;


        public bool rotated = false;
        public float rightJoyTimeHeld = 0f;

        private bool rightClickTriggered = false;
        private float timeAButtonHeld = 0f;

        string IPhysicsTrigger.Description => throw new System.NotImplementedException();
        public bool swapWeapon = false;
        public void Update()
        {
            if (dragObject != null && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > 0.7)
            {
                rightJoyTimeHeld += Time.deltaTime;
            }
            else if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) < 0.7) {
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
                PointerEventData eventData = new PointerEventData(EventSystem.current);
                GameObject hitObject = RaycastFindHit(hit, ref eventData);
                Plugin.MyLog.LogWarning("WDWD: " + hit.collider.gameObject.name + " " + hitObject?.name + " " + hit.point);
                eventData.worldPosition = hit.point;
                
                if (hitObject)
                {
                    eventData.pointerEnter = hitObject;
                    // Handle OnEnter and OnExit events
                    if (lastHighlightedObject != hitObject)
                    {
                        if (lastHighlightedObject != null)
                            ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);

                        if (dragObject == null) { 
                            ExecuteEvents.Execute(hitObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                            lastHighlightedObject = hitObject;
                        }
                    }
                    if (SteamVR_Actions._default.ButtonA.stateDown)
                    {
                        pressedObject = hitObject;
                    }

                    if (SteamVR_Actions._default.ButtonA.state)
                    {
                        timeAButtonHeld += Time.deltaTime;
                        if (!rightClickTriggered && timeAButtonHeld > 0.25) { 
                            rightClickTriggered = true;
                            eventData.pressPosition = hit.point;
                            eventData.button = PointerEventData.InputButton.Right;
                            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                        }
                    }
                    else {
                        timeAButtonHeld = 0f;
                        rightClickTriggered = false;
                    }

                    if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) > 0.7 && hitObject.GetComponentInParent<ScrollRectNoDrag>() != null) {
                        eventData.scrollDelta = new Vector2(0, SteamVR_Actions._default.RightJoystick.axis.y);
                        ExecuteEvents.Execute(hitObject.GetComponentInParent<ScrollRectNoDrag>().gameObject, eventData, ExecuteEvents.scrollHandler);
                    }
                    
                    
                    if (dragObject == null && !rightClickTriggered &&  SteamVR_Actions._default.ButtonA.stateUp && pressedObject == hitObject)
                    {
                        ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                    }
                    //if (SteamVR_Actions._default.ButtonB.state)
                    //{
                    //    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerDownHandler);
                    //}

                    // Handle start dragging
                    if (SteamVR_Actions._default.RightTrigger.axis > 0.7) { 
                        pressedObject = hitObject;
                    }
                    else { 
                        timeHeld = 0;
                        if (dragObject) {
                            eventData.pointerDrag = null;
                            eventData.dragging = false;
                            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.endDragHandler);
                            dragObject = null;
                        }
                    }

                    if (dragObject && SteamVR_Actions._default.ButtonB.stateUp)
                    {
                        dragObject = null;
                        timeHeld = 0;
                        eventData.pointerDrag = null;
                        eventData.dragging = false;
                        ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.cancelHandler);
                    }
                }
                // Handle dragging
                else if (lastHighlightedObject != null)
                {
                    ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);
                    lastHighlightedObject = null;
                }
                //if (!startDrag && dragObject && SteamVR_Actions._default.ButtonA.stateUp && Time.deltaTime - timeSinceLastPress > 0.2)
                //{
                //    eventData.button = PointerEventData.InputButton.Left;
                //    ExecuteEvents.Execute(dragObject, eventData, ExecuteEvents.endDragHandler);
                //}
                if (SteamVR_Actions._default.RightTrigger.axis > 0.7 && (dragObject || pressedObject == hitObject) )
                {
                    //Plugin.MyLog.LogWarning("state down");
                    timeHeld += Time.deltaTime;
                    if (dragObject == null && timeHeld > 0.2)
                    {
                        pressPosition = hit.point;
                        eventData.dragging = true;
                        eventData.pressPosition = pressPosition;
                        ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.beginDragHandler);
                        dragObject = hitObject;
                    }
                }
                else if (SteamVR_Actions._default.RightTrigger.axis < 0.7 && dragObject)
                {
                    eventData.dragging = true;
                    eventData.pressPosition = hit.point;
                    ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.endDragHandler);
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
            //RaycastHit hit2;
            //Vector3 direction = transform.forward;
            //if (Physics.SphereCast(transform.position, x, direction, out hit2, y))
            //{
            //            Plugin.MyLog.LogWarning($"Hand is near: {hit2.collider.gameObject.name}");
            //}

            Collider[] nearbyColliders = Physics.OverlapSphere(transform.position, 0.125f);
            swapWeapon = false;
            foreach (Collider collider in nearbyColliders) {
                if (collider.gameObject.layer == 3) { 
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                    if (SteamVR_Actions._default.RightGrip.state)
                        swapWeapon = true;
                }
            }
        }



        public void EndDrop()
        {
            dragObject = null;

        }

        public void OnTriggerEnter(Collider other)
        {
            // Add your detection logic here
            // You can identify specific objects by tag, name, or specific components
            Plugin.MyLog.LogWarning("Enter  " + other);
        }

        public bool Ginore(Collider other, Collider other2)
        {
            // Add your detection logic here
            // You can identify specific objects by tag, name, or specific components
            return Physics.GetIgnoreCollision(other, other2);
        }

        public void OnTriggerExit(Collider col)
        {
            Plugin.MyLog.LogWarning("Exit " + col);

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
            if (foundValidObject)
                return hitObject;
            else
                return null;
        }
        //else if (lastHighlightedObject != null)
        //{
        //    ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);
        //    lastHighlightedObject = null;
        //}
        //public override void Process()
        //{
        //    Plugin.MyLog.LogError("\n");
        //    var isHit = Physics.Raycast(
        //        CameraManager.RightHand.transform.position,
        //        CameraManager.RightHand.transform.forward,
        //        out var hit,
        //        rayDistance,
        //        LayerMask.GetMask("UI"));
        //    if (isHit)
        //    {
        //        Plugin.MyLog.LogWarning("WDWD: " + hit.collider.gameObject + "\n");
        //        i = 0;
        //    }
        //    else if (i < 10)
        //    {
        //        Plugin.MyLog.LogError("No hit\n");
        //        i++;
        //    }
        //}
    }
}
