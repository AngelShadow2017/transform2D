using System.Collections.Generic;
using UnityEngine;

public static class Transform2DBehaviourSettings
{
    // 关闭 = 即时风格(仍在 LateUpdate 合并一次)，开启 = 延迟写模式(仅脏时在 LateUpdate 批写)
    public const bool DeferredWriteMode = false;
}

[DisallowMultipleComponent]
[ExecuteAlways]
public class Transform2DBehaviour : MonoBehaviour
{
    public enum SyncMode { None, ReadFromUnity, WriteToUnity }

    [Header("Sync Settings")]
    public SyncMode syncMode = SyncMode.WriteToUnity;
    public bool applyScaleZAsOne = true;
    public bool readScaleFromLocal = true;
    public bool autoAttachByUnityHierarchy = true;
    public bool adoptUnityOnPlayInWriteMode = false;

    [SerializeField] private CachedTransform2DNode _node = new CachedTransform2DNode();
    public CachedTransform2DNode Node => _node;

    [SerializeField] private Vector2 _storedLocalPosition = Vector2.zero;
    [SerializeField] private float _storedLocalRotation = 0f;
    [SerializeField] private Vector2 _storedLocalScale = Vector2.one;
    [SerializeField] private bool _hasStored = false;

    public Transform2DBehaviour Parent { get; private set; }

    private bool _dirtyLocal = false;
    private bool _pendingPersist = false;
    private bool _writeAppliedThisFrame = false;
    private static readonly float kRotEps = 0.0001f;

    #region 本地属性
    public Vector2 localPosition
    {
        get => _node.localPosition;
        set { if (_node.localPosition != value) { _node.localPosition = value; MarkLocalDirty(); } }
    }
    public float localRotationDegrees
    {
        get => _node.localRotationDegrees;
        set { if (!Mathf.Approximately(_node.localRotationDegrees, value)) { _node.localRotationDegrees = value; MarkLocalDirty(); } }
    }
    public Vector2 localScale
    {
        get => _node.localScale;
        set { if (_node.localScale != value) { _node.localScale = value; MarkLocalDirty(); } }
    }
    public Vector2 position
    {
        get => _node.position;
        set { if (_node.position != value) { _node.position = value; MarkLocalDirty(); } }
    }
    public float rotationDegrees
    {
        get => _node.rotationDeg;
        set { if (Mathf.Abs(_node.rotationDeg - value) > kRotEps) { _node.rotationDeg = value; MarkLocalDirty(); } }
    }
    public Vector2 lossyScale => _node.worldScale;
    #endregion

    #region 世界只读
    public Vector2 worldPosition => _node.worldPosition;
    public float worldRotationDegrees => _node.rotationDeg;
    public Vector2 worldScale => _node.worldScale;
    #endregion

    #region 批量设置
    public void SetLocalTRSDegrees(Vector2 pos, float rotDeg, Vector2 scl)
    {
        bool changed = false;
        if (_node.localPosition != pos) { _node.localPosition = pos; changed = true; }
        if (!Mathf.Approximately(_node.localRotationDegrees, rotDeg)) { _node.localRotationDegrees = rotDeg; changed = true; }
        if (_node.localScale != scl) { _node.localScale = scl; changed = true; }
        if (changed) MarkLocalDirty();
    }
    public void SetLocalTRS(Vector2 pos, float rotRad, Vector2 scl) =>
        SetLocalTRSDegrees(pos, rotRad * Mathf.Rad2Deg, scl);
    #endregion

    #region 层级
    public void SetParent(Transform2DBehaviour newParent, bool keepWorldPosition = true)
    {
        if (Parent == newParent && transform.parent == (newParent != null ? newParent.transform : null)) return;
        Parent = newParent;
        _node.SetParent(newParent != null ? newParent._node : null, keepWorldPosition);
        if (syncMode == SyncMode.WriteToUnity)
        {
            if (autoAttachByUnityHierarchy)
            {
                transform.parent = newParent != null ? newParent.transform : null;
            }
            MarkLocalDirty();
        }
    }
    #endregion

    #region 新增：统一采集 Unity 当前本地 TRS
    private void CaptureUnityToStored()
    {
        Vector3 lp = transform.localPosition;
        Vector3 ls = transform.localScale;
        float lr = transform.localRotation.eulerAngles.z;
        _storedLocalPosition = new Vector2(lp.x, lp.y);
        _storedLocalRotation = lr;
        _storedLocalScale = new Vector2(ls.x, ls.y);
        _hasStored = true;
    }
    #endregion

    #region 生命周期（修改 InitializeState）
    private void OnEnable()
    {
        InitializeState();
    }

    private void OnTransformParentChanged()
    {
        if (autoAttachByUnityHierarchy)
        {
            if (transform.parent != null)
            {
                var pb = transform.parent.GetComponent<Transform2DBehaviour>();
                SetParent(pb, keepWorldPosition: true);
            }
            else SetParent(null, keepWorldPosition: true);
        }
    }

    private void InitializeState()
    {
        // 进入运行或编辑启用时：决定采用哪一份数据作为“真源”
        bool playing = Application.isPlaying;

        if (syncMode == SyncMode.WriteToUnity)
        {
            // Write 模式下节点驱动 Unity Transform。
            // 需要判断是否要采用当前 Unity 值而不是序列化缓存，避免“启动一瞬间被旧缓存（可能是原点）覆盖”。
#if UNITY_EDITOR
            if (playing)
            {
                if (!_hasStored || adoptUnityOnPlayInWriteMode)
                    CaptureUnityToStored();
            }
            else
            {
                // 编辑器下：如果还没有缓存，或用户在场景里直接手动改了 Transform（与缓存不一致），就采纳 Unity
                if (!_hasStored ||
                    transform.localPosition.x != _storedLocalPosition.x ||
                    transform.localPosition.y != _storedLocalPosition.y ||
                    Mathf.Abs(Mathf.DeltaAngle(transform.localRotation.eulerAngles.z, _storedLocalRotation)) > kRotEps ||
                    transform.localScale.x != _storedLocalScale.x ||
                    transform.localScale.y != _storedLocalScale.y)
                {
                    CaptureUnityToStored();
                }
            }
#else
            if (!_hasStored || adoptUnityOnPlayInWriteMode)
                CaptureUnityToStored();
#endif
            // 用缓存回填节点
            RestoreNodeFromStored();
            MarkLocalDirty(); // 触发一次写，统一化
        }
        else if (syncMode == SyncMode.ReadFromUnity)
        {
            // 读模式：节点跟随 Unity Transform，因此直接从 Unity 采集
            CaptureUnityToStored();
            RestoreNodeFromStored(); // 与缓存保持一致
            // 不需要写回
        }
        else // None
        {
            if (!_hasStored)
            {
                // 没缓存就采一次，保证 Node 有初值
                CaptureUnityToStored();
            }
            RestoreNodeFromStored();
        }

        // 自动层级绑定（放在最后，保证 Node 已初始化）
        if (autoAttachByUnityHierarchy && transform.parent != null)
        {
            var p = transform.parent.GetComponent<Transform2DBehaviour>();
            if (p != null) SetParent(p, keepWorldPosition: true);
        }
    }

    private void LateUpdate()
    {
        _writeAppliedThisFrame = false;
        switch (syncMode)
        {
            case SyncMode.ReadFromUnity: ReadFromUnityTransform(); break;
            case SyncMode.WriteToUnity: FlushIfDirty(); break;
        }
    }
    #endregion

    #region 持久化/脏标记
    private void MarkLocalDirty()
    {
        _dirtyLocal = true;
        _pendingPersist = true;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (syncMode == SyncMode.WriteToUnity) FlushIfDirty();
            else if (syncMode == SyncMode.None) PersistIfNeeded();
            return;
        }
#endif
        if (!Transform2DBehaviourSettings.DeferredWriteMode && syncMode == SyncMode.WriteToUnity)
        {
            FlushIfDirty();
            // 仍延迟到 LateUpdate 做一次性批处理，但这里提前一次保障编辑体验
        }
    }

    private void PersistIfNeeded()
    {
        if (!_pendingPersist) return;
        _storedLocalPosition = _node.localPosition;
        _storedLocalRotation = _node.localRotationDegrees;
        _storedLocalScale = _node.localScale;
        _hasStored = true;
        _pendingPersist = false;
    }

    private void FlushIfDirty()
    {
        if (syncMode != SyncMode.WriteToUnity) { PersistIfNeeded(); return; }
        if (!_dirtyLocal) { PersistIfNeeded(); return; }
        WriteToUnityTransform();
        PersistIfNeeded();
        _dirtyLocal = false;
        _writeAppliedThisFrame = true;
    }

    private void RestoreNodeFromStored()
    {
        _node.localPosition = _storedLocalPosition;
        _node.localRotationDegrees = _storedLocalRotation;
        _node.localScale = _storedLocalScale;
    }
    #endregion

    #region 同步（修改 ReadFromUnityTransform 仅使用 localPosition）
    public void ReadFromUnityTransform()
    {
        // 修复：原实现使用 transform.position（世界坐标）导致有父层级时回写错误。
        Vector3 lp = transform.localPosition;
        float rotZ = transform.localRotation.eulerAngles.z;
        Vector3 scl3 = readScaleFromLocal ? transform.localScale : transform.lossyScale;

        bool changed = false;
        Vector2 lp2 = new Vector2(lp.x, lp.y);
        if (_node.localPosition != lp2) { _node.localPosition = lp2; changed = true; }
        if (Mathf.Abs(_node.localRotationDegrees - rotZ) > kRotEps) { _node.localRotationDegrees = rotZ; changed = true; }
        Vector2 ns = new Vector2(scl3.x, scl3.y);
        if (_node.localScale != ns) { _node.localScale = ns; changed = true; }

        if (changed)
        {
            _pendingPersist = true;
            PersistIfNeeded();
        }
    }

    public void WriteToUnityTransform()
    {
        var lp = _node.localPosition;
        var lr = _node.localRotationDegrees;
        var ls = _node.localScale;

        // 仅改 X/Y，保持 Z
        transform.localPosition = new Vector3(lp.x, lp.y, transform.localPosition.z);

        float curRotZ = transform.localRotation.eulerAngles.z;
        if (Mathf.Abs(Mathf.DeltaAngle(curRotZ, lr)) > kRotEps)
            transform.localRotation = Quaternion.Euler(0, 0, lr);

        float curLSZ = transform.localScale.z;
        float targetZ = applyScaleZAsOne ? 1f : curLSZ;
        transform.localScale = new Vector3(ls.x, ls.y, targetZ);
    }
    #endregion

    #region 强制 API（仅当前实例）
    public bool IsDirty => _dirtyLocal;

    public void ForceFlush(bool persist = true)
    {
        if (syncMode != SyncMode.WriteToUnity) return;
        WriteToUnityTransform();
        _dirtyLocal = false;
        if (persist)
        {
            _pendingPersist = true;
            PersistIfNeeded();
        }
    }

    public void ForceReadFromUnity(bool persist = true, bool overrideWriteMode = false)
    {
        if (syncMode == SyncMode.WriteToUnity && !overrideWriteMode) return;
        ReadFromUnityTransform();
        if (persist && !_pendingPersist)
        {
            _pendingPersist = true;
            PersistIfNeeded();
        }
        _dirtyLocal = false;
    }

    public void ForcePersist()
    {
        _pendingPersist = true;
        PersistIfNeeded();
    }

    public void ForceSyncNow(bool persist = true)
    {
        switch (syncMode)
        {
            case SyncMode.WriteToUnity: ForceFlush(persist); break;
            case SyncMode.ReadFromUnity: ForceReadFromUnity(persist); break;
            case SyncMode.None: if (persist) ForcePersist(); break;
        }
    }

    public void ForceMarkDirty()
    {
        _dirtyLocal = true;
        _pendingPersist = true;
    }
    #endregion

    #region 编辑器
    private void OnValidate()
    {
        if (!isActiveAndEnabled) return;
        RestoreNodeFromStored();
        if (syncMode == SyncMode.ReadFromUnity) ReadFromUnityTransform();
        else if (syncMode == SyncMode.WriteToUnity)
        {
            _dirtyLocal = true;
            FlushIfDirty();
        }
    }

#if UNITY_EDITOR
    public void EditorForceApply()
    {
        if (syncMode == SyncMode.ReadFromUnity) ReadFromUnityTransform();
        else if (syncMode == SyncMode.WriteToUnity)
        {
            _dirtyLocal = true;
            FlushIfDirty();
        }
    }
#endif
    #endregion

    #region 工具
    public Vector2 TransformPointLocalToWorld(Vector2 p) => _node.TransformPoint(p);
    public Vector2 TransformPointWorldToLocal(Vector2 p) => _node.InverseTransformPoint(p);
    public Matrix4x4 GetWorldMatrix4x4() => _node.WorldMatrix4x4;
    #endregion

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        var world = _node.WorldMatrix4x4;
        Vector3 origin = world.MultiplyPoint3x4(Vector3.zero);
        Vector3 right = world.MultiplyPoint3x4(new Vector3(1, 0, 0));
        Vector3 up = world.MultiplyPoint3x4(new Vector3(0, 1, 0));
        Gizmos.color = Color.yellow; Gizmos.DrawSphere(origin, 0.05f);
        Gizmos.color = Color.red; Gizmos.DrawLine(origin, right);
        Gizmos.color = Color.green; Gizmos.DrawLine(origin, up);
    }
#endif
}