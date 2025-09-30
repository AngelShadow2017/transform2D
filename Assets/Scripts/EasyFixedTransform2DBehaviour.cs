using System;
using Core.TrueSync;
using UnityEngine;

[DisallowMultipleComponent]
public class EasyFixedTransform2DBehaviour : MonoBehaviour
{
    public enum SyncMode { WriteToUnity } // 保留枚举占位（只支持写）

    [Header("Initialization (Play Mode Only)")]
    public bool setLocalPosition;
    public TSVector2 initialLocalPosition;
    public bool errorIfUnsetLocalPosition;

    public bool setLocalRotation;
    public FP initialLocalRotationDegrees;
    public bool errorIfUnsetLocalRotation;

    public bool setLocalScale;
    public TSVector2 initialLocalScale = TSVector2.one;
    public bool errorIfUnsetLocalScale;

    [Header("Options")]
    public bool applyScaleZAsOne = true;

    [SerializeField] private Transform2DFixed _node = new Transform2DFixed();
    public Transform2DFixed Node => _node;

    public EasyFixedTransform2DBehaviour Parent { get; private set; }

    private bool _dirtyLocal;
    private bool _writeAppliedThisFrame;

    private static readonly FP kRotEps = FP.EN4;

    #region 本地属性
    public TSVector2 localPosition
    {
        get => _node.localPosition;
        set
        {
            if (_node.localPosition != value)
            {
                _node.localPosition = value;
                MarkLocalDirty();
            }
        }
    }

    public FP localRotationDegrees
    {
        get => _node.localRotationDegrees;
        set
        {
            if (FP.Abs(TSMath.DeltaAngle(_node.localRotationDegrees, value)) > kRotEps)
            {
                _node.localRotationDegrees = value;
                MarkLocalDirty();
            }
        }
    }

    public TSVector2 localScale
    {
        get => _node.localScale;
        set
        {
            if (_node.localScale != value)
            {
                _node.localScale = value;
                MarkLocalDirty();
            }
        }
    }
    #endregion

    #region 世界便捷属性
    public TSVector2 worldPosition => _node.worldPosition;
    public FP worldRotationDegrees => _node.rotationDeg;
    public TSVector2 worldScale => _node.worldScale;
    public TSVector2 lossyScale => _node.worldScale;

    public TSVector2 position
    {
        get => _node.position;
        set
        {
            if (_node.position != value)
            {
                _node.position = value;
                MarkLocalDirty();
            }
        }
    }

    public FP rotationDegrees
    {
        get => _node.rotationDeg;
        set
        {
            if (FP.Abs(TSMath.DeltaAngle(_node.rotationDeg, value)) > kRotEps)
            {
                _node.rotationDeg = value;
                MarkLocalDirty();
            }
        }
    }
    #endregion

    #region 初始化

    private void OnEnable()
    {
        OnTransformParentChanged();
    }

    private void Start()
    {
        if (!Application.isPlaying) return;

        // 初始化本地 Position
        if (setLocalPosition)
            _node.localPosition = initialLocalPosition;
        else
        {
            if (errorIfUnsetLocalPosition)
                Debug.LogError($"[EasyFixedTransform2DBehaviour] 未设置 localPosition 且被标记为必填: {name}", this);
            var lp = transform.localPosition;
            _node.localPosition = new TSVector2(lp.x, lp.y);
        }

        // 初始化本地 Rotation
        if (setLocalRotation)
            _node.localRotationDegrees = initialLocalRotationDegrees;
        else
        {
            if (errorIfUnsetLocalRotation)
                Debug.LogError($"[EasyFixedTransform2DBehaviour] 未设置 localRotation 且被标记为必填: {name}", this);
            _node.localRotationDegrees = transform.localRotation.eulerAngles.z;
        }

        // 初始化本地 Scale
        if (setLocalScale)
            _node.localScale = initialLocalScale;
        else
        {
            if (errorIfUnsetLocalScale)
                Debug.LogError($"[EasyFixedTransform2DBehaviour] 未设置 localScale 且被标记为必填: {name}", this);
            var ls = transform.localScale;
            _node.localScale = new TSVector2(ls.x, ls.y);
        }

        MarkLocalDirty();
        FlushIfDirty();
    }
    #endregion

    #region 更新
    private void LateUpdate()
    {
        if (!Application.isPlaying) return;
        _writeAppliedThisFrame = false;
        FlushIfDirty();
    }
    #endregion

    #region 脏标记 & 写入
    private void MarkLocalDirty()
    {
        if (!Application.isPlaying) return;
        _dirtyLocal = true;
    }

    private void FlushIfDirty()
    {
        if (!_dirtyLocal) return;
        WriteToUnityTransform();
        _dirtyLocal = false;
        _writeAppliedThisFrame = true;
    }

    private void WriteToUnityTransform()
    {
        // Position（保持原 z）
        Vector3 cur = transform.localPosition;
        transform.localPosition = new Vector3((float)_node.localPosition.x, (float)_node.localPosition.y, cur.z);

        // Rotation
        FP curRot = transform.localRotation.eulerAngles.z;
        if (FP.Abs(TSMath.DeltaAngle(curRot, _node.localRotationDegrees)) > kRotEps)
            transform.localRotation = Quaternion.Euler(0, 0, (float)_node.localRotationDegrees);

        // Scale
        float z = applyScaleZAsOne ? 1f : transform.localScale.z;
        transform.localScale = new Vector3((float)_node.localScale.x, (float)_node.localScale.y, z);
    }
    #endregion

    #region 层级
    public void SetParent(EasyFixedTransform2DBehaviour newParent, bool keepWorldPosition = true)
    {
        if (!Application.isPlaying) return;
        if (Parent == newParent) return;

        Parent = newParent;
        _node.SetParent(newParent ? newParent._node : null, keepWorldPosition);

        if (keepWorldPosition)
        {
            // _node 已处理相对换算, 这里只需标记写回
            MarkLocalDirty();
        }
        else
        {
            MarkLocalDirty();
        }
    }

    private void OnTransformParentChanged()
    {
        // 运行期如果用户手动改了 Unity 层级，需要同步 _node 父级
        if (!Application.isPlaying) return;
        var p = transform.parent ? transform.parent.GetComponent<EasyFixedTransform2DBehaviour>() : null;
        if (p != Parent)
        {
            Parent = p;
            _node.SetParent(p ? p._node : null, true);
            MarkLocalDirty();
        }
    }
    #endregion

    #region Force API
    public bool IsDirty => _dirtyLocal;
    public bool WriteAppliedThisFrame => _writeAppliedThisFrame;

    public void ForceFlush(bool forceWriteEvenIfClean = false)
    {
        if (!Application.isPlaying) return;
        if (forceWriteEvenIfClean)
            _dirtyLocal = true;
        FlushIfDirty();
    }

    public void ForceReadFromUnity()
    {
        if (!Application.isPlaying) return;

        Vector3 lp = transform.localPosition;
        FP lr = transform.localRotation.eulerAngles.z;
        Vector3 ls = transform.localScale;

        bool changed = false;
        TSVector2 lp2 = new TSVector2(lp.x, lp.y);
        if (_node.localPosition != lp2) { _node.localPosition = lp2; changed = true; }
        if (FP.Abs(TSMath.DeltaAngle(_node.localRotationDegrees, lr)) > kRotEps) { _node.localRotationDegrees = lr; changed = true; }
        TSVector2 ls2 = new TSVector2(ls.x, ls.y);
        if (_node.localScale != ls2) { _node.localScale = ls2; changed = true; }

        if (changed) MarkLocalDirty();
    }

    public void ForceMarkDirty()
    {
        if (!Application.isPlaying) return;
        _dirtyLocal = true;
    }

    public void ForceSyncNow()
    {
        if (!Application.isPlaying) return;
        FlushIfDirty();
    }
    #endregion

    #region TRS 批量
    public void SetLocalTRSDegrees(TSVector2 pos, FP rotDeg, TSVector2 scl)
    {
        bool changed = false;
        if (_node.localPosition != pos) { _node.localPosition = pos; changed = true; }
        if (FP.Abs(TSMath.DeltaAngle(_node.localRotationDegrees, rotDeg)) > kRotEps) { _node.localRotationDegrees = rotDeg; changed = true; }
        if (_node.localScale != scl) { _node.localScale = scl; changed = true; }
        if (changed) MarkLocalDirty();
    }

    public void SetLocalTRS(TSVector2 pos, FP rotRad, TSVector2 scl) =>
        SetLocalTRSDegrees(pos, rotRad * Mathf.Rad2Deg, scl);
    #endregion

    #region 点/矩阵工具
    public TSVector2 TransformPointLocalToWorld(TSVector2 p) => _node.TransformPoint(p);
    public TSVector2 TransformPointWorldToLocal(TSVector2 p) => _node.InverseTransformPoint(p);
    public Matrix4x4 GetWorldMatrix4x4() => _node.WorldMatrix4x4;
    #endregion
}
