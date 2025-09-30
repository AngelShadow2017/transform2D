using UnityEngine;

public static class Transform2DBehaviourSettings
{
    // 编辑器(非运行)是否允许写入序列化缓存
    public const bool EditorPersistEnabled = true;
    // Play(运行期)是否写入序列化缓存（保持 Unity 行为建议 false）
    public const bool PlayModePersistEnabled = false;
    // 是否延迟到 LateUpdate 再写 Unity Transform
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

    private bool _dirtyLocal;
    private bool _pendingPersist;
    private bool _writeAppliedThisFrame;
    private static readonly float kRotEps = 0.0001f;

#if UNITY_EDITOR
    // 用于识别刚退出 Play 的那一帧
    private static bool s_LastPlaying;
    private static bool s_JustExitedPlay;
#endif

    #region 本地/世界属性
    public Vector2 localPosition { get => _node.localPosition; set { if (_node.localPosition != value) { _node.localPosition = value; MarkLocalDirty(); } } }
    public float localRotationDegrees { get => _node.localRotationDegrees; set { if (!Mathf.Approximately(_node.localRotationDegrees, value)) { _node.localRotationDegrees = value; MarkLocalDirty(); } } }
    public Vector2 localScale { get => _node.localScale; set { if (_node.localScale != value) { _node.localScale = value; MarkLocalDirty(); } } }

    public Vector2 worldPosition => _node.worldPosition;
    public float worldRotationDegrees => _node.rotationDeg;
    public Vector2 worldScale => _node.worldScale;

    public Vector2 position { get => _node.position; set { if (_node.position != value) { _node.position = value; MarkLocalDirty(); } } }
    public float rotationDegrees { get => _node.rotationDeg; set { if (Mathf.Abs(_node.rotationDeg - value) > kRotEps) { _node.rotationDeg = value; MarkLocalDirty(); } } }
    public Vector2 lossyScale => _node.worldScale;
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
        if (Parent == newParent && transform.parent == (newParent ? newParent.transform : null)) return;
        Parent = newParent;
        _node.SetParent(newParent ? newParent._node : null, keepWorldPosition);
        if (syncMode == SyncMode.WriteToUnity && autoAttachByUnityHierarchy)
        {
            transform.parent = newParent ? newParent.transform : null;
        }
        MarkLocalDirty();
    }
    private void OnTransformParentChanged()
    {
        if (!autoAttachByUnityHierarchy) return;
        if (transform.parent)
        {
            var p = transform.parent.GetComponent<Transform2DBehaviour>();
            SetParent(p, true);
        }
        else SetParent(null, true);
    }
    #endregion

    #region 初始化/生命周期
    private void OnEnable() => InitializeState();

    private void InitializeState()
    {
        bool playing = Application.isPlaying;
#if UNITY_EDITOR
        s_JustExitedPlay = !playing && s_LastPlaying;
        s_LastPlaying = playing;
#endif
        if (syncMode == SyncMode.WriteToUnity)
        {
#if UNITY_EDITOR
            if (playing)
            {
                if (!_hasStored || adoptUnityOnPlayInWriteMode) CaptureUnityToStored();
            }
            else
            {
                bool allowCapture = true;
                if (s_JustExitedPlay && !adoptUnityOnPlayInWriteMode)
                    allowCapture = false;

                if (allowCapture)
                {
                    if (!_hasStored ||
                        transform.localPosition.x != _storedLocalPosition.x ||
                        transform.localPosition.y != _storedLocalPosition.y ||
                        Mathf.Abs(Mathf.DeltaAngle(transform.localRotation.eulerAngles.z, _storedLocalRotation)) > kRotEps ||
                        transform.localScale.x != _storedLocalScale.x ||
                        transform.localScale.y != _storedLocalScale.y)
                        CaptureUnityToStored();
                }
            }
#else
            if (!_hasStored || adoptUnityOnPlayInWriteMode) CaptureUnityToStored();
#endif
            RestoreNodeFromStored();
            MarkLocalDirty();
        }
        else
        {
            if (!_hasStored) CaptureUnityToStored();
            RestoreNodeFromStored();
            if (syncMode == SyncMode.ReadFromUnity) ReadFromUnityTransform();
        }

        if (autoAttachByUnityHierarchy && transform.parent)
        {
            var p = transform.parent.GetComponent<Transform2DBehaviour>();
            if (p) SetParent(p, true);
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

    #region 捕获/恢复
    private void CaptureUnityToStored()
    {
        var lp = transform.localPosition;
        var ls = transform.localScale;
        float lr = transform.localRotation.eulerAngles.z;
        _storedLocalPosition = new Vector2(lp.x, lp.y);
        _storedLocalRotation = lr;
        _storedLocalScale = new Vector2(ls.x, ls.y);
        _hasStored = true;
    }
    private void RestoreNodeFromStored()
    {
        _node.localPosition = _storedLocalPosition;
        _node.localRotationDegrees = _storedLocalRotation;
        _node.localScale = _storedLocalScale;
    }
    #endregion

    #region 脏标记/持久化
    private void MarkLocalDirty()
    {
        _dirtyLocal = true;
        _pendingPersist = true;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (syncMode == SyncMode.WriteToUnity && !Transform2DBehaviourSettings.DeferredWriteMode)
                FlushIfDirty();
            else if (syncMode == SyncMode.None)
                PersistIfNeeded();
            return;
        }
#endif
        if (!Transform2DBehaviourSettings.DeferredWriteMode && syncMode == SyncMode.WriteToUnity)
            FlushIfDirty();
    }

    private void PersistIfNeeded()
    {
        if (!_pendingPersist) return;

#if UNITY_EDITOR
        // 退出 Play 后首帧若不采纳运行期状态则丢弃一次持久化
        if (s_JustExitedPlay && !adoptUnityOnPlayInWriteMode)
        {
            _pendingPersist = false;
            return;
        }
#endif
        bool playing = Application.isPlaying;
#if UNITY_EDITOR
        bool allow = playing ? Transform2DBehaviourSettings.PlayModePersistEnabled
                             : Transform2DBehaviourSettings.EditorPersistEnabled;
#else
        bool allow = Transform2DBehaviourSettings.PlayModePersistEnabled;
#endif
        if (!allow)
        {
            _pendingPersist = false; // 丢弃
            return;
        }
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
    #endregion

    #region 同步
    public void ReadFromUnityTransform()
    {
        var lp = transform.localPosition;
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

        transform.localPosition = new Vector3(lp.x, lp.y, transform.localPosition.z);

        float curRotZ = transform.localRotation.eulerAngles.z;
        if (Mathf.Abs(Mathf.DeltaAngle(curRotZ, lr)) > kRotEps)
            transform.localRotation = Quaternion.Euler(0, 0, lr);

        float targetZ = applyScaleZAsOne ? 1f : transform.localScale.z;
        transform.localScale = new Vector3(ls.x, ls.y, targetZ);
    }
    #endregion

    #region Force API
    public bool IsDirty => _dirtyLocal;

    public void ForceFlush(bool persist = true)
    {
        if (syncMode != SyncMode.WriteToUnity) return;
        WriteToUnityTransform();
        _dirtyLocal = false;
        if (persist) { _pendingPersist = true; PersistIfNeeded(); }
    }

    public void ForceReadFromUnity(bool persist = true, bool overrideWriteMode = false)
    {
        if (syncMode == SyncMode.WriteToUnity && !overrideWriteMode) return;
        ReadFromUnityTransform();
        if (persist) { _pendingPersist = true; PersistIfNeeded(); }
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
