using System.Collections;
using System.Text;
using Chunks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    public class Progress : MonoBehaviour
    {
        public GameObject background;
        public TMP_Text tmp;

        private StringBuilder _sb = new();
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
                _count = Mathf.CeilToInt(_count * 0.8f);
                _chunkManager.SetUpdate(false);
            }
            _total = (2 * _count + 1) * (2 * _count + 1) * (2 * _count + 1);
            StartCoroutine(LoadChunksProgress());
            _material = GetComponent<Image>().material;
        }

        private void Update()
        {
            float progress = Mathf.Clamp01((float)_current / _total);
            _material.SetFloat(_prop, progress);
            _sb.Clear();
            _sb.Append(Mathf.Floor(progress*100));
            _sb.Append('%');
            tmp.SetText(_sb);
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
                            _budget = 4;
                            yield return null;
                        }
                    }
                }
            }
            
            _chunkManager.SetUpdate(true);
            gameObject.SetActive(false);
            if (background) background.SetActive(false);
            if (tmp) tmp.gameObject.SetActive(false); 
        }
        
    }
}


