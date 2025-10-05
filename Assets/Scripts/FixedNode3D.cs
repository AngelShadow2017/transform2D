using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using FixedNode3DInternal; // 引入 Burst 数学层

/*
 * 优化版 FixedNode3D:
 * - 逻辑与原本一致
 * - 绝大多数 math 计算调用 FixedNode3DMath (Burst)
 * - 仍保持 public 接口 (localPosition/localRotation/localScale/worldXxx ...)
 * - 脏标记 & 层级逻辑不变
 */

public class FixedNode3D
{
    #region Dirty Flags
    private const uint DIRTY_NONE             = 0;
    private const uint DIRTY_LOCAL_TRANSFORM  = 1 << 0;
    private const uint DIRTY_EULER_SCALE      = 1 << 1;
    private const uint DIRTY_GLOBAL_TRANSFORM = 1 << 2;
    #endregion

    #region Rotation Order
    public enum RotationOrder : byte
    {
        YXZ = 0,
        XYZ = 1,
        UnityZXY = 2
    }
    #endregion

    #region Hierarchy
    private FixedNode3D _parent;
    private readonly List<FixedNode3D> _children = new List<FixedNode3D>(4);
    #endregion

    #region Local Representations
    private TMatrix3x4 _localTransform = TMatrix3x4.Identity;
    private TSVector _eulerRotation = TSVector.zero; // 弧度
    private TSVector _localScale = TSVector.one;
    private TSQuaternion _localRotationQuat = TSQuaternion.identity;

    private RotationOrder _rotationOrder = RotationOrder.YXZ;
    private uint _dirtyMask = DIRTY_NONE;

    private bool _hasShear = false;
    private const bool _retainShearOnEdit = false; // 默认不保留
    private static readonly FP SHEAR_EPS = FP.EN6;
    #endregion

    #region Global Cache
    private TMatrix3x4 _globalTransform = TMatrix3x4.Identity;
    private bool _isTopLevel = false;
    #endregion

    #region Constructors
    public FixedNode3D() { }
    public FixedNode3D(FixedNode3D parent, bool keepWorld = true) { SetParent(parent, keepWorld); }
    #endregion

    #region Dirty Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDirty(uint bits)
    {
        _dirtyMask |= bits;
        if ((bits & (DIRTY_LOCAL_TRANSFORM | DIRTY_EULER_SCALE)) != 0)
            _dirtyMask |= DIRTY_GLOBAL_TRANSFORM;

        if ((bits & (DIRTY_LOCAL_TRANSFORM | DIRTY_EULER_SCALE | DIRTY_GLOBAL_TRANSFORM)) != 0)
        {
            for (int i = 0; i < _children.Count; i++)
                _children[i].MarkGlobalDirtyDownwards();
        }
    }

    private void MarkGlobalDirtyDownwards()
    {
        if ((_dirtyMask & DIRTY_GLOBAL_TRANSFORM) != 0)
            return;
        _dirtyMask |= DIRTY_GLOBAL_TRANSFORM;
        for (int i = 0; i < _children.Count; i++)
            _children[i].MarkGlobalDirtyDownwards();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDirty(uint bits) => (_dirtyMask & bits) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearDirty(uint bits) { _dirtyMask &= ~bits; }
    #endregion

    #region Public Local Properties
    public TSVector localPosition
    {
        get => new TSVector(_localTransform.m03, _localTransform.m13, _localTransform.m23);
        set
        {
            TSVector cur = localPosition;
            if (cur != value)
            {
                _localTransform.m03 = value.x;
                _localTransform.m13 = value.y;
                _localTransform.m23 = value.z;
                SetDirty(DIRTY_GLOBAL_TRANSFORM);
            }
        }
    }

    public TSQuaternion localRotation
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _localRotationQuat; }
        set
        {
            if (!TSQuaternion.ValueEquals(_localRotationQuat, value))
            {
                _localRotationQuat = value; _localRotationQuat.Normalize();
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }

    public TSVector localScale
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _localScale; }
        set
        {
            TSVector sanitized = SanitizeScale(value);
            if (_localScale != sanitized)
            {
                _localScale = sanitized;
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }

    public TSVector localEuler
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _eulerRotation; }
        set
        {
            if (_eulerRotation != value)
            {
                _eulerRotation = value;
                _localRotationQuat = EulerToQuaternion(_eulerRotation, _rotationOrder);
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }

    public RotationOrder rotationOrder
    {
        get => _rotationOrder;
        set
        {
            if (_rotationOrder != value)
            {
                UpdateLocalEulerScaleIfNeeded();
                _rotationOrder = value;
                _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);
            }
        }
    }

    public bool hasShear => _hasShear;
    public bool retainShearOnEdit => _retainShearOnEdit;
    #endregion

    #region World Properties
    public TSVector worldPosition
    {
        get { UpdateGlobalTransformIfNeeded(); return new TSVector(_globalTransform.m03, _globalTransform.m13, _globalTransform.m23); }
    }

    public TSQuaternion worldRotation
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            ExtractRotationFromLinear(_globalTransform,
                out FP r00, out FP r01, out FP r02,
                out FP r10, out FP r11, out FP r12,
                out FP r20, out FP r21, out FP r22);
            return RotationMatrixToQuat(r00, r01, r02, r10, r11, r12, r20, r21, r22);
        }
    }

    public TSVector worldScale
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            FixedNode3DMath.ColumnLength(_globalTransform.m00, _globalTransform.m10, _globalTransform.m20, out FP sx);
            FixedNode3DMath.ColumnLength(_globalTransform.m01, _globalTransform.m11, _globalTransform.m21, out FP sy);
            FixedNode3DMath.ColumnLength(_globalTransform.m02, _globalTransform.m12, _globalTransform.m22, out FP sz);
            return new TSVector(sx, sy, sz);
        }
    }

    public TMatrix3x4 localMatrix { get { UpdateLocalMatrixIfNeeded(); return _localTransform; } }
    public TMatrix3x4 globalMatrix { get { UpdateGlobalTransformIfNeeded(); return _globalTransform; } }
    #endregion

    #region Parent / Reparent
    public FixedNode3D parent => _parent;

    public void SetParent(FixedNode3D newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;

        UpdateGlobalTransformIfNeeded();
        var oldWorld = _globalTransform;

        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        if (!_isTopLevel && keepWorld)
        {
            if (_parent == null)
            {
                _localTransform = oldWorld;
            }
            else
            {
                _parent.UpdateGlobalTransformIfNeeded();
                var invP = oldWorld; // reuse var
                invP = Inverse3x4(_parent._globalTransform);
                _localTransform = invP * oldWorld;
            }
            SetDirty(DIRTY_EULER_SCALE);
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
        }
        else
        {
            SetDirty(DIRTY_GLOBAL_TRANSFORM);
        }
    }

    public void SetAsTopLevel(bool enabled, bool keepGlobal = true)
    {
        if (_isTopLevel == enabled) return;
        if (enabled)
        {
            UpdateGlobalTransformIfNeeded();
            if (keepGlobal)
            {
                _localTransform = _globalTransform;
                SetDirty(DIRTY_EULER_SCALE);
                ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
        else
        {
            if (_parent != null && keepGlobal)
            {
                UpdateGlobalTransformIfNeeded();
                _parent.UpdateGlobalTransformIfNeeded();
                var invP = Inverse3x4(_parent._globalTransform);
                _localTransform = invP * _globalTransform;
                SetDirty(DIRTY_EULER_SCALE);
                ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
        _isTopLevel = enabled;
        SetDirty(DIRTY_GLOBAL_TRANSFORM);
    }

    public bool isTopLevel => _isTopLevel;
    #endregion

    #region Set World
    public void SetWorldPosition(TSVector wPos)
    {
        if (_isTopLevel || _parent == null) { localPosition = wPos; return; }
        _parent.UpdateGlobalTransformIfNeeded();
        var invP = Inverse3x4(_parent._globalTransform);
        localPosition = invP.MultiplyPoint(wPos);
    }

    public void SetWorldRotation(TSQuaternion wRot)
    {
        if (_isTopLevel || _parent == null)
        {
            localRotation = wRot;
        }
        else
        {
            TSQuaternion pRot = _parent.worldRotation;
            var inv = TSQuaternion.Inverse(pRot);
            var lr = inv * wRot; lr.Normalize();
            localRotation = lr;
        }
    }

    public void SetWorldTRS(TSVector wPos, TSQuaternion wRot, TSVector wScale)
    {
        FixedNode3DMath.ComposeMatrix(wRot, wScale, wPos, out var w);
        if (_isTopLevel || _parent == null)
        {
            _localTransform = w;
        }
        else
        {
            _parent.UpdateGlobalTransformIfNeeded();
            var invP = Inverse3x4(_parent._globalTransform);
            _localTransform = invP * w;
        }
        SetDirty(DIRTY_EULER_SCALE);
        ClearDirty(DIRTY_LOCAL_TRANSFORM);
        SetDirty(DIRTY_GLOBAL_TRANSFORM);
    }
    #endregion

    #region Direct Local Setters
    public void SetLocalQuaternion(TSQuaternion q, TSVector? optScale = null)
    {
        q.Normalize();
        UpdateLocalEulerScaleIfNeeded();
        TSVector sc = optScale.HasValue ? SanitizeScale(optScale.Value) : _localScale;
        _localRotationQuat = q;
        _eulerRotation = QuaternionToEuler(q, _rotationOrder);
        _localScale = sc;
        SetDirty(DIRTY_LOCAL_TRANSFORM);
    }

    public void SetLocalTRS(TSVector pos, TSQuaternion rot, TSVector scale)
    {
        rot.Normalize();
        _localRotationQuat = rot;
        _eulerRotation = QuaternionToEuler(rot, _rotationOrder);
        _localScale = SanitizeScale(scale);
        _localTransform.m03 = pos.x;
        _localTransform.m13 = pos.y;
        _localTransform.m23 = pos.z;
        SetDirty(DIRTY_LOCAL_TRANSFORM);
    }

    public void SetLocalMatrix(TMatrix3x4 m)
    {
        _localTransform = m;
        SetDirty(DIRTY_EULER_SCALE);
        ClearDirty(DIRTY_LOCAL_TRANSFORM);
        SetDirty(DIRTY_GLOBAL_TRANSFORM);
    }
    #endregion

    #region Update Routines
    private void UpdateLocalMatrixIfNeeded()
    {
        if (IsDirty(DIRTY_LOCAL_TRANSFORM))
        {
            TSVector pos = localPosition;

            if (_retainShearOnEdit && _hasShear)
            {
                FixedNode3DMath.ApplyNewRotScaleWithOptionalShear(
                    _localTransform,
                    _localRotationQuat,
                    _localScale,
                    pos,
                    out _localTransform);
            }
            else
            {
                FixedNode3DMath.ComposeRotationScale(_localRotationQuat, _localScale,
                    out FP r00, out FP r01, out FP r02,
                    out FP r10, out FP r11, out FP r12,
                    out FP r20, out FP r21, out FP r22);

                _localTransform.m00 = r00; _localTransform.m01 = r01; _localTransform.m02 = r02;
                _localTransform.m10 = r10; _localTransform.m11 = r11; _localTransform.m12 = r12;
                _localTransform.m20 = r20; _localTransform.m21 = r21; _localTransform.m22 = r22;
                _localTransform.m03 = pos.x; _localTransform.m13 = pos.y; _localTransform.m23 = pos.z;
            }

            if (_retainShearOnEdit)
            {
                FixedNode3DMath.DetectShear(_localTransform, out _hasShear);
            }
            else
            {
                _hasShear = false;
            }
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
        }
    }

    private void UpdateLocalEulerScaleIfNeeded()
    {
        if (IsDirty(DIRTY_EULER_SCALE))
        {
            FixedNode3DMath.ExtractScale(_localTransform, out FP sx, out FP sy, out FP sz);
            _localScale = new TSVector(sx, sy, sz);

            FixedNode3DMath.ExtractRotationColumnsNormalized(
                _localTransform, sx, sy, sz,
                out FP ir00, out FP ir01, out FP ir02,
                out FP ir10, out FP ir11, out FP ir12,
                out FP ir20, out FP ir21, out FP ir22);

            FixedNode3DMath.RotationMatrixToQuaternion(
                ir00, ir01, ir02,
                ir10, ir11, ir12,
                ir20, ir21, ir22,
                out _localRotationQuat);

            _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);

            if (_retainShearOnEdit)
            {
                FixedNode3DMath.DetectShear(_localTransform, out _hasShear);
            }
            else
            {
                // 快速检测一次（即使不保留，也可供用户查看 hasShear）
                FixedNode3DMath.DetectShear(_localTransform, out _hasShear);
                if (_hasShear && !_retainShearOnEdit)
                {
                    // 不保留模式：后面 UpdateLocalMatrixIfNeeded 构建会清除
                }
            }

            ClearDirty(DIRTY_EULER_SCALE);
        }
    }

    private void UpdateGlobalTransformIfNeeded()
    {
        UpdateLocalMatrixIfNeeded();
        if (IsDirty(DIRTY_GLOBAL_TRANSFORM))
        {
            if (_isTopLevel || _parent == null)
                _globalTransform = _localTransform;
            else
            {
                _parent.UpdateGlobalTransformIfNeeded();
                _globalTransform = _parent._globalTransform * _localTransform;
            }
            ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
    }
    #endregion

    #region Math Wrappers

    private static TSVector SanitizeScale(TSVector s)
    {
        FP min = FP.EN6;
        if (FP.Abs(s.x) < min) s.x = s.x >= (FP)0 ? min : (FP)0 - min;
        if (FP.Abs(s.y) < min) s.y = s.y >= (FP)0 ? min : (FP)0 - min;
        if (FP.Abs(s.z) < min) s.z = s.z >= (FP)0 ? min : (FP)0 - min;
        return s;
    }

    private static void ExtractRotationFromLinear(
        in TMatrix3x4 m,
        out FP r00, out FP r01, out FP r02,
        out FP r10, out FP r11, out FP r12,
        out FP r20, out FP r21, out FP r22)
    {
        FixedNode3DMath.ExtractScale(m, out FP sx, out FP sy, out FP sz);
        FixedNode3DMath.ExtractRotationColumnsNormalized(
            m, sx, sy, sz,
            out r00, out r01, out r02,
            out r10, out r11, out r12,
            out r20, out r21, out r22);
    }

    private static TSQuaternion RotationMatrixToQuat(
        FP r00, FP r01, FP r02,
        FP r10, FP r11, FP r12,
        FP r20, FP r21, FP r22)
    {
        FixedNode3DMath.RotationMatrixToQuaternion(
            r00, r01, r02,
            r10, r11, r12,
            r20, r21, r22,
            out TSQuaternion q);
        return q;
    }

    private static TSQuaternion EulerToQuaternion(TSVector eulerRad, RotationOrder order)
    {
        switch (order)
        {
            case RotationOrder.YXZ:
                FixedNode3DMath.EulerToQuaternion_YXZ(eulerRad, out TSQuaternion qyxz);
                return qyxz;
            case RotationOrder.UnityZXY:
                FixedNode3DMath.EulerToQuaternion_UnityZXY(eulerRad, out TSQuaternion quzxy);
                return quzxy;
            case RotationOrder.XYZ:
            default:
                FixedNode3DMath.EulerToQuaternion_XYZ(eulerRad, out TSQuaternion qxyz);
                return qxyz;
        }
    }

    private static TSVector QuaternionToEuler(TSQuaternion q, RotationOrder order)
    {
        switch (order)
        {
            case RotationOrder.YXZ:
                FixedNode3DMath.QuaternionToEuler_YXZ(q, out TSVector eYXZ);
                return eYXZ;
            case RotationOrder.UnityZXY:
                FixedNode3DMath.QuaternionToEuler_UnityZXY(q, out TSVector eZXY);
                return eZXY;
            case RotationOrder.XYZ:
            default:
                FixedNode3DMath.QuaternionToEuler_XYZ(q, out TSVector eXYZ);
                return eXYZ;
        }
    }

    private static TMatrix3x4 Inverse3x4(in TMatrix3x4 m) => m.Inverse();

    #endregion

    #region Space Conversion Helpers
    public void TranslateLocal(TSVector delta)
    {
        UpdateLocalMatrixIfNeeded();
        UpdateLocalEulerScaleIfNeeded();
        ExtractRotationFromLinear(_localTransform,
            out FP r00, out FP r01, out FP r02,
            out FP r10, out FP r11, out FP r12,
            out FP r20, out FP r21, out FP r22);

        TSVector d = new TSVector(
            r00 * delta.x + r01 * delta.y + r02 * delta.z,
            r10 * delta.x + r11 * delta.y + r12 * delta.z,
            r20 * delta.x + r21 * delta.y + r22 * delta.z);
        localPosition = localPosition + d;
    }

    public void TranslateWorld(TSVector delta)
    {
        localPosition = localPosition + (_isTopLevel || _parent == null
            ? delta
            : WorldVectorToLocal(delta));
    }

    public TSVector TransformPointLocalToWorld(TSVector p)
    {
        UpdateGlobalTransformIfNeeded();
        return _globalTransform.MultiplyPoint(p);
    }

    public TSVector TransformPointWorldToLocal(TSVector p)
    {
        UpdateGlobalTransformIfNeeded();
        var inv = Inverse3x4(_globalTransform);
        return inv.MultiplyPoint(p);
    }

    public TSVector LocalVectorToWorld(TSVector v)
    {
        UpdateGlobalTransformIfNeeded();
        return _globalTransform.MultiplyVector(v);
    }

    public TSVector WorldVectorToLocal(TSVector v)
    {
        UpdateGlobalTransformIfNeeded();
        var inv = Inverse3x4(_globalTransform);
        return inv.MultiplyVector(v);
    }
    #endregion

    #region Debug
    public override string ToString()
    {
        return $"FixedNode3D LocalPos={localPosition}, Euler={localEuler}, Scale={localScale}, Shear={_hasShear}, Order={_rotationOrder}, Dirty=0x{_dirtyMask:X}";
    }
    #endregion
}