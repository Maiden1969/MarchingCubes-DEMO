using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfCylinder : SdfShape
    {
        public Vector3 top = Vector3.zero;
        public Vector3 down = Vector3.zero;
        public float radius = 0.5f;

        public override float DistanceTo(Vector3 p)
        {
            return CylinderDistance(p, top, down, radius);
        }

        // 有限圆柱(平端盖):径向距离与轴向距离合成;退化(段长为 0)视为球体,与胶囊一致。
        public static float CylinderDistance(Vector3 p, Vector3 A, Vector3 B, float radius)
        {
            Vector3 AB = B - A;
            float len2 = Vector3.Dot(AB, AB);
            if (len2 < 1e-8) // 退化,视为球体
                return (p - A).magnitude - radius;
            Vector3 axis = AB / Mathf.Sqrt(len2);
            Vector3 center = (A + B) * 0.5f;
            float halfLen = Mathf.Sqrt(len2) * 0.5f;
            Vector3 rel = p - center;
            float radial = (rel - axis * Vector3.Dot(rel, axis)).magnitude - radius;
            float axial = Mathf.Abs(Vector3.Dot(rel, axis)) - halfLen;
            Vector2 q = new Vector2(radial, axial);
            return Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f);
        }

        public override SdfShapeType Type => SdfShapeType.Cylinder;

        // args 布局: [top(3), down(3), radius],与 SdfFieldEditJob 的游标一致
        public override void BakeArgs(List<float> args)
        {
            args.Add(top.x);
            args.Add(top.y);
            args.Add(top.z);
            args.Add(down.x);
            args.Add(down.y);
            args.Add(down.z);
            args.Add(radius);
        }
    }
}
