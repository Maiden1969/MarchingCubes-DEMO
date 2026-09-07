using System.Collections.Generic;
using Chunks;
using Sdf;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nav
{
    /// <summary>
    /// 导航网格节点:位于地形表面(SDF 等值面投影),带表面法线。法线来自 SDF
    /// 梯度,因此节点天然支持任意朝向——蜘蛛上墙、倒挂不需要六轴分解。
    /// </summary>
    public struct NavNode
    {
        public Vector3 pos;
        public Vector3 normal;
        public int3 cell;       // 全局导航格点坐标(世界网格,跨块唯一)
        // A* 查询临时状态,每次查询前按 _touched 重置。
        public int parent;
        public float g;
        public float f;
        public byte state;      // 0 = 未访问, 1 = open, 2 = closed
        public int heapIndex;
    }

    public struct NavCellOut
    {
        public float3 pos;
        public float3 normal;
    }

    /// <summary>
    /// 块内导航格点行进:扫 cells³ 个格(与 SDF 格点分解整数精确一致,块内格
    /// 只读本块格点值,相邻块共享格点平面 → 跨界无缝)。检出与等值面相交的格
    /// (minCorner &lt; 0 && maxCorner &gt;= 0,负 = 实体;"单边带"规则保证等值面
    /// 恰好压在某格点平面上时也只出一层节点),再把格中心沿法线投影到表面。
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct NavSurfaceJob : IJobParallelFor
    {
        [ReadOnly] public int3 chunkCoord;
        [ReadOnly] public float chunkSize;
        [ReadOnly] public int resolution;          // 每轴格点数 = cells + 1
        [ReadOnly] public NativeArray<float> values;
        public NativeArray<NavCellOut> cellsOut;
        public NativeArray<byte> flags;            // 1 = 表面格

        private float V(int lx, int ly, int lz) =>
            values[lz * resolution * resolution + ly * resolution + lx];

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

    /// <summary>
    /// 自研 SDF 导航网格管理器。节点按块坐标增量构建:订阅 ChunkManager.SdfChanged,
    /// 只重建被编辑块的导航数据(与地形网格的脏标记管线同构,几乎零成本)。
    /// A* 在全局节点图上寻路(26 邻接,邻接关系由格点查表隐式给出,无需维护边),
    /// 之后做 LOS 平滑(沿线段采样 SDF,|sdf| 需贴着表面,允许轻微切角但不许穿墙)。
    /// 右键点击地形查询路径(左键是挖洞),黄线为寻路结果。
    /// </summary>
    public class NavManager : MonoBehaviour
    {
        public static NavManager Instance { get; private set; }

        [Header("Query")]
        [SerializeField] private LayerMask terrainMask = 1 << 6;   // Generated

        [Header("Visualization")]
        [SerializeField] private bool showNodes = true;
        [SerializeField] private bool showAllNodes = false;   // 全量绘制所有节点(单次批绘制)
        [SerializeField] private bool showPath = true;
        [SerializeField] private float nodeDrawRadius = 25f;

        // LOS 平滑准则:线段全程距表面 ≤ LosMaxAir(空中)且切入实体 ≤ LosMaxSolid(角)。
        private const float LosStep = 0.5f;
        private const float LosMaxAir = 1.2f;
        private const float LosMaxSolid = 0.5f;
        private const int SnapRadius = 3;    // 起终点吸附的最大格数

        private static readonly int3[] NeighborOffsets = BuildOffsets();

        private readonly Dictionary<long, int> _cellToNode = new();     // 格点 key → 节点下标
        private readonly List<NavNode> _nodes = new();                  // 节点池
        private readonly Stack<int> _freeSlots = new();                 // 已释放节点下标
        private readonly Dictionary<Vector3Int, List<int>> _chunkNodes = new();  // 块 → 所属节点
        private readonly HashSet<Vector3Int> _dirtyNav = new();         // 待重建导航的块

        // A* 状态
        private readonly List<int> _touched = new();
        private readonly List<int> _heapIdx = new();
        private readonly List<float> _heapKey = new();
        private readonly List<Vector3> _rawPath = new();

        private readonly List<Vector3> _path = new();
        private Vector3 _target;
        private bool _hasTarget;
        private bool _rebuildHappened;
        private Vector3[] _allNodeLines;      // showAllNodes 的批绘制数组(每节点 2 顶点)
        private bool _allNodesDirty = true;

        private static int3[] BuildOffsets()
        {
            var list = new List<int3>(26);  
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                if (dx != 0 || dy != 0 || dz != 0)
                    list.Add(new int3(dx, dy, dz));
            return list.ToArray();
        }

        // 格点 → 长整型 key(范围 ±100000,当前 maxDistance 只用到 ±208)。
        private static long CellKey(int3 c) =>
            ((long)(c.x + 100000) << 40) | ((long)(c.y + 100000) << 20) | (long)(c.z + 100000);

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            ChunkManager.Instance.SdfChanged += OnSdfChanged;
        }

        private void OnDestroy()
        {
            if (ChunkManager.Instance != null) ChunkManager.Instance.SdfChanged -= OnSdfChanged;
        }

        private void OnSdfChanged(Vector3Int chunkCoord)
        {
            _dirtyNav.Add(chunkCoord);
        }

        private void Update()
        {
            if (_dirtyNav.Count > 0)
            {
                // 事件在 ChunkManager.Update 内触发,这里仅入队;统一在本帧重建,避免重入。
                var coords = new List<Vector3Int>(_dirtyNav);
                _dirtyNav.Clear();
                foreach (Vector3Int coord in coords) RebuildChunk(coord);
                _rebuildHappened = true;
                _allNodesDirty = true;
            }

            HandleQueryInput();

            // 地形被编辑后自动重查,保证画面上的路径始终有效。
            if (_rebuildHappened)
            {
                _rebuildHappened = false;
                if (_hasTarget && Camera.main != null)
                    QueryPath(Camera.main.transform.position, _target);
            }

            DrawPath();
        }

        /// <summary>
        /// 重建某块的导航节点:跑 Burst 表面行进 job,先移除该块旧节点再换入新节点。
        /// 邻接不存储——A* 展开时按格点查表,重建即自动重连。
        /// </summary>
        private void RebuildChunk(Vector3Int chunkCoord)
        {
            SdfField field = ChunkManager.Instance.GetSdfField(chunkCoord);

            if (_chunkNodes.TryGetValue(chunkCoord, out List<int> owned))
            {
                foreach (int i in owned)
                {
                    _cellToNode.Remove(CellKey(_nodes[i].cell));
                    _freeSlots.Push(i);
                }
                owned.Clear();
            }
            else
            {
                owned = new List<int>();
                _chunkNodes[chunkCoord] = owned;
            }

            int cells = field.Resolution - 1;
            int cellCount = cells * cells * cells;

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

                    int n2 = cells * cells;
                    int3 local = new int3(i % cells, (i / cells) % cells, i / n2);
                    int idx = AllocNode();
                    NavNode node = _nodes[idx];
                    node.pos = outs[i].pos;
                    node.normal = outs[i].normal;
                    node.cell = field.ChunkCoord * cells + local;
                    node.state = 0;
                    _nodes[idx] = node;
                    _cellToNode[CellKey(node.cell)] = idx;
                    owned.Add(idx);
                }
            }
        }

        private int AllocNode()
        {
            if (_freeSlots.Count > 0) return _freeSlots.Pop();
            int idx = _nodes.Count;
            _nodes.Add(default);
            return idx;
        }

        // ------------------------------------------------------------------ A*

        /// <summary>寻路:起终点吸附到最近节点 → A* → LOS 平滑。结果写入 path(世界坐标)。</summary>
        public bool FindPath(Vector3 from, Vector3 to, List<Vector3> path)
        {
            path.Clear();
            if (!SnapToNode(from, out int start) || !SnapToNode(to, out int goal)) return false;
            if (start == goal)
            {
                path.Add(to);
                return true;
            }

            ResetQueryState();
            Vector3 goalPos = _nodes[goal].pos;

            NavNode sn = _nodes[start];
            sn.state = 1;
            sn.g = 0f;
            sn.f = Vector3.Distance(sn.pos, goalPos);
            sn.parent = -1;
            _nodes[start] = sn;
            _touched.Add(start);
            HeapPush(start, sn.f);

            bool found = false;
            while (_heapIdx.Count > 0)
            {
                int cur = HeapPop();
                NavNode cn = _nodes[cur];
                if (cn.state == 2) continue;    // 过期堆条目
                cn.state = 2;
                _nodes[cur] = cn;
                _touched.Add(cur);
                if (cur == goal)
                {
                    found = true;
                    break;
                }

                int3 c = cn.cell;
                for (int k = 0; k < NeighborOffsets.Length; k++)
                {
                    if (!_cellToNode.TryGetValue(CellKey(c + NeighborOffsets[k]), out int ni)) continue;
                    NavNode nn = _nodes[ni];
                    if (nn.state == 2) continue;

                    float ng = cn.g + Vector3.Distance(cn.pos, nn.pos);
                    if (nn.state != 1 || ng < nn.g)
                    {
                        nn.parent = cur;
                        nn.g = ng;
                        nn.f = ng + Vector3.Distance(nn.pos, goalPos);
                        if (nn.state != 1)
                        {
                            nn.state = 1;
                            _nodes[ni] = nn;
                            _touched.Add(ni);
                            HeapPush(ni, nn.f);
                        }
                        else
                        {
                            _nodes[ni] = nn;
                            HeapDecrease(ni, nn.heapIndex, nn.f);
                        }
                    }
                }
            }

            if (!found) return false;

            _rawPath.Clear();
            int p = goal;
            while (p != -1)
            {
                _rawPath.Add(_nodes[p].pos);
                p = _nodes[p].parent;
            }
            _rawPath.Reverse();

            Smooth(_rawPath, path);
            return true;
        }

        // 查询残留状态清零(堆 + 本查询摸过的节点)。
        private void ResetQueryState()
        {
            foreach (int i in _touched)
            {
                NavNode n = _nodes[i];
                n.state = 0;
                _nodes[i] = n;
            }
            _touched.Clear();
            _heapIdx.Clear();
            _heapKey.Clear();
        }

        // 世界坐标 → 全局导航格点(与 SDF 格点分解一致)。
        private int3 WorldToNavCell(Vector3 pos)
        {
            ChunkManager cm = ChunkManager.Instance;
            return (int3)math.floor((new float3(pos.x, pos.y, pos.z) + cm.HalfChunkSize) / cm.VoxelSize);
        }

        // 把点吸附到附近节点(半径 SnapRadius 格内取最近)。
        private bool SnapToNode(Vector3 pos, out int node)
        {
            node = -1;
            float best = float.MaxValue;
            int3 cell = WorldToNavCell(pos);
            for (int r = 0; r <= SnapRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (math.max(math.max(math.abs(dx), math.abs(dy)), math.abs(dz)) != r) continue;
                    if (!_cellToNode.TryGetValue(CellKey(cell + new int3(dx, dy, dz)), out int ni)) continue;
                    float d2 = (_nodes[ni].pos - pos).sqrMagnitude;
                    if (d2 < best)
                    {
                        best = d2;
                        node = ni;
                    }
                }
            }
            return node >= 0;
        }

        // 二叉堆(最小堆,按 f 值)。  
        private void HeapPush(int node, float f)
        {
            _heapIdx.Add(node);
            _heapKey.Add(f);
            int i = _heapIdx.Count - 1;
            SetHeapIndex(node, i);
            HeapBubbleUp(i);
        }

        private int HeapPop()
        {
            int root = _heapIdx[0];
            int last = _heapIdx.Count - 1;
            _heapIdx[0] = _heapIdx[last];
            _heapKey[0] = _heapKey[last];
            SetHeapIndex(_heapIdx[0], 0);
            _heapIdx.RemoveAt(last);
            _heapKey.RemoveAt(last);
            if (_heapIdx.Count > 1) HeapBubbleDown(0);
            return root;
        }

        private void HeapDecrease(int node, int heapIndex, float newKey)
        {
            _heapKey[heapIndex] = newKey;
            HeapBubbleUp(heapIndex);
        }

        private void HeapBubbleUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapKey[i] >= _heapKey[parent]) break;
                HeapSwap(i, parent);
                i = parent;
            }
        }

        private void HeapBubbleDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, smallest = i;
                if (l < _heapIdx.Count && _heapKey[l] < _heapKey[smallest]) smallest = l;
                if (r < _heapIdx.Count && _heapKey[r] < _heapKey[smallest]) smallest = r;
                if (smallest == i) break;
                HeapSwap(i, smallest);
                i = smallest;
            }
        }

        private void HeapSwap(int a, int b)
        {
            (_heapIdx[a], _heapIdx[b]) = (_heapIdx[b], _heapIdx[a]);
            (_heapKey[a], _heapKey[b]) = (_heapKey[b], _heapKey[a]);
            SetHeapIndex(_heapIdx[a], a);
            SetHeapIndex(_heapIdx[b], b);
        }

        private void SetHeapIndex(int node, int i)
        {
            NavNode n = _nodes[node];
            n.heapIndex = i;
            _nodes[node] = n;
        }

        // ---------------------------------------------------------------- 平滑

        // 贪心 LOS:从当前路点找最远可见路点跳过去。可见 = 线段全程贴表面。
        private void Smooth(List<Vector3> raw, List<Vector3> smooth)
        {
            smooth.Clear();
            if (raw.Count == 0) return;
            smooth.Add(raw[0]);
            int i = 0;
            while (i < raw.Count - 1)
            {
                int j = raw.Count - 1;
                for (; j > i + 1; j--)
                {
                    if (LosClear(raw[i], raw[j])) break;
                }
                smooth.Add(raw[j]);
                i = j;
            }
        }

        // 线段贴表面检查:密集采样 SDF,离表面太远或切入实体过深都不许。
        // 已知限制:极薄(&lt; 2×LosMaxSolid)的墙可能被对角穿越,当前地形尺度下不构成问题。
        private bool LosClear(Vector3 a, Vector3 b)
        {
            ChunkManager cm = ChunkManager.Instance;
            if (cm == null) return false;
            float dist = Vector3.Distance(a, b);
            int steps = Mathf.CeilToInt(dist / LosStep);
            for (int s = 1; s < steps; s++)
            {
                float d = cm.Sample(Vector3.Lerp(a, b, s / (float)steps));
                if (d > LosMaxAir || d < -LosMaxSolid) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 查询与可视化

        // 右键:从相机位置到射线命中点寻路。
        private void HandleQueryInput()
        {
            Camera cam = Camera.main;
            if (cam == null || Mouse.current == null) return;
            if (!Mouse.current.rightButton.wasPressedThisFrame) return;

            Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, 500f);
            Physics.queriesHitBackfaces = backfaces;
            if (!hit) return;

            _target = hitInfo.point;
            QueryPath(cam.transform.position, _target);
        }

        /// <summary>查询并缓存路径;结果同时用于画面绘制。</summary>
        public void QueryPath(Vector3 from, Vector3 to)
        {
            _path.Clear();
            if (FindPath(from, to, _path))
            {
                _hasTarget = true;
                _target = to;
                Debug.Log($"Nav: 路径找到,{_path.Count} 个路点,总长 {PathLength():F1}");
            }
            else
            {
                Debug.LogWarning("Nav: 未找到路径(起点或目标附近没有导航节点?)");
            }
        }

        private float PathLength()
        {
            float sum = 0f;
            for (int i = 0; i < _path.Count - 1; i++) sum += Vector3.Distance(_path[i], _path[i + 1]);
            return sum;
        }

        private void DrawPath()
        {
            if (!showPath || _path.Count < 2) return;
            for (int i = 0; i < _path.Count - 1; i++)
                Debug.DrawLine(_path[i], _path[i + 1], Color.yellow);
            Debug.DrawLine(_path[0], _path[0] + Vector3.up * 2f, Color.green);
            Debug.DrawLine(_path[_path.Count - 1], _path[_path.Count - 1] + Vector3.up * 2f, Color.red);
        }

        private void OnDrawGizmos()
        {
            if (showAllNodes)
            {
                // 全量模式:所有表面节点一次 DrawLineList 批绘制(数千条线 = 一次调用)。
                // 数组只在导航变化后按需重建;节点池的已释放槽位不在 _cellToNode 里,不会画出幽灵节点。
                if (_allNodesDirty || _allNodeLines == null || _allNodeLines.Length != _cellToNode.Count * 2)
                {
                    _allNodeLines = new Vector3[_cellToNode.Count * 2];
                    int i = 0;
                    foreach (KeyValuePair<long, int> kv in _cellToNode)
                    {
                        NavNode n = _nodes[kv.Value];
                        _allNodeLines[i++] = n.pos;
                        _allNodeLines[i++] = n.pos + n.normal * 0.45f;
                    }
                    _allNodesDirty = false;
                }
                Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
                Gizmos.DrawLineList(_allNodeLines);
            }
            else if (showNodes)
            {
                Camera cam = Camera.current;
                if (cam == null) return;
                Vector3 camPos = cam.transform.position;
                float r2 = nodeDrawRadius * nodeDrawRadius;

                Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
                foreach (KeyValuePair<long, int> kv in _cellToNode)
                {
                    NavNode n = _nodes[kv.Value];
                    if ((n.pos - camPos).sqrMagnitude > r2) continue;
                    Gizmos.DrawLine(n.pos, n.pos + n.normal * 0.45f);
                }
            }

            if (_hasTarget)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(_target, 0.7f);
            }
        }
    }
}
