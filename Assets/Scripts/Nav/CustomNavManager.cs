using System;
using System.Collections.Generic;
using Chunks;
using DS;
using Sdf;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Nav
{
    public class PriorityQueue
    {
        private struct Pair
        {
            public int element;
            public float priority;

            public Pair(int element, float priority)
            {
                this.element = element;
                this.priority = priority;
            }
        }
        private readonly List<Pair> _heap = new();
        public int Count => _heap.Count - 1;
        
        public PriorityQueue()
        {
            _heap.Add(new Pair(0, float.PositiveInfinity));
        }
        
        public void Push(int element, float priority)
        {
            _heap.Add(new Pair(element, priority));
            BubbleUp(_heap.Count - 1);
        }

        public int Pop()
        {
            var top = _heap[1];
            var size = _heap.Count - 1;
            (_heap[1], _heap[size]) = (_heap[size], _heap[1]);
            _heap.RemoveAt(size);
            BubbleDown(1);
            return top.element;
        }

        private void BubbleUp(int n)
        {
            for (int i = n; i > 1 && _heap[i / 2].priority > _heap[i].priority; i /= 2)
            {
                (_heap[i], _heap[i / 2]) = (_heap[i / 2], _heap[i]);
            }
        }

        // 找到较小的子节点(只比较两个子节点)
        private int Son(int n)
        {
            int l = n * 2, r = l + 1, smallest = l;
            if (r < _heap.Count && _heap[r].priority < _heap[l].priority) smallest = r;
            return smallest;
        }
        
        private void BubbleDown(int n)
        {
            for (int i = n, t = Son(i); t < _heap.Count && _heap[t].priority < _heap[i].priority; i = t, t = Son(i))
            {
                (_heap[i], _heap[t]) = (_heap[t], _heap[i]);
            }
        }
    }
    
    public struct CustomNavNode
    {
        public Vector3Int chunkCoord;
        public int cellIndex;
        public Vector3 position;
        public Vector3 normal;
    }

    public struct NavCellOut
    {
        public float3 pos;
        public float3 normal;
    }
    
    /// 块内导航格点行进:扫 cells³ 个格(与 SDF 格点分解整数精确一致,块内格
    /// 只读本块格点值,相邻块共享格点平面 → 跨界无缝)。检出与等值面相交的格
    /// (minCorner &lt; 0 && maxCorner &gt;= 0,负 = 实体;"单边带"规则保证等值面
    /// 恰好压在某格点平面上时也只出一层节点),再把格中心沿法线投影到表面。
    /// 场为稀疏砖存储:缺砖格无表面直接跳过;表面格的梯度 stencil(±1 格)
    /// 由 halo(4 格)保证落在落盘砖内,值精确。
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct NavSurfaceJob : IJobParallelFor
    {
        [ReadOnly] public int3 chunkCoord;
        [ReadOnly] public float chunkSize;
        [ReadOnly] public int resolution;          // 每轴格点数 = cells + 1
        [ReadOnly] public NativeArray<uint> indirection;
        [ReadOnly] public NativeArray<half> brickData;
        [ReadOnly] public int bricksPerAxis;
        public NativeArray<NavCellOut> cellsOut;
        public NativeArray<byte> flags;            // 1 = 表面格

        // 格点值:砖内直接取;缺砖按槽内符号默认深值(±MaxValue,避免 lerp 出 NaN;
        // 缺砖格会提前跳过,此分支仅为安全兜底)。
        private float V(int lx, int ly, int lz)
        {
            uint id = indirection[BrickGrid.SlotOf(lx, ly, lz, bricksPerAxis)];
            if (id >= BrickGrid.AbsentSolid)
                return id == BrickGrid.AbsentAir ? float.MaxValue : float.MinValue;
            int bx = math.min(lx >> 3, bricksPerAxis - 1);
            int by = math.min(ly >> 3, bricksPerAxis - 1);
            int bz = math.min(lz >> 3, bricksPerAxis - 1);
            int ox = lx - bx * BrickGrid.BrickSize;
            int oy = ly - by * BrickGrid.BrickSize;
            int oz = lz - bz * BrickGrid.BrickSize;
            int bid = (int)id;
            return brickData[bid * BrickGrid.BrickValueCount + ox + oy * BrickGrid.BrickValuesPerAxis
                + oz * BrickGrid.BrickValuesPerAxis * BrickGrid.BrickValuesPerAxis];
        }

        // 局部格点坐标三线性采样,越界钳制(边界格的梯度因此近似;跨界连续性
        // 不受影响:投影位置用块内值计算,且采样坐标严格在块内)。
        private float Trilinear(float3 p)
        {
            int cells = resolution - 1;
            p = math.clamp(p, 0f, (float)cells);
            int3 lo = (int3)math.floor(p);
            int3 hi = math.min(lo + 1, cells);
            float3 f = p - lo;

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

        public void Execute(int index)
        {
            int cells = resolution - 1;
            int n2 = cells * cells;
            int z = index / n2;
            int y = (index - z * n2) / cells;
            int x = index - z * n2 - y * cells;

            // 缺砖格: 无表面,直接跳过(表面格必在其所在砖内)。
            uint cellBrick = indirection[(x >> 3) + bricksPerAxis * ((y >> 3) + bricksPerAxis * (z >> 3))];
            if (cellBrick >= BrickGrid.AbsentSolid)
            {
                flags[index] = 0;
                return;
            }

            float minV = float.MaxValue, maxV = float.MinValue;
            for (int dz = 0; dz <= 1; dz++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                float v = V(x + dx, y + dy, z + dz);
                minV = math.min(minV, v);
                maxV = math.max(maxV, v);
            }

            if (!(minV < 0f && maxV >= 0f))
            {
                flags[index] = 0;
                return;
            }

            flags[index] = 1;
            float voxelSize = chunkSize / cells;
            float halfSize = chunkSize / 2;

            float3 center = new float3(x + 0.5f, y + 0.5f, z + 0.5f);
            float d = Trilinear(center);
            float3 grad = new float3(
                Trilinear(center + new float3(1f, 0f, 0f)) - Trilinear(center - new float3(1f, 0f, 0f)),
                Trilinear(center + new float3(0f, 1f, 0f)) - Trilinear(center - new float3(0f, 1f, 0f)),
                Trilinear(center + new float3(0f, 0f, 1f)) - Trilinear(center - new float3(0f, 0f, 1f)));
            float len = math.length(grad);
            float3 n = len > 1e-6f ? grad / len : new float3(0f, 1f, 0f);

            float3 world = (float3)(chunkCoord * cells) * voxelSize + center * voxelSize - halfSize;
            cellsOut[index] = new NavCellOut { pos = world - n * d, normal = n };
        }
    }
    
    public class CustomNavManager : MonoBehaviour
    {
        [Header("Nav")] 
        public int snapRadius = 5;
        [Header("Debug")] 
        public bool showNodes;
        public static CustomNavManager Instance;
        private ChunkManager _chunkManager;
        private readonly List<CustomNavNode> _nodes = new ();
        private readonly Dictionary<Vector3Int, int[]> _chunkNodes = new();
        private readonly Stack<int> _freeNodes = new ();

        public Action<Vector3Int> OnChunkNavMeshRebuild;

        private static Vector3Int[] NeighborOffsets = 
        {
            // Z = -1 层
            new (-1, -1, -1), new (0, -1, -1), new (1, -1, -1),
            new (-1,  0, -1), new (0,  0, -1), new (1,  0, -1),
            new (-1,  1, -1), new (0,  1, -1), new (1,  1, -1),
            // Z = 0 层
            new (-1, -1,  0), new (0, -1,  0), new (1, -1,  0),
            new (-1,  0,  0), new (1,  0,  0),
            new (-1,  1,  0), new (0,  1,  0), new (1,  1,  0),
            // Z = 1 层
            new (-1, -1,  1), new (0, -1,  1), new (1, -1,  1),
            new (-1,  0,  1), new (0,  0,  1), new (1,  0,  1),
            new (-1,  1,  1), new (0,  1,  1), new (1,  1,  1),
        };

        private Vector3Int CellIndexToCoord(int idx)
        {
            int c = _chunkManager.CellResolution;
            return new Vector3Int(idx % c, (idx / c) % c, idx / (c*c));
        }
        
        private void Awake()
        {
            if (Instance && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            _chunkManager = ChunkManager.Instance;
            if (_chunkManager)
            {
                _chunkManager.SdfChanged += RebuildChunkNavMesh;
            }
        }

        private int AllocNode()
        {
            if (_freeNodes.Count > 0)
                return _freeNodes.Pop();
            
            int idx = _nodes.Count;
            _nodes.Add(default);
            return idx;
        }
        
        public void RebuildChunkNavMesh(Vector3Int chunkCoord)
        {
            
            if (!_chunkManager) return;
            
            int cellCount = _chunkManager.CellCount;
            if (!_chunkNodes.TryGetValue(chunkCoord, out int[] nodes))
            {
                nodes = new int[cellCount];
                _chunkNodes[chunkCoord] = nodes;
            }
            else
            {
                foreach (var idx in nodes)
                {
                    if (idx >= 0)
                        _freeNodes.Push(idx);
                }
            }
            Array.Fill(nodes, -1);
            
            SdfField field = _chunkManager.GetSdfField(chunkCoord);
            
            using (NativeArray<NavCellOut> outs = new(cellCount, Allocator.TempJob))
            using (NativeArray<byte> flags = new(cellCount, Allocator.TempJob))
            {
                NavSurfaceJob job = new()
                {
                    chunkCoord = field.ChunkCoord,
                    chunkSize = field.ChunkSize,
                    resolution = field.Resolution,
                    indirection = field.BrickIndirection,
                    brickData = field.BrickData,
                    bricksPerAxis = field.BricksPerAxis,
                    cellsOut = outs,
                    flags = flags,
                };
                job.Schedule(cellCount, 256).Complete();

                for (int i = 0; i < cellCount; i++)
                {
                    if (flags[i] == 0) continue;
                    
                    int idx = AllocNode();
                    CustomNavNode node = _nodes[idx];
                    node.position = outs[i].pos;
                    node.normal = outs[i].normal;
                    node.cellIndex = i;
                    node.chunkCoord = chunkCoord;
                    _nodes[idx] = node;
                    nodes[i] = idx;
                    _chunkNodes[chunkCoord][i] = idx;
                }
            }
            OnChunkNavMeshRebuild?.Invoke(field.ChunkCoord);
        }
        
        private bool SnapToNode(Vector3 point, out int node)
        {
            node = -1;
            if (!_chunkManager) return false;

            for (int r = 0; r <= snapRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int DX = Mathf.Abs(dx);
                    for (int dy = DX - r; dy <= r - DX; dy++)
                    {
                        int DY = Mathf.Abs(dy);
                        for (int j = 0, dz = r - DX - DY; j < 2; j++, dz = -dz)
                        {
                            Vector3 p = point + new Vector3(dx, dy, dz) * _chunkManager.CellSize;
                            var chunkCoord = _chunkManager.WorldPosToChunkCoord(p);
                            var cellIdx = _chunkManager.WorldPosToCellIndex(p);
                            if (_chunkNodes.TryGetValue(chunkCoord, out int[] cells) && cells[cellIdx] >= 0)
                            {
                                node = cells[cellIdx];
                                return true; 
                            }
                        }
                    }
                }
            }

            return false;
        }
        
        // A* 寻路
        public bool FindPath(Vector3 from, Vector3 to, List<Vector3> path)
        {
            path.Clear();
            if (!SnapToNode(from, out int start) || !SnapToNode(to, out int target))
                return false;
            if (start == target)
            {
                path.Add(_nodes[start].position);
                return true;
            }

            int c = _chunkManager.CellResolution;
            Vector3 goalPos = _nodes[target].position;
            var queue = new PriorityQueue();
            Dictionary<int, float> g = new();
            Dictionary<int, int> parent = new();
            HashSet<int> closed = new();
            g[start] = 0f;
            parent[start] = -1;
            queue.Push(start, Vector3.Distance(_nodes[start].position, goalPos));

            while (queue.Count > 0)
            {
                int cur = queue.Pop();
                if (closed.Contains(cur)) continue;          
                closed.Add(cur);
                
                if (cur == target)
                {
                    for (int n = target; n != -1; n = parent[n])
                        path.Add(_nodes[n].position);
                    path.Reverse();
                    return true;
                }

                Vector3Int chunk = _nodes[cur].chunkCoord;
                Vector3Int local = CellIndexToCoord(_nodes[cur].cellIndex);
                for (int i = 0; i < 26; ++i)
                {
                    Vector3Int off = NeighborOffsets[i];

                    // 越界分量折回 [0, c) 并向对应方向换块
                    int lx = local.x + off.x, cx = chunk.x;
                    if (lx < 0)       { lx += c; cx--; }
                    else if (lx >= c) { lx -= c; cx++; }
                    int ly = local.y + off.y, cy = chunk.y;
                    if (ly < 0)       { ly += c; cy--; }
                    else if (ly >= c) { ly -= c; cy++; }
                    int lz = local.z + off.z, cz = chunk.z;
                    if (lz < 0)       { lz += c; cz--; }
                    else if (lz >= c) { lz -= c; cz++; }

                    if (!_chunkNodes.TryGetValue(new Vector3Int(cx, cy, cz), out int[] arr)) continue;
                    int ni = arr[lx + ly * c + lz * c * c];   // 邻居节点
                    if (ni < 0) continue;
                    if (closed.Contains(ni)) continue;

                    float ng = g[cur] + Vector3.Distance(_nodes[cur].position, _nodes[ni].position);
                    if (!g.TryGetValue(ni, out float oldG) || ng < oldG)
                    {
                        g[ni] = ng;
                        parent[ni] = cur;
                        queue.Push(ni, ng + Vector3.Distance(_nodes[ni].position, goalPos));
                    }
                }
            }
            return false;
        }

        private void Los(List<Vector3> path)
        {
            int len = path.Count;
            List<Vector3> newPath = new List<Vector3>();
            for (int i = 0; i < len; i++)
            {
                for (int j = len - 1; j > i; j--)
                {
                    
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (!showNodes) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
            Vector3[] lines = new Vector3[_nodes.Count * 2];
            for (int i = 0; i < _nodes.Count * 2; i += 2)
            {
                lines[i] = _nodes[i / 2].position;
                lines[i + 1] = _nodes[i / 2].position + _nodes[i / 2].normal * 0.5f;
            }
            Gizmos.DrawLineList(lines);
        }

        private void OnDestroy()
        {
            if (_chunkManager)
            {
                _chunkManager.SdfChanged -= RebuildChunkNavMesh;
            }
        }
    }
}