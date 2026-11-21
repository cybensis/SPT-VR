using System;
using System.Collections.Generic;
using UnityEngine;

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
            
            // Not cached yet: gather the parts from all children
            WeaponMeshParts newWeaponMeshParts = new WeaponMeshParts();

            GatherWeaponParts(weaponRoot, newWeaponMeshParts);

            weaponMeshDictionary.Add(weaponName, newWeaponMeshParts);

            return weaponMeshDictionary[weaponName];
        }

        private static void GatherWeaponParts(Transform root, WeaponMeshParts newWeaponMeshParts)
        {
            // The keyword order matters: the first match wins, and some parts are children of others (e.g. "weapon_cylinder_axis" is
            // a child of "magazine", and must be found first).

            // MAGAZINE
            string[] magazineKeywords = new string[]
            {
                "weapon_cylinder_axis",
                "weapon_switch",
                "ejector_mp18_762x54r",
                "weapon_crane",
                "magazine",
                "feeder"
            };

            foreach (string keyword in magazineKeywords)
            {
                if (FindTransformByKeyword(root, keyword, out string result))
                {
                    newWeaponMeshParts.magazine.Add(result);
                    break;
                }
            }

            // CHAMBER
            string[] chamberKeywords = new string[]
            {
                "chamber",
                "bolt",
                "charge",
                "slide",
                "reciever",
                "weapon_charghing_rail"
            };

            foreach (string keyword in chamberKeywords)
            {
                if (FindTransformByKeyword(root, keyword, out string result))
                {
                    newWeaponMeshParts.chamber.Add(result);
                    break;
                }
            }

            // STOCK
            if (FindTransformByKeyword(root, "stock", out string stockResult))
            {
                newWeaponMeshParts.stock.Add(stockResult);
            }

            // FIRING MODE SWITCH
            string[] firingModeKeywords = new string[] { "selector", "safety" };

            foreach (string keyword in firingModeKeywords)
            {
                if (FindTransformByKeyword(root, keyword, out string result))
                {
                    newWeaponMeshParts.firingModeSwitch.Add(result);
                    break;
                }
            }
        }
        private static bool FindTransformByKeyword(Transform root, string keyword, out string foundName)
        {
            string lowerName = root.name.ToLower();
            if (lowerName.Contains(keyword))
            {
                foundName = root.name;
                return true;
            }

            foreach (Transform child in root)
            {
                if (FindTransformByKeyword(child, keyword, out foundName))
                    return true;
            }

            foundName = null;
            return false;
        }
    }
}
