using System.Collections.Generic;
using Sdf;
using UnityEngine;

namespace Chunks
{
    /// <summary>
    /// Per-coordinate caches: the generated mesh and the SDF lattice texture. Chunk
    /// unload only returns the GameObject to the pool — these stay alive, so reload
    /// is a cache hit and edits can re-mesh from the kept SDF field.
    /// </summary>
    public class ChunkCache
    {
        private readonly Dictionary<Vector3Int, Mesh> _meshCache = new ();
        private readonly Dictionary<Vector3Int, SdfField> _sdfFieldCache = new ();

        public bool TryGetMeshFromCache(Vector3Int chunkCoord, out Mesh mesh)
        {
            return _meshCache.TryGetValue(chunkCoord, out mesh);
        }

        public void AddMeshToCache(Vector3Int chunkCoord, Mesh mesh)
        {
            if (_meshCache.TryGetValue(chunkCoord, out Mesh old) && old && old != mesh)
            {
                Object.Destroy(old);
            }
            _meshCache[chunkCoord] = mesh;
        }

        public bool TryGetSdfFromCache(Vector3Int chunkCoord, out SdfField sdfField)
        {
            return _sdfFieldCache.TryGetValue(chunkCoord, out sdfField);
        }

        public void AddSdfToCache(Vector3Int chunkCoord, SdfField sdfField)
        {
            if (_sdfFieldCache.TryGetValue(chunkCoord, out SdfField old) && old != null && old != sdfField)
            {
                old.Destroy();
            }
            _sdfFieldCache[chunkCoord] = sdfField;
        }

        public void Dispose()
        {
            foreach (var mesh in _meshCache.Values)
            {
                if (mesh)
                {
                    Object.Destroy(mesh);
                }
            }
            _meshCache.Clear();

            foreach (var field in _sdfFieldCache.Values)
            {
                field?.Destroy();
            }
            _sdfFieldCache.Clear();
        }
    }
}
