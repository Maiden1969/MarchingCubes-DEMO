using System;
using System.Collections.Generic;
using DS;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Sdf
{
    public enum SdfEditOp {Add, Sub}

    // args 依次存储着用于计算某种 SDF 值所需的最少参数，通过遍历 shapeTypes 来确定当前需要读取多少个 arg
    // Sphere:      [position(3个float), radius]
    // Capsule:     [top position(3个float), down position(3个float), radius]
    // HalfSphere:  [position(3个float), radius, normal(3个float)]
    // HalfCapsule: [top(3个float), down(3个float), radius, cutPoint(3个float), normal(3个float)]
    // Cylinder:    [top(3个float), down(3个float), radius]
    // HalfCylinder:[top(3个float), down(3个float), radius, cutPoint(3个float), normal(3个float)]
    // Floor:       [height]
    [BurstCompile]
    public struct SdfFieldEditJob : IJobParallelFor
    {
        [ReadOnly] public int3 chunkCoord;
        [ReadOnly] public float chunkSize;
        [ReadOnly] public int resolution;
        [ReadOnly] public NativeArray<SdfShapeType> shapeTypes;
        [ReadOnly] public NativeArray<SdfEditOp> editOps;
        [ReadOnly] public NativeArray<float> args;
        [ReadOnly] public NativeArray<NoiseParams> noiseParams;
        public NativeArray<float> values;   // 密集草稿（构建期临时数组，用后即弃）

        private static float Fbm(float3 p, int seed, int octaves)
        {
            octaves = math.max(1, octaves);
            float a = 0.5f, f = 1f, sum = 0f, norm = 0f;
            float3 sp = p + seed * 13.37f;
            for (int i = 0; i < octaves; i++) { sum += a * noise.snoise(sp * f); norm += a; a *= 0.5f; f *= 2f; }
            return sum / norm;
        }

        private static float3 Warp(float3 p, int seed, int octaves, float warpAmplitude)
        {
            return new float3(
                Fbm(p + new float3(31.4f, 0, 0), seed, octaves),
                Fbm(p + new float3(0, 62.8f, 0), seed, octaves),
                Fbm(p + new float3(0, 0, 94.2f), seed, octaves)) * warpAmplitude;
        }

        // 最近邻值噪声:每 blockSize³ 一个恒定随机值,块间不插值，表面呈不规则立方砖块。
        private static float BlockNoise(float3 p, int blockSize, int seed)
        {
            blockSize = math.max(1, blockSize);
            int3 cell = (int3)math.floor(p / (float)blockSize);
            uint h = math.hash((uint3)(cell + seed));
            return (h * (1f / uint.MaxValue)) * 2f - 1f;
        }

        // 表面位移
        private static float Displace(float sdf, float3 p, NoiseParams np)
        {
            if (np.displacement == 0f) return sdf;
            float n = np.noiseFunction == SdfNoiseFunction.Block
                ? BlockNoise(p, np.blockSize, np.seed)
                : Fbm(p * np.frequency, np.seed, np.octaves);
            return sdf - np.displacement * n;
        }

        private float Edit(float sdf1, float sdf2, SdfEditOp op)
        {
            switch (op)
            {
                case SdfEditOp.Add:
                    return math.min(sdf1, sdf2);
                case SdfEditOp.Sub:
                    return math.max(sdf1, -sdf2);
                default:
                    return sdf1;
            }
        }

        private static float CapsuleSdf(float3 p, float3 top, float3 down, float radius)
        {
            float3 AB = down - top;
            float len2 = math.dot(AB, AB);
            if (len2 < 1e-8)
            {
                return math.length(p - top) - radius;
            }
            float t = math.clamp(math.dot(p - top, AB) / len2, 0, 1);
            return math.length(p - (top + t * AB)) - radius;
        }

        private static float SphereSdf(float3 p, float3 center, float radius)
        {
            return math.distance(center, p) - radius;
        }

        private static float HalfSphereSdf(float3 p, float3 center, float radius, float3 normal)
        {
            return math.max(SphereSdf(p, center, radius), math.dot(p - center, normal));
        }

        private static float HalfCapsuleSdf(float3 p, float3 top, float3 down, float radius, float3 cutPoint, float3 normal)
        {
            return math.max(CapsuleSdf(p, top, down, radius), math.dot(p - cutPoint, normal));
        }

        private static float FloorSdf(float3 p, float height)
        {
            return p.y - height;
        }

        private static float CylinderSdf(float3 p, float3 top, float3 down, float radius)
        {
            float3 AB = down - top;
            float len2 = math.dot(AB, AB);
            if (len2 < 1e-8)
            {
                return math.length(p - top) - radius;
            }
            float3 axis = AB / math.sqrt(len2);
            float3 center = (top + down) * 0.5f;
            float halfLen = math.sqrt(len2) * 0.5f;
            float3 rel = p - center;
            float radial = math.length(rel - axis * math.dot(rel, axis)) - radius;
            float axial = math.abs(math.dot(rel, axis)) - halfLen;
            float2 q = new float2(radial, axial);
            return math.length(math.max(q, 0f)) + math.min(math.max(q.x, q.y), 0f);
        }

        private static float HalfCylinderSdf(float3 p, float3 top, float3 down, float radius, float3 cutPoint, float3 normal)
        {
            return math.max(CylinderSdf(p, top, down, radius), math.dot(p - cutPoint, normal));
        }

        public void Execute(int index)
        {
            int n2 = resolution * resolution;
            int z = index / n2;
            int y = (index - z * n2) / resolution;
            int x = index - z * n2 - y * resolution;
            int cells = resolution - 1;
            float voxelSize = chunkSize / cells;
            float halfSize = chunkSize / 2;
            float3 latticePos = (float3)(chunkCoord * cells + new int3(x, y, z)) * voxelSize - halfSize;
            float d = values[index];
            int idx = 0;

            for (int i = 0; i < shapeTypes.Length; i++)
            {
                SdfShapeType shapeType = shapeTypes[i];
                SdfEditOp editOp = editOps[i];
                NoiseParams np = noiseParams[i];

                // 域扭曲
                float3 q = np.warpAmplitude != 0f
                    ? latticePos + Warp(latticePos * np.warpFrequency, np.seed, np.octaves, np.warpAmplitude)
                    : latticePos;

                switch (shapeType)
                {
                    case SdfShapeType.Sphere:
                        float3 position = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float radiusSphere = args[idx + 3];
                        d = Edit(d, Displace(SphereSdf(q, position, radiusSphere), latticePos, np), editOp);
                        idx += 4;
                        break;

                    case SdfShapeType.HalfSphere:
                        float3 centerHs = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float radiusHs = args[idx + 3];
                        float3 normalHs = new float3(args[idx + 4], args[idx + 5], args[idx + 6]);
                        d = Edit(d, Displace(HalfSphereSdf(q, centerHs, radiusHs, normalHs), latticePos, np), editOp);
                        idx += 7;
                        break;

                    case SdfShapeType.Capsule:
                        float3 top = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 down = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusCapsule = args[idx + 6];
                        d = Edit(d, Displace(CapsuleSdf(q, top, down, radiusCapsule), latticePos, np), editOp);
                        idx += 7;
                        break;

                    case SdfShapeType.HalfCapsule:
                        float3 topHc = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 downHc = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusHc = args[idx + 6];
                        float3 cutPointHc = new float3(args[idx + 7], args[idx + 8], args[idx + 9]);
                        float3 normalHc = new float3(args[idx + 10], args[idx + 11], args[idx + 12]);
                        d = Edit(d, Displace(HalfCapsuleSdf(q, topHc, downHc, radiusHc, cutPointHc, normalHc), latticePos, np), editOp);
                        idx += 13;
                        break;

                    case SdfShapeType.Floor:
                        float height = args[idx];
                        d = Edit(d, Displace(FloorSdf(q, height), latticePos, np), editOp);
                        idx += 1;
                        break;

                    case SdfShapeType.Cylinder:
                        float3 topCy = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 downCy = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusCy = args[idx + 6];
                        d = Edit(d, Displace(CylinderSdf(q, topCy, downCy, radiusCy), latticePos, np), editOp);
                        idx += 7;
                        break;

                    case SdfShapeType.HalfCylinder:
                        float3 topHcy = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 downHcy = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusHcy = args[idx + 6];
                        float3 cutPointHcy = new float3(args[idx + 7], args[idx + 8], args[idx + 9]);
                        float3 normalHcy = new float3(args[idx + 10], args[idx + 11], args[idx + 12]);
                        d = Edit(d, Displace(HalfCylinderSdf(q, topHcy, downHcy, radiusHcy, cutPointHcy, normalHcy), latticePos, np), editOp);
                        idx += 13;
                        break;
                }
            }
            values[index] = d;
        }
    }
    
    /// 稀疏砖存储的 SDF 场（CPU 真值）。持久数据只有 BrickGrid（间接表 + 紧凑砖池）；
    /// 密集 values 数组降级为构建期临时草稿：Bake/Edit 时临时展开 → 求值 → 砖化 → 丢弃。
    /// 表面存在性（HasSurface）由砖化时的表面格扫描得出：有表面格 ⇔ 至少一个砖落盘。
    public class SdfField
    {
        private readonly int _resolution;
        private readonly int3 _chunkCoord;
        private readonly float _chunkSize;
        private readonly BrickGrid _bricks;

        public int Resolution => _resolution;
        public int3 ChunkCoord => _chunkCoord;
        public float ChunkSize => _chunkSize;
        public bool HasSurface => _bricks.PresentCount > 0;   // 块内是否有表面(决定 MC 是否出网格)
        public int PresentBrickCount => _bricks.PresentCount;
        public int BricksPerAxis => _bricks.BricksPerAxis;
        public NativeArray<uint> BrickIndirection => _bricks.Indirection;
        public NativeArray<half> BrickData => _bricks.BrickData.AsArray();

        public SdfField(int resolution, int3 chunkCoord, float chunkSize)
        {
            _resolution = resolution;
            _chunkCoord = chunkCoord;
            _chunkSize = chunkSize;
            _bricks = new BrickGrid(resolution, Allocator.Persistent);
        }
        
        /// 全形状烘焙 + 砖化。ChunkManager.InitializeSdfField 调用：
        /// 稠密烘焙（-∞ 初值 + 全部形状）→ 砖化 → 丢弃草稿。
        public void Bake(NativeArray<SdfShapeType> shapeTypes, NativeArray<SdfEditOp> editOps,
            NativeArray<float> args, NativeArray<NoiseParams> noiseParams)
        {
            int n = _resolution * _resolution * _resolution;
            using (NativeArray<float> dense = new(n, Allocator.TempJob))
            {
                float[] init = new float[n];
                Array.Fill(init, float.NegativeInfinity);
                dense.CopyFrom(init);

                SdfFieldEditJob job = new()
                {
                    chunkCoord = _chunkCoord,
                    chunkSize = _chunkSize,
                    resolution = _resolution,
                    shapeTypes = shapeTypes,
                    editOps = editOps,
                    args = args,
                    noiseParams = noiseParams,
                    values = dense,
                };
                job.Schedule(n, 256).Complete();

                Brickify(dense);
            }
        }
        
        /// 单形状编辑：反砖化（缺砖按 ±∞ 深值重建）→ 编辑 job → 砖化。
        public void Edit(SdfShape shape, SdfEditOp editOp)
        {
            int n = _resolution * _resolution * _resolution;
            var argList = new List<float>();
            shape.BakeArgs(argList);

            using (NativeArray<float> dense = new(n, Allocator.TempJob))
            using (NativeArray<SdfShapeType> shapeTypes = new (new [] { shape.Type }, Allocator.TempJob))
            using (NativeArray<SdfEditOp> editOps = new (new [] { editOp }, Allocator.TempJob))
            using (NativeArray<NoiseParams> noiseParams = new (new [] { default(NoiseParams) }, Allocator.TempJob))   // CarveAt 单形状编辑:零噪声(挖掘球保持精确球面)
            using (NativeArray<float> args = new (argList.ToArray(), Allocator.TempJob))
            {
                new BrickGrid.DeBrickifyJob
                {
                    indirection = _bricks.Indirection,
                    brickData = _bricks.BrickData.AsArray(),
                    resolution = _resolution,
                    bricksPerAxis = _bricks.BricksPerAxis,
                    values = dense,
                }.Schedule(n, 256).Complete();

                SdfFieldEditJob job = new()
                {
                    chunkCoord = _chunkCoord,
                    chunkSize = _chunkSize,
                    resolution = _resolution,
                    shapeTypes = shapeTypes,
                    editOps = editOps,
                    args = args,
                    noiseParams = noiseParams,
                    values = dense,
                };
                job.Schedule(n, 256).Complete();

                Brickify(dense);
            }
        }
        
        /// 密集草稿 → 稀疏砖：表面格扫描 → halo 存在性 → 紧凑映射（前缀和）→ 拷贝值。
        /// 缺砖槽按砖原点值符号填 AbsentSolid/AbsentAir（无表面砖内所有格点同号）。
        private void Brickify(NativeArray<float> dense)
        {
            int cells = _resolution - 1;
            int cellCount = cells * cells * cells;
            int bpa = _bricks.BricksPerAxis;
            int slotCount = bpa * bpa * bpa;

            using (NativeArray<byte> flags = new(cellCount, Allocator.TempJob))
            using (NativeArray<int> presence = new(slotCount, Allocator.TempJob))
            {
                new BrickGrid.BrickSurfaceFlagsJob { values = dense, resolution = _resolution, surfaceFlags = flags }
                    .Schedule(cellCount, 256).Complete();
                new BrickGrid.BrickPresenceJob { surfaceFlags = flags, resolution = _resolution, bricksPerAxis = bpa, presence = presence }
                    .Schedule(slotCount, 256).Complete();

                int count = 0;
                for (int s = 0; s < slotCount; s++)
                    if (presence[s] != 0) count++;

                // 主线程紧凑映射(前缀和): 直接写持久的间接表,slots 供拷贝 job 反查槽位。
                // 注意 using 变量只读,不能走 indexer setter → slots 用 try/finally 释放。
                var slots = new NativeArray<int>(count, Allocator.TempJob);
                try
                {
                    int next = 0;
                    int n2 = _resolution * _resolution;
                    for (int s = 0; s < slotCount; s++)
                    {
                        if (presence[s] != 0)
                        {
                            _bricks.Indirection[s] = (uint)next;
                            slots[next++] = s;
                        }
                        else
                        {
                            int bz = s / (bpa * bpa);
                            int by = (s - bz * bpa * bpa) / bpa;
                            int bx = s - bz * bpa * bpa - by * bpa;
                            float origin = dense[bz * BrickGrid.BrickSize * n2 + by * BrickGrid.BrickSize * _resolution + bx * BrickGrid.BrickSize];
                            _bricks.Indirection[s] = origin < 0f ? BrickGrid.AbsentSolid : BrickGrid.AbsentAir;
                        }
                    }

                    _bricks.BrickData.Resize(count * BrickGrid.BrickValueCount, NativeArrayOptions.UninitializedMemory);
                    _bricks.BrickData.TrimExcess();
                    _bricks.PresentCount = count;

                    if (count > 0)
                    {
                        new BrickGrid.BrickCopyJob
                        {
                            values = dense,
                            brickSlots = slots,
                            resolution = _resolution,
                            bricksPerAxis = bpa,
                            brickData = _bricks.BrickData.AsArray(),
                        }.Schedule(count * BrickGrid.BrickValueCount, 256).Complete();
                    }
                }
                finally
                {
                    slots.Dispose();
                }
            }
        }

        // 三线性采样场：缺砖格点返回缺省深值(±∞)，与旧版越界返回 +∞ 的行为兼容。
        public float Sample(Vector3 worldPos)
        {
            int cells = _resolution - 1;
            float voxelSize = _chunkSize / cells;
            float halfSize = _chunkSize / 2;

            float3 p = new float3(worldPos.x, worldPos.y, worldPos.z);
            float3 latticeF = (p + halfSize) / voxelSize - _chunkCoord * cells;

            if (math.any(latticeF < 0f) || math.any(latticeF > cells))
            {
                return float.PositiveInfinity;
            }

            int3 lo = (int3)math.floor(latticeF);
            int3 hi = math.min(lo + 1, cells);
            float3 f = latticeF - lo;

            float v000 = V(lo.x, lo.y, lo.z);
            float v100 = V(hi.x, lo.y, lo.z);
            float v010 = V(lo.x, hi.y, lo.z);
            float v110 = V(hi.x, hi.y, lo.z);
            float v001 = V(lo.x, lo.y, hi.z);
            float v101 = V(hi.x, lo.y, hi.z);
            float v011 = V(lo.x, hi.y, hi.z);
            float v111 = V(hi.x, hi.y, hi.z);

            float x00 = math.lerp(v000, v100, f.x);
            float x10 = math.lerp(v010, v110, f.x);
            float x01 = math.lerp(v001, v101, f.x);
            float x11 = math.lerp(v011, v111, f.x);
            float y0 = math.lerp(x00, x10, f.y);
            float y1 = math.lerp(x01, x11, f.y);
            return math.lerp(y0, y1, f.z);
        }

        // 格点值：砖内存真值，缺砖按槽内符号默认深值(±MaxValue,不用 ±∞ 防止三线性 lerp 出现 ∞-∞=NaN)。
        private float V(int lx, int ly, int lz)
        {
            int bpa = _bricks.BricksPerAxis;
            uint id = _bricks.Indirection[BrickGrid.SlotOf(lx, ly, lz, bpa)];
            if (id >= BrickGrid.AbsentSolid)
                return id == BrickGrid.AbsentAir ? float.MaxValue : float.MinValue;
            int bx = math.min(lx >> 3, bpa - 1);
            int by = math.min(ly >> 3, bpa - 1);
            int bz = math.min(lz >> 3, bpa - 1);
            int ox = lx - bx * BrickGrid.BrickSize;
            int oy = ly - by * BrickGrid.BrickSize;
            int oz = lz - bz * BrickGrid.BrickSize;
            int bid = (int)id;
            return _bricks.BrickData[bid * BrickGrid.BrickValueCount + ox + oy * BrickGrid.BrickValuesPerAxis
                + oz * BrickGrid.BrickValuesPerAxis * BrickGrid.BrickValuesPerAxis];
        }

        public void Destroy()
        {
            _bricks.Dispose();
        }
    }
}
