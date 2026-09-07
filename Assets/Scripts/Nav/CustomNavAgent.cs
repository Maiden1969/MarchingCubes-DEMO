using System.Collections.Generic;
using UnityEngine;

namespace Nav
{
    /// <summary>
    /// 每敌人一份的路径跟随组件:向 manager 取路径(唯一入口 TryGetPath),
    /// 沿路点移动(加速度/转向/到达减速),并处理失败重试、卡死重寻与路径失效。
    /// 只做路径消费者:图与 A* 全部在 CustomNavManager。
    /// </summary>
    public class CustomNavAgent : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float speed = 4f;
        [SerializeField] private float acceleration = 12f;          // 速度变化率(加速/减速共用)
        [SerializeField] private float angularSpeed = 360f;         // 度/秒
        [SerializeField] private float waypointRadius = 0.8f;       // 中间路点到达判定(提前转向)
        [SerializeField] private float stoppingDistance = 0.5f;     // 最终路点停止距离

        [Header("Repath")]
        [SerializeField] private float targetMoveThreshold = 1f;    // 目标移动超过才重寻
        [SerializeField] private float repathInterval = 0.5f;       // 失败重试/卡死重寻的最小间隔
        [SerializeField] private float stuckTime = 1.5f;            // 有路径却持续无进展的判定时长

        private CustomNavManager _manager;
        private readonly List<Vector3> _path = new();
        private int _curPathIndex;
        private Vector3 _destination;
        private bool _hasTarget;
        private bool _arrived;              // 已到达终点:停止移动,保留目标
        private Vector3 _curVelocity;
        private float _repathTimer;         // 距下次允许重寻的倒计时(节流)
        private float _stuckTimer;
        private Vector3 _lastProgressPos;

        private void Start()
        {
            _manager = CustomNavManager.Instance;
            if (_manager)
                _manager.OnChunkNavMeshRebuild += InvalidatePath;
        }

        private void Update()
        {
            if (!_manager) return;
            _repathTimer -= Time.deltaTime;
            FollowPath();
            UpdateRepath();
        }

        private void OnDestroy()
        {
            if (_manager) _manager.OnChunkNavMeshRebuild -= InvalidatePath;
        }

        // ---------------------------------------------------------- 对外接口

        public void SetDestination(Vector3 dest)
        {
            bool needRepath = 
                !_hasTarget
                || Vector3.Distance(dest, _destination) > targetMoveThreshold
                || (_arrived && Vector3.Distance(dest, transform.position) > stoppingDistance);
            _hasTarget = true;
            _destination = dest;
            if (needRepath) Repath(true);      // 新目标:无视节流立即重寻
        }

        /// <summary>
        /// 路径失效(如地形被挖、导航重建)。由 manager 的事件接线调用;
        /// 立刻允许重寻——敌人少时无所谓,敌人多时应由 manager 层错峰后调用。
        /// </summary>
        public void InvalidatePath(Vector3Int chunkCoord)
        {
            if (!_hasTarget) return;
            _arrived = false;
            _curPathIndex = _path.Count;   // 视为无路径 → UpdateRepath 周期重寻
            _repathTimer = 0f;
        }

        // ---------------------------------------------------------- 取路径
        
        private bool TryGetPath(Vector3 from, Vector3 to)
        {
            return _manager.FindPath(from, to, _path);
        }

        private void Repath(bool ignoreCooldown)
        {
            if (!_manager || (!ignoreCooldown && _repathTimer > 0f)) return;
            _repathTimer = repathInterval;
            _curVelocity = Vector3.zero;
            _stuckTimer = 0f;
            _lastProgressPos = transform.position;

            if (!TryGetPath(transform.position, _destination))
            {
                _curPathIndex = _path.Count;   // 无路径:待机, UpdateRepath 周期重试
                _arrived = false;
                return;
            }
            if (_path.Count <= 1)
            {
                Arrive();                      // 已到达
                return;
            }
            _curPathIndex = 1;                 // path[0] 是起点吸附点,跳过
            _arrived = false;
        }

        // ---------------------------------------------------------- 路径跟随

        private void FollowPath()
        {
            if (!_hasTarget || _arrived) return;
            if (_curPathIndex >= _path.Count) return;   // 无路径:待机(等 UpdateRepath 重试)

            Vector3 waypoint = _path[_curPathIndex];
            bool isLast = _curPathIndex == _path.Count - 1;
            float reachRadius = isLast ? stoppingDistance : waypointRadius;

            Vector3 delta = waypoint - transform.position;
            float dist = delta.magnitude;
            if (dist <= reachRadius)
            {
                if (isLast) { Arrive(); return; }
                _curPathIndex++;               // 到达中间路点:转向下一个
                return;
            }

            Vector3 direction = delta / dist;

            // 
            Quaternion targetRot = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, angularSpeed * Time.deltaTime);

            // 目标速度:最后一段线性减速(在 stoppingDistance 处恰好减到 0),避免冲过终点
            float targetSpeed = speed;
            if (isLast)
                targetSpeed = speed * Mathf.Clamp01((dist - stoppingDistance) / stoppingDistance);

            Vector3 desired = direction * targetSpeed;
            _curVelocity = Vector3.MoveTowards(_curVelocity, desired, acceleration * Time.deltaTime);
            transform.position += _curVelocity * Time.deltaTime;

            if (!isLast) UpdateStuck();        // 终点减速段不算卡死
        }

        private void Arrive()
        {
            _arrived = true;
            _curVelocity = Vector3.zero;
            _curPathIndex = _path.Count;
        }

        // ---------------------------------------------------------- 兜底

        // 卡死检测:有路径但几乎无进展 → 强制重寻(挖洞制造的死路靠这个兜底)
        private void UpdateStuck()
        {
            float moved = (transform.position - _lastProgressPos).magnitude;
            if (moved > speed * 0.05f * Time.deltaTime)
            {
                _stuckTimer = 0f;
                _lastProgressPos = transform.position;
            }
            else
            {
                _stuckTimer += Time.deltaTime;
                if (_stuckTimer >= stuckTime)
                {
                    _stuckTimer = 0f;
                    Repath(false);             // 受 repathInterval 节流
                }
            }
        }

        // 无路径时按 repathInterval 周期重试(寻路失败/导航覆盖未就绪的兜底)
        private void UpdateRepath()
        {
            if (!_hasTarget || _arrived) return;
            if (_curPathIndex >= _path.Count && _repathTimer <= 0f)
                Repath(false);
        }
    }
}
