using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;


namespace CubicMethod
{
    /// <summary>
    /// Compute-shader Marching Cubes: one persistent buffer set, dispatched per chunk.
    /// Sampling resolution (cells per axis) = sdfResolution - 1. The SDF lattice is
    /// uploaded to one reusable scratch StructuredBuffer per dispatch — the CPU values
    /// array is the only truth, there is no per-chunk GPU copy. The kernel emits
    /// per-triangle vertices in winding (v0, v2, v1) and does not write the Normals
    /// buffer (CPU gradient normals are computed by the caller from the SDF lattice).
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
            { "res", Shader.PropertyToID("res") },
            { "SDF_VALUES", Shader.PropertyToID("SDF_VALUES") },
        };

        private ComputeShader _cs;
        private ComputeBuffer _verticesBuffer;
        private ComputeBuffer _normalsBuffer;
        private ComputeBuffer _trianglesBuffer;
        private ComputeBuffer _countersBuffer;
        private ComputeBuffer _edgeTableBuffer;
        private ComputeBuffer _triTableBuffer;
        private ComputeBuffer _sdfBuffer;      // 可复用 scratch:每次 dispatch 上传格点值
        private int _kernelIndex;
        private int _maxVertices;
        private int _resolution;
        private int _latticeRes;               // 每轴格点数 = resolution + 1
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
            _latticeRes = resolution + 1;
            _groups = Mathf.CeilToInt(resolution / 4f); // matches [numthreads(4,4,4)] — see X4714 note in MarchingCubes.compute
            _sdfBuffer = new ComputeBuffer(_latticeRes * _latticeRes * _latticeRes, sizeof(float));
            _verticesBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
            _normalsBuffer = new ComputeBuffer(_maxVertices, sizeof(float) * 3);
            _trianglesBuffer = new ComputeBuffer(_maxVertices, sizeof(int));
            _countersBuffer = new ComputeBuffer(2, sizeof(int));
            _edgeTableBuffer = new ComputeBuffer(McTables.EdgeTable.Length, sizeof(int));
            _edgeTableBuffer.SetData(McTables.EdgeTable);
            _triTableBuffer = new ComputeBuffer(McTables.TriTable.Length, sizeof(int));
            _triTableBuffer.SetData(McTables.TriTable);
        }


        public void Dispatch(NativeArray<float> sdfValues)
        {
            int[] counters = { 0, 0 };
            _countersBuffer.SetData(counters);
            _cs.SetInt(PropertyIds["resolution"], _resolution);
            _cs.SetInt(PropertyIds["res"], _latticeRes);
            _cs.SetFloat(PropertyIds["size"], _size);
            _cs.SetInt(PropertyIds["maxVertices"], _maxVertices);

            _cs.SetBuffer(_kernelIndex, PropertyIds["edgeTable"], _edgeTableBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["triTable"], _triTableBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Vertices"], _verticesBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Normals"], _normalsBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Triangles"], _trianglesBuffer);
            _cs.SetBuffer(_kernelIndex, PropertyIds["Counters"], _countersBuffer);
            _sdfBuffer.SetData(sdfValues);
            _cs.SetBuffer(_kernelIndex, PropertyIds["SDF_VALUES"], _sdfBuffer);

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
            _sdfBuffer.Release();
        }
    }
}
