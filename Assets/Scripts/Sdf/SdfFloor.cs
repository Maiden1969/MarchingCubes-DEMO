using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public class SdfFloor: SdfShape
    {
        public float height;
        public override float DistanceTo(Vector3 point)
        {
            return point.y - height;
        }

        public override SdfShapeType Type => SdfShapeType.Floor;

        public override void BakeArgs(List<float> args)
        {
            args.Add(height);
        }
    }
}