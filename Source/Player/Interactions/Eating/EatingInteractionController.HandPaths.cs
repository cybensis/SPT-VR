using EFT.InventoryLogic;
using RootMotion.FinalIK;
using System;
using System.Text;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // Baked open/reach hand paths (recon [HANDPATH] captures) - see the trap note
    // below on why these MUST be properties, not fields.
    internal static partial class EatingInteractionController
    {
        // ===== Baked open-hand paths (recon [HANDPATH], 2026-06) =================
        // The vanilla clip's opening-palm pose vs the item root, replayed by the pull
        // (see FoodDef.openHandPath). Packed stride 8: t, px,py,pz, qx,qy,qz,qw.
        // Trailing keys where the vanilla hand LEAVES the item (the tushonka clip's
        // spoon-grab dive, the bottles carrying the cap away) are trimmed — the replay
        // holds the last kept key instead of yanking the hand off the food.
        // PROPERTIES, not readonly fields: C# initializes static fields in DECLARATION
        // order and Defs is declared ABOVE these — as fields they were still null while
        // Defs built itself, so every HandPath silently stored null (observed: all foods
        // fell back to the OpenGrip/freeze latch). A getter evaluates at call time.

        // Tushonka-measured; shared by every can on the saira rig (big cans too — same
        // clip, slightly larger body). Spoon-dive keys (t≈0.80/0.90, 0.5-0.6m off) trimmed.
        private static float[] SairaOpenPath => new float[]
        {
            0.108f, 0.089f, -0.126f, 0.308f, 0.0748f, -0.5211f, -0.3189f, 0.7881f,
            0.207f, -0.08f, -0.131f, 0.079f, 0.3442f, -0.4858f, -0.7011f, 0.3924f,
            0.306f, -0.078f, -0.141f, 0.054f, 0.4403f, -0.4614f, -0.6888f, 0.3448f,
            0.404f, -0.101f, -0.089f, 0.14f, 0.3142f, -0.7651f, -0.3754f, 0.4183f,
            0.503f, -0.115f, -0.024f, 0.151f, 0.223f, -0.8777f, -0.1889f, 0.3799f,
            0.602f, -0.138f, 0.075f, 0.042f, 0.1106f, -0.9928f, -0.0153f, 0.0438f,
            0.7f, -0.184f, 0.052f, 0.071f, 0.232f, -0.9376f, -0.2067f, 0.1562f,
            0.996f, -0.19f, -0.013f, 0.153f, 0.2383f, -0.9308f, -0.2562f, 0.1063f,
        };

        private static float[] SpratsOpenPath => new float[]
        {
            0.163f, 0.119f, -0.12f, 0.281f, 0.0419f, -0.4838f, -0.346f, 0.8028f,
            0.256f, -0.08f, -0.133f, 0.086f, 0.3455f, -0.4833f, -0.7008f, 0.3949f,
            0.349f, -0.082f, -0.133f, 0.082f, 0.3565f, -0.4804f, -0.7003f, 0.3895f,
            0.441f, -0.081f, -0.139f, 0.066f, 0.4303f, -0.4771f, -0.6817f, 0.3499f,
            0.534f, -0.101f, -0.096f, 0.142f, 0.3139f, -0.749f, -0.4091f, 0.4161f,
            0.627f, -0.104f, -0.059f, 0.157f, 0.2692f, -0.818f, -0.2586f, 0.4376f,
            0.72f, -0.137f, 0.021f, 0.123f, 0.1777f, -0.942f, -0.1617f, 0.2343f,
            0.812f, -0.134f, 0.091f, 0.045f, 0.0842f, -0.9957f, 0.0055f, 0.0375f,
            0.905f, -0.169f, 0.043f, 0.082f, 0.2297f, -0.9392f, -0.2042f, 0.1533f,
            0.998f, -0.232f, -0.064f, 0.12f, 0.2581f, -0.8293f, -0.4525f, 0.2023f,
        };

        // L palm peeling the wrapper (vs item_slickers_LOD0).
        private static float[] ChocolateOpenPath => new float[]
        {
            0.048f, 0.194f, 0.232f, 0.168f, 0.1461f, -0.4374f, -0.0836f, 0.8833f,
            0.148f, 0.038f, 0.135f, 0.09f, 0.1159f, -0.479f, 0.2922f, 0.8196f,
            0.247f, 0.019f, 0.128f, 0.091f, 0.019f, -0.6009f, 0.3008f, 0.7404f,
            0.346f, 0.06f, 0.127f, 0.135f, -0.3256f, -0.7773f, -0.0833f, 0.5318f,
            0.446f, 0.125f, 0.061f, 0.252f, -0.3344f, -0.7755f, -0.0628f, 0.5319f,
            0.545f, 0.139f, 0.006f, 0.262f, -0.5182f, -0.7198f, 0.0803f, 0.4548f,
            0.644f, 0.104f, -0.084f, 0.327f, -0.774f, -0.4794f, 0.0702f, 0.4077f,
            0.744f, -0.029f, -0.272f, 0.263f, -0.6738f, -0.4812f, -0.0554f, 0.558f,
            0.843f, 0.108f, -0.154f, 0.387f, -0.5447f, -0.6732f, 0.1442f, 0.4788f,
            0.942f, 0.292f, 0.034f, 0.149f, -0.1875f, -0.6027f, 0.2202f, 0.7437f,
        };

        // L palm opening/tearing the bag (vs bone_upakovka); shared by both croutons.
        private static float[] CroutonOpenPath => new float[]
        {
            0.222f, 0.192f, 0.202f, 0.209f, -0.5399f, 0.0275f, 0.0033f, -0.8413f,
            0.308f, 0.185f, 0.184f, 0.088f, -0.5013f, -0.1179f, -0.0013f, -0.8572f,
            0.394f, 0.132f, 0.083f, 0.086f, -0.7314f, -0.1787f, 0.0162f, -0.6579f,
            0.48f, 0.088f, 0.06f, 0.096f, -0.6901f, -0.2643f, -0.152f, -0.6564f,
            0.566f, 0.087f, 0.051f, 0.098f, -0.6688f, -0.1874f, -0.1236f, -0.7088f,
            0.652f, 0.021f, 0.077f, 0.122f, -0.8215f, -0.0722f, 0.1153f, -0.5538f,
            0.738f, 0.034f, 0.065f, 0.151f, -0.8558f, -0.0687f, 0.1744f, -0.4822f,
            0.824f, 0.142f, -0.03f, 0.174f, -0.2733f, 0.006f, 0.2283f, -0.9344f,
            0.91f, 0.162f, 0.024f, 0.175f, 0.2972f, 0.0682f, 0.3235f, -0.8957f,
            0.996f, 0.198f, 0.036f, 0.142f, 0.4435f, 0.1367f, 0.458f, -0.7582f,
        };

        // L palm popping the drink tab (vs hr_root / tc_root); last key (hand pulling
        // away) trimmed on both.
        private static float[] HotrodOpenPath => new float[]
        {
            0.057f, 0.285f, 0.009f, 0.194f, 0.6403f, -0.034f, -0.3103f, 0.7019f,
            0.161f, 0.156f, -0.067f, 0.2f, 0.3845f, -0.1034f, -0.3646f, 0.8417f,
            0.265f, 0.106f, -0.069f, 0.196f, 0.5662f, -0.2278f, -0.344f, 0.7135f,
            0.37f, 0.097f, -0.075f, 0.182f, 0.5375f, -0.2142f, -0.426f, 0.6955f,
            0.474f, 0.138f, -0.125f, 0.199f, 0.6923f, -0.2699f, -0.2597f, 0.6168f,
            0.578f, 0.151f, -0.062f, 0.158f, 0.4642f, -0.203f, -0.3909f, 0.7684f,
            0.683f, 0.136f, -0.022f, 0.118f, 0.2569f, -0.029f, -0.4107f, 0.8743f,
            0.787f, 0.148f, 0.01f, 0.119f, 0.1635f, -0.0193f, -0.3656f, 0.9161f,
            0.891f, 0.212f, -0.076f, 0.062f, -0.1837f, -0.2365f, -0.4058f, 0.8635f,
        };

        private static float[] SodaOpenPath => new float[]
        {
            0.053f, 0.295f, 0.016f, 0.18f, 0.6567f, -0.058f, -0.312f, 0.6841f,
            0.158f, 0.16f, -0.069f, 0.163f, 0.3839f, -0.0908f, -0.3635f, 0.844f,
            0.263f, 0.106f, -0.071f, 0.162f, 0.5687f, -0.2257f, -0.3415f, 0.7134f,
            0.368f, 0.095f, -0.078f, 0.149f, 0.528f, -0.2132f, -0.4321f, 0.6994f,
            0.473f, 0.138f, -0.124f, 0.164f, 0.7035f, -0.2575f, -0.2563f, 0.6109f,
            0.578f, 0.15f, -0.072f, 0.135f, 0.4842f, -0.2178f, -0.3819f, 0.7565f,
            0.683f, 0.135f, -0.028f, 0.086f, 0.2607f, -0.0295f, -0.4244f, 0.8666f,
            0.788f, 0.149f, 0.003f, 0.088f, 0.1721f, -0.0233f, -0.3492f, 0.9208f,
            0.893f, 0.216f, -0.063f, 0.016f, -0.1749f, -0.1509f, -0.3677f, 0.9008f,
        };

        // R palm unscrewing/working the cap (vs mod_item) — the carry-the-cap-away tails
        // (hand 0.4-0.65m off the bottle) trimmed on all of these.
        private static float[] VodkaOpenPath => new float[]
        {
            0.218f, 0.24f, 0.018f, 0.251f, -0.6847f, 0.0075f, -0.541f, -0.4884f,
            0.304f, 0.152f, 0.04f, 0.129f, -0.7882f, -0.246f, -0.2503f, -0.5056f,
            0.391f, 0.107f, 0.052f, 0.132f, -0.6865f, -0.2996f, -0.2722f, -0.604f,
            0.478f, 0.105f, 0.069f, 0.13f, -0.6607f, -0.3761f, -0.2987f, -0.577f,
            0.565f, 0.113f, 0.062f, 0.124f, -0.6599f, -0.3561f, -0.2468f, -0.6139f,
            0.652f, 0.113f, 0.024f, 0.164f, -0.6865f, -0.157f, -0.2666f, -0.658f,
            0.739f, 0.282f, 0.131f, 0.19f, -0.6458f, -0.5687f, -0.261f, -0.4375f,
        };

        private static float[] MoonshineOpenPath => new float[]
        {
            0.178f, 0.236f, -0.028f, 0.245f, -0.68f, 0.045f, -0.5004f, -0.534f,
            0.269f, 0.133f, 0.044f, 0.106f, -0.7916f, -0.3183f, -0.2112f, -0.4769f,
            0.361f, 0.11f, 0.036f, 0.154f, -0.6852f, -0.1822f, -0.2636f, -0.6541f,
            0.452f, 0.133f, 0.055f, 0.156f, -0.7675f, -0.2996f, -0.2726f, -0.4969f,
            0.543f, 0.172f, 0.013f, 0.2f, -0.6431f, -0.2849f, -0.4473f, -0.5524f,
        };

        private static float[] WhiskeyOpenPath => new float[]
        {
            0.373f, 0.237f, -0.013f, -0.256f, -0.8972f, -0.0758f, 0.0522f, -0.4318f,
            0.442f, 0.127f, 0.044f, 0.097f, -0.7754f, -0.3453f, -0.1988f, -0.4899f,
            0.512f, 0.11f, 0.034f, 0.156f, -0.6928f, -0.1796f, -0.2611f, -0.6478f,
            0.581f, 0.104f, 0.061f, 0.14f, -0.6774f, -0.3291f, -0.3038f, -0.5835f,
            0.651f, 0.109f, 0.058f, 0.127f, -0.6694f, -0.3539f, -0.2555f, -0.6011f,
            0.72f, 0.111f, 0.02f, 0.169f, -0.6867f, -0.1522f, -0.2747f, -0.6556f,
            0.79f, 0.315f, 0.141f, 0.17f, -0.6466f, -0.5759f, -0.2545f, -0.4306f,
        };

        private static float[] WaterBottleOpenPath => new float[]
        {
            0.201f, 0.186f, -0.009f, 0.291f, -0.6413f, -0.2242f, -0.3054f, -0.6673f,
            0.29f, 0.065f, 0.105f, 0.22f, -0.5347f, -0.3472f, -0.5066f, -0.5805f,
            0.378f, 0.038f, 0.114f, 0.219f, -0.4849f, -0.4145f, -0.5785f, -0.5084f,
            0.466f, 0.029f, 0.118f, 0.219f, -0.466f, -0.4371f, -0.6004f, -0.4809f,
            0.555f, 0.04f, 0.115f, 0.222f, -0.4855f, -0.4146f, -0.5779f, -0.5084f,
            0.643f, 0.029f, 0.118f, 0.228f, -0.4658f, -0.4379f, -0.6006f, -0.4802f,
            0.731f, 0.035f, 0.116f, 0.232f, -0.4739f, -0.4283f, -0.592f, -0.4915f,
            0.82f, 0.094f, 0.107f, 0.236f, -0.6747f, -0.4626f, -0.4458f, -0.3633f,
        };

        private static float[] KvasOpenPath => new float[]
        {
            0.2f, 0.186f, -0.008f, 0.292f, -0.6398f, -0.2413f, -0.3052f, -0.6628f,
            0.289f, 0.067f, 0.104f, 0.221f, -0.5361f, -0.3469f, -0.501f, -0.5841f,
            0.377f, 0.038f, 0.114f, 0.22f, -0.4793f, -0.4207f, -0.5812f, -0.5055f,
            0.466f, 0.03f, 0.117f, 0.22f, -0.4623f, -0.4403f, -0.6003f, -0.4817f,
            0.555f, 0.041f, 0.114f, 0.223f, -0.4812f, -0.4184f, -0.5784f, -0.5087f,
            0.643f, 0.029f, 0.118f, 0.228f, -0.4599f, -0.4422f, -0.6029f, -0.479f,
            0.732f, 0.037f, 0.115f, 0.233f, -0.472f, -0.4285f, -0.5905f, -0.4948f,
            0.821f, 0.097f, 0.106f, 0.236f, -0.6784f, -0.4677f, -0.4439f, -0.352f,
        };

        // R palm flipping the sport cap (vs fb_root); hand-drifts-off tail trimmed.
        private static float[] AquamariOpenPath => new float[]
        {
            0.242f, 0.226f, -0.201f, 0.167f, 0.9498f, -0.2468f, 0.1514f, 0.1186f,
            0.326f, 0.079f, -0.156f, 0.145f, 0.9317f, -0.3218f, -0.0807f, 0.1477f,
            0.409f, 0.079f, -0.139f, 0.151f, 0.938f, -0.2968f, -0.0855f, 0.1574f,
            0.493f, 0.098f, -0.095f, 0.125f, 0.9741f, -0.1021f, -0.1485f, 0.1362f,
            0.576f, 0.109f, -0.107f, 0.126f, 0.9552f, -0.0344f, -0.1888f, 0.2252f,
            0.66f, 0.136f, -0.177f, 0.143f, 0.9545f, -0.1632f, -0.1093f, 0.2242f,
            0.743f, 0.167f, -0.257f, 0.167f, 0.9118f, -0.3428f, 0.0694f, 0.2154f,
        };

        // R palm rolling the can key (vs saira_root) — the condensed milk uses the can
        // clip; its spoon-dive tail trimmed like SairaOpenPath.
        private static float[] CondMilkOpenPath => new float[]
        {
            0.109f, 0.092f, -0.122f, 0.315f, 0.072f, -0.5104f, -0.3096f, 0.799f,
            0.208f, -0.083f, -0.133f, 0.08f, 0.3497f, -0.4819f, -0.7002f, 0.3939f,
            0.307f, -0.082f, -0.14f, 0.059f, 0.4361f, -0.4569f, -0.6916f, 0.3502f,
            0.406f, -0.103f, -0.095f, 0.141f, 0.3133f, -0.7421f, -0.4209f, 0.4171f,
            0.505f, -0.108f, -0.034f, 0.162f, 0.2255f, -0.8451f, -0.2166f, 0.4336f,
            0.604f, -0.136f, 0.078f, 0.047f, 0.0993f, -0.9934f, -0.0391f, 0.0414f,
            0.703f, -0.169f, 0.067f, 0.073f, 0.1908f, -0.9608f, -0.1119f, 0.1671f,
        };

        // Vanilla tears the pouch with the R hand while WE hold it right (mirror food) —
        // the path is item-relative so it may still read fine; judge in the headset.
        private static float[] RationOpenPath => new float[]
        {
            0.18f, -0.285f, 0.141f, 0.142f, 0.0085f, -0.7366f, 0.3071f, 0.6026f,
            0.271f, -0.162f, 0.058f, 0.168f, 0.31f, -0.6766f, 0.424f, 0.516f,
            0.362f, -0.108f, 0.017f, 0.136f, 0.4596f, -0.5766f, 0.4921f, 0.4628f,
            0.453f, -0.112f, 0.012f, 0.138f, 0.4594f, -0.5829f, 0.5104f, 0.4344f,
            0.544f, -0.102f, 0.024f, 0.13f, 0.4368f, -0.5473f, 0.4788f, 0.5295f,
            0.635f, -0.095f, 0.041f, 0.165f, 0.3454f, -0.5047f, 0.505f, 0.6091f,
            0.727f, -0.124f, 0.053f, 0.156f, 0.216f, -0.3195f, 0.7806f, 0.4919f,
            0.818f, -0.125f, -0.038f, 0.081f, 0.2215f, -0.2313f, 0.9297f, 0.182f,
            0.909f, -0.109f, -0.055f, 0.079f, 0.271f, -0.2348f, 0.9242f, 0.1317f,
            1f, -0.108f, -0.054f, 0.08f, 0.2756f, -0.2483f, 0.9169f, 0.1476f,
        };

        private static float[] SuperwaterOpenPath => new float[]
        {
            0.222f, 0.117f, -0.107f, 0.305f, -0.6832f, 0.1958f, -0.2122f, -0.6707f,
            0.309f, 0.107f, -0.137f, 0.226f, -0.6145f, 0.1914f, 0.1041f, -0.7582f,
            0.395f, 0.119f, -0.126f, 0.197f, -0.643f, 0.0721f, 0.1011f, -0.7558f,
            0.481f, 0.094f, -0.132f, 0.276f, -0.4772f, 0.3163f, 0.1374f, -0.8083f,
            0.568f, 0.122f, -0.116f, 0.187f, -0.6916f, -0.0105f, 0.0782f, -0.7179f,
            0.654f, 0.104f, -0.137f, 0.29f, -0.473f, 0.3186f, 0.1561f, -0.8065f,
            0.74f, 0.195f, -0.283f, 0.145f, -0.9606f, -0.0589f, -0.1134f, -0.2469f,
            0.827f, 0.08f, -0.279f, -0.047f, -0.8572f, 0.1521f, -0.488f, 0.0629f,
            0.913f, -0.015f, -0.237f, -0.061f, -0.7347f, 0.0794f, -0.6678f, 0.0888f,
            0.999f, -0.014f, -0.238f, -0.062f, -0.7352f, 0.0791f, -0.6673f, 0.0895f,
        };

        // ===== 2026-06-13 batch (the "logs for food" recon run) =====
        // Tetrapak corner tear (milk-measured; shared by both cartons). Last two keys
        // (the torn nose carried away, z 0.19->0.41) trimmed.
        private static float[] TetraPakOpenPath => new float[]
        {
            0.077f, -0.232f, 0.081f, 0.242f, -0.6145f, 0.4164f, -0.3035f, -0.5974f,
            0.18f, -0.176f, -0.042f, 0.198f, -0.739f, 0.361f, -0.5285f, -0.2107f,
            0.282f, -0.184f, -0.047f, 0.199f, -0.7243f, 0.3787f, -0.5508f, -0.1688f,
            0.384f, -0.184f, 0.056f, 0.141f, -0.3434f, 0.5719f, -0.2926f, -0.6851f,
            0.487f, -0.137f, 0.038f, 0.158f, -0.4092f, 0.5557f, -0.0899f, -0.7181f,
            0.589f, -0.11f, -0.033f, 0.195f, -0.596f, 0.5527f, -0.1148f, -0.5711f,
            0.691f, -0.093f, -0.025f, 0.19f, -0.6346f, 0.516f, -0.1116f, -0.5644f,
            0.794f, -0.075f, -0.04f, 0.193f, -0.609f, 0.5491f, -0.1059f, -0.5625f,
        };

        // Juice screw cap (vita-measured; apple/vita/grand share the rig). Last three keys
        // (the unscrewed cap carried aside) trimmed.
        private static float[] JuiceOpenPath => new float[]
        {
            0.085f, 0.279f, -0.045f, 0.196f, 0.5257f, -0.4313f, -0.1751f, 0.712f,
            0.186f, 0.17f, -0.088f, 0.271f, 0.7581f, -0.1207f, -0.2623f, 0.5847f,
            0.287f, 0.143f, -0.065f, 0.255f, 0.8491f, -0.0529f, -0.2f, 0.486f,
            0.388f, 0.131f, -0.074f, 0.254f, 0.8441f, -0.0702f, -0.2269f, 0.4808f,
            0.489f, 0.143f, -0.066f, 0.251f, 0.8516f, -0.0501f, -0.1982f, 0.4826f,
            0.59f, 0.124f, -0.082f, 0.238f, 0.8285f, -0.0824f, -0.292f, 0.4707f,
            0.691f, 0.117f, -0.115f, 0.241f, 0.6618f, -0.0707f, -0.4281f, 0.6114f,
        };

        // Galette pack open (modest 0.4m travel, no runaway — all keys kept).
        private static float[] GaletteOpenPath => new float[]
        {
            0f, 0.15f, 0.235f, 0.037f, -0.0357f, 0.273f, -0.008f, -0.9613f,
            0.111f, 0.118f, 0.199f, 0.023f, -0.0681f, 0.1828f, -0.1126f, -0.9743f,
            0.221f, 0.056f, 0.139f, -0.007f, -0.142f, 0.0013f, -0.2463f, -0.9587f,
            0.331f, 0.044f, 0.127f, -0.013f, -0.1594f, -0.0365f, -0.2658f, -0.9501f,
            0.442f, 0.045f, 0.126f, -0.011f, -0.1593f, -0.0361f, -0.2664f, -0.9499f,
            0.552f, 0.042f, 0.121f, 0.063f, -0.1606f, 0.4224f, -0.2418f, -0.8587f,
            0.663f, 0.05f, 0.166f, 0.099f, 0.0579f, 0.7087f, -0.2307f, -0.6642f,
            0.773f, 0.046f, 0.164f, 0.092f, 0.0734f, 0.6979f, -0.2355f, -0.6724f,
            0.884f, 0.028f, 0.157f, 0.075f, 0.1122f, 0.7042f, -0.2467f, -0.6562f,
            0.994f, 0.014f, 0.162f, 0.052f, 0.1543f, 0.6506f, -0.3626f, -0.6492f,
        };

        // Oat flakes box tear (croutons-like; tail toss kept like CroutonOpenPath).
        private static float[] OatmealOpenPath => new float[]
        {
            0.321f, 0.334f, 0.029f, 0.087f, -0.4929f, -0.0535f, -0.0083f, -0.8684f,
            0.396f, 0.251f, 0.01f, 0.073f, -0.8088f, -0.161f, 0.0994f, -0.5568f,
            0.471f, 0.142f, -0.016f, 0.106f, -0.9224f, -0.1021f, -0.0112f, -0.3724f,
            0.546f, 0.123f, -0.02f, 0.118f, -0.9498f, -0.1259f, -0.095f, -0.2703f,
            0.621f, 0.129f, -0.028f, 0.125f, -0.858f, 0.0094f, 0.0525f, -0.5108f,
            0.696f, 0.123f, 0f, 0.105f, -0.6136f, 0.0873f, 0.1116f, -0.7768f,
            0.771f, 0.136f, 0.01f, 0.099f, -0.3726f, 0.0164f, 0.1611f, -0.9137f,
            0.846f, 0.181f, 0.014f, 0.111f, -0.0124f, 0.0204f, 0.1391f, -0.99f,
            0.921f, 0.183f, 0.077f, 0.116f, 0.2897f, 0.0785f, 0.1828f, -0.9362f,
            0.996f, 0.191f, 0.077f, 0.135f, 0.4357f, 0.1245f, 0.3458f, -0.8216f,
        };

        // Alyonka wrapper peel (slickers-family clip; all keys kept like ChocolateOpenPath).
        private static float[] AlyonkaOpenPath => new float[]
        {
            0.054f, 0.247f, -0.193f, 0.139f, 0.1952f, 0.3774f, 0.7166f, -0.5531f,
            0.156f, 0.169f, -0.033f, 0.09f, 0.2113f, 0.4007f, 0.3818f, -0.8056f,
            0.257f, 0.173f, -0.028f, 0.085f, 0.364f, 0.3344f, 0.3708f, -0.7863f,
            0.358f, 0.285f, -0.125f, 0.124f, 0.7817f, 0.1533f, 0.1869f, -0.5749f,
            0.46f, 0.203f, -0.184f, 0.28f, 0.4437f, 0.4424f, 0.2148f, -0.7492f,
            0.561f, 0.151f, -0.184f, 0.283f, 0.571f, 0.3363f, 0.1398f, -0.7357f,
            0.662f, 0.162f, -0.177f, 0.359f, 0.6862f, 0.2319f, 0.1079f, -0.681f,
            0.764f, 0.1f, -0.11f, 0.481f, 0.7252f, 0.3859f, 0.1827f, -0.5402f,
            0.865f, -0.04f, -0.118f, 0.518f, 0.6288f, 0.5487f, 0.2517f, -0.4902f,
            0.966f, 0.115f, -0.35f, 0.238f, 0.1885f, 0.6532f, 0.4817f, -0.5529f,
        };

        // Sugar box open (RIGHT hand works the lid; no runaway — all keys kept).
        private static float[] SugarOpenPath => new float[]
        {
            0.193f, 0.31f, -0.066f, 0.136f, 0.4721f, 0.1612f, 0.6328f, -0.5921f,
            0.283f, 0.178f, 0.073f, 0.161f, 0.4914f, 0.4879f, 0.4751f, -0.5428f,
            0.372f, 0.124f, 0.077f, 0.144f, 0.6055f, 0.5621f, 0.4264f, -0.3684f,
            0.462f, 0.154f, 0.069f, 0.132f, 0.7619f, 0.4675f, 0.3903f, -0.2205f,
            0.551f, 0.179f, 0.06f, 0.129f, 0.808f, 0.3877f, 0.3985f, -0.1949f,
            0.641f, 0.175f, 0.04f, 0.132f, 0.7755f, 0.3881f, 0.4047f, -0.2901f,
            0.731f, 0.169f, -0.012f, 0.079f, 0.722f, 0.495f, 0.3439f, -0.3397f,
            0.82f, 0.175f, 0.019f, 0.12f, 0.6356f, 0.5885f, 0.3717f, -0.334f,
            0.91f, 0.141f, 0.001f, 0.196f, 0.6537f, 0.478f, 0.497f, -0.3116f,
            1f, 0.102f, -0.042f, 0.198f, 0.75f, 0.1894f, 0.583f, -0.2486f,
        };

        // Iskra strap rip (L palm vs IFR_CAT). Last two keys (the torn strip carried up and
        // away, y -> 0.27) trimmed; the pull only samples to openReadyTime 0.6 anyway.
        private static float[] IskraOpenPath => new float[]
        {
            0f, 0.136f, 0.185f, 0.073f, -0.1181f, -0.4629f, -0.1456f, 0.8664f,
            0.111f, 0.095f, 0.185f, 0.06f, -0.0201f, -0.3673f, -0.0196f, 0.9297f,
            0.221f, 0.028f, 0.187f, 0.04f, 0.1623f, -0.1774f, 0.1098f, 0.9644f,
            0.332f, 0.02f, 0.185f, 0.038f, 0.1917f, -0.1523f, 0.1228f, 0.9618f,
            0.442f, 0.017f, 0.184f, 0.049f, 0.1505f, -0.2842f, 0.1278f, 0.9382f,
            0.553f, 0.01f, 0.173f, 0.017f, -0.1826f, -0.7203f, 0.143f, 0.6537f,
            0.663f, 0.08f, 0.173f, -0.017f, -0.483f, -0.8628f, 0.074f, 0.1297f,
            0.774f, 0.14f, 0.208f, 0.02f, -0.5626f, -0.7761f, 0.0496f, 0.2805f,
        };

        // MRE strap rip (L palm vs mre_CAT). Same family/treatment as IskraOpenPath.
        private static float[] MreOpenPath => new float[]
        {
            0f, 0.197f, 0.161f, 0.07f, -0.1479f, -0.4542f, -0.2016f, 0.8551f,
            0.111f, 0.145f, 0.166f, 0.049f, -0.1112f, -0.3557f, -0.1024f, 0.9223f,
            0.222f, 0.065f, 0.176f, 0.014f, -0.0199f, -0.1825f, 0.0164f, 0.9829f,
            0.333f, 0.06f, 0.175f, 0.01f, -0.0018f, -0.1631f, 0.0313f, 0.9861f,
            0.444f, 0.055f, 0.176f, 0.024f, 0.0028f, -0.2451f, 0.0344f, 0.9689f,
            0.556f, 0.036f, 0.188f, 0.026f, -0.2271f, -0.7397f, 0.0989f, 0.6257f,
            0.667f, 0.141f, 0.158f, -0.017f, -0.5333f, -0.8276f, 0.0859f, 0.1526f,
            0.778f, 0.206f, 0.187f, 0.019f, -0.6106f, -0.7392f, 0.0314f, 0.2826f,
        };

        // Iskra reach-in (L palm vs IFR_CAT, STATE_USE time axis). Headset-verified layout:
        // low t = hand OUTSIDE at the bag mouth (Take@0.014 is just the food-appear event),
        // deep IN the bag ≈ t 0.23-0.34, mid keys 0.45-0.89 are the mouth trip. The scrub
        // samples t between reachStartTime and reachDeepTime (0.02 -> 0.3); the FULL pass
        // is kept so the segment can be retuned anywhere on the clip without a recapture.
        private static float[] IskraReachPath => new float[]
        {
            0.007f, -0.017f, 0.26f, 0.105f, -0.4258f, -0.7702f, 0.1603f, 0.4469f,
            0.117f, -0.031f, 0.218f, 0.054f, -0.4493f, -0.7943f, 0.2474f, 0.3256f,
            0.227f, -0.049f, 0.215f, 0.025f, -0.4735f, -0.7859f, 0.3239f, 0.2309f,
            0.337f, -0.045f, 0.224f, 0.012f, -0.4681f, -0.7625f, 0.383f, 0.2299f,
            0.447f, -0.013f, 0.244f, 0.064f, -0.4994f, -0.8095f, 0.1742f, 0.2548f,
            0.557f, -0.008f, 0.262f, 0.104f, -0.3197f, -0.7113f, 0.1644f, 0.604f,
            0.667f, -0.033f, 0.288f, 0.066f, -0.3575f, -0.3622f, -0.0298f, 0.8603f,
            0.777f, -0.088f, 0.221f, 0.088f, -0.6112f, -0.2928f, -0.2888f, 0.6763f,
            0.887f, -0.102f, 0.277f, 0.068f, -0.4994f, -0.5899f, -0.1266f, 0.6218f,
            0.997f, -0.018f, 0.249f, 0.088f, -0.4226f, -0.7793f, 0.1716f, 0.4297f,
        };

        // MRE reach-in (L palm vs mre_CAT). Same family/treatment as IskraReachPath.
        private static float[] MreReachPath => new float[]
        {
            0.005f, 0.026f, 0.264f, 0.095f, -0.5188f, -0.6976f, 0.1217f, 0.479f,
            0.116f, 0.007f, 0.232f, 0.049f, -0.4953f, -0.759f, 0.2237f, 0.3586f,
            0.226f, -0.002f, 0.236f, 0.028f, -0.5093f, -0.7265f, 0.3052f, 0.3459f,
            0.336f, -0.018f, 0.23f, 0.006f, -0.4317f, -0.7205f, 0.425f, 0.3375f,
            0.446f, -0.003f, 0.245f, 0.031f, -0.5513f, -0.7762f, 0.157f, 0.2624f,
            0.556f, 0.014f, 0.267f, 0.09f, -0.36f, -0.6877f, 0.1292f, 0.6171f,
            0.667f, 0.033f, 0.292f, 0.071f, -0.3776f, -0.3393f, -0.0835f, 0.8575f,
            0.777f, -0.02f, 0.225f, 0.084f, -0.6249f, -0.2563f, -0.329f, 0.6599f,
            0.887f, -0.027f, 0.284f, 0.066f, -0.534f, -0.5578f, -0.1661f, 0.6133f,
            0.997f, 0.053f, 0.258f, 0.105f, -0.4556f, -0.7503f, 0.1064f, 0.4671f,
        };

        // Mayo screw cap. Last four keys (the cap carried aside, x 0.1->0.28) trimmed.
        private static float[] MayoOpenPath => new float[]
        {
            0.105f, 0.307f, 0f, 0.141f, -0.5126f, 0.1638f, 0.375f, -0.7549f,
            0.204f, 0.129f, -0.016f, 0.153f, -0.6818f, 0.0912f, 0.1634f, -0.7072f,
            0.303f, 0.124f, -0.006f, 0.166f, -0.6758f, 0.1471f, 0.0938f, -0.7161f,
            0.403f, 0.127f, -0.008f, 0.163f, -0.6757f, 0.1463f, 0.1004f, -0.7155f,
            0.502f, 0.103f, -0.073f, 0.164f, -0.6298f, 0.3263f, 0.2656f, -0.6529f,
            0.601f, 0.096f, -0.069f, 0.196f, -0.4871f, 0.3587f, 0.3016f, -0.7369f,
        };

        // Tarka dried-meat pack rip. Last two keys (the cover strip yanked away, y -> 0.45)
        // trimmed.
        private static float[] TarkaOpenPath => new float[]
        {
            0.004f, -0.069f, 0.069f, 0.146f, 0.2624f, 0.5207f, 0.7354f, 0.3453f,
            0.114f, -0.163f, 0.136f, 0.11f, 0.1757f, 0.6747f, 0.6381f, 0.3266f,
            0.225f, -0.243f, 0.11f, 0.007f, 0.1054f, 0.2185f, 0.8187f, 0.5205f,
            0.335f, -0.097f, 0.103f, -0.083f, -0.1335f, -0.7138f, 0.441f, 0.5274f,
            0.446f, -0.072f, 0.082f, -0.044f, -0.078f, -0.7701f, 0.31f, 0.552f,
            0.556f, -0.071f, 0.08f, -0.043f, -0.0687f, -0.7694f, 0.2998f, 0.5598f,
            0.667f, -0.066f, 0.08f, -0.052f, -0.1249f, -0.7583f, 0.3403f, 0.5418f,
            0.777f, -0.072f, 0.083f, -0.05f, -0.1147f, -0.7646f, 0.347f, 0.5309f,
        };

        // Salad box lid peel (saira-family clip): the 0.80/0.90 spoon-dive keys trimmed,
        // final settle key kept — exactly the SairaOpenPath treatment.
        private static float[] SaladOpenPath => new float[]
        {
            0.113f, 0.214f, -0.149f, 0.209f, -0.0371f, -0.4195f, -0.4625f, 0.7802f,
            0.211f, -0.012f, -0.2f, 0.073f, 0.3643f, -0.3943f, -0.7538f, 0.3789f,
            0.31f, -0.018f, -0.149f, 0.15f, 0.1666f, -0.561f, -0.672f, 0.4538f,
            0.408f, -0.063f, -0.131f, 0.121f, 0.1082f, -0.397f, -0.878f, 0.2446f,
            0.507f, -0.098f, 0.134f, 0.191f, 0.1339f, -0.8938f, -0.3749f, 0.2066f,
            0.605f, -0.171f, 0.039f, 0.152f, 0.2101f, -0.6787f, -0.7037f, 0.0077f,
            0.704f, -0.3f, -0.046f, 0.038f, 0.2729f, -0.7202f, -0.5934f, 0.2339f,
            0.999f, -0.148f, -0.093f, 0.12f, 0.6261f, -0.6873f, -0.3658f, 0.0435f,
        };

        // Noodles bag handling/rip — LEFT palm vs pack_CAT over STATE_OPEN (its open state).
        // Self-contained reach-up (y peaks ~0.197 at t≈0.44) then tear-down arc; no carry-away
        // runaway, so all 10 keys kept (the pre-0.45 keys are unsampled at PullStart 0.45 but
        // left in so lowering it live still works). The actual rip VISUAL is OpenPlay, separate.
        private static float[] NoodlesOpenPath => new float[]
        {
            0f, 0.304f, 0.011f, -0.019f, -0.8398f, 0.0213f, 0.4825f, -0.2479f,
            0.111f, 0.304f, 0.04f, -0.034f, -0.8168f, -0.0138f, 0.5139f, -0.2619f,
            0.222f, 0.303f, 0.102f, -0.065f, -0.7644f, -0.0937f, 0.5701f, -0.2861f,
            0.333f, 0.301f, 0.165f, -0.094f, -0.7084f, -0.1836f, 0.6119f, -0.3001f,
            0.443f, 0.3f, 0.197f, -0.107f, -0.6807f, -0.2305f, 0.6259f, -0.3031f,
            0.554f, 0.294f, 0.163f, -0.096f, -0.7345f, -0.2075f, 0.5531f, -0.334f,
            0.665f, 0.266f, 0.11f, -0.063f, -0.8451f, -0.2636f, 0.3516f, -0.3045f,
            0.776f, 0.211f, 0.072f, -0.029f, -0.86f, -0.4107f, 0.186f, -0.2389f,
            0.887f, 0.15f, 0.033f, -0.013f, -0.8377f, -0.4801f, 0.1289f, -0.2261f,
            0.997f, 0.131f, 0.032f, 0.002f, -0.8181f, -0.5381f, 0.0665f, -0.1917f,
        };

        // Beer cap pop. Last two keys (the cap carried away, x/z grow) trimmed.
        private static float[] BeerOpenPath => new float[]
        {
            0.352f, 0.252f, 0.047f, 0.232f, -0.681f, 0.1064f, -0.4073f, -0.5992f,
            0.424f, 0.174f, 0.043f, 0.147f, -0.743f, -0.0957f, -0.2885f, -0.5963f,
            0.496f, 0.12f, 0.053f, 0.11f, -0.7453f, -0.346f, -0.2126f, -0.5287f,
            0.568f, 0.103f, 0.059f, 0.12f, -0.6815f, -0.3397f, -0.2656f, -0.5913f,
            0.639f, 0.111f, 0.03f, 0.158f, -0.6883f, -0.1608f, -0.2626f, -0.6569f,
            0.711f, 0.111f, 0.02f, 0.17f, -0.6963f, -0.1438f, -0.2823f, -0.6441f,
            0.783f, 0.184f, -0.015f, 0.16f, -0.7345f, -0.1317f, -0.3404f, -0.5721f,
            0.855f, 0.27f, 0.027f, 0.111f, -0.7542f, -0.0263f, -0.4957f, -0.4298f,
        };
    }
}
