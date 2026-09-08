using System.Collections;
using System.Diagnostics;
using Chunks;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    public class Progress : MonoBehaviour
    {
        public GameObject background;
        private ChunkManager _chunkManager;
        private int _budget = 4;
        private int _count;
        private int _total;
        private int _current;
        private Material _material;
        private int _prop = Shader.PropertyToID("_Progress");
        
        private void Start()
        {
            _chunkManager = ChunkManager.Instance;
            if (_chunkManager)
            {
                _count = _chunkManager.MaxChunkCountPerAxis;
                _chunkManager.SetUpdate(false);
            }
            _total = (2 * _count + 1) * (2 * _count + 1) * (2 * _count + 1);
            StartCoroutine(LoadChunksProgress());
            _material = GetComponent<Image>().material;
        }

        private void Update()
        {
            _material.SetFloat(_prop, (float) _current/_total);
        }

        private IEnumerator LoadChunksProgress()
        {
            if (!_chunkManager) yield break;

            for (int i = -_count; i <= _count; i++)
            {
                for (int j = -_count; j <= _count; j++)
                {
                    for (int k = -_count; k <= _count; k++)
                    {
                        Vector3Int coord = new(i, j, k);
                        _chunkManager.LoadChunk(coord);
                        _budget--;
                        _current++;
                        if (_budget <= 0)
                        {
                            _budget = 2;
                            yield return null;
                        }
                    }
                }
            }
            
            _chunkManager.SetUpdate(true);
            gameObject.SetActive(false);
            if (background) background.SetActive(false);
        }
        
    }
}


