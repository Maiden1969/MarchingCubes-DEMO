using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfSphere : SdfShape
    {
        public Vector3 center = Vector3.zero;
        public float radius = 1.0f;

        public override float DistanceTo(Vector3 point)
        {
            return Vector3.Distance(point, center) - radius;
        }

        public override SdfShapeType Type => SdfShapeType.Sphere;

        // args 布局: [center(3), radius]
        public override void BakeArgs(List<float> args)
        {
            args.Add(center.x);
            args.Add(center.y);
            args.Add(center.z);
            args.Add(radius);
        }
    }
}