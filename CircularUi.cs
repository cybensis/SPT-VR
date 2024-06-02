using TarkovVR;
using UnityEngine;
using UnityEngine.UI;

public class EnsureCanvasOnTop : MonoBehaviour
{
    public int sortingOrder = 1000;
    public string sortingLayerName = "UI";

    private string shaderCode = @"
    Shader ""UI/AlwaysOnTop""
    {
        SubShader
        {
            Tags { ""Queue"" = ""Overlay+1"" }
            Pass
            {
                ZTest Always
                ZWrite Off
                Cull Off
                Blend SrcAlpha OneMinusSrcAlpha

                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #include ""UnityCG.cginc""

                struct appdata_t
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                    float4 color : COLOR;
                };

                struct v2f
                {
                    float2 uv : TEXCOORD0;
                    float4 vertex : SV_POSITION;
                    float4 color : COLOR;
                };

                sampler2D _MainTex;
                float4 _MainTex_ST;

                v2f vert(appdata_t v)
                {
                    v2f o;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                    o.color = v.color;
                    return o;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    fixed4 col = tex2D(_MainTex, i.uv) * i.color;
                    return col;
                }
                ENDCG
            }
        }
    }";

    void Start()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            // Ensure the canvas is in World Space render mode
            canvas.renderMode = RenderMode.WorldSpace;

            // Create or find the sorting layer
            canvas.sortingLayerName = sortingLayerName;
            canvas.sortingOrder = sortingOrder;

            // Create the shader and material at runtime
            Shader shader = Shader.Find("UI/Additive");
            if (shader != null)
            {
                Material additiveMaterial = new Material(shader);
                ApplyMaterialToCanvas(canvas, additiveMaterial);
            }
            else
            {
                Plugin.MyLog.LogError("UI/Additive shader not found!");
            }
        }
    }

    void ApplyMaterialToCanvas(Canvas canvas, Material material)
    {
        // Get all Image components in the canvas
        Image[] images = canvas.GetComponentsInChildren<Image>();
        foreach (Image image in images)
        {
                image.material = material;
            //if (image.material == null || image.material.name == "Default UI Material")
            //{
            //}
        }

        // Get all Text components in the canvas (if any)
        Text[] texts = canvas.GetComponentsInChildren<Text>();
        foreach (Text text in texts)
        {
                text.material = material;
            //if (text.material == null || text.material.name == "Default UI Material")
            //{
            //}
        }
    }

    void AddMaterialProperties(Material existingMaterial, Material newMaterial)
    {
        // Add or override the properties from the new material to the existing material
        existingMaterial.CopyPropertiesFromMaterial(newMaterial);
    }
}

public static class ShaderUtil
{
    public static Shader CreateShaderFromString(string shaderCode, string shaderName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            shader = new Shader();
            shader.hideFlags = HideFlags.HideAndDontSave;
            shader.name = shaderName;
            // Use reflection or other methods to dynamically create a shader from the string if possible
        }
        return shader;
    }
}