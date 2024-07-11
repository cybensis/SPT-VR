using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovVR.Source.Weapons
{
    public class WeaponMeshParts
    {
        public List<string> magazine;
        public List<string> chamber;
        public List<string> stock;
        public List<string> firingModeSwitch;

        public WeaponMeshParts()
        {
            magazine = new List<string>();
            chamber = new List<string>();
            stock = new List<string>();
            firingModeSwitch = new List<string>();
        }
    }
}
