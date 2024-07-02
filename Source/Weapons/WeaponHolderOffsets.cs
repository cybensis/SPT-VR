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
            weaponOffsets.Add("weapon_springfield_m1a_762x51_container(Clone)", new Vector3(0.27f, -0.08f, -0.16f));
            weaponOffsets.Add("weapon_izhmeh_mr43_sawed_off_12g_container(Clone)", new Vector3(0.23f, -0.08f, -0.15f));
            weaponOffsets.Add("weapon_izhmash_sv-98_762x54r_container(Clone)", new Vector3(0.29f, -0.11f, -0.18f));
            weaponOffsets.Add("weapon_fn_p90_57x28_container(Clone)", new Vector3(0.31f, -0.08f, -0.18f));
            weaponOffsets.Add("weapon_toz_toz-106_20g_container(Clone)", new Vector3(0.28f, -0.09f, -0.14f));
            weaponOffsets.Add("weapon_tdi_kriss_vector_gen_2_1143x23_container(Clone)", new Vector3(0.29f, -0.09f, -0.13f));


            classOffsets.Add("shotgun", new Vector3(0.17f, -0.1f, -0.09f));
            classOffsets.Add("smg", new Vector3(0.40f, -0.05f, -0.18f));
            classOffsets.Add("pistol", new Vector3(0.40f, -0.05f, -0.18f));
            classOffsets.Add("marksmanRifle", new Vector3(0.19f, 0.04f, -0.09f));
            classOffsets.Add("sniperRifle", new Vector3(0.27f, -0.11f, -0.18f));
            classOffsets.Add("assaultRifle", new Vector3(0.19f, -0.07f, -0.18f));

        }

        public static Vector3 GetWeaponHolderOffset(string weaponName, string weaponClass) {
            if (weaponOffsets.ContainsKey(weaponName)) { return weaponOffsets[weaponName]; }
            else if (classOffsets.ContainsKey(weaponClass)) { return classOffsets[weaponClass]; }
            else return defaultOffset;
        }
    }
}
