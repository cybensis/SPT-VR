using BepInEx;
using BepInEx.Logging;
using TarkovVR.Source.Dev;

namespace SptVrDevTools
{
    // SPT-VR's developer tools: every [Tunable] member of SPT-VR.dll (and the FikaSync module's knobs, FikaSyncKnobs) as a setting,
    // and the drawer behind SPT-VR's DebugDraw. Not for players: the mod runs the same without it, its debug views just draw nothing.
    [BepInPlugin(Guid, "SPT-VR Dev Tools", "1.0.0")]
    [BepInDependency("com.matsix.sptvr", BepInDependency.DependencyFlags.HardDependency)]
    // loaded after the FikaSync module when that's installed, so its knobs can be found
    [BepInDependency("com.matsix.sptvr.fikasync", BepInDependency.DependencyFlags.SoftDependency)]
    public class DevToolsPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.matsix.sptvr.devtools";
        internal static ManualLogSource Log;

        private TunableSettings settings;

        private void Awake()
        {
            Log = Logger;
            DebugDraw.Drawer = new ShapeDrawer();
            settings = new TunableSettings(Config);
            settings.Bind();
        }

        private void Update()
        {
            settings.Sync();
        }
    }
}
