using System.Collections.Generic;
using UnityEngine;

namespace Nav
{
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
        
        /// 路径失效(如地形被挖、导航重建)。由 manager 的事件接线调用;
        /// 立刻允许重寻——敌人少时无所谓,敌人多时应由 manager 层错峰后调用。
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

        // 重寻路
        private void Repath(bool ignoreCooldown)
        {
            if (!_manager || (!ignoreCooldown && _repathTimer > 0f)) return;
            _repathTimer = repathInterval;
            Vector3 moveDir = _curVelocity;      // 清零前捕获运动方向,供跳过守卫使用
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

            // 跳过身后/已越过的路点(path[0] 是起点吸附点,固定跳过)
            _curPathIndex = 1;
            AdvancePastWaypoints(moveDir);
            _arrived = false;
        }

        /// 推进 _curPathIndex 越过 agent 已经过掉的路点。
        /// "已越过" = 过了该路点沿下一段方向的垂直平面,且该路点不在当前运动方向前方。
        /// 第二个条件是守卫:链折痕处(折痕/碗沿/接触带,下一段往回折 >90°)垂直平面测试
        /// 会把还没到的前方路点误判成已越过,反而跳过前方、锚到身后路点(折返跑)。
        /// moveDir 为 0(起步/急停)时退化为纯平面测试。
        private void AdvancePastWaypoints(Vector3 moveDir)
        {
            Vector3 pos = transform.position;
            while (_curPathIndex < _path.Count - 1)
            {
                Vector3 wk = _path[_curPathIndex];
                Vector3 wk1 = _path[_curPathIndex + 1];
                bool pastPlane = Vector3.Dot(wk1 - wk, pos - wk) >= 0f;
                bool aheadOfMotion = Vector3.Dot(wk - pos, moveDir) > 0f;
                if (pastPlane && !aheadOfMotion) _curPathIndex++;
                else break;
            }
        }

        // ---------------------------------------------------------- 路径跟随

        private void FollowPath()
        {
            if (!_hasTarget || _arrived) return;
            if (_curPathIndex >= _path.Count) return;   // 无路径:待机(等 UpdateRepath 重试)

            // 每帧先跳过已越过的路点:高速/掉帧(如挖坑时 MeshCollider 重烘焙卡顿)
            // 一帧跨过多个路点、或陡坡压缩链(相邻节点间距 < waypointRadius)时,到达
            // 判定只加 1 会让下一帧回头追身后的路点(折返跑),这里一次推进到位。
            AdvancePastWaypoints(_curVelocity);

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

        // 卡死检测:有路径但几乎无进展 → 强制重寻
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

        // 无路径时按 repathInterval 周期重试
        private void UpdateRepath()
        {
            if (!_hasTarget || _arrived) return;
            if (_curPathIndex >= _path.Count && _repathTimer <= 0f)
                Repath(false);
        }
    }
}
