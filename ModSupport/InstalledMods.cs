using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovVR.ModSupport
{
    internal static class InstalledMods
    {
        //public static bool EFTApiInstalled { get; set; }
        public static bool AmandsGraphicsInstalled { get; set; }
        public static bool FIKAInstalled { get; set; }
        public static bool DynamicMapsInstalled { get; set; }
        public static bool WeaponCustomizerInstalled { get; set; }
        // HollywoodFX (com.janky.hollywoodfx). Soft dependency only: body-dragging keeps the ragdoll
        // live by itself (see HandsInteractionController.BodyGrab), so this is purely informational —
        // when present, HFX's corpse-physics tuning (bounce/drag/mass, no auto-sleep) makes the drag
        // feel better, and we skip a couple of tweaks it already owns.
        public static bool HollywoodFXInstalled { get; set; }

        static InstalledMods()
        {
            //EFTApiInstalled = false;
            AmandsGraphicsInstalled = false;
            FIKAInstalled = false;
            DynamicMapsInstalled = false;
            WeaponCustomizerInstalled = false;
            HollywoodFXInstalled = false;
        }
    }
}
