using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using TarkovVR.Source.Dev;
using UnityEngine;

namespace SptVrDevTools
{
    // Every [Tunable] member of SPT-VR.dll (plus FikaSyncKnobs) as an entry of this plugin's config. Sections: the marker's own, except
    // that every Log is under "1 Logs" and every View under "2 Debug views" (one place to switch them), Status under "3 Status".
    // A setting changed in the config is written to its field at once; a field the mod (or UnityExplorer) changes is copied back to its
    // setting. Values start at the mod's own defaults each launch unless "Keep values after restart" is on.
    internal sealed class TunableSettings
    {
        private const string ToolsSection = "0 Dev tools";
        private const string LogsSection = "1 Logs";
        private const string ViewsSection = "2 Debug views";
        private const string StatusSection = "3 Status";
        private const float SyncInterval = 0.25f;

        private sealed class Binding
        {
            public string key;
            public TunableKind kind;
            public FieldInfo field;
            public PropertyInfo property;
            public MethodInfo method;
            public ConfigEntryBase entry;
            public Func<object, object> toEntry;    // field value -> setting value
            public Func<object, object> toField;    // setting value -> field value (null = can't set)
            public object lastFieldValue;
        }

        private readonly ConfigFile config;
        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Dictionary<ConfigEntryBase, Binding> byEntry = new Dictionary<ConfigEntryBase, Binding>();
        private readonly HashSet<string> usedKeys = new HashSet<string>();
        private ConfigEntry<bool> keepValues, allLogsOff, allViewsOff;
        private int order = 100000;
        private bool applying;
        private float nextSync;

        internal TunableSettings(ConfigFile config)
        {
            this.config = config;
        }

        internal void Bind()
        {
            config.SaveOnConfigSet = false;
            keepValues = config.Bind(ToolsSection, "Keep values after restart", false, Describe(
                "Off: every setting here starts at the mod's own default each launch, like an UnityExplorer edit. On: what you set stays.", false));
            allLogsOff = config.Bind(ToolsSection, "Turn every log off", false, Describe("Sets every setting under 1 Logs to off.", false));
            allViewsOff = config.Bind(ToolsSection, "Turn every debug view off", false, Describe("Sets every setting under 2 Debug views to off.", false));
            allLogsOff.Value = false;
            allViewsOff.Value = false;

            Assembly mod = typeof(TunableAttribute).Assembly;
            foreach (Type type in LoadableTypes(mod))
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (MemberInfo member in type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags)).Concat(type.GetMethods(flags)))
                {
                    TunableAttribute marker;
                    try { marker = (TunableAttribute)member.GetCustomAttributes(typeof(TunableAttribute), false).FirstOrDefault(); }
                    catch { continue; }
                    if (marker != null)
                        TryBind(type, member, marker.Section, marker.Kind, marker.Description, marker.Unit);
                }
            }
            foreach (FikaSyncKnobs.Knob knob in FikaSyncKnobs.All)
            {
                Type type = FindType(knob.assembly, knob.type);
                FieldInfo field = type?.GetField(knob.field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    TryBind(type, field, knob.section, knob.kind, knob.description + " (FikaSync module: acts on the other players' VR bodies / doors.)", knob.unit);
            }

            bool keep = keepValues.Value;
            foreach (Binding binding in bindings)
            {
                try
                {
                    if (binding.field == null)
                        continue;
                    if (keep && binding.toField != null)
                        binding.field.SetValue(null, binding.toField(binding.entry.BoxedValue));
                    else
                        binding.entry.BoxedValue = binding.entry.DefaultValue;
                    binding.lastFieldValue = binding.field.GetValue(null);
                }
                catch (Exception exception)
                {
                    DevToolsPlugin.Log.LogWarning($"[DevTools] {binding.key}: {exception.Message}");
                }
            }
            config.Save();
            config.SettingChanged += OnSettingChanged;
            DevToolsPlugin.Log.LogInfo($"[DevTools] {bindings.Count} settings bound");
        }

        // Every quarter second: a field changed by the mod (a capture flag resetting itself, a live status) shows in its setting
        internal void Sync()
        {
            if (Time.unscaledTime < nextSync)
                return;
            nextSync = Time.unscaledTime + SyncInterval;
            applying = true;
            try
            {
                foreach (Binding binding in bindings)
                {
                    object current;
                    try { current = binding.field != null ? binding.field.GetValue(null) : binding.property?.GetValue(null); }
                    catch { continue; }
                    if (binding.method != null || Equals(current, binding.lastFieldValue))
                        continue;
                    binding.lastFieldValue = current;
                    binding.entry.BoxedValue = binding.toEntry(current);
                }
            }
            finally
            {
                applying = false;
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs change)
        {
            if (applying)
                return;
            ConfigEntryBase entry = change.ChangedSetting;
            if (entry == allLogsOff || entry == allViewsOff)
            {
                if ((bool)entry.BoxedValue)
                    SwitchOff(entry == allLogsOff ? TunableKind.Log : TunableKind.View);
                Set(entry, false);
                config.Save();
                return;
            }
            if (entry == keepValues)
            {
                config.Save();
                return;
            }
            if (!byEntry.TryGetValue(entry, out Binding binding))
                return;
            try
            {
                if (binding.method != null)
                {
                    if ((bool)entry.BoxedValue)
                    {
                        Set(entry, false);
                        binding.method.Invoke(null, null);
                    }
                    return;
                }
                if (binding.field == null || binding.toField == null)
                {
                    Set(entry, binding.toEntry(binding.lastFieldValue));   // a status: shown, not set
                    return;
                }
                binding.field.SetValue(null, binding.toField(entry.BoxedValue));
                binding.lastFieldValue = binding.field.GetValue(null);
                config.Save();
            }
            catch (Exception exception)
            {
                DevToolsPlugin.Log.LogWarning($"[DevTools] {binding.key}: {exception.Message}");
            }
        }

        private void SwitchOff(TunableKind kind)
        {
            foreach (Binding binding in bindings.Where(b => b.kind == kind && b.field != null && b.field.FieldType == typeof(bool)))
            {
                binding.field.SetValue(null, false);
                binding.lastFieldValue = false;
                Set(binding.entry, false);
            }
        }

        private void Set(ConfigEntryBase entry, object value)
        {
            applying = true;
            try { entry.BoxedValue = value; }
            finally { applying = false; }
        }

        //--- binding one member ---------------------------------------------------
        private void TryBind(Type type, MemberInfo member, string section, TunableKind kind, string description, string unit)
        {
            string key = KeyName(type.Name + "." + member.Name);
            try
            {
                Binding binding = new Binding { key = key, kind = kind };
                string bindSection = kind == TunableKind.Log ? LogsSection : kind == TunableKind.View ? ViewsSection
                    : kind == TunableKind.Status ? StatusSection : KeyName(section);
                // two classes of one name (in different namespaces / outer classes): the full name tells them apart
                if (!usedKeys.Add(bindSection + "/" + key))
                {
                    key = KeyName((type.FullName ?? type.Name).Replace('+', '.') + "." + member.Name);
                    usedKeys.Add(bindSection + "/" + key);
                    binding.key = key;
                }
                string where = (type.FullName ?? type.Name).Replace('+', '.') + "." + member.Name;
                string text = description + (string.IsNullOrEmpty(unit) ? "" : $" ({unit})") + (kind == TunableKind.Log || kind == TunableKind.View ? $" [{section}]" : "");

                if (member is MethodInfo method)
                {
                    if (method.GetParameters().Length != 0)
                        return;
                    binding.method = method;
                    binding.toEntry = value => false;
                    binding.entry = config.Bind(bindSection, KeyName("Run " + key), false, Describe(text + "\n" + where + "()", false));
                    Set(binding.entry, false);
                }
                else if (member is PropertyInfo property)
                {
                    binding.property = property;
                    binding.toEntry = value => value?.ToString() ?? "";
                    object current = property.GetValue(null);
                    binding.lastFieldValue = current;
                    binding.entry = config.Bind(bindSection, key, binding.toEntry(current) as string, Describe(text + "\n" + where, true));
                    Set(binding.entry, binding.toEntry(current));
                }
                else if (member is FieldInfo field)
                {
                    binding.field = field;
                    object current = field.GetValue(null);
                    Type fieldType = field.FieldType;
                    string defaultText = $"\nDefault: {Show(current)}. {where}";
                    if (fieldType == typeof(bool))
                        binding.entry = config.Bind(bindSection, key, (bool)current, Describe(text + defaultText, false));
                    else if (fieldType == typeof(float))
                        binding.entry = config.Bind(bindSection, key, (float)current, Describe(text + defaultText, false));
                    else if (fieldType == typeof(int))
                        binding.entry = config.Bind(bindSection, key, (int)current, Describe(text + defaultText, false));
                    else if (fieldType == typeof(string))
                        binding.entry = config.Bind(bindSection, key, (string)current ?? "", Describe(text + defaultText, false));
                    else if (fieldType == typeof(Vector3))
                    {
                        // a text setting: Integrated Configuration Manager can't edit a Vector3
                        binding.toEntry = value => VectorText((Vector3)value);
                        binding.toField = value => ParseVector((string)value, (Vector3)field.GetValue(null));
                        binding.entry = config.Bind(bindSection, key, VectorText((Vector3)current), Describe(text + " (x, y, z)" + defaultText, false));
                    }
                    else if (fieldType.IsEnum)
                    {
                        binding.toEntry = value => value.ToString();
                        binding.toField = value => Enum.Parse(fieldType, (string)value);
                        binding.entry = config.Bind(bindSection, key, current.ToString(), new ConfigDescription(text + defaultText,
                            new AcceptableValueList<string>(Enum.GetNames(fieldType)), Attributes(false)));
                    }
                    else
                    {
                        DevToolsPlugin.Log.LogWarning($"[DevTools] {key}: {fieldType.Name} can't be a setting");
                        return;
                    }
                    binding.toEntry = binding.toEntry ?? (value => value);
                    binding.toField = binding.toField ?? (value => value);
                }
                bindings.Add(binding);
                byEntry[binding.entry] = binding;
            }
            catch (Exception exception)
            {
                DevToolsPlugin.Log.LogWarning($"[DevTools] {key}: not bound ({exception.Message})");
            }
        }

        private ConfigDescription Describe(string text, bool readOnly) => new ConfigDescription(text, null, Attributes(readOnly));

        private ConfigurationManagerAttributes Attributes(bool readOnly) => new ConfigurationManagerAttributes { Order = order--, ReadOnly = readOnly ? true : (bool?)null };

        // BepInEx refuses these in a section or key name (Bind throws)
        private static string KeyName(string name)
        {
            foreach (char forbidden in new[] { '=', '\n', '\t', '\\', '"', '\'', '[', ']' })
                name = name.Replace(forbidden.ToString(), "");
            return name;
        }

        private static string Show(object value) => value is float number ? number.ToString(CultureInfo.InvariantCulture)
            : value is Vector3 vector ? VectorText(vector) : value?.ToString() ?? "null";

        private static string VectorText(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", value.x, value.y, value.z);

        private static Vector3 ParseVector(string text, Vector3 fallback)
        {
            string[] parts = (text ?? "").Trim('(', ')', ' ').Split(',');
            if (parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return new Vector3(x, y, z);
            return fallback;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(t => t != null); }
        }

        private static Type FindType(string assemblyName, string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == assemblyName)
                    return assembly.GetType(typeName, false);
            }
            return null;
        }
    }
}
