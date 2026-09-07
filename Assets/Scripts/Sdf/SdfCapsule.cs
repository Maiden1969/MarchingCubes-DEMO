using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfCapsule : SdfShape
    {
        public Vector3 top = Vector3.zero;
        public Vector3 down = Vector3.zero;
        public float radius = 0.5f;

        public override float DistanceTo(Vector3 p)
        {
            return CapsuleDistance(p, top, down, radius);
        }
        
        public static float CapsuleDistance(Vector3 p, Vector3 A, Vector3 B, float radius)
        {
            Vector3 AB = B - A;
            float len2 = Vector3.Dot(AB, AB);
            if (len2 < 1e-8) // 退化，视为球体
                return (p - A).magnitude - radius;
            // 否则正常计算
            float t = Vector3.Dot(p - A, AB) / len2;
            t = Mathf.Clamp01(t);
            Vector3 closest = A + t * AB;
            return (p - closest).magnitude - radius;
        }

        public override SdfShapeType Type => SdfShapeType.Capsule;

        // args 布局: [top(3), down(3), radius]
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