using System.Collections.Generic;
using UnityEngine;

namespace Sdf
{
    public enum SdfShapeType : byte { Sphere, Capsule, Floor, HalfSphere, HalfCapsule, Cylinder, HalfCylinder }

    // 纯数据类(非 UnityEngine.Object):几何 + 烘焙参数,与场景组件解耦。
    // 场景侧的创作/预览由 SdfWorldPart 子类负责;未来运行时实体层可直接持有。
    public abstract class SdfShape
    {
        
        public abstract float DistanceTo(Vector3 point);

        public abstract SdfShapeType Type { get; }

        public abstract void BakeArgs(List<float> args);
    }
}