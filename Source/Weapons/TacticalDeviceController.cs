using EFT.InventoryLogic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovVR.Source.Weapons
{
    internal class TacticalDeviceController
    {
        private TacticalComboVisualController tacDeviceController;
        private FirearmsAnimator animator;
        public TacticalDeviceController(TacticalComboVisualController controller, FirearmsAnimator animator) { 
            tacDeviceController = controller;
            this.animator = animator;
        }

        public void ToggleTacDevice() {
            animator.ModToggleTrigger();
            tacDeviceController.LightMod.IsActive = !tacDeviceController.LightMod.IsActive;
            tacDeviceController.UpdateBeams(true);  
        }

        public void ChangeTacDeviceSetting() {
            animator.ModToggleTrigger();
            if (tacDeviceController.laserBeam_0.Count() > 0) 
                tacDeviceController.LightMod.SelectedMode = (tacDeviceController.LightMod.SelectedMode + 1) % tacDeviceController.laserBeam_0.Count();
            tacDeviceController.UpdateBeams(true);
        }
    }
}
