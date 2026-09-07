using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfHalfSphere : SdfShape
    {
        public Vector3 center = Vector3.zero;
        public float radius = 1.0f;
        public Vector3 normal = Vector3.down;   

        public override float DistanceTo(Vector3 point)
        {
            return Mathf.Max(Vector3.Distance(point, center) - radius, Vector3.Dot(point - center, normal));
        }

        public override SdfShapeType Type => SdfShapeType.HalfSphere;

        // args 布局: [center(3), radius, normal(3)]
        public override void BakeArgs(List<float> args)
        {
            Vector3 n = normal.sqrMagnitude < 1e-8f ? Vector3.down : normal.normalized;
            args.Add(center.x);
            args.Add(center.y);
            args.Add(center.z);
            args.Add(radius);
            args.Add(n.x);
            args.Add(n.y);
            args.Add(n.z);
        }
    }
}
