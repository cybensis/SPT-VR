using System;

namespace TarkovVR.Source.Dev
{
    // What a marked member is to SPT-VR-DevTools and the debug panel (Tools/debug_panel)
    public enum TunableKind
    {
        Switch,     // turns a behaviour on / off
        Tune,       // a number to tune
        Log,        // a debug log
        View,       // a debug view (DebugDraw shapes)
        Test,       // forces something for a test
        Action,     // a static method to run (no parameters)
        Status,     // a static property to read (shown, not set)
    }

    // Marks a static field (or a method for Action, a property for Status) for SPT-VR-DevTools: with that plugin installed each one
    // is a setting in its own config, in-game through Integrated Configuration Manager and on the desktop through ConfigurationManager,
    // under its section (every Log and View also in one section of their own). Without the plugin the marker does nothing.
    // Tools/debug_panel/gen_debug_panel.py builds the UnityExplorer panel from the same markers.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class TunableAttribute : Attribute
    {
        public TunableAttribute(string section, TunableKind kind, string description)
        {
            Section = section;
            Kind = kind;
            Description = description;
        }

        public string Section { get; }
        public TunableKind Kind { get; }
        public string Description { get; }
        public string Unit { get; set; }        // shown after the value: "m", "s", "°" ...
        public object Suggested { get; set; }   // the value the debug panel opens a log / test on, when not its default
    }
}
