using UnityEngine;
using UnityEngine.InputSystem;

namespace Chunks
{
    public class FlyCamera : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 20f;
        [SerializeField] private float lookSpeed = 2f;

        private float _pitch;

        private void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
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
        }
    }
}
