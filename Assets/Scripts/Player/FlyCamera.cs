using Nav;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Chunks
{
    public class FlyCamera : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 20f;
        [SerializeField] private float lookSpeed = 2f;

        [Header("Interaction")]
        [SerializeField] private float carveRadius = 6f;      // 左键挖球半径
        [SerializeField] private CustomNavAgent navAgent;       // 右键导航目标(可留空,Start 自动查找)

        private Camera _camera;
        private ChunkManager _chunkManager;
        private float _pitch;

        private void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            _camera = GetComponent<Camera>() ?? Camera.main;
            _chunkManager = ChunkManager.Instance;
            if (!navAgent) navAgent = FindAnyObjectByType<CustomNavAgent>();
        }

        private void Update()
        {
            if (Keyboard.current == null || Mouse.current == null) return;

            Vector2 delta = Mouse.current.delta.ReadValue();
            _pitch = Mathf.Clamp(_pitch - delta.y * lookSpeed * 0.1f, -89f, 89f);
            transform.localRotation = Quaternion.Euler(_pitch,
                transform.localEulerAngles.y + delta.x * lookSpeed * 0.1f, 0f);

            Vector3 move = Vector3.zero;
            if (Keyboard.current.wKey.isPressed) move += transform.forward;
            if (Keyboard.current.sKey.isPressed) move -= transform.forward;
            if (Keyboard.current.dKey.isPressed) move += transform.right;
            if (Keyboard.current.aKey.isPressed) move -= transform.right;
            if (Keyboard.current.spaceKey.isPressed) move += Vector3.up;
            if (Keyboard.current.leftCtrlKey.isPressed) move -= Vector3.up;

            float speed = moveSpeed * (Keyboard.current.leftShiftKey.isPressed ? 3f : 1f);
            transform.position += move.normalized * (speed * Time.deltaTime);

            HandleInteraction();
        }

        // 地形射线(背面命中开启: 挖出的碗形内壁/外壁都可点击),左右键共用。
        private bool TryRaycastTerrain(out RaycastHit hit)
        {
            hit = default;
            if (!_camera || !_chunkManager) return false;

            Ray ray = _camera.ScreenPointToRay(Mouse.current.position.ReadValue());
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool didHit = Physics.Raycast(ray, out hit, _chunkManager.MaxDistance);
            Physics.queriesHitBackfaces = backfaces;
            return didHit;
        }

        private void HandleInteraction()
        {
            // 左键: 挖球
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                if (TryRaycastTerrain(out RaycastHit hit))
                    _chunkManager.CarveAt(hit.point, carveRadius);
            }

            // 右键: 让导航 agent 走到击中点
            if (Mouse.current.rightButton.wasPressedThisFrame && navAgent)
            {
                if (TryRaycastTerrain(out RaycastHit hit))
                    navAgent.SetDestination(hit.point);
            }
        }
    }
}
