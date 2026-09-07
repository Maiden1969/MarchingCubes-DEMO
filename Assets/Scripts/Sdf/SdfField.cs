using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        public NativeArray<float> values;
        [NativeDisableParallelForRestriction] public NativeArray<float> threadMinMax;
        [NativeSetThreadIndex] private int m_ThreadIndex;

        public float Edit(float sdf1, float sdf2, SdfEditOp op)
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
                
                switch (shapeType)
                {
                    case SdfShapeType.Sphere:
                        float3 position = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float radiusSphere = args[idx + 3];
                        float sdfSphere = math.distance(position, latticePos) - radiusSphere;
                        d = Edit(d, sdfSphere, editOp);
                        idx += 4;
                        break;

                    case SdfShapeType.HalfSphere:
                        float3 centerHs = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float radiusHs = args[idx + 3];
                        float3 normalHs = new float3(args[idx + 4], args[idx + 5], args[idx + 6]);
                        float sdfHalfSphere = math.max(math.distance(centerHs, latticePos) - radiusHs,
                                                       math.dot(latticePos - centerHs, normalHs));
                        d = Edit(d, sdfHalfSphere, editOp);
                        idx += 7;
                        break;

                    case SdfShapeType.Capsule:
                        float3 top = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 down = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusCapsule = args[idx + 6];
                        d = Edit(d, CapsuleSdf(latticePos, top, down, radiusCapsule), editOp);
                        idx += 7;
                        break;

                    case SdfShapeType.HalfCapsule:
                        float3 topHc = new float3(args[idx], args[idx + 1], args[idx + 2]);
                        float3 downHc = new float3(args[idx + 3], args[idx + 4], args[idx + 5]);
                        float radiusHc = args[idx + 6];
                        float3 cutPointHc = new float3(args[idx + 7], args[idx + 8], args[idx + 9]);
                        float3 normalHc = new float3(args[idx + 10], args[idx + 11], args[idx + 12]);
                        float sdfHalfCapsule = math.max(CapsuleSdf(latticePos, topHc, downHc, radiusHc),
                                                        math.dot(latticePos - cutPointHc, normalHc));
                        d = Edit(d, sdfHalfCapsule, editOp);
                        idx += 13;
                        break;
                    
                    case SdfShapeType.Floor:
                        float height = args[idx];
                        float sdfFloor = latticePos.y - height;
                        d = Edit(d, sdfFloor, editOp);
                        idx += 1;
                        break;
                }
            }
            values[index] = d;
            int slot = m_ThreadIndex * 2;
            threadMinMax[slot] = math.min(threadMinMax[slot], d);
            threadMinMax[slot + 1] = math.max(threadMinMax[slot + 1], d);
        }
    }
    
    public class SdfField
    {
        private readonly int _resolution;
        private readonly int3 _chunkCoord;
        private readonly float _chunkSize;
        private const int MaxThreads = 128;   // Unity worker 线程数上限(JobsUtility.JobWorkerMaximumCount)
        private bool _hasSurface;

        public NativeArray<float> values;
        // 每线程归约槽(2×MaxThreads = 256):偶数 = min,奇数 = max。由编辑 job 写入,Apply() 合并。
        public NativeArray<float> threadMinMax;
        public int Resolution => _resolution;
        public int3 ChunkCoord => _chunkCoord;
        public float ChunkSize => _chunkSize;
        public bool HasSurface => _hasSurface;   // 块内是否有表面(决定 MC 是否出网格)

        public SdfField(int resolution, int3 chunkCoord, float chunkSize)
        {
            _resolution = resolution;
            _chunkCoord = chunkCoord;
            _chunkSize = chunkSize;
            values = new NativeArray<float>(_resolution * _resolution * _resolution,
                Allocator.Persistent);
            threadMinMax = new NativeArray<float>(2 * MaxThreads, Allocator.Persistent);
            ResetThreadMinMax();
            float[] initialValues = new float[_resolution * _resolution * _resolution];
            Array.Fill(initialValues, float.PositiveInfinity);
            values.CopyFrom(initialValues);
            Apply();
        }
        
        public void Apply()
        {
            // 主线程合并各线程归约槽 → 全局 min/max(线性合并,~128 次迭代,纳秒级)。
            // 存在负值且存在 ≥0 值 = 有等值面穿过(负 = 实体)。
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 0; i < threadMinMax.Length; i += 2)
            {
                min = math.min(min, threadMinMax[i]);
                max = math.max(max, threadMinMax[i + 1]);
            }
            _hasSurface = min < 0f && max >= 0f;
        }

        // 归约槽重置:偶数槽 = +∞(min),奇数槽 = -∞(max)。每次调度编辑 job 前必须调用,
        // 否则未参与本轮调度的线程槽会残留上一次的 min/max。
        public void ResetThreadMinMax()
        {
            for (int i = 0; i < threadMinMax.Length; i++)
            {
                threadMinMax[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            }
        }

        public void Edit(SdfShape shape, SdfEditOp editOp)
        {
            var argList = new List<float>();
            shape.BakeArgs(argList);

            using (NativeArray<SdfShapeType> shapeTypes = new (new [] { shape.Type }, Allocator.TempJob))
            using (NativeArray<SdfEditOp> editOps = new (new [] { editOp }, Allocator.TempJob))
            using (NativeArray<float> args = new (argList.ToArray(), Allocator.TempJob))
            {
                SdfFieldEditJob job = new()
                {
                    chunkCoord = _chunkCoord,
                    chunkSize = _chunkSize,
                    resolution = _resolution,
                    shapeTypes = shapeTypes,
                    editOps = editOps,
                    args = args,
                    values = values,
                    threadMinMax = threadMinMax,
                };

                ResetThreadMinMax();
                job.Schedule(_resolution * _resolution * _resolution, 256).Complete();
            }
            
            Apply();
        }

        /// <summary>
        /// 在世界坐标采样该场的 SDF 值(三线性插值,负 = 实体)。场覆盖以
        /// chunkCoord * chunkSize 为中心、边长 chunkSize 的立方体(边界含在内);
        /// 点在场外时返回 float.PositiveInfinity,与场的基础值(+∞ = 空气)一致。
        /// </summary>
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
            int3 hi = math.min(lo + 1, _resolution - 1);
            float3 f = latticeF - lo;

            int n = _resolution;
            int n2 = n * n;
            float v000 = values[lo.z * n2 + lo.y * n + lo.x];
            float v100 = values[lo.z * n2 + lo.y * n + hi.x];
            float v010 = values[lo.z * n2 + hi.y * n + lo.x];
            float v110 = values[lo.z * n2 + hi.y * n + hi.x];
            float v001 = values[hi.z * n2 + lo.y * n + lo.x];
            float v101 = values[hi.z * n2 + lo.y * n + hi.x];
            float v011 = values[hi.z * n2 + hi.y * n + lo.x];
            float v111 = values[hi.z * n2 + hi.y * n + hi.x];

            float x00 = math.lerp(v000, v100, f.x);
            float x10 = math.lerp(v010, v110, f.x);
            float x01 = math.lerp(v001, v101, f.x);
            float x11 = math.lerp(v011, v111, f.x);
            float y0 = math.lerp(x00, x10, f.y);
            float y1 = math.lerp(x01, x11, f.y);
            return math.lerp(y0, y1, f.z);
        }

        public void Destroy()
        {
            if (values.IsCreated)
            {
                values.Dispose();
            }
            if (threadMinMax.IsCreated)
            {
                threadMinMax.Dispose();
            }
        }
    }
}
