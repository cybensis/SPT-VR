using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovVR.ModSupport
{
    internal static class InstalledMods
    {
        public static bool EFTApiInstalled { get; set; }
        public static bool AmandsGraphicsInstalled { get; set; }
        public static bool FIKAInstalled { get; set; }

        static InstalledMods()
        {
            EFTApiInstalled = false;
            AmandsGraphicsInstalled = false;
            FIKAInstalled = false;
        }
    }
}
