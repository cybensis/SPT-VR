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

        private void Update()
        {
            transform.localPosition = Vector3.zero;
        }
        private void LateUpdate()
        {
            transform.localPosition = Vector3.zero;
        }

    }
}
