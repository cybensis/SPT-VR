    using System;
using System.Collections.Generic;


namespace TarkovVR.Source.Weapons
{

    public class Scope
    {
        public string Name { get; set; }
        public float ZoomLevel { get; set; }
        public float FOV { get; set; }

        public Scope(string name, float zoomLevel, float fov)
        {
            Name = name;
            ZoomLevel = zoomLevel;
            FOV = fov;
        }
    }

    public static class ScopeManager
    {
        private static List<Scope> scopes = new List<Scope>();

        static ScopeManager()
        {
            AddScope("scope_30mm_s&b_pm_ii_1_8x24(Clone)", 1, 27);
            AddScope("scope_30mm_s&b_pm_ii_1_8x24(Clone)", 8f, 3);
            AddScope("scope_all_sig_sauer_echo1_thermal_reflex_sight_1_2x_30hz(Clone)", 1, 15);
            AddScope("scope_all_sig_sauer_echo1_thermal_reflex_sight_1_2x_30hz(Clone)", 2, 4);
            AddScope("scope_all_torrey_pines_logic_t12_w_30hz(Clone)", 1, 15);
            AddScope("scope_30mm_eotech_vudu_1_6x24(Clone)", 1, 26);
            AddScope("scope_g36_hensoldt_hkv_single_optic_carry_handle_1,5x(Clone)", 1.5f, 18);
            AddScope("scope_aug_steyr_rail_optic_1,5x(Clone)", 1.5f, 14);
            AddScope("scope_aug_steyr_stg77_optic_1,5x", 1.5f, 15);
            AddScope("scope_all_monstrum_compact_prism_scope_2x32(Clone)", 2, 11);
            AddScope("scope_g36_hensoldt_hkv_carry_handle_3x(Clone)", 3, 7.5f);
            AddScope("3", 3, 7.5f);
            AddScope("scope_base_kmz_1p59_3_10x(Clone)", 3, 7.6f);
            AddScope("scope_base_kmz_1p59_3_10x(Clone)", 10f, 2.5f);
            AddScope("scope_all_ncstar_advance_dual_optic_3_9x_42(Clone)", 3, 7.6f);
            AddScope("scope_all_ncstar_advance_dual_optic_3_9x_42(Clone)", 9f, 2.9f);
            AddScope("scope_base_npz_1p78_1_2,8x24(Clone)", 3, 9);
            AddScope("scope_34mm_s&b_pm_ii_3_12x50(Clone)", 3, 12);
            AddScope("scope_34mm_s&b_pm_ii_3_12x50(Clone)", 12f, 1.9f);
            AddScope("scope_base_progress_pu_3,5x(Clone)", 3.5f, 6);
            AddScope("scope_dovetail_npz_nspum_3,5x(Clone)", 3.5f, 6.5f);
            AddScope("scope_all_swampfox_trihawk_prism_scope_3x30(Clone)", 3.5f, 7.5f);
            AddScope("scope_base_trijicon_acog_ta11_3,5x35(Clone)", 3.5f, 7);
            AddScope("", 6f, 3.2f);
            AddScope("", 16f, 1.2f);
            AddScope("scope_34mm_nightforce_atacr_7_35x56(Clone)", 16f, 1);
            AddScope("scope_34mm_nightforce_atacr_7_35x56(Clone)", 7f, 1.6f);
            AddScope("scope_base_primary_arms_compact_prism_scope_2,5x(Clone)", 2.5f, 10);
            AddScope("scope_30mm_leupold_mark4_lr_6,5_20x50(Clone)", 20f, 1.5f);
            AddScope("scope_34mm_s&b_pm_ii_5_25x56(Clone)", 5f, 3.6f);
            AddScope("scope_34mm_s&b_pm_ii_5_25x56(Clone)", 25f, 1.9f);
            AddScope("scope_25_4mm_vomz_pilad_4x32m(Clone)", 4f, 6);
            AddScope("scope_all_leupold_mark4_hamr(Clone)", 4f, 6);
            AddScope("scope_all_sig_bravo4_4x30(Clone)", 4f, 6);
            AddScope("scope_34mm_hensoldt_zf_4_16x56_ff(Clone)", 4f, 6f);
            AddScope("scope_34mm_hensoldt_zf_4_16x56_ff(Clone)", 16f, 1.2f);
            AddScope("scope_dovetail_npz_1p29_4x(Clone)", 4f, 6f);
            AddScope("scope_all_elcan_specter_dr_1-4_fde(Clone)", 4f, 6f);
            AddScope("scope_all_elcan_specter_dr_1-4_fde(Clone)", 1f, 27);

        }

        public static void AddScope(string name, float zoomLevel, float fov)
        {
            scopes.Add(new Scope(name, zoomLevel, fov));
        }

        public static float GetFOV(string name, float zoomLevel)
        {
            foreach (var scope in scopes)
            {
                if (scope.Name == name && Math.Abs(scope.ZoomLevel - zoomLevel) < 0.01f)
                {
                    return scope.FOV;
                }
            }
            // If the scope with the specified name and zoom level is not found, return a default FOV
            return -1f; // Or any other default value you choose
        }

        public static float GetMinFOV(string name)
        {
            float minFOV = float.MaxValue;
            foreach (var scope in scopes)
            {
                if (scope.Name == name && scope.FOV < minFOV)
                {
                    minFOV = scope.FOV;
                }
            }
            return minFOV;
        }

        public static float GetMaxFOV(string name)
        {
            float maxFOV = float.MinValue;
            foreach (var scope in scopes)
            {
                if (scope.Name == name && scope.FOV > maxFOV)
                {
                    maxFOV = scope.FOV;
                }
            }
            return maxFOV;
        }
    }
}
