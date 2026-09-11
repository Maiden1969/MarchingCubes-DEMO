using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;


namespace CubicMethod
{
    /// <summary>
    /// Compute-shader Marching Cubes: one persistent buffer set, dispatched per chunk.
    /// Sampling resolution (cells per axis) = sdfResolution - 1. The sparse brick map
    /// (compact brick pool + brick indirection) is uploaded per dispatch — the CPU brick
    /// data is the only truth; kernel skips cells whose brick is absent (no surface).
    /// The kernel emits per-triangle vertices in winding (v0, v2, v1) and does not write
    /// the Normals buffer (CPU gradient normals are computed by the caller from the SDF lattice).
    /// </summary>
    public class MarchingCubes
    {
        private static readonly Dictionary<string, int> PropertyIds = new ()
        {
            { "resolution", Shader.PropertyToID("resolution") },
            { "size", Shader.PropertyToID("size") },
            { "maxVertices", Shader.PropertyToID("maxVertices") },
            { "edgeTable", Shader.PropertyToID("edgeTable") },
            { "triTable", Shader.PropertyToID("triTable") },
            { "Vertices", Shader.PropertyToID("Vertices") },
            { "Normals", Shader.PropertyToID("Normals") },
            { "Triangles", Shader.PropertyToID("Triangles") },
            { "Counters", Shader.PropertyToID("Counters") },
            { "bricksPerAxis", Shader.PropertyToID("bricksPerAxis") },
            { "BRICK_DATA", Shader.PropertyToID("BRICK_DATA") },
            { "BRICK_INDIRECTION", Shader.PropertyToID("BRICK_INDIRECTION") },
        };

        private ComputeShader _cs;
        private ComputeBuffer _verticesBuffer;
        private ComputeBuffer _normalsBuffer;
        private ComputeBuffer _trianglesBuffer;
        private ComputeBuffer _countersBuffer;
        private ComputeBuffer _edgeTableBuffer;
        private ComputeBuffer _triTableBuffer;
        // half 砖池 → float32 上传草稿的转换 job(所有 float→half 的逆转换只在此处发生,
        // GPU 与 kernel 完全不动)。
        [BurstCompile]
        private struct HalfToFloatJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<half> src;
            public NativeArray<float> dst;
            public void Execute(int i) => dst[i] = src[i];
        }

        private ComputeBuffer _brickBuffer;        // 可复用 scratch:每次 dispatch 上传紧凑砖池
        private ComputeBuffer _indirectionBuffer;  // 砖间接表(每块 bpa³ 个 uint)
        private NativeArray<float> _uploadScratch; // CPU 侧 half→float 转换草稿(按高水位增长)
        private int _kernelIndex;
        private int _maxVertices;
        private int _resolution;
        private float _size;
        private int _groups;
        private readonly int[] _counters = new int[2];

        public MarchingCubes(ComputeShader cs, int resolution, float size)
        {
            _cs = cs;
            _resolution = resolution;
            _size = size;
            _kernelIndex = _cs.FindKernel("MarchingCubes");
            _maxVertices = resolution * resolution * resolution * 15;
            _groups = Mathf.CeilToInt(resolution / 4f); // matches [numthreads(4,4,4)] — see X4714 note in MarchingCubes.compute
            _verticesBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
            _normalsBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
            _trianglesBuffer = new ComputeBuffer(_maxVertices, sizeof(int));
            _countersBuffer = new ComputeBuffer(2, sizeof(int));
            _edgeTableBuffer = new ComputeBuffer(McTables.EdgeTable.Length, sizeof(int));
            _edgeTableBuffer.SetData(McTables.EdgeTable);
            _triTableBuffer = new ComputeBuffer(McTables.TriTable.Length, sizeof(int));
            _triTableBuffer.SetData(McTables.TriTable);
        }


        /// <summary>
        /// 上传稀疏砖数据并 dispatch:CPU 侧 half 砖池先经 Burst job 转成 float32 草稿
        /// (GPU 与 kernel 不变),砖池 scratch 按需扩容(上限 bpa³×729,通常远小于此),
        /// kernel 对缺砖格子直接跳过。
        /// </summary>
        public void Dispatch(NativeArray<half> brickData, NativeArray<uint> indirection, int bricksPerAxis)
        {
            int[] counters = { 0, 0 };
            _countersBuffer.SetData(counters);
            _cs.SetInt(PropertyIds["resolution"], _resolution);
            _cs.SetFloat(PropertyIds["size"], _size);
            _cs.SetInt(PropertyIds["maxVertices"], _maxVertices);
            _cs.SetInt(PropertyIds["bricksPerAxis"], bricksPerAxis);

            _cs.SetBuffer(_kernelIndex, PropertyIds["edgeTable"], _edgeTableBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["triTable"], _triTableBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Vertices"], _verticesBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Normals"], _normalsBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Triangles"], _trianglesBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Counters"], _countersBuffer);

            if (_brickBuffer == null || _brickBuffer.count < brickData.Length)
            {
                _brickBuffer?.Release();
                _brickBuffer = new ComputeBuffer(Mathf.Max(brickData.Length, 1), sizeof(float));
            }
            if (_indirectionBuffer == null || _indirectionBuffer.count != indirection.Length)
            {
                _indirectionBuffer?.Release();
                _indirectionBuffer = new ComputeBuffer(indirection.Length, sizeof(uint));
            }
            if (_uploadScratch.IsCreated == false || _uploadScratch.Length < brickData.Length)
            {
                if (_uploadScratch.IsCreated) _uploadScratch.Dispose();
                _uploadScratch = new NativeArray<float>(Mathf.Max(brickData.Length, 1), Allocator.Persistent);
            }
            if (brickData.Length > 0)
            {
                new HalfToFloatJob { src = brickData, dst = _uploadScratch }.Schedule(brickData.Length, 256).Complete();
                _brickBuffer.SetData(_uploadScratch, 0, 0, brickData.Length);
            }
            _indirectionBuffer.SetData(indirection);
            _cs.SetBuffer(_kernelIndex, PropertyIds["BRICK_DATA"], _brickBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["BRICK_INDIRECTION"], _indirectionBuffer);

            _cs.Dispatch(_kernelIndex, _groups, _groups, _groups);
        }

        /// <summary>
        /// Read the dispatch result back into the mesh (vertices + indices). Normals are
        /// NOT filled here — the caller computes them from the SDF lattice.
        /// </summary>
        public void BuildMesh(out Vector3[] vertices, out int[] triangles)
        {
            _countersBuffer.GetData(_counters);
            // The kernel guards writes with maxVertices but the counters keep counting; clamp.
            int vertexCount = Mathf.Min(_counters[0], _maxVertices);
            int indexCount = Mathf.Min(_counters[1], _maxVertices);
            vertices = new Vector3[vertexCount];
            triangles = new int[indexCount];

            if (vertexCount == 0 || indexCount == 0) return;

            _verticesBuffer.GetData(vertices, 0, 0, vertexCount);
            _trianglesBuffer.GetData(triangles, 0, 0, indexCount);
        }

        public void Release()
        {
            _verticesBuffer.Release();
            _normalsBuffer.Release();
            _trianglesBuffer.Release();
            _countersBuffer.Release();
            _edgeTableBuffer.Release();
            _triTableBuffer.Release();
            _brickBuffer?.Release();
            _indirectionBuffer?.Release();
            if (_uploadScratch.IsCreated) _uploadScratch.Dispose();
        }
    }
}
