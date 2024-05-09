using UnityEngine;

namespace TarkovVR
{

    internal class CanvasCast : MonoBehaviour
    {
        public Camera targetCamera; // Assign this in the inspector

        void Start()
        {
            // Create a new Render Texture
            RenderTexture renderTexture = new RenderTexture(1024, 768, 24); // Width, height, depth
            renderTexture.antiAliasing = 2; // Set anti-aliasing as needed

            // Assign the created Render Texture to the target camera
            //targetCamera.targetTexture = renderTexture;

            // Optionally, assign this texture to a UI element or another object that should display it
            // For example, a UI RawImage component
            // You can find this component at runtime and set its texture like so:
            // GetComponent<RawImage>().texture = renderTexture;
        }
    }
}
