using UnityEngine;

namespace Sdf
{
    public class SdfCapsuleWorldPart : SdfWorldPart
    {
        public float height = 2f;
        public float radius = 0.5f;

        public override SdfShape Shape => new SdfCapsule
        {
            top = transform.position + transform.up * height * 0.5f,
            down = transform.position - transform.up * height * 0.5f,
            radius = radius,
        };

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Mesh mesh = CapsuleGizmoMesh();
            if (mesh != null)
            {
                Vector3 scale = new Vector3(radius / 0.5f, height / 2f, radius / 0.5f);
                Gizmos.DrawWireMesh(mesh, transform.position, transform.rotation, scale);
                return;
            }
            
            Vector3 up = transform.up;
            Vector3 top = transform.position + up * (height * 0.5f);
            Vector3 down = transform.position - up * (height * 0.5f);
            Gizmos.DrawWireSphere(top, radius);
            Gizmos.DrawWireSphere(down, radius);
            Vector3 side = Vector3.Cross(up, Mathf.Abs(up.y) < 0.99f ? Vector3.up : Vector3.forward).normalized * radius;
            Vector3 side2 = Vector3.Cross(up, side).normalized * radius;
            Gizmos.DrawLine(top + side, down + side);
            Gizmos.DrawLine(top - side, down - side);
            Gizmos.DrawLine(top + side2, down + side2);
            Gizmos.DrawLine(top - side2, down - side2);
        }

        private static Mesh _capsuleMesh;

        private static Mesh CapsuleGizmoMesh()
        {
            if (_capsuleMesh == null)
                _capsuleMesh = Resources.GetBuiltinResource<Mesh>("Capsule.fbx");
            return _capsuleMesh;
        }
    }
}