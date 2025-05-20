using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static TarkovVR.Source.Weapons.WeaponMeshParts;

namespace TarkovVR.Source.Weapons
{

    
    public class WeaponMeshParts
    {
        public List<string> magazine = new List<string>();
        public List<string> chamber = new List<string>();
        public List<string> stock = new List<string>();
        public List<string> firingModeSwitch = new List<string>();
    }
    public static class WeaponMeshList
    {
        private static Dictionary<string, WeaponMeshParts> weaponMeshDictionary;

        static WeaponMeshList()
        {
            weaponMeshDictionary = new Dictionary<string, WeaponMeshParts>();
        }

        public static WeaponMeshParts GetWeaponMeshList(Transform weaponRoot)
        {
            String weaponName = weaponRoot.transform.root.name;

            if (weaponMeshDictionary.ContainsKey(weaponName))
            {
                return weaponMeshDictionary[weaponName];
            }
            
            // Automatically gather mesh data if weapon is not found
            WeaponMeshParts newWeaponMeshParts = new WeaponMeshParts();

            // Recursively gather weapon parts from all children
            GatherWeaponParts(weaponRoot, newWeaponMeshParts);

            weaponMeshDictionary.Add(weaponName, newWeaponMeshParts);
            //Plugin.MyLog.LogInfo($"Added weapon {weaponName} with detected parts to dictionary.");

            return weaponMeshDictionary[weaponName];//newWeaponMeshParts;
        }
        private static void GatherWeaponParts(Transform root, WeaponMeshParts newWeaponMeshParts)
        {
            foreach (Transform child in root)
            {
                //Plugin.MyLog.LogInfo($"Child name: {child.name}");
                string partName = child.name.ToLower();
                if (partName.Contains("magazine") || partName.Contains("weapon_cylinder_axis") || partName.Contains("feeder") || partName.Contains("ejector_mp18_762x54r") || partName.Contains("weapon_switch") || partName.Contains("weapon_crane")) newWeaponMeshParts.magazine.Add(child.name);
                else if (partName.Contains("chamber") || partName.Contains("bolt") || partName.Contains("charge") || partName.Contains("slide") || partName.Contains("reciever") || partName.Contains("weapon_charghing_rail")) newWeaponMeshParts.chamber.Add(child.name);
                else if (partName.Contains("stock")) newWeaponMeshParts.stock.Add(child.name);
                else if (partName.Contains("selector") || partName.Contains("safety")) newWeaponMeshParts.firingModeSwitch.Add(child.name);

                // Recursively process nested children
                GatherWeaponParts(child, newWeaponMeshParts);
            }
        }
    }
}
