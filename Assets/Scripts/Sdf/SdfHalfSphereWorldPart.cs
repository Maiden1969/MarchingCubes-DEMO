using UnityEngine;

namespace Sdf
{
    public class SdfHalfSphereWorldPart : SdfWorldPart
    {
        public float radius = 1.0f;

        // 半球朝 transform.up 隆起;剖切面过球心,法线指向被切掉的一侧(-up)。
        // 旋转物体即可调整半球朝向。
        public override SdfShape Shape => new SdfHalfSphere
        {
            center = transform.position,
            radius = radius,
            normal = -transform.up,
        };

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, radius);

            // 剖切圆盘(过球心、垂直于 up)+ 指向被切掉一侧的箭头
            Vector3 up = transform.up;
            Vector3 b1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.99f ? Vector3.up : Vector3.forward).normalized * radius;
            Vector3 b2 = Vector3.Cross(up, b1).normalized * radius;
            const int seg = 48;
            Vector3 prev = transform.position + b1;
            for (int i = 1; i <= seg; i++)
            {
                float a = Mathf.PI * 2f * i / seg;
                Vector3 cur = transform.position + Mathf.Cos(a) * b1 + Mathf.Sin(a) * b2;
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
            Gizmos.DrawLine(transform.position, transform.position - up * radius * 1.5f);
        }
    }
}
