namespace TarkovVR.Source.Settings
{
    // Tag for a BepInEx config entry, read by name by the Configuration Manager (F12). Browsable = false hides the entry from its
    // list, because the VR settings menu shows it instead. Only the fields this mod uses are declared.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
