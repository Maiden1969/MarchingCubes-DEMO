using UnityEngine;

namespace Sdf
{
    public class SdfHalfCylinderWorldPart : SdfWorldPart
    {
        public float height = 2f;
        public float radius = 0.5f;
        public Vector3 cutNormal = Vector3.down;
        public float cutOffset = 0f;

        public override SdfShape Shape => new SdfHalfCylinder
        {
            top = transform.position + transform.up * height * 0.5f,
            down = transform.position - transform.up * height * 0.5f,
            radius = radius,
            cutPoint = transform.position + transform.up * cutOffset,
            normal = transform.rotation * cutNormal,
        };

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;

            Mesh mesh = CylinderGizmoMesh();
            if (mesh != null)
            {
                Vector3 scale = new Vector3(radius / 0.5f, height / 2f, radius / 0.5f);
                Gizmos.DrawWireMesh(mesh, transform.position, transform.rotation, scale);
            }

            Vector3 n = (transform.rotation * cutNormal).normalized;
            if (n.sqrMagnitude > 1e-8f)
            {
                Vector3 center = transform.position + transform.up * cutOffset;
                Vector3 b1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.forward).normalized * radius;
                Vector3 b2 = Vector3.Cross(n, b1).normalized * radius;
                const int seg = 48;
                Vector3 prev = center + b1;
                for (int i = 1; i <= seg; i++)
                {
                    float a = Mathf.PI * 2f * i / seg;
                    Vector3 cur = center + Mathf.Cos(a) * b1 + Mathf.Sin(a) * b2;
                    Gizmos.DrawLine(prev, cur);
                    prev = cur;
                }
                Gizmos.DrawLine(center, center + n * radius * 1.5f);
            }
        }

        private static Mesh _cylinderMesh;

        private static Mesh CylinderGizmoMesh()
        {
            if (_cylinderMesh == null)
                _cylinderMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            return _cylinderMesh;
        }
    }
}
