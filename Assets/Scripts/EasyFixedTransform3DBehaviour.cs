using System;
using Core.TrueSync;
using UnityEngine;

[DisallowMultipleComponent]
public class EasyFixedTransform3DBehaviour : MonoBehaviour
{
    public enum SyncMode { WriteToUnity }

    [Header("Initialization (Play Mode Only)")]
    public bool setLocalPosition;
    public TSVector initialLocalPosition; // (x,y,z) in world units
    public bool errorIfUnsetLocalPosition;

    public bool setLocalEulerDegrees; // use degrees for inspector friendliness
    public TSVector initialLocalEulerDegrees; // degrees
    public bool errorIfUnsetLocalEuler;

    public bool setLocalScale;
    public TSVector initialLocalScale = TSVector.one;
    public bool errorIfUnsetLocalScale;

    [Header("Synchronization Toggles")] 
    public bool syncPosition = true;            // 写入 Unity Transform 的平移部分
    public bool syncLinear = true;              // 写入 Unity Transform 的旋转 + 缩放（线性部分）

    [Header("Options")] 
    public bool keepUnityScaleWOnWrite = false; // 若想只改 x,y,z 全部则关掉此项；true 则保持写之前的 scaleW(无意义, 仅占位)

    private FixedNode3D _node = new FixedNode3D();
    public FixedNode3D Node => _node;

    public EasyFixedTransform3DBehaviour Parent { get; private set; }

    private bool _dirtyLocal;
    private bool _writeAppliedThisFrame;

    private static readonly FP kRotEpsRad = FP.EN4; // 约 ~1e-4 弧度
    private static readonly FP Deg2Rad = (FP) (Mathf.Deg2Rad);
    private static readonly FP Rad2Deg = (FP) (Mathf.Rad2Deg);

    #region Local Properties (Degrees for Euler exposed)
    public TSVector localPosition
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

    public TSVector localEulerDegrees
    {
        get
        {
            TSVector rad = _node.localEuler; // stored in radians
            return new TSVector(rad.x * Rad2Deg, rad.y * Rad2Deg, rad.z * Rad2Deg);
        }
        set
        {
            TSVector curRad = _node.localEuler;
            TSVector newRad = new TSVector(value.x * Deg2Rad, value.y * Deg2Rad, value.z * Deg2Rad);
            if (FP.Abs(curRad.x - newRad.x) > kRotEpsRad || FP.Abs(curRad.y - newRad.y) > kRotEpsRad || FP.Abs(curRad.z - newRad.z) > kRotEpsRad)
            {
                _node.localEuler = newRad;
                MarkLocalDirty();
            }
        }
    }

    public TSQuaternion localRotation
    {
        get => _node.localRotation;
        set
        {
            if (!TSQuaternion.ValueEquals(_node.localRotation, value))
            {
                _node.localRotation = value;
                MarkLocalDirty();
            }
        }
    }

    public TSVector localScale
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

    #region World Readonly Convenience
    public TSVector worldPosition => _node.worldPosition;
    public TSQuaternion worldRotation => _node.worldRotation;
    public TSVector worldScale => _node.worldScale;
    public TSVector worldEulerDegrees
    {
        get
        {
            TSQuaternion q = _node.worldRotation;
            Quaternion uq = new Quaternion(q.x.AsFloat(), q.y.AsFloat(), q.z.AsFloat(), q.w.AsFloat());
            Vector3 e = uq.eulerAngles; // Unity returns 0..360 deg
            return new TSVector((FP)e.x, (FP)e.y, (FP)e.z);
        }
    }
    #endregion

    #region Unity Lifecycle
    private void OnEnable()
    {
        OnTransformParentChanged();
    }

    private void Start()
    {
        if (!Application.isPlaying) return;

        // Local Position Init
        if (setLocalPosition)
        {
            _node.localPosition = initialLocalPosition;
        }
        else
        {
            if (errorIfUnsetLocalPosition)
                Debug.LogError($"[EasyFixedTransform3DBehaviour] 未设置 localPosition 且被标记为必填: {name}", this);
            Vector3 lp = transform.localPosition;
            _node.localPosition = new TSVector(lp.x, lp.y, lp.z);
        }

        // Local Euler Init (degrees -> radians)
        if (setLocalEulerDegrees)
        {
            _node.localEuler = new TSVector(initialLocalEulerDegrees.x * Deg2Rad, initialLocalEulerDegrees.y * Deg2Rad, initialLocalEulerDegrees.z * Deg2Rad);
        }
        else
        {
            if (errorIfUnsetLocalEuler)
                Debug.LogError($"[EasyFixedTransform3DBehaviour] 未设置 localEuler 且被标记为必填: {name}", this);
            Vector3 le = transform.localEulerAngles;
            _node.localEuler = new TSVector((FP)le.x * Deg2Rad, (FP)le.y * Deg2Rad, (FP)le.z * Deg2Rad);
        }

        // Local Scale Init
        if (setLocalScale)
        {
            _node.localScale = initialLocalScale;
        }
        else
        {
            if (errorIfUnsetLocalScale)
                Debug.LogError($"[EasyFixedTransform3DBehaviour] 未设置 localScale 且被标记为必填: {name}", this);
            Vector3 ls = transform.localScale;
            _node.localScale = new TSVector(ls.x, ls.y, ls.z);
        }

        MarkLocalDirty();
        FlushIfDirty();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying) return;
        _writeAppliedThisFrame = false;
        FlushIfDirty();
    }
    #endregion

    #region Dirty / Flush
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
        if (syncPosition)
        {
            Vector3 curPos = transform.localPosition; // not needed but pattern preserved
            TSVector p = _node.localPosition;
            transform.localPosition = new Vector3(p.x.AsFloat(), p.y.AsFloat(), p.z.AsFloat());
        }

        if (syncLinear)
        {
            // Rotation
            TSQuaternion q = _node.localRotation; // Ensure up-to-date
            transform.localRotation = new Quaternion(q.x.AsFloat(), q.y.AsFloat(), q.z.AsFloat(), q.w.AsFloat());

            // Scale
            TSVector s = _node.localScale;
            Vector3 ns = new Vector3(s.x.AsFloat(), s.y.AsFloat(), s.z.AsFloat());
            transform.localScale = ns;
        }
    }
    #endregion

    #region Parenting
    public void SetParent(EasyFixedTransform3DBehaviour newParent, bool keepWorld = true)
    {
        if (!Application.isPlaying) return;
        if (Parent == newParent) return;
        Parent = newParent;
        _node.SetParent(newParent ? newParent._node : null, keepWorld);
        MarkLocalDirty();
        Debug.Log(_node.localRotation+" "+_node.localEuler);
    }

    private void OnTransformParentChanged()
    {
        if (!Application.isPlaying) return;
        var p = transform.parent ? transform.parent.GetComponent<EasyFixedTransform3DBehaviour>() : null;
        if (p != Parent)
        {
            Parent = p;
            _node.SetParent(p ? p._node : null, true);
            MarkLocalDirty();
        }
    }
    #endregion

    #region Force APIs
    public bool IsDirty => _dirtyLocal;
    public bool WriteAppliedThisFrame => _writeAppliedThisFrame;

    public void ForceFlush(bool forceWriteEvenIfClean = false)
    {
        if (!Application.isPlaying) return;
        if (forceWriteEvenIfClean) _dirtyLocal = true;
        FlushIfDirty();
    }

    public void ForceReadFromUnity()
    {
        if (!Application.isPlaying) return;
        Vector3 lp = transform.localPosition;
        Vector3 le = transform.localEulerAngles;
        Vector3 ls = transform.localScale;

        bool changed = false;
        TSVector nlp = new TSVector(lp.x, lp.y, lp.z);
        if (_node.localPosition != nlp) { _node.localPosition = nlp; changed = true; }

        TSVector newEulerRad = new TSVector((FP)le.x * Deg2Rad, (FP)le.y * Deg2Rad, (FP)le.z * Deg2Rad);
        TSVector curRad = _node.localEuler;
        if (FP.Abs(curRad.x - newEulerRad.x) > kRotEpsRad || FP.Abs(curRad.y - newEulerRad.y) > kRotEpsRad || FP.Abs(curRad.z - newEulerRad.z) > kRotEpsRad)
        {
            _node.localEuler = newEulerRad; changed = true;
        }

        TSVector nls = new TSVector(ls.x, ls.y, ls.z);
        if (_node.localScale != nls) { _node.localScale = nls; changed = true; }

        if (changed) MarkLocalDirty();
    }

    public void ForceMarkDirty()
    {
        if (!Application.isPlaying) return; _dirtyLocal = true;
    }

    public void ForceSyncNow()
    {
        if (!Application.isPlaying) return; FlushIfDirty();
    }
    #endregion

    #region Batch TRS (Degrees)
    public void SetLocalTRSDegrees(TSVector pos, TSVector eulerDeg, TSVector scl)
    {
        bool changed = false;
        if (_node.localPosition != pos) { _node.localPosition = pos; changed = true; }
        TSVector newRad = new TSVector(eulerDeg.x * Deg2Rad, eulerDeg.y * Deg2Rad, eulerDeg.z * Deg2Rad);
        TSVector curRad = _node.localEuler;
        if (FP.Abs(curRad.x - newRad.x) > kRotEpsRad || FP.Abs(curRad.y - newRad.y) > kRotEpsRad || FP.Abs(curRad.z - newRad.z) > kRotEpsRad) { _node.localEuler = newRad; changed = true; }
        if (_node.localScale != scl) { _node.localScale = scl; changed = true; }
        if (changed) MarkLocalDirty();
    }
    #endregion

    #region Point Helpers
    public TSVector TransformPointLocalToWorld(TSVector p) => _node.TransformPointLocalToWorld(p);
    public TSVector TransformPointWorldToLocal(TSVector p) => _node.TransformPointWorldToLocal(p);
    #endregion
}

