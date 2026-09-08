using System;

namespace Sdf
{
    // 位移通道的噪声函数:Smooth = 平滑 Fbm(连续起伏);Block = 最近邻块噪声(不规则立方砖块)。
    // 枚举值为 0 的项是默认值——新字段在旧场景数据上反序列化后必须落到安全行为。
    public enum SdfNoiseFunction { Smooth, Block }

    // 每形状的噪声扰动参数(可序列化、Burst 兼容)。
    // 幅度全零 = 无扰动(与旧行为位级一致);seed 烘焙后恒定不变,是跨块接缝
    // 位级一致的前提(同一世界点两侧块必须算出同一个噪声值)。
    [Serializable]
    public struct NoiseParams
    {
        public SdfNoiseFunction noiseFunction;  // 位移通道用哪种噪声
        public float warpFrequency;  // 域扭曲频率(弯曲的波长尺度,0.03~0.08)
        public float warpAmplitude;  // 域扭曲强度(≈ 弯曲位移,世界单位;> ~1.5× 形状半径会折叠)
        public float frequency;      // 表面位移频率(起伏的波长尺度,0.05~0.15;Block 函数忽略)
        public float displacement;   // 表面位移幅度(世界单位 ≈ 体素数)
        public int blockSize;        // Block 函数的砖块边长(世界单位;≥1,0 会被守卫修正)
        public int octaves;          // Fbm 层数(1~3;0 会产生 NaN,已由 Fbm 守卫)
        public int seed;             // 图案选择;同形状内恒定,形状间可不同
    }
}
