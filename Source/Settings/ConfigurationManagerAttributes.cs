namespace TarkovVR.Source.Settings
{
    // Tag for a BepInEx config entry, read by name by the Configuration Manager (F12): Browsable = false keeps the entry out of its
    // list (the VR settings menu shows it instead). Only the fields used are declared.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
