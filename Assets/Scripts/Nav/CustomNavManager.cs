using System;
using System.Collections.Generic;
using Chunks;
using Sdf;
using Unity.Collections;
using Unity.Jobs;
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
            var top = _heap[0];
            var size = _heap.Count - 1;
            (_heap[0], _heap[size]) = (_heap[size], _heap[0]);
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
                    values = field.values,
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