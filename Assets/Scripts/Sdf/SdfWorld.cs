using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Sdf
{
    public class SdfWorld : MonoBehaviour
    {
        private readonly List<SdfWorldPart> _parts = new();
        private readonly List<SdfShape> _shapes = new();

        private NativeArray<SdfShapeType> _shapeTypes;
        private NativeArray<SdfEditOp> _editOps;
        private NativeArray<float> _args;

        public List<SdfShape> Shapes => _shapes;
        public NativeArray<SdfShapeType> ShapeTypes => _shapeTypes;
        public NativeArray<SdfEditOp> EditOps => _editOps;
        public NativeArray<float> Args => _args;
        public static SdfWorld Instance;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            Bake();
        }

        /// <summary>
        /// 烘焙一次,结果存 Persistent NativeArray。启动时调用;以后运行时
        /// 增删/移动形状后需重新调用(并对受影响块 MarkChunkDirty)。
        /// 每次重新收集场景中的 SdfWorldPart,按当前 Transform 重建纯数据形状。
        /// </summary>
        public void Bake()
        {
            DisposeBaked();

            _parts.Clear();
            _parts.AddRange(transform.GetComponentsInChildren<SdfWorldPart>());
            
            _shapes.Clear();
            foreach (SdfWorldPart part in _parts)
                _shapes.Add(part.Shape);

            BakeJobParams(out SdfShapeType[] shapeTypes, out SdfEditOp[] editOps, out float[] args);
            _shapeTypes = new NativeArray<SdfShapeType>(shapeTypes, Allocator.Persistent);
            _editOps = new NativeArray<SdfEditOp>(editOps, Allocator.Persistent);
            _args = new NativeArray<float>(args, Allocator.Persistent);
        }

        private void DisposeBaked()
        {
            if (_shapeTypes.IsCreated) _shapeTypes.Dispose();
            if (_editOps.IsCreated) _editOps.Dispose();
            if (_args.IsCreated) _args.Dispose();
        }

        private void OnDestroy()
        {
            DisposeBaked();
        }

        /// <summary>
        /// 把形状列表烘焙成 SdfFieldEditJob 消费的三组平行数组。
        /// args 布局(与 job 内游标步进一致):Sphere = [pos(3), radius];
        /// Capsule = [top(3), down(3), radius]; HalfSphere = [pos(3), radius, normal(3)];
        /// HalfCapsule = [top(3), down(3), radius, cutPoint(3), normal(3)]。
        /// 扩展新形状 = 子类实现 Type/BakeArgs,再在 SdfFieldEditJob 的 switch 里加对应 case。
        /// </summary>
        public void BakeJobParams(out SdfShapeType[] shapeTypes, out SdfEditOp[] editOps, out float[] args)
        {
            shapeTypes = new SdfShapeType[_shapes.Count];
            editOps = new SdfEditOp[_shapes.Count];

            var argList = new List<float>();
            for (int i = 0; i < _shapes.Count; i++)
            {
                SdfShape shape = _shapes[i];
                shapeTypes[i] = shape.Type;
                editOps[i] = _parts[i].editOp;
                shape.BakeArgs(argList);
            }
            args = argList.ToArray();
        }
    }
}