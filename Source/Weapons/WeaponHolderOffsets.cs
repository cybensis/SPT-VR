using Sirenix.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Source.Weapons
{
    public static class WeaponHolderOffsets
    {
        private static Dictionary<string, Vector3> weaponOffsets = new Dictionary<string, Vector3>();
        private static Dictionary<string, Vector3> classOffsets = new Dictionary<string, Vector3>();
        private static Vector3 defaultOffset = new Vector3(0.141f, 0.0204f, -0.1303f);
        static WeaponHolderOffsets()
        {

            // Assault carbines
            weaponOffsets.Add("weapon_kel_tec_rfb_762x51_container(Clone)", new Vector3(0.29f, -0.11f, -0.16f));
            weaponOffsets.Add("weapon_molot_op_sks_762x39_container(Clone)", new Vector3(0.211f, -0.1f, -0.12f));
            weaponOffsets.Add("weapon_toz_sks_762x39_container(Clone)", new Vector3(0.211f, -0.1f, -0.13f));
            weaponOffsets.Add("weapon_sag_ak545_short_545x39_container(Clone)", new Vector3(0.21f, -0.07f, -0.16f));
            weaponOffsets.Add("weapon_toz_svt_40_762x54r_container(Clone)", new Vector3(0.211f, -0.09f, -0.16f));
            // Assault rifles
            weaponOffsets.Add("weapon_tochmash_val_9x39_container(Clone)", new Vector3(0.28f, -0.1f, -0.16f));
            weaponOffsets.Add("weapon_ckib_ash_12_127x55_container(Clone)", new Vector3(0.29f, -0.07f, -0.18f));
            weaponOffsets.Add("weapon_dsa_sa58_762x51_container(Clone)", new Vector3(0.3f, -0.07f, -0.16f));
            weaponOffsets.Add("weapon_molot_vepr_hunter_vpo-101_762x51_container(Clone)", new Vector3(0.21f, -0.07f, -0.13f));
            weaponOffsets.Add("weapon_dt_mdr_556x45_container(Clone)", new Vector3(0.27f, -0.07f, -0.18f));
            weaponOffsets.Add("weapon_steyr_aug_a1_556x45_container(Clone)", new Vector3(0.28f, -0.07f, -0.15f));
            weaponOffsets.Add("weapon_dt_mdr_762x51_container(Clone)", new Vector3(0.24f, -0.08f, -0.16f));
            weaponOffsets.Add("weapon_cmmg_mk47_762x39_container(Clone)", new Vector3(0.23f, -0.09f, -0.16f));
            weaponOffsets.Add("weapon_fn_mk16_556x45_fde_container(Clone)", new Vector3(0.22f, -0.09f, -0.16f));
            // Bolt-action rifles
            weaponOffsets.Add("weapon_izhmash_sv-98_762x54r_container(Clone)", new Vector3(0.29f, -0.11f, -0.18f));
            weaponOffsets.Add("weapon_molot_vpo_215_366tkm_container(Clone)", new Vector3(0.27f, -0.11f, -0.15f));
            weaponOffsets.Add("weapon_izhmash_mosin_rifle_762x54_container(Clone)", new Vector3(0.27f, -0.11f, -0.15f));
            weaponOffsets.Add("weapon_izhmash_mosin_infantry_762x54_container(Clone)", new Vector3(0.27f, -0.11f, -0.15f));
            // Machine guns
            weaponOffsets.Add("weapon_izhmash_rpk16_545x39_container(Clone)", new Vector3(0.23f, -0.11f, -0.15f));
            weaponOffsets.Add("weapon_zid_rpdn_762x39_container(Clone)", new Vector3(0.24f, -0.11f, -0.15f));
            // Marksman Rifle
            weaponOffsets.Add("weapon_springfield_m1a_762x51_container(Clone)", new Vector3(0.27f, -0.08f, -0.16f));
            weaponOffsets.Add("weapon_izhmash_svd_s_762x54_container(Clone)", new Vector3(0.21f, -0.11f, -0.17f));
            weaponOffsets.Add("weapon_kac_sr25_762x51_container(Clone)", new Vector3(0.19f, -0.11f, -0.17f));
            weaponOffsets.Add("weapon_tochmash_vss_9x39_container(Clone)", new Vector3(0.27f, -0.09f, -0.15f));
            weaponOffsets.Add("weapon_hk_g28_762x51_container(Clone)", new Vector3(0.24f, -0.09f, -0.16f));
            weaponOffsets.Add("weapon_sig_mcx_spear_68x51_container(Clone)", new Vector3(0.21f, -0.08f, - 0.16f));
            // Pistols
            weaponOffsets.Add("weapon_chiappa_rhino_200ds_9x19_container(Clone)", new Vector3(0.41f, -0.04f, -0.13f));
            weaponOffsets.Add("weapon_chiappa_rhino_50ds_9x33R_container(Clone)", new Vector3(0.41f, -0.04f, -0.13f));
            // SMGs
            weaponOffsets.Add("weapon_fn_p90_57x28_container(Clone)", new Vector3(0.31f, -0.08f, -0.18f));
            weaponOffsets.Add("weapon_tdi_kriss_vector_gen_2_1143x23_container(Clone)", new Vector3(0.29f, -0.08f, -0.13f));
            weaponOffsets.Add("weapon_tdi_kriss_vector_gen_2_9x19_container(Clone)", new Vector3(0.29f, -0.08f, -0.13f));
            weaponOffsets.Add("weapon_hk_mp5_kurtz_9x19_container(Clone)", new Vector3(0.25f, -0.08f, -0.19f));
            weaponOffsets.Add("weapon_bt_mp9_9x19_container(Clone)", new Vector3(0.29f, -0.08f, -0.15f));
            weaponOffsets.Add("weapon_hk_mp7a1_46x30_container(Clone)", new Vector3(0.35f, -0.05f, -0.18f));
            weaponOffsets.Add("weapon_hk_ump_1143x23_container(Clone)", new Vector3(0.29f, -0.05f, -0.2f));
            weaponOffsets.Add("weapon_hk_mp5_navy3_9x19_container(Clone)", new Vector3(0.24f, -0.06f, -0.18f));
            weaponOffsets.Add("weapon_zmz_pp-9_9x18pmm_container(Clone)", new Vector3(0.26f, -0.08f, -0.16f));
            weaponOffsets.Add("weapon_zmz_pp-91-01_9x18pm_container(Clone)", new Vector3(0.32f, -0.05f, -0.15f));
            weaponOffsets.Add("weapon_zmz_pp-91_9x18pm_container(Clone)", new Vector3(0.32f, -0.05f, -0.15f));
            weaponOffsets.Add("weapon_zis_ppsh41_762x25_container(Clone)", new Vector3(0.25f, -0.08f, -0.15f));
            weaponOffsets.Add("weapon_sig_mpx_9x19_container(Clone)", new Vector3(0.25f, -0.05f, -0.18f));
            weaponOffsets.Add("weapon_tochmash_sr2m_veresk_9x21_container(Clone)", new Vector3(0.37f, -0.04f, -0.15f));
            weaponOffsets.Add("weapon_stmarms_stm_9_9x19_container(Clone)", new Vector3(0.26f, -0.07f, -0.18f));
            weaponOffsets.Add("weapon_bt_mp9n_9x19_container(Clone)", new Vector3(0.29f, -0.08f, -0.15f));
            weaponOffsets.Add("weapon_hk_mp7a2_46x30_container(Clone)", new Vector3(0.29f, -0.07f, -0.17f));
            weaponOffsets.Add("weapon_izhmash_pp-19-01_9x19_container(Clone)", new Vector3(0.26f, -0.08f, -0.16f));
            weaponOffsets.Add("weapon_izhmash_saiga_9_9x19_container(Clone)", new Vector3(0.26f, -0.08f, -0.16f));
            // Shotguns
            weaponOffsets.Add("weapon_izhmeh_mr43_sawed_off_12g_container(Clone)", new Vector3(0.23f, -0.08f, -0.15f));
            weaponOffsets.Add("weapon_toz_toz-106_20g_container(Clone)", new Vector3(0.28f, -0.09f, -0.14f));
            weaponOffsets.Add("weapon_benelli_m3_s90_12g_container(Clone)", new Vector3(0.24f, -0.09f, -0.12f));
            weaponOffsets.Add("weapon_izhmash_mp18_multi_container(Clone)", new Vector3(0.28f, -0.09f, -0.13f));
            weaponOffsets.Add("weapon_izhmeh_mr43_12g_container(Clone)", new Vector3(0.27f, -0.11f, -0.13f));
            weaponOffsets.Add("weapon_ckib_mc_255_12g_container(Clone)", new Vector3(0.27f, -0.09f, -0.13f));
            weaponOffsets.Add("weapon_izhmash_saiga12k_10_12g_container(Clone)", new Vector3(0.23f, -0.08f, -0.16f));


            classOffsets.Add("shotgun", new Vector3(0.21f, -0.1f, -0.09f));
            classOffsets.Add("smg", new Vector3(0.40f, -0.05f, -0.18f));
            classOffsets.Add("pistol", new Vector3(0.41f, -0.05f, -0.16f));
            classOffsets.Add("marksmanRifle", new Vector3(0.26f, -0.09f, -0.16f));
            classOffsets.Add("sniperRifle", new Vector3(0.27f, -0.11f, -0.18f));
            classOffsets.Add("assaultRifle", new Vector3(0.19f, -0.07f, -0.18f));
            classOffsets.Add("grenade", new Vector3(0.2f, -0.13f, -0.15f));
        }

        public static Vector3 GetWeaponHolderOffset(string weaponName, string weaponClass) {
            if (weaponOffsets.ContainsKey(weaponName)) { return weaponOffsets[weaponName]; }
            else if (classOffsets.ContainsKey(weaponClass)) { return classOffsets[weaponClass]; }
            else return defaultOffset;
        }
    }
}
