using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Source.Misc
{
    internal class WeaponPositioner : MonoBehaviour
    {
        private int i = 0;
        private void Update()
        {
            Plugin.MyLog.LogWarning(VRGlobals.player._markers[0].transform.position.ToString("F4") + "  |  " + i);
        }
        private void LateUpdate()
        {
            Plugin.MyLog.LogError(VRGlobals.player._markers[0].transform.position.ToString("F4") + "  |  " + i);
            i++;
            if (i > 100)
                i = 0;
        }

    }
}
