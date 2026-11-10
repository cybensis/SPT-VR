
namespace TarkovVR.ModSupport
{
    internal static class InstalledMods
    {
        public static bool EFTApiInstalled { get; set; }
        public static bool AmandsGraphicsInstalled { get; set; }
        public static bool FIKAInstalled { get; set; }
        public static bool DynamicMapsInstalled { get; set; }
        public static bool WeaponCustomizerInstalled { get; set; }
        public static bool ORBITInstalled { get; set; }
        public static bool FuckThisCullingInstalled { get; set; }

        static InstalledMods()
        {
            EFTApiInstalled = false;
            AmandsGraphicsInstalled = false;
            FIKAInstalled = false;
            DynamicMapsInstalled = false;
            WeaponCustomizerInstalled = false;
            ORBITInstalled = false;
        }
    }
}
