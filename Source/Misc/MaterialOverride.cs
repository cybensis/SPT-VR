using UnityEngine;
using System.Collections.Generic;

public class MaterialOverrideManager : MonoBehaviour
{
    [Header("Targeting")]
    public List<string> targetShaderNames = new List<string> {
        "Standard",
        "Standard (Specular setup)",
        "p0/Reflective/Specular",
        "p0/Reflective/Bumped Specular",
        "p0/Reflective/Bumped Specular SMap",
        "p0/Reflective/Bumped Specular SMap_Decal"
    };

    [Header("Normal Settings")]
    public Vector2 normalScaleClamp = new Vector2(0.5f, 1.0f);
    public Vector2 bumpScaleClamp = new Vector2(0.5f, 1.0f);

    [Header("Specular Settings")]
    public Vector2 glossinessClamp = new Vector2(0.1f, 0.6f);
    public Vector2 smoothnessClamp = new Vector2(0.1f, 0.6f);

    private Dictionary<Material, Material> overriddenMaterials = new Dictionary<Material, Material>();

    void Start()
    {
        ApplyOverrides();
    }

    public void ApplyOverrides()
    {
        var renderers = FindObjectsOfType<Renderer>();

        foreach (var renderer in renderers)
        {
            var sharedMats = renderer.sharedMaterials;
            bool modified = false;

            for (int i = 0; i < sharedMats.Length; i++)
            {
                Material original = sharedMats[i];
                if (original == null || original.shader == null)
                    continue;

                string shaderName = original.shader.name;

                // Only override if the shader is explicitly whitelisted
                if (!targetShaderNames.Contains(shaderName))
                    continue;

                // Clone only if we haven't already
                if (!overriddenMaterials.TryGetValue(original, out Material cloned))
                {
                    cloned = new Material(original);
                    cloned.name = original.name + "_Clamped";

                    if (cloned.HasProperty("_NormalScale"))
                    {
                        float current = cloned.GetFloat("_NormalScale");
                        cloned.SetFloat("_NormalScale", Mathf.Clamp(current, normalScaleClamp.x, normalScaleClamp.y));
                    }

                    if (cloned.HasProperty("_BumpScale"))
                    {
                        float current = cloned.GetFloat("_BumpScale");
                        cloned.SetFloat("_BumpScale", Mathf.Clamp(current, bumpScaleClamp.x, bumpScaleClamp.y));
                    }

                    if (cloned.HasProperty("_Glossiness"))
                    {
                        float current = cloned.GetFloat("_Glossiness");
                        cloned.SetFloat("_Glossiness", Mathf.Clamp(current, glossinessClamp.x, glossinessClamp.y));
                    }

                    if (cloned.HasProperty("_Smoothness"))
                    {
                        float current = cloned.GetFloat("_Smoothness");
                        cloned.SetFloat("_Smoothness", Mathf.Clamp(current, smoothnessClamp.x, smoothnessClamp.y));
                    }

                    overriddenMaterials[original] = cloned;
                }

                sharedMats[i] = cloned;
                modified = true;
            }

            if (modified)
            {
                renderer.sharedMaterials = sharedMats;
            }
        }

        Debug.Log($"[MaterialOverrideManager] Applied {overriddenMaterials.Count} material overrides.");
    }

}
