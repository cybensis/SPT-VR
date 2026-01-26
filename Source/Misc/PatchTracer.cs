using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TarkovVR;

public static class PatchTracer
{
    private static Harmony _traceHarmony;

    public static void TraceAllMyPatches(Harmony myHarmony)
    {
        _traceHarmony = new Harmony("com.debug.patchtracer");

        // Get all methods patched by your mod
        var patchedMethods = myHarmony.GetPatchedMethods();

        foreach (var method in patchedMethods)
        {
            var patchInfo = Harmony.GetPatchInfo(method);

            // Log what's patched
            Plugin.MyLog.LogWarning($"[PatchTracer] Found patch on: {method.DeclaringType?.Name}.{method.Name}");

            // Add a trace prefix to the ORIGINAL method
            try
            {
                var tracePrefix = typeof(PatchTracer).GetMethod(nameof(TracePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                _traceHarmony.Patch(method, prefix: new HarmonyMethod(tracePrefix));
            }
            catch { }
        }
    }

    private static HashSet<string> _alreadyLogged = new HashSet<string>();

    private static void TracePrefix(MethodBase __originalMethod)
    {
        string key = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";

        if (_alreadyLogged.Contains(key))
            return;

        _alreadyLogged.Add(key);
        Plugin.MyLog.LogInfo($"[PATCH CALLED] {key}");
    }

    public static void StopTracing()
    {
        _traceHarmony?.UnpatchSelf();
    }
}
