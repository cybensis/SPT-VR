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
        private Vector3 pos;
        private Vector3 rot;
        private void Update()
        {
            transform.localPosition = pos;
            transform.localEulerAngles = rot;
        }
        private void LateUpdate()
        {
            transform.localPosition = pos;
            transform.localEulerAngles = rot;
        }

    }
}
