using EFT;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VRManager
{
    internal class RaidVRPlayerManager : VRPlayerManager
    {
        public float rayDistance = 0.75f;
        // Interactable layer is 22
        // Dead bodies are 23
        //private LayerMask interactableLayerMask = (1 << 22) | (1 << 23); // Layer 22 and Layer 23
        //private GameObject cube;
        // When displaying the interactable UI it needs to be brought a bit closer to the player
        // otherwise it could end up inside the object
        public float dirMultiplier = 0.1f;
        // The center of the camera doesn't feel like it aligns with the center of my vision in VR
        // so use this to drop it down a little bit
        public float downwardOffset = 0.1f;
        private Vector3 interactUiPos;
        private bool raycastHit = false;

        //public void Awake()
        //{
        //    cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        //    cube.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);

        //}

        private void Update() {
            base.Update(); 
            if (interactionUi) {
                if (raycastHit && interactionUi.gameObject.active)
                {
                    interactionUi.position = interactUiPos;
                    interactionUi.LookAt(Camera.main.transform);
                    // Need to rotate 180 degrees otherwise it shows up backwards
                    interactionUi.Rotate(0, 180, 0);
                }
                else if (raycastHit && !interactionUi.gameObject.active)
                    raycastHit = false;
            }
        }



        public void PlaceUiInteracter()
        {

            // Verify if the current hit object is still the same after the delay
            Vector3 rayOrigin = Camera.main.transform.position;
            Vector3 rayDirection = Camera.main.transform.forward;
            RaycastHit hit;
            rayDirection.y -= downwardOffset;
            float adjustedRayDistance = rayDistance * GetDistanceMultiplier(rayDirection);
            if (Physics.Raycast(rayOrigin, rayDirection, out hit, adjustedRayDistance, GameWorld.int_0))
            {

                BoxCollider boxCollider = hit.collider as BoxCollider;
                Vector3 offsetDirection = (rayOrigin - hit.point).normalized;
                interactUiPos = hit.point + offsetDirection * dirMultiplier; // Adjust the offset distance as needed
                // If the interactable object has a box collider then use this to set the UI position to the very center
                // of the face of the collider the raycast has hit.
                if (boxCollider != null)
                {
                    Vector3 localHitPoint = hit.transform.InverseTransformPoint(hit.point);
                    Vector3 localCenter = boxCollider.center;

                    // Determine the hit face
                    Vector3 hitFaceCenter = localCenter;

                    float halfX = boxCollider.size.x / 2;
                    float halfY = boxCollider.size.y / 2;
                    float halfZ = boxCollider.size.z / 2;

                    if (Mathf.Abs(localHitPoint.x - localCenter.x) > halfX - 0.01f)
                        hitFaceCenter.x = localHitPoint.x > localCenter.x ? localCenter.x + halfX : localCenter.x - halfX;
                    if (Mathf.Abs(localHitPoint.y - localCenter.y) > halfY - 0.01f)
                        hitFaceCenter.y = localHitPoint.y > localCenter.y ? localCenter.y + halfY : localCenter.y - halfY;
                    if (Mathf.Abs(localHitPoint.z - localCenter.z) > halfZ - 0.01f)
                        hitFaceCenter.z = localHitPoint.z > localCenter.z ? localCenter.z + halfZ : localCenter.z - halfZ;

                    Vector3 worldHitFaceCenter = hit.transform.TransformPoint(hitFaceCenter);
                    interactUiPos = worldHitFaceCenter + (rayOrigin - worldHitFaceCenter).normalized * dirMultiplier;
                }
                    Plugin.MyLog.LogWarning(boxCollider + " | " + interactUiPos + " |  " + interactionUi);
                raycastHit = true;
                // Set the interactions UI position
            }



        }

        // We need to extend the raycast distance when the player is looking down because if a filing cabinet was in
        // front of them, the raycast distance might be enough to reach the top shelf, but standing in the same place
        // it might not be enough to reach the bottom.
        public float GetDistanceMultiplier(Vector3 rayDirection)
        {
            // Calculate the angle between the camera's forward direction and the horizontal plane
            float angle = Vector3.Angle(rayDirection, Vector3.down);

            // Increase the multiplier as the angle gets closer to looking down
            if (angle > 90)
            {
                angle = 180 - angle; // Adjust the angle range to [0, 90] for looking down
            }

            // Minimal increase when looking up (angle close to 90 or higher), significant increase when looking down (angle close to 0)
            float multiplier = 1.0f;
            if (angle < 90)
            {
                multiplier = 1 + (90 - angle) / 45; // Increase more rapidly when looking down
            }
            else if (angle > 90)
            {
                multiplier = 1 + (angle - 90) / 180; // Minimal increase when looking up
            }

            return multiplier;
        }

        //0.296 0.1104 -0.0803

        //protected override void SpawnHands()
        //{
        //    if(!RightHand)
        //    {
        //        RightHand = new GameObject("RightHand");
        //        RightHand.AddComponent<SteamVR_Behaviour_Pose>();
        //        //RightHand.AddComponent<SteamVR_Skeleton_Poser>();
        //        RightHand.transform.parent = VRGlobals.camHolder.transform.parent;
        //        //MenuPatches.vrUiInteracter = RightHand.AddComponent<VRUIInteracter>();


        //    }
        //    if (!LeftHand)
        //    {
        //        LeftHand = new GameObject("LeftHand");
        //        LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
        //        LeftHand.transform.parent = VRGlobals.camHolder.transform.parent;
        //    }
        //}
    }
}
