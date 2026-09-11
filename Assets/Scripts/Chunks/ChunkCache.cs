using System.Collections.Generic;
using Sdf;
using UnityEngine;

namespace Chunks
{
    // 坐标级缓存，LRU
    public class ChunkCache
    {
        private struct ChunkData
        {
            public Vector3Int coord;
            public Mesh mesh;
            public SdfField sdfField;
        }

        private int _capacity;
        private readonly LinkedList<ChunkData> _list = new ();
        private readonly Dictionary<Vector3Int, LinkedListNode<ChunkData>> _entries = new ();

        public int Count => _entries.Count;
        public int Capacity { get => _capacity; set => _capacity = value; }

        // 命中或新建条目并移到链表头,返回该坐标的节点。
        private LinkedListNode<ChunkData> Get(Vector3Int coord)
        {
            if (_entries.TryGetValue(coord, out LinkedListNode<ChunkData> node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
            }
            else
            {
                node = _list.AddFirst(new ChunkData { coord = coord });
                _entries[coord] = node;
            }
            return node;
        }

        public bool TryGetMeshFromCache(Vector3Int chunkCoord, out Mesh mesh)
        {
            mesh = null;
            if (_entries.TryGetValue(chunkCoord, out LinkedListNode<ChunkData> node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                mesh = node.Value.mesh;
                return mesh;
            }
            return false;
        }

        public void AddMeshToCache(Vector3Int chunkCoord, Mesh mesh)
        {
            LinkedListNode<ChunkData> node = Get(chunkCoord);
            ChunkData data = node.Value;
            if (data.mesh && data.mesh != mesh)
            {
                Object.Destroy(data.mesh);
            }
            data.mesh = mesh;
            node.Value = data;
        }

        public bool TryGetSdfFromCache(Vector3Int chunkCoord, out SdfField sdfField)
        {
            sdfField = null;
            if (_entries.TryGetValue(chunkCoord, out LinkedListNode<ChunkData> node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                sdfField = node.Value.sdfField;
                return sdfField != null;
            }
            return false;
        }

        public void AddSdfToCache(Vector3Int chunkCoord, SdfField sdfField)
        {
            LinkedListNode<ChunkData> node = Get(chunkCoord);
            ChunkData data = node.Value;
            if (data.sdfField != null && data.sdfField != sdfField)
            {
                data.sdfField.Destroy();
            }
            data.sdfField = sdfField;
            node.Value = data;
        }
        
        public void Trim(int capacity, ICollection<Vector3Int> keepAlive)
        {
            if (capacity <= 0) return;

            LinkedListNode<ChunkData> node = _list.Last;
            while (node != null && Count > capacity)
            {
                LinkedListNode<ChunkData> prev = node.Previous;
                if (keepAlive == null || !keepAlive.Contains(node.Value.coord))
                {
                    Evict(node);
                }
                node = prev;
            }
        }

        private void Evict(LinkedListNode<ChunkData> node)
        {
            ChunkData data = node.Value;
            _entries.Remove(data.coord);
            _list.Remove(node);
            if (data.mesh)
            {
                Object.Destroy(data.mesh);
            }
            data.sdfField?.Destroy();
        }

        public void Dispose()
        {
            foreach (ChunkData data in _list)
            {
                if (data.mesh)
                {
                    Object.Destroy(data.mesh);
                }
                data.sdfField?.Destroy();
            }
            _entries.Clear();
            _list.Clear();
        }
    }
}
