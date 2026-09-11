using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DS
{
    /// <summary>
    /// 稀疏砖存储（结构参考 Falcor SDFSBS 的砖 + 间接表，但真相留在 CPU）：
    /// - Indirection: 密集砖指针表，每块 bricksPerAxis³ 个槽；槽值 = 紧凑砖 ID（0..N-1），
    ///   或 AbsentSolid / AbsentAir —— 该砖不含表面格，整体处于深实体 / 深空气
    ///   （缺省深值 ±∞ 语义，编辑与采样时按此重建）。
    /// - BrickData: 紧凑砖值池，长度 = PresentCount × BrickValueCount，
    ///   砖内布局 z*81 + y*9 + x（与 MC kernel、Nav 采样共享同一布局约定）。
    /// 存在性规则：砖含表面格（min&lt;0 &amp;&amp; max&gt;=0）或距表面格 ≤ HaloCells 时落盘；
    /// 其余格点值由缺省深值表达 —— 密集 values 数组只作为构建期的临时草稿。
    /// </summary>
    public class BrickGrid : IDisposable
    {
        public const int BrickSize = 8;           
        public const int BrickValuesPerAxis = 9;  
        public const int BrickValueCount = 729;   

        public const uint AbsentSolid = 0xFFFFFFFEu;   // 深实体缺省（-∞）
        public const uint AbsentAir   = 0xFFFFFFFFu;   // 深空气缺省（+∞）

        /// <summary>
        /// halo 推导：编辑（Sub/Add）的结果在浅值点依赖旧场（|f| &lt; max(R, r) 处），
        /// 而 1-Lipschitz 保证这些点距表面 ≤ max(R, r) 个体素，故 halo 取
        /// ceil(maxShapeRadius / voxelSize) + 1。当前 R ≤ 2.5r = 3.75、voxel = 1 → 4。
        /// halo 内旧值必须精确（砖落盘），halo 外 ±∞ 填充不影响任何表面格。
        /// </summary>
        public const int HaloCells = 4;

        public NativeArray<uint> Indirection;
        // 砖值池用 half(16-bit) 存储: 表面带 + halo 内 |f| ≤ ~16 世界单位,量化误差
        // ≤ 8e-3(0.8% 体素),MC/Nav/编辑全部无感;深值仍走间接表 ±∞ 缺省不占存储。
        // 所有 float→half 转换只在 Burst 的 BrickCopyJob 内发生(确定性舍入),
        // 两边块对相同 float32 产生相同 half → 接缝 bit-identical 保持。
        public NativeList<half> BrickData;
        public int BricksPerAxis;
        public int PresentCount;

        public BrickGrid(int resolution, Allocator allocator)
        {
            int cells = resolution - 1;
            if (cells % BrickSize != 0)
                throw new InvalidOperationException(
                    $"BrickGrid: 每轴格数 {cells} 必须能被砖宽 {BrickSize} 整除（sdfResolution 需为 8 的倍数 + 1）。");

            BricksPerAxis = cells / BrickSize;
            Indirection = new NativeArray<uint>(BricksPerAxis * BricksPerAxis * BricksPerAxis, allocator);
            // 初始世界 = 全实体（-∞）
            for (int i = 0; i < Indirection.Length; i++) Indirection[i] = AbsentSolid;
            BrickData = new NativeList<half>(allocator);
            PresentCount = 0;
        }

        public void Dispose()
        {
            if (Indirection.IsCreated) Indirection.Dispose();
            if (BrickData.IsCreated) BrickData.Dispose();
        }

        // 格点 → 砖槽：格点 x ∈ [0, cells] 存于砖 min(x/8, bpa-1)，边界格点（如 x=8）
        // 同时是左砖的最大角与右砖的最小角，两份拷贝值相同（确定性计算保证一致）。
        public static int SlotOf(int x, int y, int z, int bricksPerAxis)
        {
            int bx = math.min(x >> 3, bricksPerAxis - 1);
            int by = math.min(y >> 3, bricksPerAxis - 1);
            int bz = math.min(z >> 3, bricksPerAxis - 1);
            return bx + bricksPerAxis * (by + bricksPerAxis * bz);
        }

        /// <summary>
        /// 砖 → 密集草稿：现存的砖拷贝真值，缺砖按槽内缺省（±∞）填充。
        /// 编辑的正确性：写入 max(f, -g) 依赖 f 的点（浅值点）必在 halo 内存砖；
        /// 缺砖填充只影响"结果不依赖 f"的深值点，且缺砖区域永不落盘、不发散。
        /// </summary>
        [BurstCompile]
        public struct DeBrickifyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<uint> indirection;
            [ReadOnly] public NativeArray<half> brickData;
            [ReadOnly] public int resolution;      // 每轴格点数 = cells + 1
            [ReadOnly] public int bricksPerAxis;
            public NativeArray<float> values;

            public void Execute(int index)
            {
                int n2 = resolution * resolution;
                int z = index / n2;
                int y = (index - z * n2) / resolution;
                int x = index - z * n2 - y * resolution;

                uint id = indirection[SlotOf(x, y, z, bricksPerAxis)];
                if (id < AbsentSolid)
                {
                    int bx = math.min(x >> 3, bricksPerAxis - 1);
                    int by = math.min(y >> 3, bricksPerAxis - 1);
                    int bz = math.min(z >> 3, bricksPerAxis - 1);
                    int lx = x - bx * BrickSize;
                    int ly = y - by * BrickSize;
                    int lz = z - bz * BrickSize;
                    int bid = (int)id;
                    values[index] = brickData[bid * BrickValueCount + lx + ly * BrickValuesPerAxis
                        + lz * BrickValuesPerAxis * BrickValuesPerAxis];
                }
                else
                {
                    values[index] = id == AbsentAir ? float.PositiveInfinity : float.NegativeInfinity;
                }
            }
        }

        // 表面格扫描：8 角值 min<0 && max>=0（与 MC/HasSurface 同一约定，负 = 实体）。
        [BurstCompile]
        public struct BrickSurfaceFlagsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> values;
            [ReadOnly] public int resolution;
            public NativeArray<byte> surfaceFlags;

            public void Execute(int index)
            {
                int cells = resolution - 1;
                int n2 = cells * cells;
                int z = index / n2;
                int y = (index - z * n2) / cells;
                int x = index - z * n2 - y * cells;

                float minV = float.MaxValue, maxV = float.MinValue;
                for (int dz = 0; dz <= 1; dz++)
                for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    float v = values[(z + dz) * resolution * resolution + (y + dy) * resolution + (x + dx)];
                    minV = math.min(minV, v);
                    maxV = math.max(maxV, v);
                }
                surfaceFlags[index] = (minV < 0f && maxV >= 0f) ? (byte)1 : (byte)0;
            }
        }

        // 存在性：按砖调度，每砖扫"本砖格子 ± halo"窗口内的表面格（窗口含相邻砖的
        // 溢出部分，跨砖读 flags 只读安全）；每砖单写者，无需原子操作。
        [BurstCompile]
        public struct BrickPresenceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> surfaceFlags;
            [ReadOnly] public int resolution;
            [ReadOnly] public int bricksPerAxis;
            public NativeArray<int> presence;

            public void Execute(int slot)
            {
                int cells = resolution - 1;
                int b2 = bricksPerAxis * bricksPerAxis;
                int bz = slot / b2;
                int by = (slot - bz * b2) / bricksPerAxis;
                int bx = slot - bz * b2 - by * bricksPerAxis;

                int3 winMin = math.max(new int3(bx, by, bz) * BrickSize - HaloCells, int3.zero);
                int3 winMax = math.min(new int3(bx, by, bz) * BrickSize + (BrickSize - 1) + HaloCells, cells - 1);

                int near = 0;
                for (int z = winMin.z; z <= winMax.z && near == 0; z++)
                for (int y = winMin.y; y <= winMax.y && near == 0; y++)
                for (int x = winMin.x; x <= winMax.x; x++)
                {
                    if (surfaceFlags[x + cells * (y + cells * z)] != 0) { near = 1; break; }
                }
                presence[slot] = near;
            }
        }

        // 把现存的砖从密集草稿拷进紧凑砖池（按前缀和的砖 ID 顺序，布局确定性）。
        [BurstCompile]
        public struct BrickCopyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> values;
            [ReadOnly] public NativeArray<int> brickSlots;   // 紧凑砖 ID → 槽序号
            [ReadOnly] public int resolution;
            [ReadOnly] public int bricksPerAxis;
            public NativeArray<half> brickData;

            public void Execute(int index)
            {
                int brickID = index / BrickValueCount;
                int r = index - brickID * BrickValueCount;
                int v2 = BrickValuesPerAxis * BrickValuesPerAxis;
                int lz = r / v2;
                int ly = (r - lz * v2) / BrickValuesPerAxis;
                int lx = r - lz * v2 - ly * BrickValuesPerAxis;

                int slot = brickSlots[brickID];
                int b2 = bricksPerAxis * bricksPerAxis;
                int bz = slot / b2;
                int by = (slot - bz * b2) / bricksPerAxis;
                int bx = slot - bz * b2 - by * bricksPerAxis;

                int gx = bx * BrickSize + lx;
                int gy = by * BrickSize + ly;
                int gz = bz * BrickSize + lz;

                brickData[index] = (half)values[gz * resolution * resolution + gy * resolution + gx];
            }
        }
    }
}
