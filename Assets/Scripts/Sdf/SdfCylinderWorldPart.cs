using UnityEngine;

namespace Sdf
{
    public class SdfCylinderWorldPart : SdfWorldPart
    {
        public float height = 2f;
        public float radius = 0.5f;

        public override SdfShape Shape => new SdfCylinder
        {
            top = transform.position + transform.up * height * 0.5f,
            down = transform.position - transform.up * height * 0.5f,
            radius = radius,
        };

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Mesh mesh = CylinderGizmoMesh();
            if (mesh != null)
            {
                Vector3 scale = new Vector3(radius / 0.5f, height / 2f, radius / 0.5f);
                Gizmos.DrawWireMesh(mesh, transform.position, transform.rotation, scale);
                return;
            }

            Vector3 up = transform.up;
            Vector3 top = transform.position + up * (height * 0.5f);
            Vector3 down = transform.position - up * (height * 0.5f);
            Vector3 side = Vector3.Cross(up, Mathf.Abs(up.y) < 0.99f ? Vector3.up : Vector3.forward).normalized * radius;
            Vector3 side2 = Vector3.Cross(up, side).normalized * radius;
            Gizmos.DrawWireSphere(top, radius);
            Gizmos.DrawWireSphere(down, radius);
            Gizmos.DrawLine(top + side, down + side);
            Gizmos.DrawLine(top - side, down - side);
            Gizmos.DrawLine(top + side2, down + side2);
            Gizmos.DrawLine(top - side2, down - side2);
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
