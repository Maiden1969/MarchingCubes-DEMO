using System;
using System.Collections.Generic;
using CubicMethod;
using Sdf;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using Mesh = UnityEngine.Mesh;

namespace Chunks
{
    public struct ChunkVisibilityResult
    {
        public int3 ChunkCoord;
        public bool IsVisible;
    }

    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct ChunkVisibilityJob : IJobParallelFor
    {
        [ReadOnly] public int3 CurChunkCoord;
        [ReadOnly] public int ChunkCountPerAxis;
        [ReadOnly] public float3 PlayerPos;
        [ReadOnly] public float SqrMaxDistance;
        [ReadOnly] public NativeArray<Plane> FrustumPlanes;
        [ReadOnly] public float Size;

        [WriteOnly] public NativeArray<ChunkVisibilityResult> Results;

        [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
        private static bool TestPlanesAABB(in NativeArray<Plane> planes, in Bounds aabb)
        {
            foreach (var plane in planes)
            {
                float3 normal = plane.normal;
                var dot = math.dot(normal, aabb.center);
                var radius = math.dot(math.abs(normal), aabb.extents);
                if (dot + radius < -plane.distance - 0.1f)
                {
                    return false;
                }
            }
            return true;
        }

        public void Execute(int index)
        {
            var totalPerI = (2 * ChunkCountPerAxis + 1) * (2 * ChunkCountPerAxis + 1);
            var i = index / totalPerI - ChunkCountPerAxis;
            var remaining = index % totalPerI;
            var j = remaining / (2 * ChunkCountPerAxis + 1) - ChunkCountPerAxis;
            var k = remaining % (2 * ChunkCountPerAxis + 1) - ChunkCountPerAxis;

            var chunkCoord = CurChunkCoord + math.int3(i, j, k);
            var chunkPos = math.float3(chunkCoord) * Size;
            var sqrDistance = math.lengthsq(chunkPos - PlayerPos);

            var isVisible = false;

            // 3×3×3 around the player is always loaded (feet must not fall through);
            // everything else is distance culled only — frustum culling disabled,
            // Unity's renderer culling already skips off-frustum draw calls.
            if (math.abs(i) <= 1 && math.abs(j) <= 1 && math.abs(k) <= 1)
            {
                isVisible = true;
            }
            else if (sqrDistance < SqrMaxDistance)
            {
                isVisible = true;
                // 视锥检测禁用
                // Bounds chunkBounds = new Bounds(chunkPos, new Vector3(Size, Size, Size));
                // isVisible = TestPlanesAABB(FrustumPlanes, chunkBounds);
            }

            Results[index] = new ChunkVisibilityResult
            {
                ChunkCoord = chunkCoord,
                IsVisible = isVisible
            };
        }
    }
    
    public class ChunkManager : MonoBehaviour
    {
        [Header("Chunking")] 
        [SerializeField] private bool update;
        [SerializeField] private int chunkSize = 16;              // world units per chunk
        [SerializeField] private float maxDistance = 100f;
        [SerializeField] private int updateFrequency = 2;   // chunk visibility refresh interval (frames)

        [Header("Rendering / Physics")]
        [SerializeField] private ComputeShader computeShader;
        [SerializeField] private int sdfResolution = 33;
        [SerializeField] private Material material;
        [SerializeField] private LayerMask generationLayer;

        [Header("Carve")]
        [SerializeField] private float carveRadius = 1.5f;

        [Header("Cache")]
        [SerializeField] private int cacheCapacity = 2000;   // LRU 缓存条目上限

        private readonly Dictionary<Vector3Int, Chunk> _activeChunks = new ();
        private readonly Queue<Chunk> _chunkPool = new ();
        private readonly HashSet<Vector3Int> _visibleChunks = new ();
        private readonly List<Vector3Int> _chunksToUnLoad = new ();
        private readonly HashSet<Vector3Int> _dirtyChunks = new ();
        private NativeArray<Plane> _frustumPlanes;
        private ChunkCache _cache;
        private MarchingCubes _marchingCubes;
        private int _mcResolution;         // sdfResolution - 1
        private int _maxChunkCountPerAxis;
        private int _updateCounter;
        private float _sqrMaxDistance;
        private Transform _playerTransform;
        private Camera _playerCamera;
        private SdfWorld _sdfWorld;
        
        public static ChunkManager Instance { get; private set; }
        
        public event Action<Vector3Int> SdfChanged;
        
        public float VoxelSize => chunkSize / (float)_mcResolution;
        public float CellSize => chunkSize / (float)_mcResolution;
        public float HalfChunkSize => chunkSize * 0.5f;
        public int CellCount => _mcResolution * _mcResolution * _mcResolution;
        public int SdfResolution => sdfResolution;
        public int McResolution => _mcResolution;
        public int CellResolution => _mcResolution;
        public float MaxDistance => maxDistance;
        public int MaxChunkCountPerAxis => _maxChunkCountPerAxis;
        
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            
            sdfResolution = Mathf.Max(2, sdfResolution);
            _mcResolution = sdfResolution - 1;

            _cache = new ChunkCache { Capacity = cacheCapacity };
            _sqrMaxDistance = maxDistance * maxDistance;
            _maxChunkCountPerAxis = Mathf.CeilToInt(maxDistance / chunkSize);
            if (computeShader != null)
            {
                _marchingCubes = new MarchingCubes(computeShader, _mcResolution, chunkSize);
            }
            else
            {
                Debug.LogError("ChunkManager: computeShader 未赋值,chunk 将不会生成网格。");
            }
            _frustumPlanes = new NativeArray<Plane>(6, Allocator.Persistent);
        }
        
        private void Start()
        {
            PrepareChunkPool();
            _sdfWorld = SdfWorld.Instance;
            _playerTransform = GameObject.FindGameObjectWithTag("Player")?.transform ?? Camera.main?.transform;
            _playerCamera = Camera.main;
        }

        private void Update()
        {
            if (!update) return;
            if (_updateCounter >= updateFrequency)
            {
                _updateCounter = 0;
                UpdateChunks();
            }
            else
            {
                _updateCounter++;
            }

            HandleCarveInput();
        }

        public void SetUpdate(bool state)
        {
            update = state;
        }

        // 左键点击挖球。
        private void HandleCarveInput()
        {
            if (Mouse.current == null || _playerCamera == null) return;
            if (!Mouse.current.leftButton.wasPressedThisFrame) return;

            Ray ray = _playerCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, maxDistance);
            Physics.queriesHitBackfaces = backfaces;

            if (hit) CarveAt(hitInfo.point, carveRadius);
        }
        
        public void CarveAt(Vector3 hit, float radius)
        {
            Vector3Int minCoord = WorldPosToChunkCoord(hit - Vector3.one * (3f * radius));
            Vector3Int maxCoord = WorldPosToChunkCoord(hit + Vector3.one * (3f * radius));

            var carve = new SdfSphere { center = hit, radius = radius };

            for (int x = minCoord.x; x <= maxCoord.x; x++)
            for (int y = minCoord.y; y <= maxCoord.y; y++)
            for (int z = minCoord.z; z <= maxCoord.z; z++)
            {
                Vector3Int coord = new Vector3Int(x, y, z);
                GetSdfField(coord).Edit(carve, SdfEditOp.Sub);
                MarkChunkDirty(coord);
                SdfChanged?.Invoke(coord);
            }

            // GetSdfField 可能顺带为非活动块创建了场,裁一次压回容量。
            _cache.Trim(cacheCapacity, _activeChunks.Keys);
        }

        private Chunk CreateChunk(int id = 0)
        {
            GameObject go = new GameObject("Chunk" + id);
            go.transform.SetParent(transform);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mc = go.AddComponent<MeshCollider>();
            var chunk = go.AddComponent<Chunk>();
            chunk.Init(chunkSize, mf, mc, mr, material, generationLayer);
            go.SetActive(false);
            return chunk;
        }

        private void PrepareChunkPool()
        {
            _chunkPool.Clear();
            for (int i = 0; i < 350; i++)
            {
                _chunkPool.Enqueue(CreateChunk(i));
            }
        }

        // 世界坐标转块坐标,世界中心为零块中心。
        public Vector3Int WorldPosToChunkCoord(Vector3 pos)
        {
            int x = Mathf.FloorToInt(pos.x / chunkSize + 0.5f);
            int y = Mathf.FloorToInt(pos.y / chunkSize + 0.5f);
            int z = Mathf.FloorToInt(pos.z / chunkSize + 0.5f);
            return new Vector3Int(x, y, z);
        }
        
        public Vector3Int WorldPosToCellCoord(Vector3 pos)
        {
            Vector3Int chunkCoord = WorldPosToChunkCoord(pos);
            Vector3 chunkPos = chunkCoord * chunkSize;
            Vector3 localPos = pos - chunkPos;
            float half = HalfChunkSize;
            return Vector3Int.FloorToInt((localPos + new Vector3(half, half, half)) / CellSize);
        }

        public int WorldPosToCellIndex(Vector3 pos)
        {
            Vector3Int cellCoord = WorldPosToCellCoord(pos);
            return cellCoord.x + cellCoord.y * _mcResolution + cellCoord.z * _mcResolution * _mcResolution;
        }

        // 块坐标转世界坐标(块中心)
        public Vector3 ChunkCoordToWorldPos(Vector3Int chunkCoord)
        {
            return (Vector3)chunkCoord * chunkSize;
        }

        private void UpdateFrustumPlanes()
        {
            Plane[] temp = GeometryUtility.CalculateFrustumPlanes(_playerCamera);
            for (int i = 0; i < 6; ++i)
            {
                _frustumPlanes[i] = temp[i];
            }
        }
        
        public void MarkChunkDirty(Vector3Int chunkCoord)
        {
            _dirtyChunks.Add(chunkCoord);
        }
        
        // 初始化某个块的SDF场(稠密烘焙 → 稀疏砖化,草稿数组用后即弃)
        public SdfField InitializeSdfField(Vector3Int chunkCoord)
        {
            var field = new SdfField(sdfResolution, math.int3(chunkCoord.x, chunkCoord.y, chunkCoord.z), chunkSize);
            if (_sdfWorld)
            {
                field.Bake(_sdfWorld.ShapeTypes, _sdfWorld.EditOps, _sdfWorld.Args, _sdfWorld.NoiseParams);
            }

            _cache.AddSdfToCache(chunkCoord, field);
            SdfChanged?.Invoke(chunkCoord);

            return field;
        }
        
        // 获得某个块的 SDF 场
        public SdfField GetSdfField(Vector3Int chunkCoord)
        {
            if (!_cache.TryGetSdfFromCache(chunkCoord, out SdfField field))
            {
                field = InitializeSdfField(chunkCoord);
            }
            return field;
        }
        
        /// 采样世界空间任意一点的 SDF 值
        public float Sample(Vector3 worldPos)
        {
            Vector3Int chunkCoord = WorldPosToChunkCoord(worldPos);
            return GetSdfField(chunkCoord).Sample(worldPos);
        }
        
        private void GenerateChunk(Vector3Int chunkCoord, Chunk chunk)
        {
            if (_marchingCubes == null) return;

            SdfField field = GetSdfField(chunkCoord);

            // 无表面
            Vector3[] vertices = Array.Empty<Vector3>();
            int[] triangles = Array.Empty<int>();
            if (field.HasSurface)
            {
                _marchingCubes.Dispatch(field.BrickData, field.BrickIndirection, field.BricksPerAxis);
                _marchingCubes.BuildMesh(out vertices, out triangles);
            }
            
            // 脏块
            if (_cache.TryGetMeshFromCache(chunkCoord, out Mesh cachedMesh))
            {
                cachedMesh.Clear();
                cachedMesh.vertices = vertices;
                cachedMesh.triangles = triangles;
                cachedMesh.RecalculateBounds();
                cachedMesh.RecalculateNormals();
                chunk.SetMesh(cachedMesh);
            }
            // 无缓存
            else
            {
                var mesh = new Mesh{ name = $"ChunkMesh_{chunkCoord}" };
                mesh.vertices = vertices;
                mesh.triangles = triangles;
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                _cache.AddMeshToCache(chunkCoord, mesh);   
                chunk.SetMesh(mesh);
            }
        }

        // 脏坐标或缓存缺失，重新生成并更新缓存，否则直接复用缓存网格。
        private void UpdateChunkMesh(Vector3Int chunkCoord, Chunk chunk)
        {
            bool dirty = _dirtyChunks.Remove(chunkCoord);
            if (!dirty && _cache.TryGetMeshFromCache(chunkCoord, out Mesh cached))
            {
                chunk.SetMesh(cached);
                return;
            }
            GenerateChunk(chunkCoord, chunk);
        }

        // 加载块到指定的块坐标
        public void LoadChunk(Vector3Int chunkCoord)
        {
            if (_activeChunks.ContainsKey(chunkCoord)) return;

            Chunk chunk = _chunkPool.Count > 0 ? _chunkPool.Dequeue() : CreateChunk(_chunkPool.Count + _activeChunks.Count);
            chunk.transform.position = ChunkCoordToWorldPos(chunkCoord);
            chunk.gameObject.SetActive(true);
            _activeChunks.Add(chunkCoord, chunk);

            UpdateChunkMesh(chunkCoord, chunk);
        }

        // 卸载指定坐标的块
        private void UnloadChunk(Vector3Int chunkCoord)
        {
            if (!_activeChunks.TryGetValue(chunkCoord, out Chunk chunk)) return;
            chunk.gameObject.SetActive(false);
            _activeChunks.Remove(chunkCoord);
            _chunkPool.Enqueue(chunk);
        }

        // 帧调用
        private void UpdateChunks()
        {
            Vector3 playerPos = _playerTransform ? _playerTransform.position : _playerCamera.transform.position;
            Vector3Int curChunkCoord = WorldPosToChunkCoord(playerPos);
            UpdateFrustumPlanes();   

            int totalJobs = (2 * _maxChunkCountPerAxis + 1) * (2 * _maxChunkCountPerAxis + 1) * (2 * _maxChunkCountPerAxis + 1);

            using (NativeArray<ChunkVisibilityResult> results = new NativeArray<ChunkVisibilityResult>(totalJobs, Allocator.TempJob))
            {
                ChunkVisibilityJob job = new ChunkVisibilityJob
                {
                    CurChunkCoord = math.int3(curChunkCoord.x, curChunkCoord.y, curChunkCoord.z),
                    ChunkCountPerAxis = _maxChunkCountPerAxis,
                    PlayerPos = playerPos,
                    SqrMaxDistance = _sqrMaxDistance,
                    FrustumPlanes = _frustumPlanes,   
                    Size = chunkSize,
                    Results = results
                };
            
                JobHandle jobHandle = job.Schedule(totalJobs, 256);
                jobHandle.Complete();
            
                foreach (var result in results)
                {
                    if (result.IsVisible)
                    {
                        _visibleChunks.Add(new Vector3Int(result.ChunkCoord.x, result.ChunkCoord.y, result.ChunkCoord.z));
                    }
                }
            }

            foreach (var kvp in _activeChunks)
                if (!_visibleChunks.Contains(kvp.Key))
                    _chunksToUnLoad.Add(kvp.Key);
            foreach (var coord in _chunksToUnLoad)
                UnloadChunk(coord);
            foreach (var coord in _visibleChunks)
                if (!_activeChunks.ContainsKey(coord))
                    LoadChunk(coord);

            _visibleChunks.Clear();
            _chunksToUnLoad.Clear();

            // 已加载的脏坐标本帧直接重网格化;卸载中的脏坐标保留标记,下次加载时再生。
            foreach (var coord in _dirtyChunks)
                if (_activeChunks.TryGetValue(coord, out Chunk chunk))
                    GenerateChunk(coord, chunk);
            _dirtyChunks.RemoveWhere(coord => _activeChunks.ContainsKey(coord));

            // 逐出最久未用的非活动块,把缓存压到容量内
            _cache.Trim(cacheCapacity, _activeChunks.Keys);
        }
        
        private void OnDestroy()
        {
            if (_frustumPlanes.IsCreated) _frustumPlanes.Dispose();
            _marchingCubes?.Release();
            _cache?.Dispose();
        }
    }
}
