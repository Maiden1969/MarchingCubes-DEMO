using System;
using UnityEngine;

namespace Sdf
{
    public class SdfFloorWorldPart : SdfWorldPart
    {
        public override SdfShape Shape => new SdfFloor { height = transform.position.y};

        public void OnDrawGizmos()
        {
            Vector3 p1 = transform.position + Vector3.left * 50f + Vector3.forward * 50f;
            Vector3 p2 = transform.position + Vector3.right * 50f + Vector3.forward * 50f;
            Vector3 p3 = transform.position + Vector3.right * 50f + Vector3.back * 50f;
            Vector3 p4 = transform.position + Vector3.left * 50f + Vector3.back * 50f;
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p4);
            Gizmos.DrawLine(p4, p1);
        }
    }
    
}