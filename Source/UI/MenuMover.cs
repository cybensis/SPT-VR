using UnityEngine;
using UnityEngine.EventSystems;

namespace TarkovVR.Source.UI
{
    internal class MenuMover : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private Vector3 offset;
        private Camera mainCamera;
        public GameObject commonUI;
        public GameObject menuUI;
        public GameObject preloaderUI;
        public Rigidbody raycastReceiver;
        public BoxCollider menuCollider;

        void Awake()
        {
            mainCamera = Camera.main; // Cache the main camera
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            Vector3 screenPoint = mainCamera.WorldToScreenPoint(transform.position);
            offset = transform.position - mainCamera.ScreenToWorldPoint(new Vector3(eventData.position.x, eventData.position.y, screenPoint.z));
            menuCollider.enabled = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            //Vector3 currentScreenPoint = new Vector3(eventData.position.x, eventData.position.y, mainCamera.WorldToScreenPoint(transform.position).z);
            //Vector3 currentPosition = mainCamera.ScreenToWorldPoint(currentScreenPoint) + offset;
            Vector3 newPos = eventData.worldPosition;
            newPos.y = transform.position.y;
            raycastReceiver.position = newPos;
            //menuCollider.center = commonUI.transform.position;
            transform.position = newPos;
            if (commonUI)
            {
                commonUI.transform.position = new Vector3(transform.position.x - 1.283f, commonUI.transform.position.y, transform.position.z - 0.0252f);
            }
            if (menuUI)
            {
                menuUI.transform.position = new Vector3(transform.position.x, menuUI.transform.position.y, transform.position.z);
            }
            if (preloaderUI)
            {
                preloaderUI.transform.position = new Vector3(transform.position.x, preloaderUI.transform.position.y, transform.position.z);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            menuCollider.enabled = true;
            menuCollider.center = commonUI.transform.position;
            // Optionally handle any cleanup or reset after dragging ends
        }
    }
}
