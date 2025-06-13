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
        /*
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
        */
        /*
        private static void GatherWeaponParts(Transform root, WeaponMeshParts newWeaponMeshParts)
        {
            bool foundMagazine = newWeaponMeshParts.magazine.Count > 0;
            bool foundChamber = newWeaponMeshParts.chamber.Count > 0;
            bool foundStock = newWeaponMeshParts.stock.Count > 0;
            bool foundFiringSwitch = newWeaponMeshParts.firingModeSwitch.Count > 0;

            if (foundMagazine && foundChamber && foundStock && foundFiringSwitch)
                return;

            foreach (Transform child in root)
            {
                string partName = child.name.ToLower();
                if (!foundMagazine &&
                    (partName.Contains("weapon_cylinder_axis") || partName.Contains("weapon_switch") || partName.Contains("ejector_mp18_762x54r") || partName.Contains("weapon_crane") || partName.Contains("magazine") || partName.Contains("feeder")))
                {
                    newWeaponMeshParts.magazine.Add(child.name);
                    foundMagazine = true;
                }
                else if (!foundChamber &&
                    (partName.Contains("chamber") || partName.Contains("bolt") || partName.Contains("charge") || partName.Contains("slide") || partName.Contains("reciever") || partName.Contains("weapon_charghing_rail")))
                {
                    newWeaponMeshParts.chamber.Add(child.name);
                    foundChamber = true;
                }
                else if (!foundStock && partName.Contains("stock"))
                {
                    newWeaponMeshParts.stock.Add(child.name);
                    foundStock = true;
                }
                else if (!foundFiringSwitch && (partName.Contains("selector") || partName.Contains("safety")))
                {
                    newWeaponMeshParts.firingModeSwitch.Add(child.name);
                    foundFiringSwitch = true;
                }

                if (!(foundMagazine && foundChamber && foundStock && foundFiringSwitch))
                {
                    GatherWeaponParts(child, newWeaponMeshParts);

                    if (newWeaponMeshParts.magazine.Count > 0 &&
                        newWeaponMeshParts.chamber.Count > 0 &&
                        newWeaponMeshParts.stock.Count > 0 &&
                        newWeaponMeshParts.firingModeSwitch.Count > 0)
                        return;
                }
            }
        }
        */
        private static void GatherWeaponParts(Transform root, WeaponMeshParts newWeaponMeshParts)
        {
            //Order of the strings do matter here, reason being that some of the partnames are children of others. For example "weapon_cylinder_axis" is a child of "magazine"
            //so if we find "weapon_cylinder_axis" first, we won't find "magazine" later on.

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
