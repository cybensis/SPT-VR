using EFT.InventoryLogic;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // Aim steadying: a One Euro filter on the gun hand's rotation (before the weight), in the rig's frame (a stick turn isn't the hand).
    // Its cutoff rises with how fast the hand turns, so a nearly still hand - the shake and tracking noise a scope magnifies - is
    // smoothed hard while a deliberate move passes through (the most it lags is 1 / (2 pi speedResponse) deg as seen in the sight).
    // Only while aiming down sights; the still cutoff drops with the sight's magnification. Holding breath is the "holding still now":
    // much heavier smoothing (more lag on a move), fading out as the lungs run out; out of breath the normal steadying weakens.
    public static class AimSteady
    {
        public static bool enabled = true;              // false = the old hold-breath slerp (Hold Breath Sensitivity)
        public static float stillCutoff = 4f;           // Hz a still hand is filtered at through a 1x sight (/ the magnification); lower = steadier, slower to settle
        public static float minCutoff = 0.15f;          // Hz floor (a high zoom)
        public static float speedResponse = 0.4f;       // Hz added per deg/s the sight's view turns: higher = less lag moving, more shake left
        public static float speedCutoff = 1f;           // Hz the turn rate is smoothed at before it opens the filter (lower = shake opens it less, a move later)
        public static float breathStillScale = 0.25f;   // x the still cutoff holding breath
        public static float breathSpeedScale = 0.25f;   // x the speed response holding breath (lags a move more)
        public static float breathFadeBelow = 0.4f;     // lungs (0..1) below which the hold-breath steadying fades out
        public static float exhaustedStrength = 0.5f;   // x the steadying while out of breath
        public static float easeTime = 0.2f;            // s it fades in / out as you aim / hold breath
        public static float maxMagnification = 40f;
        public static bool debugLog = false;            // a line a second: magnification, breath, the cutoffs, the hand's turn rate

        private static Quaternion filtered = Quaternion.identity;   // in the frame
        private static Quaternion lastRaw = Quaternion.identity;
        private static Vector3 turnRate;                            // rad/s, smoothed, the frame's axes
        private static float lastTime = -10f;
        // The state at the start of this frame: a second call in the same frame (a newer pose) redoes the frame from it
        private static int stateFrame = -1;
        private static Quaternion frameStartFiltered = Quaternion.identity;
        private static Quaternion frameStartRaw = Quaternion.identity;
        private static Vector3 frameStartTurnRate;
        private static float frameStartAim;
        private static float frameStartBreath;
        private static float frameDeltaTime;
        private static bool frameStartsOver;
        private static float aimWeight;
        private static float breathWeight;
        private static float nextLog;

        public static bool Active => enabled && VRSettings.SmoothScopeAim() && VRSettings.GetAimSteadyStrength() > 0f;

        // The hand's rotation (world) steadied for this frame. frame = what the rotation rides (the rig)
        public static Quaternion Filter(Quaternion target, Transform frame, float dt)
        {
            Quaternion frameRotation = frame != null ? frame.rotation : Quaternion.identity;
            Quaternion raw = Quaternion.Inverse(frameRotation) * target;
            if (Time.frameCount != stateFrame)
            {
                stateFrame = Time.frameCount;
                frameStartFiltered = filtered;
                frameStartRaw = lastRaw;
                frameStartTurnRate = turnRate;
                frameStartAim = aimWeight;
                frameStartBreath = breathWeight;
                frameDeltaTime = Mathf.Clamp(dt, 1e-4f, 0.05f);
                frameStartsOver = Time.time - lastTime > 0.25f;   // a gap (no gun, a menu): start over
                lastTime = Time.time;
            }
            filtered = frameStartFiltered;
            lastRaw = frameStartRaw;
            turnRate = frameStartTurnRate;
            aimWeight = frameStartAim;
            breathWeight = frameStartBreath;
            dt = frameDeltaTime;
            if (frameStartsOver || Quaternion.Angle(raw, lastRaw) >= 30f)
            {
                filtered = raw;
                lastRaw = raw;
                turnRate = Vector3.zero;
                aimWeight = 0f;
                breathWeight = 0f;
            }

            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            bool aiming = Active && firearmController != null && firearmController.IsAiming;
            float ease = 1f - Mathf.Exp(-dt / Mathf.Max(easeTime, 1e-3f));
            aimWeight = Mathf.Lerp(aimWeight, aiming ? 1f : 0f, ease);
            Lungs(out bool holdingBreath, out float lungs, out bool exhausted);
            float breathTarget = aiming && holdingBreath ? Mathf.Clamp01(lungs / Mathf.Max(breathFadeBelow, 1e-3f)) : 0f;
            breathWeight = Mathf.Lerp(breathWeight, breathTarget, ease);

            Vector3 turnNow = RotationVector(raw * Quaternion.Inverse(lastRaw)) / dt;
            lastRaw = raw;
            turnRate = Vector3.Lerp(turnRate, turnNow, Alpha(speedCutoff, dt));

            if (aimWeight < 1e-3f)
            {
                filtered = raw;
                return target;
            }
            float magnification = aiming ? Magnification() : 1f;
            float strength = VRSettings.GetAimSteadyStrength() * aimWeight * (exhausted && !holdingBreath ? exhaustedStrength : 1f);
            float still = Mathf.Max(stillCutoff / magnification * Mathf.Lerp(1f, breathStillScale, breathWeight), minCutoff);
            float response = speedResponse * Mathf.Lerp(1f, breathSpeedScale, breathWeight);
            float apparentSpeed = turnRate.magnitude * Mathf.Rad2Deg * magnification;
            float cutoff = (still + response * apparentSpeed) / Mathf.Max(strength, 1e-3f);
            filtered = Quaternion.Slerp(filtered, raw, Alpha(cutoff, dt));

            if (debugLog && Time.time >= nextLog)
            {
                nextLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[AimSteady] {magnification:0.0}x, breath {breathWeight:0.00} (lungs {lungs:0.00}{(exhausted ? ", out of breath" : "")}), strength {strength:0.00},"
                    + $" still {still:0.00} Hz, turning {apparentSpeed:0.0} deg/s in the sight -> {cutoff:0.00} Hz, {Quaternion.Angle(filtered, raw):0.00} deg off the hand");
            }
            return frameRotation * filtered;
        }

        private static float Alpha(float cutoff, float dt)
        {
            float timeConstant = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-3f));
            return 1f / (1f + timeConstant / dt);
        }

        private static void Lungs(out bool holdingBreath, out float lungs, out bool exhausted)
        {
            holdingBreath = Controls.InputHandlers.BreathingHandler.isHoldingBreath;
            lungs = 1f;
            exhausted = false;
            try
            {
                var oxygen = VRGlobals.player?.Physical?.Oxygen;
                if (oxygen == null)
                    return;
                lungs = Mathf.Clamp01(oxygen.NormalValue);
                exhausted = oxygen.Exhausted;
            }
            catch { }
        }

        // The aimed sight's magnification: 1 for irons / a red dot; a variable scope's from its camera's view against its widest
        private static float Magnification()
        {
            var weaponAnimation = VRGlobals.player != null ? VRGlobals.player.ProceduralWeaponAnimation : null;
            if (weaponAnimation == null || weaponAnimation.CurrentScope == null || !weaponAnimation.CurrentScope.IsOptic)
                return 1f;
            SightComponent sight = weaponAnimation.CurrentAimingMod;
            if (sight == null)
                return 1f;
            float zoom = 1f;
            try
            {
                VROpticController optic = VRGlobals.vrOpticController;
                if (optic != null && optic.isSmoothZoom && optic.scopeCamera != null && optic.baseZoomHandler != null
                    && optic.baseZoomHandler._sightComponent == sight && optic.maxFov > 0f)
                {
                    float widest = Mathf.Tan(optic.maxFov * 0.5f * Mathf.Deg2Rad);
                    float now = Mathf.Tan(Mathf.Max(optic.scopeCamera.fieldOfView, 0.01f) * 0.5f * Mathf.Deg2Rad);
                    zoom = Mathf.Max(sight.GetMinOpticZoom(), 1f) * widest / now;
                }
                else
                    zoom = sight.GetCurrentOpticZoom();
            }
            catch { }
            return Mathf.Clamp(zoom, 1f, Mathf.Max(maxMagnification, 1f));
        }

        private static Vector3 RotationVector(Quaternion rotation)
        {
            if (rotation.w < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            rotation.ToAngleAxis(out float degrees, out Vector3 axis);
            if (degrees > 180f)
                degrees -= 360f;
            if (Mathf.Abs(degrees) < 1e-6f || float.IsInfinity(axis.x) || float.IsNaN(axis.x))
                return Vector3.zero;
            return axis.normalized * (degrees * Mathf.Deg2Rad);
        }
    }
}
