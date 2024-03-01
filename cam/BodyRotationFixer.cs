
using EFT.Hideout;
using EFT.UI;
using System.Collections.Generic;
using TarkovVR.Input;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

namespace TarkovVR.cam
{
    internal class BodyRotationFixer : MonoBehaviour
    {
        private float x, y, z;
        private const float rayDistance = 30f;
        private int i = 0;
        private PointerEventData pointerData;
        private GameObject lastHighlightedObject;
        public void Update()
        {

            bool isHit = Physics.Raycast(
                transform.position,
                transform.forward,
                out var hit,
                rayDistance,
                LayerMask.GetMask("UI"));
            if (isHit)
            {
                //Plugin.MyLog.LogWarning("WDWD: " + hit.collider.gameObject + "\n");
                //i++;

                var pointerPosition = Camera.main.WorldToScreenPoint(hit.point);

                PointerEventData eventData = new PointerEventData(EventSystem.current);
                eventData.position = pointerPosition;

                // Send a pointer enter event to the UI element
                List<RaycastResult> uiRaycasts = new List<RaycastResult>();
                EventSystem.current.RaycastAll(eventData, uiRaycasts);
                if (uiRaycasts.Count > 0)
                {
                    GameObject hitObject = uiRaycasts[0].gameObject;
                    int i = 0;
                    bool foundValidObject = false;
                    while (i < 5)
                    {
                        if (hitObject.GetComponent<IPointerEnterHandler>() != null)
                        {
                            foundValidObject = true;
                            break;
                        }
                        if (hitObject.transform.parent != null)
                            hitObject = hitObject.transform.parent.gameObject;
                        else {
                            break;
                        }
                        i++;
                    }
                    if (foundValidObject) { 
                        if (lastHighlightedObject != hitObject)
                        {
                            if (lastHighlightedObject != null)
                                ExecuteEvents.Execute(lastHighlightedObject, eventData, ExecuteEvents.pointerExitHandler);

                            ExecuteEvents.Execute(hitObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                            lastHighlightedObject = hitObject;
                        }
                        if (SteamVR_Actions._default.ButtonA.stateDown)
                        {
                            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerDownHandler);
                        }
                        if (SteamVR_Actions._default.ButtonA.stateUp)
                        {
                            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.Execute(hitObject, eventData, ExecuteEvents.pointerClickHandler);
                        }
                    }
                }
                else
                    lastHighlightedObject = null;
            }
            else
                lastHighlightedObject = null;
        }
            //public override void Process()
            //{
            //Plugin.MyLog.LogError("\n");
            //var isHit = Physics.Raycast(
            //    CameraManager.RightHand.transform.position,
            //    CameraManager.RightHand.transform.forward,
            //    out var hit,
            //    rayDistance,
            //    LayerMask.GetMask("UI"));
            //if (isHit)
            //{
            //    Plugin.MyLog.LogWarning("WDWD: " + hit.collider.gameObject + "\n");
            //    i = 0;
            //}
            //else if (i < 10){ 
            //    Plugin.MyLog.LogError("No hit\n");
            //    i++;
            //}
            // }
        }
}
