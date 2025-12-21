using UnityEngine;

namespace TarkovVR.Source.Misc
{
    internal class GrenadeFingerPositioner : MonoBehaviour
    {

        private float fingerBaseRotation = 22;
        private float x, y, z;
        private float rx, ry, rz;
        public Transform target;
        public bool left = false;
        private void Awake()
        {
            x = 90f;

            //rx = 15;
            //ry = 275;
            //rz = 90f;
        }

        private void Update()
        {
            transform.localRotation = Quaternion.Euler(0,0,fingerBaseRotation);
            transform.GetChild(0).localRotation = Quaternion.Euler(0, 0, 350);
            transform.GetChild(0).GetChild(0).localRotation = Quaternion.Euler(0, 0, 0);
        }
        private void LateUpdate()
        {
            transform.localRotation = Quaternion.Euler(0,0, fingerBaseRotation);
            transform.GetChild(0).localRotation = Quaternion.Euler(0, 0, 350);
            transform.GetChild(0).GetChild(0).localRotation = Quaternion.Euler(0, 0, 0);


        }
    }
}
