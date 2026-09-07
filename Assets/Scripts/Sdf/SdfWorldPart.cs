using UnityEngine;

namespace Sdf
{
    // 场景侧的 SDF 形状组件:持有可序列化参数,按当前 Transform 构建纯数据 SdfShape。
    // 编辑器预览(Gizmos)由子类实现;SdfWorld.Bake 通过 Shape 收集。
    public abstract class SdfWorldPart : MonoBehaviour
    {
        public bool isGlobal = false;
        public SdfEditOp editOp = SdfEditOp.Add;

        public abstract SdfShape Shape { get; }
    }
}