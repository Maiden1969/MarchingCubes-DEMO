using UnityEngine;
using UnityEngine.Rendering;


namespace Chunks
{
    /// <summary>
    /// Pooled mesh holder for one chunk. Carries no state: dirty tracking lives on the
    /// chunk COORDINATE in ChunkManager (the GameObject is reused across coords).
    /// </summary>
    public class Chunk : MonoBehaviour
    {
        private float _chunkSize;
        private MeshFilter _mf;
        private MeshCollider _mc;
        private MeshRenderer _mr;

        public void Init(float chunkSize, MeshFilter mf, MeshCollider mc, MeshRenderer mr, Material material, LayerMask mask)
        {
            _chunkSize = chunkSize;
            _mf = mf;
            _mc = mc;
            _mr = mr;
            _mr.sharedMaterial = material;
            _mr.shadowCastingMode = ShadowCastingMode.TwoSided;
        }

        public void SetMesh(Mesh mesh)
        {
            _mf.sharedMesh = mesh;
            _mc.sharedMesh = mesh;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(transform.position, new Vector3(_chunkSize, _chunkSize, _chunkSize));
        }
    }
}
