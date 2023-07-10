using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.cam
{
    internal class BodyRotationFixer : MonoBehaviour
    {
        private float x, y, z;

        public void LateUpdate() {
            transform.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 280);
            transform.localRotation = Quaternion.Euler(x,y,z);

            if (name == "Base HumanSpine3")
                transform.localPosition = new Vector3(-0.2258f, 0.0254f, 0.0051f);
            //transform.localPosition = Vector3.zero;
        } 

    }
}
