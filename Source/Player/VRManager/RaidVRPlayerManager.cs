using EFT.Interactive;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    internal class RaidVRPlayerManager : VRPlayerManager
    {
        public float rayDistance = 0.75f;
        // The interaction UI is brought a bit toward the player, otherwise it can end up inside the object
        public float dirMultiplier = 0.1f;
        // The camera's centre doesn't line up with the centre of vision in VR, so this drops the gaze ray a little
        public float downwardOffset = 0.1f;
        private Vector3 interactUiPos;
        private LootItem interactLootItem; // the loot item the menu is for, so it can track its movement
        private bool raycastHit = false;
        public bool positionTransitUi;
        private Quaternion camRotation;



        private void Update() {
            base.Update();
            if (interactionUi && (!WeaponPatches.currentGunInteractController || !WeaponPatches.currentGunInteractController.highlightingMesh))
            {
                if (positionTransitUi) {
                    float yRotationDifference = Mathf.Abs(Quaternion.Angle(VRGlobals.VRCam.transform.localRotation, camRotation));

                    if (yRotationDifference > 30)
                    {

                        camRotation = VRGlobals.VRCam.transform.localRotation;

                        // Set position, not local position, so it doesn't inherit the rotated position from camRoot
                        interactionUi.position = VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward * 0.4f + VRGlobals.VRCam.transform.up * -0.2f + VRGlobals.VRCam.transform.right * 0;
                        interactionUi.LookAt(VRGlobals.VRCam.transform);
                        // Need to rotate 180 degrees otherwise it shows up backwards
                        interactionUi.Rotate(0, 180, 0);
                    }
                }
                else if (raycastHit && interactionUi.gameObject.active)
                {
                    // Keep the menu centred on the loot item as it moves (e.g. while held): the raycast only fires
                    // PlaceUiInteracter on change.
                    if (interactLootItem != null && interactLootItem._boundCollider != null)
                    {
                        Vector3 c = interactLootItem._boundCollider.bounds.center;
                        interactUiPos = c + (VRGlobals.VRCam.transform.position - c).normalized * dirMultiplier;
                    }
                    interactionUi.position = interactUiPos;
                    interactionUi.LookAt(VRGlobals.VRCam.transform);
                    // Need to rotate 180 degrees otherwise it shows up backwards
                    interactionUi.Rotate(0, 180, 0);
                }
                else if (raycastHit && !interactionUi.gameObject.active)
                    raycastHit = false;

            }
            else if (positionTransitUi && !interactionUi.gameObject.active)
            {
                positionTransitUi = false;
            }
        }


        public override void PositionLeftWristUi()
        {
            leftWristUi.transform.SetParent(InitVRPatches.leftWrist, false);
            leftWristUi.transform.localPosition = new Vector3(-0.1f, 0.04f, 0.035f);
            leftWristUi.transform.localEulerAngles = new Vector3(304, 180, 180);

            UIPatches.extractionTimerUi.transform.SetParent(leftWristUi.transform, false);
            UIPatches.extractionTimerUi.transform.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.037f, 0.12f, -0.015f) : new Vector3(0.01f, 0.12f, -0.05f);
            UIPatches.extractionTimerUi.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(307, 81, 20) : new Vector3(18, 183, 285);
            UIPatches.extractionTimerUi._mainContainer.localEulerAngles = new Vector3(0, 342, 0);
            UIPatches.extractionTimerUi.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);

            UIPatches.healthPanel.transform.SetParent(leftWristUi.transform, false);
            UIPatches.healthPanel.transform.localPosition = Vector3.zero;
            UIPatches.healthPanel.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(270, 269, 180) : new Vector3(270, 87, 0);
            UIPatches.healthPanel.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);

            UIPatches.stancePanel.transform.SetParent(leftWristUi.transform, false);
            UIPatches.stancePanel.transform.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.1f, 0, -0.075f) : new Vector3(0.1f, 0, 0.03f);
            UIPatches.stancePanel.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(90, 93, 180) : new Vector3(270, 87, 0);
            UIPatches.stancePanel.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);

            UIPatches.notifierUi.transform.SetParent(leftWristUi.transform, false);
            UIPatches.notifierUi.transform.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.1247f, 0f, 0.055f) : new Vector3(0.12f, 0f, -0.085f);
            UIPatches.notifierUi.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(90, 272, 0) : new Vector3(272, 163, 283);
            UIPatches.notifierUi.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);
        }
        public void PlaceUiInteracter(RaycastHit hit)
        {

            Vector3 rayOrigin = VRGlobals.VRCam.transform.position;

                BoxCollider boxCollider = hit.collider as BoxCollider;
                Vector3 offsetDirection = (rayOrigin - hit.point).normalized;
                interactUiPos = hit.point + offsetDirection * dirMultiplier;

                // Loose loot items: anchor the menu to the item's bound-collider centre, not the face the gaze ray hit.
                // Remembered so Update keeps it on the item as it moves.
                LootItem lootItem = hit.collider.GetComponentInParent<LootItem>();
                interactLootItem = (lootItem != null && lootItem._boundCollider != null) ? lootItem : null;
                if (interactLootItem != null)
                {
                    Vector3 itemCenter = lootItem._boundCollider.bounds.center;
                    interactUiPos = itemCenter + (rayOrigin - itemCenter).normalized * dirMultiplier;
                    raycastHit = true;
                    return;
                }

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
                raycastHit = true;
        }

        public void StopPlacingUi() { raycastHit = false; interactLootItem = null; }


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
    }
}
