using UnityEngine;

namespace Sdf
{
    public class SdfSphereWorldPart : SdfWorldPart
    {
        public float radius = 1.0f;

        public override SdfShape Shape => new SdfSphere
        {
            center = transform.position,
            radius = radius,
        };

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}