using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static TarkovVR.Source.Weapons.WeaponMeshParts;

namespace TarkovVR.Source.Weapons
{
    public static class WeaponMeshList
    {

        private static Dictionary<string, WeaponMeshParts> weaponMeshDictionary;
        static WeaponMeshList()
        {
            // TODO
            // - Mk47 Mutant
            // - Desert tech 7.62 MDR
            // - Accuracy international AXMC
            // - Sword internation mk18
            // - RSASS
            // - SPEAR
            weaponMeshDictionary = new Dictionary<string, WeaponMeshParts>();

            // ---------------- ASSAULT CARBINES ----------------
            WeaponMeshParts rfb = new WeaponMeshParts();
            rfb.magazine.Add("mod_magazine");
            rfb.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_kel_tec_rfb_762x51_container(Clone)", rfb);

            WeaponMeshParts adar = new WeaponMeshParts();
            adar.magazine.Add("mod_magazine");
            adar.chamber.Add("mod_charge");
            adar.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_adar_2-15_556x45_container(Clone)", adar);

            WeaponMeshParts tx15 = new WeaponMeshParts();
            tx15.magazine.Add("mod_magazine");
            tx15.chamber.Add("mod_charge");
            tx15.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_lone_star_tx15_designated_marksman_556x45_container(Clone)", tx15);

            WeaponMeshParts veprHunter = new WeaponMeshParts();
            veprHunter.magazine.Add("mod_magazine");
            veprHunter.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_molot_vepr_hunter_vpo-101_762x51_container(Clone)", veprHunter);

            WeaponMeshParts akmVpo209 = new WeaponMeshParts();
            akmVpo209.magazine.Add("mod_magazine");
            akmVpo209.chamber.Add("weapon_bolt");
            akmVpo209.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_molot_akm_vpo_209_366TKM_container(Clone)", akmVpo209);

            WeaponMeshParts sagAk545 = new WeaponMeshParts();
            sagAk545.magazine.Add("mod_magazine");
            sagAk545.chamber.Add("weapon_bolt");
            sagAk545.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_sag_ak545_545x39_container(Clone)", sagAk545);

            WeaponMeshParts akVeprVpo136 = new WeaponMeshParts();
            akVeprVpo136.magazine.Add("mod_magazine");
            akVeprVpo136.chamber.Add("weapon_bolt");
            akVeprVpo136.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_molot_vepr_km_vpo_136_762x39_container(Clone)", akVeprVpo136);

            WeaponMeshParts sagAk545Short = new WeaponMeshParts();
            sagAk545Short.magazine.Add("mod_magazine");
            sagAk545Short.chamber.Add("weapon_bolt");
            sagAk545Short.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_sag_ak545_short_545x39_container(Clone)", sagAk545Short);

            WeaponMeshParts opSKS = new WeaponMeshParts();
            opSKS.magazine.Add("mod_magazine");
            opSKS.chamber.Add("weapon_bolt_assembly");
            weaponMeshDictionary.Add("weapon_molot_op_sks_762x39_container(Clone)", opSKS);

            WeaponMeshParts tozSKS = new WeaponMeshParts();
            tozSKS.magazine.Add("mod_magazine");
            tozSKS.chamber.Add("weapon_bolt_assembly");
            weaponMeshDictionary.Add("weapon_toz_sks_762x39_container(Clone)", tozSKS);

            WeaponMeshParts tozSVT = new WeaponMeshParts();
            tozSVT.magazine.Add("mod_magazine");
            tozSVT.chamber.Add("weapon_bolt_assembly");
            weaponMeshDictionary.Add("weapon_toz_svt_40_762x54r_container(Clone)", tozSVT);


            // ---------------- ASSAULT RIFLES ----------------
            WeaponMeshParts ash12 = new WeaponMeshParts();
            ash12.magazine.Add("mod_magazine");
            ash12.chamber.Add("weapon_bolt");
            ash12.firingModeSwitch.Add("weapon_selector_left");
            ash12.firingModeSwitch.Add("weapon_selector_right");
            weaponMeshDictionary.Add("weapon_ckib_ash_12_127x55_container(Clone)", ash12);

            WeaponMeshParts asVal = new WeaponMeshParts();
            asVal.magazine.Add("mod_magazine");
            asVal.chamber.Add("weapon_bolt");
            asVal.stock.Add("mod_stock");
            asVal.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_tochmash_val_9x39_container(Clone)", asVal);

            WeaponMeshParts m4a1 = new WeaponMeshParts();
            m4a1.magazine.Add("mod_magazine");
            m4a1.chamber.Add("mod_charge");
            m4a1.stock.Add("mod_stock");
            m4a1.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_colt_m4a1_556x45_container(Clone)", m4a1);

            WeaponMeshParts mdr556 = new WeaponMeshParts();
            mdr556.magazine.Add("mod_magazine");
            mdr556.chamber.Add("weapon_charge");
            mdr556.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_dt_mdr_556x45_container(Clone)", mdr556);

            WeaponMeshParts sa58 = new WeaponMeshParts();
            sa58.magazine.Add("mod_magazine");
            sa58.chamber.Add("weapon_bolt_carrier");
            sa58.stock.Add("mod_stock");
            sa58.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_dsa_sa58_762x51_container(Clone)", sa58);

            WeaponMeshParts mk17 = new WeaponMeshParts();
            mk17.magazine.Add("mod_magazine");
            mk17.chamber.Add("mod_charge");
            mk17.stock.Add("mod_stock");
            mk17.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_fn_mk17_762x51_container(Clone)", mk17);

            WeaponMeshParts mk17fde = new WeaponMeshParts();
            mk17.magazine.Add("mod_magazine");
            mk17.chamber.Add("mod_charge");
            mk17.stock.Add("mod_stock");
            mk17.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_fn_mk17_762x51_fde_container(Clone)", mk17);

            WeaponMeshParts x17 = new WeaponMeshParts();
            x17.magazine.Add("mod_magazine");
            x17.chamber.Add("weapon_bolt");
            x17.stock.Add("mod_stock");
            x17.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_x_products_x17_scar_17_762x51_container(Clone)", x17);

            WeaponMeshParts hk = new WeaponMeshParts();
            hk.magazine.Add("mod_magazine");
            hk.chamber.Add("mod_charge");
            hk.stock.Add("mod_stock");
            hk.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_hk_416a5_556x45_container(Clone)", hk);

            WeaponMeshParts mk16 = new WeaponMeshParts();
            mk16.magazine.Add("mod_magazine");
            mk16.chamber.Add("mod_charge");
            mk16.stock.Add("mod_stock");
            mk16.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_fn_mk16_556x45_container(Clone)", mk16);

            WeaponMeshParts mk16fde = new WeaponMeshParts();
            mk16fde.magazine.Add("mod_magazine");
            mk16fde.chamber.Add("mod_charge");
            mk16fde.stock.Add("mod_stock");
            mk16fde.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_fn_mk16_556x45_fde_container(Clone)", mk16fde);

            WeaponMeshParts hkg36 = new WeaponMeshParts();
            hkg36.magazine.Add("mod_magazine");
            hkg36.chamber.Add("weapon_bolt_carrier");
            hkg36.stock.Add("mod_stock");
            hkg36.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_hk_g36_556x45_container(Clone)", hkg36);

            WeaponMeshParts ak101 = new WeaponMeshParts();
            ak101.magazine.Add("mod_magazine");
            ak101.chamber.Add("weapon_bolt");
            ak101.stock.Add("mod_stock");
            ak101.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak101_556x45_container(Clone)", ak101);

            WeaponMeshParts ak102 = new WeaponMeshParts();
            ak102.magazine.Add("mod_magazine");
            ak102.chamber.Add("weapon_bolt");
            ak102.stock.Add("mod_stock");
            ak102.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak102_556x45_container(Clone)", ak102);

            WeaponMeshParts ak103 = new WeaponMeshParts();
            ak103.magazine.Add("mod_magazine");
            ak103.chamber.Add("weapon_bolt");
            ak103.stock.Add("mod_stock");
            ak103.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak103_762x39_container(Clone)", ak103);

            WeaponMeshParts ak104 = new WeaponMeshParts();
            ak104.magazine.Add("mod_magazine");
            ak104.chamber.Add("weapon_bolt");
            ak104.stock.Add("mod_stock");
            ak104.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak104_762x39_container(Clone)", ak104);

            WeaponMeshParts ak105 = new WeaponMeshParts();
            ak105.magazine.Add("mod_magazine");
            ak105.chamber.Add("weapon_bolt");
            ak105.stock.Add("mod_stock");
            ak105.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak105_545x39_container(Clone)", ak105);

            WeaponMeshParts ak74 = new WeaponMeshParts();
            ak74.magazine.Add("mod_magazine");
            ak74.chamber.Add("weapon_bolt");
            ak74.stock.Add("mod_stock");
            ak74.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak74_545x39_container(Clone)", ak74);

            WeaponMeshParts ak74m = new WeaponMeshParts();
            ak74m.magazine.Add("mod_magazine");
            ak74m.chamber.Add("weapon_bolt");
            ak74m.stock.Add("mod_stock");
            ak74m.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak74m_545x39_container(Clone)", ak74m);

            WeaponMeshParts ak74n = new WeaponMeshParts();
            ak74n.magazine.Add("mod_magazine");
            ak74n.chamber.Add("weapon_bolt");
            ak74n.stock.Add("mod_stock");
            ak74n.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak74n_545x39_container(Clone)", ak74n);

            WeaponMeshParts akm = new WeaponMeshParts();
            akm.magazine.Add("mod_magazine");
            akm.chamber.Add("weapon_bolt");
            akm.stock.Add("mod_stock");
            akm.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_akm_762x39_container(Clone)", akm);

            WeaponMeshParts akmsn = new WeaponMeshParts();
            akmsn.magazine.Add("mod_magazine");
            akmsn.chamber.Add("weapon_bolt");
            akmsn.stock.Add("mod_stock");
            akmsn.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_akmsn_762x39_container(Clone)", akmsn);

            WeaponMeshParts akms = new WeaponMeshParts();
            akms.magazine.Add("mod_magazine");
            akms.chamber.Add("weapon_bolt");
            akms.stock.Add("mod_stock");
            akms.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_akms_762x39_container(Clone)", akms);

            WeaponMeshParts akmn = new WeaponMeshParts();
            akmn.magazine.Add("mod_magazine");
            akmn.chamber.Add("weapon_bolt");
            akmn.stock.Add("mod_stock");
            akmn.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_akmn_762x39_container(Clone)", akmn);

            WeaponMeshParts ak12 = new WeaponMeshParts();
            ak12.magazine.Add("mod_magazine");
            ak12.chamber.Add("weapon_bolt");
            ak12.stock.Add("mod_stock");
            ak12.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_ak12_545x39_container(Clone)", ak12);

            WeaponMeshParts aks74n = new WeaponMeshParts();
            aks74n.magazine.Add("mod_magazine");
            aks74n.chamber.Add("weapon_bolt");
            aks74n.stock.Add("mod_stock");
            aks74n.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_aks74n_545x39_container(Clone)", aks74n);

            WeaponMeshParts aks74 = new WeaponMeshParts();
            aks74.magazine.Add("mod_magazine");
            aks74.chamber.Add("weapon_bolt");
            aks74.stock.Add("mod_stock");
            aks74.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_aks74_545x39_container(Clone)", aks74);

            WeaponMeshParts aks74ub = new WeaponMeshParts();
            aks74ub.magazine.Add("mod_magazine");
            aks74ub.chamber.Add("weapon_bolt");
            aks74ub.stock.Add("mod_stock");
            aks74ub.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_aks74ub_545x39_container(Clone)", aks74ub);

            WeaponMeshParts aks74u = new WeaponMeshParts();
            aks74u.magazine.Add("mod_magazine");
            aks74u.chamber.Add("weapon_bolt");
            aks74u.stock.Add("mod_stock");
            aks74u.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_izhmash_aks74u_545x39_container(Clone)", aks74u);

            WeaponMeshParts aklys = new WeaponMeshParts();
            aklys.magazine.Add("mod_magazine");
            aklys.chamber.Add("weapon_bolt");
            aklys.stock.Add("mod_stock");
            aklys.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_aklys_defense_velociraptor_762x35_container(Clone)", aklys);

            WeaponMeshParts auga1 = new WeaponMeshParts();
            auga1.magazine.Add("mod_magazine");
            auga1.chamber.Add("mod_charge");
            auga1.firingModeSwitch.Add("weapon_safety");
            weaponMeshDictionary.Add("weapon_steyr_aug_a1_556x45_container(Clone)", auga1);

            WeaponMeshParts auga3 = new WeaponMeshParts();
            auga3.magazine.Add("mod_magazine");
            auga3.chamber.Add("mod_charge");
            auga3.firingModeSwitch.Add("weapon_safety");
            weaponMeshDictionary.Add("weapon_steyr_aug_a3_m1_556x45_container(Clone)", auga3);

            WeaponMeshParts auga3blk = new WeaponMeshParts();
            auga3blk.magazine.Add("mod_magazine");
            auga3blk.chamber.Add("mod_charge");
            auga3blk.firingModeSwitch.Add("weapon_safety");
            weaponMeshDictionary.Add("weapon_steyr_aug_a3_m1_556x45_blk_container(Clone)", auga3blk);

            WeaponMeshParts mcx = new WeaponMeshParts();
            mcx.magazine.Add("mod_magazine");
            mcx.chamber.Add("mod_charge");
            mcx.firingModeSwitch.Add("weapon_selector");
            mcx.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_sig_mcx_gen1_762x35_container(Clone)", mcx);

            WeaponMeshParts rd704 = new WeaponMeshParts();
            rd704.magazine.Add("mod_magazine");
            rd704.chamber.Add("weapon_bolt");
            rd704.firingModeSwitch.Add("weapon_selector");
            rd704.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_rifle_dynamics_704_762x39_container(Clone)", rd704);

            WeaponMeshParts mdr762 = new WeaponMeshParts();
            mdr762.magazine.Add("mod_magazine");
            mdr762.chamber.Add("weapon_charge_000");
            mdr762.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_dt_mdr_762x51_container(Clone)", mdr762);

            WeaponMeshParts mutant = new WeaponMeshParts();
            mutant.magazine.Add("mod_magazine");
            mutant.chamber.Add("mod_charge");
            mutant.firingModeSwitch.Add("mod_selector");
            mutant.stock.Add("mod_stock_000");
            weaponMeshDictionary.Add("weapon_cmmg_mk47_762x39_container(Clone)", mutant);


            // ---------------- BOLT ACTION RIFLES ----------------
            WeaponMeshParts sako = new WeaponMeshParts();
            sako.magazine.Add("mod_magazine");
            sako.chamber.Add("mod_charge");
            weaponMeshDictionary.Add("weapon_sako_trg_m10_86x70_container(Clone)", sako);

            WeaponMeshParts mosinSniper = new WeaponMeshParts();
            mosinSniper.magazine.Add("mod_magazine");
            mosinSniper.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_izhmash_mosin_rifle_762x54_container(Clone)", mosinSniper);

            WeaponMeshParts mosinInfantry = new WeaponMeshParts();
            mosinInfantry.magazine.Add("mod_magazine");
            mosinInfantry.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_izhmash_mosin_infantry_762x54_container(Clone)", mosinInfantry);

            WeaponMeshParts vpo215 = new WeaponMeshParts();
            vpo215.magazine.Add("mod_magazine");
            vpo215.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_molot_vpo_215_366tkm_container(Clone)", vpo215);

            WeaponMeshParts sv98 = new WeaponMeshParts();
            sv98.magazine.Add("mod_magazine");
            sv98.chamber.Add("weapon_slide1");
            weaponMeshDictionary.Add("weapon_izhmash_sv-98_762x54r_container(Clone)", sv98);

            WeaponMeshParts t5000 = new WeaponMeshParts();
            t5000.magazine.Add("mod_magazine");
            t5000.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_orsis_t5000_762x51_container(Clone)", t5000);

            WeaponMeshParts dvl10 = new WeaponMeshParts();
            dvl10.magazine.Add("mod_magazine");
            dvl10.chamber.Add("weapon_slide1");
            weaponMeshDictionary.Add("weapon_lobaev_dvl-10_308_container(Clone)", dvl10);

            WeaponMeshParts m700 = new WeaponMeshParts();
            m700.magazine.Add("mod_magazine");
            m700.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_remington_model_700_762x51_container(Clone)", m700);

            WeaponMeshParts rsass = new WeaponMeshParts();
            rsass.magazine.Add("mod_magazine");
            rsass.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_remington_r11_rsass_762x51_container(Clone)", rsass);

            // ----------------  MACHINE GUNS ----------------

            WeaponMeshParts rpk = new WeaponMeshParts();
            rpk.magazine.Add("mod_magazine");
            rpk.chamber.Add("weapon_bolt");
            rpk.firingModeSwitch.Add("weapon_selector");
            rpk.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_izhmash_rpk16_545x39_container(Clone)", rpk);

            WeaponMeshParts rpdn = new WeaponMeshParts();
            rpdn.magazine.Add("mod_magazine");
            rpdn.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_zid_rpdn_762x39_container(Clone)", rpdn);

            WeaponMeshParts m60e6 = new WeaponMeshParts();
            m60e6.magazine.Add("mod_magazine");
            m60e6.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_usord_m60e6_v1_762x51_container(Clone)", m60e6);

            WeaponMeshParts m60e6fde = new WeaponMeshParts();
            m60e6fde.magazine.Add("mod_magazine");
            m60e6fde.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_usord_m60e6_v1_762x51_fde_container(Clone)", m60e6fde);

            WeaponMeshParts m60e4 = new WeaponMeshParts();
            m60e4.magazine.Add("mod_magazine");
            m60e4.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_usord_m60e4_v1_762x51_container(Clone)", m60e4);
            // ----------------  MARKSMAN RIFLE ----------------

            WeaponMeshParts g28 = new WeaponMeshParts();
            g28.magazine.Add("mod_magazine");
            g28.chamber.Add("mod_charge");
            g28.firingModeSwitch.Add("mod_selector");
            g28.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_hk_g28_762x51_container(Clone)", g28);

            WeaponMeshParts sr25 = new WeaponMeshParts();
            sr25.magazine.Add("mod_magazine");
            sr25.chamber.Add("mod_charge");
            sr25.firingModeSwitch.Add("weapon_selector");
            sr25.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_kac_sr25_762x51_container(Clone)", sr25);

            WeaponMeshParts vss = new WeaponMeshParts();
            vss.magazine.Add("mod_magazine");
            vss.chamber.Add("weapon_bolt");
            vss.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_tochmash_vss_9x39_container(Clone)", vss);

            WeaponMeshParts m1a = new WeaponMeshParts();
            m1a.magazine.Add("mod_magazine");
            m1a.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_springfield_m1a_762x51_container(Clone)", m1a);

            WeaponMeshParts svds = new WeaponMeshParts();
            svds.magazine.Add("mod_magazine");
            svds.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_izhmash_svd_s_762x54_container(Clone)", svds);

            WeaponMeshParts axmc = new WeaponMeshParts();
            axmc.magazine.Add("mod_magazine");
            axmc.chamber.Add("mod_charge");
            weaponMeshDictionary.Add("weapon_accuracy_international_axmc_86x70_container(Clone)", axmc);

            WeaponMeshParts mk18 = new WeaponMeshParts();
            mk18.magazine.Add("mod_magazine");
            mk18.chamber.Add("weapon_charge");
            weaponMeshDictionary.Add("weapon_sword_int_mk_18_mjolnir_86x70_container(Clone)", mk18);

            WeaponMeshParts spear = new WeaponMeshParts();
            spear.magazine.Add("mod_magazine");
            spear.chamber.Add("weapon_bolt_assembly");
            spear.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_sig_mcx_spear_68x51_container(Clone)", spear);

            // ----------------  PISTOLS ----------------
            WeaponMeshParts deaglel59x33 = new WeaponMeshParts();
            deaglel59x33.magazine.Add("mod_magazine");
            deaglel59x33.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_magnum_research_desert_eagle_l5_9x33r_container(Clone)", deaglel59x33);

            WeaponMeshParts deaglel5127x33 = new WeaponMeshParts();
            deaglel5127x33.magazine.Add("mod_magazine");
            deaglel5127x33.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_magnum_research_desert_eagle_l5_127x33_container(Clone)", deaglel5127x33);

            WeaponMeshParts deaglel6 = new WeaponMeshParts();
            deaglel6.magazine.Add("mod_magazine");
            deaglel6.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_magnum_research_desert_eagle_l6_127x33_container(Clone)", deaglel6);

            WeaponMeshParts deaglel6tiger = new WeaponMeshParts();
            deaglel6tiger.magazine.Add("mod_magazine");
            deaglel6tiger.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_magnum_research_desert_eagle_l6_tiger_127x33_container(Clone)", deaglel6tiger);

            WeaponMeshParts deaglemk19 = new WeaponMeshParts();
            deaglemk19.magazine.Add("mod_magazine");
            deaglemk19.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_magnum_research_desert_eagle_mk19_127x33_container(Clone)", deaglemk19);

            WeaponMeshParts apb = new WeaponMeshParts();
            apb.magazine.Add("mod_magazine");
            apb.chamber.Add("weapon_slide");
            apb.firingModeSwitch.Add("weapon_selector");
            apb.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_toz_apb_9x18pm_container(Clone)", apb);

            WeaponMeshParts rhino9x19 = new WeaponMeshParts();
            rhino9x19.magazine.Add("mod_magazine");
            weaponMeshDictionary.Add("weapon_chiappa_rhino_200ds_9x19_container(Clone)", rhino9x19);

            WeaponMeshParts rhino9x33 = new WeaponMeshParts();
            rhino9x33.magazine.Add("weapon_crane");
            weaponMeshDictionary.Add("weapon_chiappa_rhino_50ds_9x33R_container(Clone)", rhino9x33);

            WeaponMeshParts m45a1 = new WeaponMeshParts();
            m45a1.magazine.Add("mod_magazine");
            m45a1.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_colt_m45a1_1143x23_container(Clone)", m45a1);

            WeaponMeshParts m9a3 = new WeaponMeshParts();
            m9a3.magazine.Add("mod_magazine");
            m9a3.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_beretta_m9a3_9x19_container(Clone)", m9a3);

            WeaponMeshParts fiveseven = new WeaponMeshParts();
            fiveseven.magazine.Add("mod_magazine");
            fiveseven.chamber.Add("weapon_reciever");
            weaponMeshDictionary.Add("weapon_fn_five_seven_57x28_container(Clone)", fiveseven);

            WeaponMeshParts fivesevenfde = new WeaponMeshParts();
            fivesevenfde.magazine.Add("mod_magazine");
            fivesevenfde.chamber.Add("weapon_reciever");
            weaponMeshDictionary.Add("weapon_fn_five_seven_57x28_fde_container(Clone)", fivesevenfde);

            WeaponMeshParts glock17 = new WeaponMeshParts();
            glock17.magazine.Add("mod_magazine");
            glock17.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_glock_glock_17_gen3_9x19_container(Clone)", glock17);

            WeaponMeshParts m1911 = new WeaponMeshParts();
            m1911.magazine.Add("mod_magazine");
            m1911.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_colt_m1911a1_1143x23_container(Clone)", m1911);

            WeaponMeshParts glock19x = new WeaponMeshParts();
            glock19x.magazine.Add("mod_magazine");
            glock19x.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_glock_glock_19x_9x19_container(Clone)", glock19x);

            WeaponMeshParts usp = new WeaponMeshParts();
            usp.magazine.Add("mod_magazine");
            usp.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_hk_usp_45_container(Clone)", usp);

            WeaponMeshParts pl15 = new WeaponMeshParts();
            pl15.magazine.Add("mod_magazine");
            pl15.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_izhmash_pl15_9x19_container(Clone)", pl15);

            WeaponMeshParts makarov = new WeaponMeshParts();
            makarov.magazine.Add("mod_magazine");
            makarov.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_izhmeh_pm_threaded_9x18pm_container(Clone)", makarov);


            WeaponMeshParts glock18c = new WeaponMeshParts();
            glock18c.magazine.Add("mod_magazine");
            glock18c.chamber.Add("mod_reciever");
            glock18c.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_glock_glock_18c_gen3_9x19_container(Clone)", glock18c);

            WeaponMeshParts pb = new WeaponMeshParts();
            pb.magazine.Add("mod_magazine");
            pb.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_tochmash_pb_9x18pm_container(Clone)", pb);

            WeaponMeshParts kpbrsh = new WeaponMeshParts();
            kpbrsh.magazine.Add("mod_magazine");
            kpbrsh.firingModeSwitch.Add("weapon_mag_release");
            weaponMeshDictionary.Add("weapon_kbp_rsh_12_127x55_container(Clone)", kpbrsh);

            WeaponMeshParts pm = new WeaponMeshParts();
            pm.magazine.Add("mod_magazine");
            pm.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_izhmeh_pm_9x18pm_container(Clone)", pm);

            WeaponMeshParts sigp226r = new WeaponMeshParts();
            sigp226r.magazine.Add("mod_magazine");
            sigp226r.chamber.Add("mod_reciever");
            weaponMeshDictionary.Add("weapon_sig_p226r_9x19_container(Clone)", sigp226r);

            WeaponMeshParts sr1mp = new WeaponMeshParts();
            sr1mp.magazine.Add("mod_magazine");
            sr1mp.chamber.Add("weapon_slide");
            weaponMeshDictionary.Add("weapon_tochmash_sr1mp_9x21_container(Clone)", sr1mp);

            WeaponMeshParts stechkin = new WeaponMeshParts();
            stechkin.magazine.Add("mod_magazine");
            stechkin.chamber.Add("weapon_slide");
            stechkin.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_molot_aps_9x18pm_container(Clone)", stechkin);

            WeaponMeshParts tt = new WeaponMeshParts();
            tt.magazine.Add("mod_magazine");
            tt.chamber.Add("weapon_slide");
            weaponMeshDictionary.Add("weapon_toz_tt_762x25tt_container(Clone)", tt);

            WeaponMeshParts goldtt = new WeaponMeshParts();
            goldtt.magazine.Add("mod_magazine");
            goldtt.chamber.Add("weapon_slide");
            weaponMeshDictionary.Add("weapon_toz_tt_gold_762x25tt_container(Clone)", goldtt);

            WeaponMeshParts mp443 = new WeaponMeshParts();
            mp443.magazine.Add("mod_magazine");
            mp443.chamber.Add("weapon_slide");
            weaponMeshDictionary.Add("weapon_izhmeh_mp443_9x19_container(Clone)", mp443);



            // ----------------  SUBMACHINE GUNS ----------------
            WeaponMeshParts uzipistol = new WeaponMeshParts();
            uzipistol.magazine.Add("mod_magazine");
            uzipistol.chamber.Add("weapon_bolt");
            uzipistol.stock.Add("mod_stock");
            uzipistol.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_iwi_uzi_pro_pistol_9x19_container(Clone)", uzipistol);

            WeaponMeshParts uzismg = new WeaponMeshParts();
            uzismg.magazine.Add("mod_magazine");
            uzismg.chamber.Add("weapon_bolt");
            uzismg.stock.Add("mod_stock");
            uzismg.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_iwi_uzi_pro_smg_9x19_container(Clone)", uzismg);

            WeaponMeshParts uzi = new WeaponMeshParts();
            uzi.magazine.Add("mod_magazine");
            uzi.chamber.Add("weapon_bolt");
            uzi.stock.Add("mod_stock");
            uzi.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_iwi_uzi_9x19_container(Clone)", uzi);

            WeaponMeshParts mp9 = new WeaponMeshParts();
            mp9.magazine.Add("mod_magazine");
            mp9.chamber.Add("mod_charge");
            mp9.stock.Add("mod_stock");
            mp9.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_bt_mp9_9x19_container(Clone)", mp9);

            WeaponMeshParts mp9n = new WeaponMeshParts();
            mp9n.magazine.Add("mod_magazine");
            mp9n.chamber.Add("mod_charge");
            mp9n.stock.Add("mod_stock");
            mp9n.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_bt_mp9n_9x19_container(Clone)", mp9n);

            WeaponMeshParts mp5 = new WeaponMeshParts();
            mp5.magazine.Add("mod_magazine");
            mp5.chamber.Add("mod_charge");
            mp5.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_hk_mp5_navy3_9x19_container(Clone)", mp5);

            WeaponMeshParts mp7a1 = new WeaponMeshParts();
            mp7a1.magazine.Add("mod_magazine");
            mp7a1.chamber.Add("weapon_charghing_handle");
            mp7a1.firingModeSwitch.Add("weapon_selector");
            mp9n.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_hk_mp7a1_46x30_container(Clone)", mp7a1);

            WeaponMeshParts mp7a2 = new WeaponMeshParts();
            mp7a2.magazine.Add("mod_magazine");
            mp7a2.chamber.Add("weapon_charghing_handle");
            mp7a2.firingModeSwitch.Add("weapon_selector");
            mp7a2.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_hk_mp7a2_46x30_container(Clone)", mp7a2);

            WeaponMeshParts p90 = new WeaponMeshParts();
            p90.magazine.Add("mod_magazine");
            p90.chamber.Add("weapon_charghing_rail");
            p90.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_fn_p90_57x28_container(Clone)", p90);

            WeaponMeshParts ump45 = new WeaponMeshParts();
            ump45.magazine.Add("mod_magazine");
            ump45.chamber.Add("weapon_charge");
            ump45.firingModeSwitch.Add("weapon_selector");
            ump45.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_hk_ump_1143x23_container(Clone)", ump45);

            WeaponMeshParts pp19 = new WeaponMeshParts();
            pp19.magazine.Add("mod_magazine");
            pp19.chamber.Add("weapon_bolt");
            pp19.firingModeSwitch.Add("mod_selector");
            pp19.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_izhmash_pp-19-01_9x19_container(Clone)", pp19);

            WeaponMeshParts pp91 = new WeaponMeshParts();
            pp91.magazine.Add("mod_magazine");
            pp91.chamber.Add("weapon_bolt");
            pp91.firingModeSwitch.Add("weapon_selector");
            pp91.stock.Add("weapon_stock");
            weaponMeshDictionary.Add("weapon_zmz_pp-91_9x18pm_container(Clone)", pp91);

            WeaponMeshParts pp9 = new WeaponMeshParts();
            pp9.magazine.Add("mod_magazine");
            pp9.chamber.Add("weapon_bolt");
            pp9.firingModeSwitch.Add("weapon_selector");
            pp9.stock.Add("weapon_stock");
            weaponMeshDictionary.Add("weapon_zmz_pp-9_9x18pmm_container(Clone)", pp9);

            WeaponMeshParts mp5mini = new WeaponMeshParts();
            mp5mini.magazine.Add("mod_magazine");
            mp5mini.chamber.Add("mod_charge");
            mp5mini.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_hk_mp5_kurtz_9x19_container(Clone)", mp5mini);

            WeaponMeshParts p91zmz = new WeaponMeshParts();
            p91zmz.magazine.Add("mod_magazine");
            p91zmz.chamber.Add("weapon_bolt");
            p91zmz.firingModeSwitch.Add("weapon_selector");
            p91zmz.stock.Add("weapon_stock");
            weaponMeshDictionary.Add("weapon_zmz_pp-91-01_9x18pm_container(Clone)", p91zmz);

            WeaponMeshParts ppsh = new WeaponMeshParts();
            ppsh.magazine.Add("mod_magazine");
            ppsh.chamber.Add("weapon_bolt");
            ppsh.firingModeSwitch.Add("weapon_selector");
            weaponMeshDictionary.Add("weapon_zis_ppsh41_762x25_container(Clone)", ppsh);

            WeaponMeshParts vector = new WeaponMeshParts();
            vector.magazine.Add("mod_magazine");
            vector.chamber.Add("weapon_charge");
            vector.firingModeSwitch.Add("weapon_selector");
            vector.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_tdi_kriss_vector_gen_2_1143x23_container(Clone)", vector);

            WeaponMeshParts vector9x19 = new WeaponMeshParts();
            vector9x19.magazine.Add("mod_magazine");
            vector9x19.chamber.Add("weapon_charge");
            vector9x19.firingModeSwitch.Add("weapon_selector");
            vector9x19.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_tdi_kriss_vector_gen_2_9x19_container(Clone)", vector9x19);

            WeaponMeshParts saiga9x19 = new WeaponMeshParts();
            saiga9x19.magazine.Add("mod_magazine");
            saiga9x19.chamber.Add("weapon_bolt");
            saiga9x19.firingModeSwitch.Add("weapon_selector");
            saiga9x19.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_izhmash_saiga_9_9x19_container(Clone)", saiga9x19);

            WeaponMeshParts mpx = new WeaponMeshParts();
            mpx.magazine.Add("mod_magazine");
            mpx.chamber.Add("weapon_bolt");
            mpx.firingModeSwitch.Add("weapon_selector");
            mpx.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_sig_mpx_9x19_container(Clone)", mpx);

            WeaponMeshParts sr2m = new WeaponMeshParts();
            sr2m.magazine.Add("mod_magazine");
            sr2m.chamber.Add("weapon_bolt_carrier");
            sr2m.firingModeSwitch.Add("weapon_selector");
            sr2m.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_tochmash_sr2m_veresk_9x21_container(Clone)", sr2m);

            WeaponMeshParts sr3m = new WeaponMeshParts();
            sr3m.magazine.Add("mod_magazine");
            sr3m.chamber.Add("weapon_bolt");
            sr3m.firingModeSwitch.Add("weapon_selector");
            sr3m.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_tochmash_sr3m_9x39_container(Clone)", sr3m);

            WeaponMeshParts stm9 = new WeaponMeshParts();
            stm9.magazine.Add("mod_magazine");
            stm9.chamber.Add("weapon_bolt");
            stm9.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_stmarms_stm_9_9x19_container(Clone)", stm9);

            // ----------------  SHOTGUNS ----------------
            WeaponMeshParts m3 = new WeaponMeshParts();
            m3.magazine.Add("mod_magazine");
            m3.chamber.Add("weapon_bolt");
            m3.firingModeSwitch.Add("mod_selector");
            weaponMeshDictionary.Add("weapon_benelli_m3_s90_12g_container(Clone)", m3);

            WeaponMeshParts mr133 = new WeaponMeshParts();
            mr133.magazine.Add("weapon_ammo_feeder");
            mr133.chamber.Add("weapon_slide");
            weaponMeshDictionary.Add("weapon_izhmeh_mr133_12g_container(Clone)", mr133);

            WeaponMeshParts mp155 = new WeaponMeshParts();
            mp155.magazine.Add("weapon_ammo_feeder");
            mp155.chamber.Add("weapon_slide_0");
            weaponMeshDictionary.Add("weapon_kalashnikov_mp155_12g_container(Clone)", mp155);

            WeaponMeshParts mr153 = new WeaponMeshParts();
            mr153.magazine.Add("weapon_ammo_feeder");
            mr153.chamber.Add("weapon_slide_0");
            weaponMeshDictionary.Add("weapon_izhmeh_mr153_12g_container(Clone)", mr153);

            WeaponMeshParts mp18 = new WeaponMeshParts();
            mp18.magazine.Add("ejector_mp18_762x54r");
            weaponMeshDictionary.Add("weapon_izhmash_mp18_multi_container(Clone)", mr153);

            WeaponMeshParts toz = new WeaponMeshParts();
            toz.magazine.Add("mod_magazine");
            toz.chamber.Add("weapon_slide_000");
            toz.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_toz_toz-106_20g_container(Clone)", toz);

            WeaponMeshParts sawedoff = new WeaponMeshParts();
            sawedoff.magazine.Add("weapon_switch");
            weaponMeshDictionary.Add("weapon_izhmeh_mr43_sawed_off_12g_container(Clone)", sawedoff);

            WeaponMeshParts mr43 = new WeaponMeshParts();
            mr43.magazine.Add("weapon_switch");
            weaponMeshDictionary.Add("weapon_izhmeh_mr43_12g_container(Clone)", mr43);

            WeaponMeshParts mc255 = new WeaponMeshParts();
            mc255.magazine.Add("mod_magazine");
            weaponMeshDictionary.Add("weapon_ckib_mc_255_12g_container(Clone)", mc255);

            WeaponMeshParts mossberg = new WeaponMeshParts();
            mossberg.magazine.Add("weapon_feeder");
            mossberg.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_mossberg_590a1_12g_container(Clone)", mossberg);

            WeaponMeshParts saiga12k = new WeaponMeshParts();
            saiga12k.magazine.Add("mod_magazine");
            saiga12k.chamber.Add("weapon_bolt");
            saiga12k.stock.Add("mod_stock");
            weaponMeshDictionary.Add("weapon_izhmash_saiga12k_10_12g_container(Clone)", saiga12k);

            WeaponMeshParts remington = new WeaponMeshParts();
            remington.magazine.Add("weapon_feeder");
            remington.chamber.Add("weapon_bolt");
            weaponMeshDictionary.Add("weapon_remington_model_870_12g_container(Clone)", remington);
        }

        public static WeaponMeshParts GetWeaponMeshList(string weaponName)
        {
            if (weaponMeshDictionary.ContainsKey(weaponName)) return weaponMeshDictionary[weaponName];
            return null;
        }
    }
}
