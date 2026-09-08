using UnityEngine;

namespace Sdf
{
    // 场景侧的 SDF 形状组件:持有可序列化参数,按当前 Transform 构建纯数据 SdfShape。
    // 编辑器预览(Gizmos)由子类实现;SdfWorld.Bake 通过 Shape 收集。
    public abstract class SdfWorldPart : MonoBehaviour
    {
        public bool isGlobal = false;
        public SdfEditOp editOp = SdfEditOp.Add;

        [SerializeField] private NoiseParams noiseParams = new NoiseParams
        {
            noiseFunction = SdfNoiseFunction.Smooth,
            warpFrequency = 0.04f,
            warpAmplitude = 0f,
            frequency = 0.1f,
            displacement = 0f,
            blockSize = 2,
            octaves = 2,
            seed = 0,
        };

        // 该形状的噪声扰动参数(Bake 时烘焙进平行数组)。默认幅度全 0 = 无扰动。
        public NoiseParams NoiseParams => noiseParams;

        public abstract SdfShape Shape { get; }
    }
}