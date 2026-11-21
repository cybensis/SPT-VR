using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Weapons
{
    // Decides which controllers feel something that happens to the gun. The gun is one solid object: the hand doing the work feels
    // it most, and every other hand on the gun feels it through the gun. The gun's own parts moving (a slam, a seat, a wall hit)
    // reach every hand on its body at full strength. A hand that has let go feels nothing.
    public static class GunHaptics
    {
        public enum Feel
        {
            Alone,      // only the hand doing it: off the gun (a pouch, a zone blip)
            Carried,    // that hand at full, every other hand on the gun through the gun (HapticMixer.throughGain / throughPitch)
            Mechanism,  // the gun's own parts: every hand on its body at full, a hand on one of its parts through the gun
        }

        // Recoil kick on every shot: a sharp snap, then the shove
        public static bool recoilKick = true;
        public static float recoilLightest = 0.55f;     // kick strength for the softest-recoiling guns...
        public static float recoilHeaviest = 1f;        // ...and the hardest
        public static float recoilSoftForce = 150f;     // RecoilForceBack mapped onto that range
        public static float recoilHardForce = 700f;
        public static float recoilSupportGain = 0.7f;   // share for the hand on the handguard
        public static float recoilSnapPitch = 160f;
        public static float recoilSnapLength = 0.012f;
        public static float recoilShovePitch = 70f;
        public static float recoilShoveLength = 0.06f;
        public static float recoilShoveGain = 0.85f;
        public static bool recoilDebugLog = false;      // the gun's recoil force and the kick, every shot

        private static bool GunHeld => VRGlobals.player != null && VRGlobals.player.HandsController is EFT.Player.FirearmController;

        // The gun hand on the gun's grip: not slung, not left to the support hand, not off working a part
        public static bool GunHandOnBody => GunHeld && !ChestHang.Detached && !VRReloadController.GunHandOnGunPart;
        public static bool GunHandOnPart => GunHeld && VRReloadController.GunHandOnGunPart;
        // The off hand on the foregrip, or holding the gun alone
        public static bool OffHandOnBody => GunHeld && ((VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting) || ChestHang.SupportOnly);
        public static bool OffHandOnPart => GunHeld && VRReloadController.OffHandOnGunPart;

        // gunHandActs: true = the gun hand does it, false = the off hand, null = no hand (the gun's own parts)
        public static void Gains(bool? gunHandActs, Feel feel, out float gunGain, out float gunPitch, out float offGain, out float offPitch)
        {
            gunGain = gunHandActs == true ? 1f : 0f;
            offGain = gunHandActs == false ? 1f : 0f;
            gunPitch = 1f;
            offPitch = 1f;
            if (feel == Feel.Alone)
                return;
            if (gunHandActs != true)
                Reach(GunHandOnBody, GunHandOnPart, feel, ref gunGain, ref gunPitch);
            if (gunHandActs != false)
                Reach(OffHandOnBody, OffHandOnPart, feel, ref offGain, ref offPitch);
        }

        private static void Reach(bool onBody, bool onPart, Feel feel, ref float gain, ref float pitch)
        {
            if (feel == Feel.Mechanism && onBody)
            {
                gain = 1f;
                return;
            }
            if (onBody || onPart)
            {
                gain = HapticMixer.throughGain;
                pitch = HapticMixer.throughPitch;
            }
        }

        public static void Pulse(bool? gunHandActs, Feel feel, float amplitude, float duration, float frequency = 120f, float delay = 0f)
        {
            Gains(gunHandActs, feel, out float gunGain, out float gunPitch, out float offGain, out float offPitch);
            if (gunGain > 0f)
                HapticMixer.PlayPulse(Handedness.GunHandSource, amplitude * gunGain, duration, frequency * gunPitch, delay);
            if (offGain > 0f)
                HapticMixer.PlayPulse(Handedness.OffHandSource, amplitude * offGain, duration, frequency * offPitch, delay);
        }

        // Called by RecoilHapticsPatch (FirearmController.Shot) for our player's shots only. The hand on the grip feels it at full,
        // the hand on the handguard most of it; a gun held any other way can't fire.
        public static void Recoil(Weapon weapon)
        {
            if (!recoilKick || weapon == null)
                return;
            float force = weapon.RecoilForceBack;
            float kick = Mathf.Lerp(recoilLightest, recoilHeaviest, Mathf.InverseLerp(recoilSoftForce, recoilHardForce, force));
            if (recoilDebugLog)
                Plugin.MyLog.LogInfo($"[GunHaptics] shot: {weapon.LocalizedShortName()} RecoilForceBack {force:0}, kick {kick:0.00}, grip hand {GunHandOnBody}, support hand {OffHandOnBody}");
            // A mounted gun: the hands on its handles
            if (TarkovVR.Source.Player.Interactions.MountedGun.Recoil(kick))
                return;
            if (GunHandOnBody)
                Kick(Handedness.GunHandSource, kick);
            if (OffHandOnBody)
                Kick(Handedness.OffHandSource, kick * recoilSupportGain);
        }

        private static void Kick(SteamVR_Input_Sources hand, float amplitude)
        {
            HapticMixer.PlayPulse(hand, amplitude, recoilSnapLength, recoilSnapPitch);
            HapticMixer.PlayPulse(hand, amplitude * recoilShoveGain, recoilShoveLength, recoilShovePitch, delay: recoilSnapLength * 0.8f);
        }
    }
}
