using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Source.Misc
{
    internal class BuildMenuLines : MonoBehaviour
    {
        public LineRenderer lr;
        public Transform startNode;
        public Transform endNode;
        public float worldWidth = 0.005f;

        private void LateUpdate()
        {
            if (lr == null || startNode == null || endNode == null) return;
            lr.useWorldSpace = true;
            lr.startWidth = worldWidth;
            lr.endWidth = worldWidth;
            lr.SetPosition(0, startNode.position);
            lr.SetPosition(1, endNode.position);
        }
    }
}
