using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfHalfCylinder : SdfShape
    {
        public Vector3 top = Vector3.zero;
        public Vector3 down = Vector3.zero;
        public float radius = 0.5f;
        public Vector3 cutPoint = Vector3.zero;
        public Vector3 normal = Vector3.down;   // 剖切平面外法线

        public override float DistanceTo(Vector3 p)
        {
            return Mathf.Max(SdfCylinder.CylinderDistance(p, top, down, radius), Vector3.Dot(p - cutPoint, normal));
        }

        public override SdfShapeType Type => SdfShapeType.HalfCylinder;

        // args 布局: [top(3), down(3), radius, cutPoint(3), normal(3)],与 SdfFieldEditJob 的游标一致
        public override void BakeArgs(List<float> args)
        {
            Vector3 n = normal.sqrMagnitude < 1e-8f ? Vector3.down : normal.normalized;
            args.Add(top.x);
            args.Add(top.y);
            args.Add(top.z);
            args.Add(down.x);
            args.Add(down.y);
            args.Add(down.z);
            args.Add(radius);
            args.Add(cutPoint.x);
            args.Add(cutPoint.y);
            args.Add(cutPoint.z);
            args.Add(n.x);
            args.Add(n.y);
            args.Add(n.z);
        }
    }
}
