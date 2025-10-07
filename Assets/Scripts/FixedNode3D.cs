using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using FixedNode3DInternal;

/*
 * FixedNode3D (Pre-FastPath Snapshot, modified to use ThreadStatic stack)
 * - 包含层级版本号惰性刷新（_worldVersion / _cachedParentWorldVersion）
 * - 不包含 fast path IsWorldUpToDateFast / EnsureGlobalUpToDateForQuery
 * - 现在使用 ThreadStatic 共享栈（_tlsUpdateStack)，替换原先的每实例临时栈 List
 * - 不包含 Reparent keepWorld=false 跳过旧 world 捕获优化
 * - 不包含 FastPath / Reparent 相关新增统计字段
 * - 修改日期: 2025-10-07 (把 _tlsUpdateStack 改成 ThreadStatic)
 */
public class FixedNode3D
{
    [ThreadStatic] private static List<FixedNode3D> _tlsUpdateStack; // 线程本地共享栈（原每实例临时链栈）

    #region Dirty Flags
    private const uint DIRTY_NONE             = 0;
    private const uint DIRTY_LOCAL_TRANSFORM  = 1 << 0;
    private const uint DIRTY_EULER_SCALE      = 1 << 1;
    private const uint DIRTY_GLOBAL_TRANSFORM = 1 << 2;
    #endregion

    #region Rotation Order
    //测试unity实际上使用的是YXZ顺序
    public enum RotationOrder : byte { YXZ = 0, XYZ = 1, ZXY = 2 }
    #endregion

    #region Hierarchy
    private FixedNode3D _parent;
    private readonly List<FixedNode3D> _children = new List<FixedNode3D>(4);
    #endregion

    #region Local Representations
    private TMatrix3x4 _localTransform = TMatrix3x4.Identity;
    private TSVector _eulerRotation = TSVector.zero;
    private TSVector _localScale = TSVector.one;
    private TSQuaternion _localRotationQuat = TSQuaternion.identity;
    private RotationOrder _rotationOrder = RotationOrder.YXZ;
    private uint _dirtyMask = DIRTY_NONE;
    private bool _hasShear = false;
    private const bool _retainShearOnEdit = false;
    private static readonly FP SHEAR_EPS = FP.EN6;
    #endregion

    #region Global Cache & Versioning
    private TMatrix3x4 _globalTransform = TMatrix3x4.Identity;
    private bool _isTopLevel = false;
    private uint _localVersion = 1;   // 仅统计用
    private uint _worldVersion = 1;   // 每次 world 重建 +1
    private uint _cachedParentWorldVersion = 0; // 上次重建时父版本号

#if FIXEDNODE3D_PROFILE
    public static long Stat_WorldQueryCount = 0;
    public static long Stat_WorldRecomputeCount = 0;
    public static long Stat_LocalMatrixRecomputeCount = 0;
    public static long Stat_EulerScaleExtractCount = 0;
    public static long Stat_WorldRotScaleCacheMiss = 0;
    public static long Stat_InverseCacheMiss = 0;
    public static long Stat_MaxBatchRecomputeDepth = 0;
    public static void ResetStats()
    {
        Stat_WorldQueryCount = 0;
        Stat_WorldRecomputeCount = 0;
        Stat_LocalMatrixRecomputeCount = 0;
        Stat_EulerScaleExtractCount = 0;
        Stat_WorldRotScaleCacheMiss = 0;
        Stat_InverseCacheMiss = 0;
        Stat_MaxBatchRecomputeDepth = 0;
    }
#endif
    #endregion

    #region Cached World Decomposition
    private TSQuaternion _cachedWorldRotationQuat = TSQuaternion.identity;
    private TSVector _cachedWorldScale = TSVector.one;
    private uint _cachedWorldRSVersion = 0;
    private TSVector _cachedWorldRight = TSVector.right;
    private TSVector _cachedWorldUp = TSVector.up;
    private TSVector _cachedWorldForward = TSVector.forward;
    private TMatrix3x4 _cachedInverseGlobalTransform;
    private uint _cachedInverseVersion = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateWorldRotScaleCacheIfNeeded()
    {
        // 重要修复：先确保全局矩阵依据本地脏位刷新，再判断缓存版本。
        // 之前的实现先用版本号短路，导致 localRotation 改变后 worldVersion 尚未递增，
        // _cachedWorldRSVersion == _worldVersion 直接返回，worldRotation 停留在第一次值。
        UpdateGlobalTransformIfNeeded();
        if (_cachedWorldRSVersion == _worldVersion) return;
        FixedNode3DMath.ExtractScale(_globalTransform, out FP sx, out FP sy, out FP sz);
        _cachedWorldScale = new TSVector(sx, sy, sz);
        FixedNode3DMath.ExtractRotationColumnsNormalized(
            _globalTransform, sx, sy, sz,
            out FP r00, out FP r01, out FP r02,
            out FP r10, out FP r11, out FP r12,
            out FP r20, out FP r21, out FP r22);
        FixedNode3DMath.RotationMatrixToQuaternion(
            r00, r01, r02,
            r10, r11, r12,
            r20, r21, r22,
            out _cachedWorldRotationQuat);
        _cachedWorldRight = new TSVector(r00, r10, r20);
        _cachedWorldUp = new TSVector(r01, r11, r21);
        _cachedWorldForward = new TSVector(r02, r12, r22);
        _cachedWorldRSVersion = _worldVersion;
#if FIXEDNODE3D_PROFILE
        Stat_WorldRotScaleCacheMiss++;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateInverseCacheIfNeeded()
    {
        if (_cachedInverseVersion == _worldVersion) return;
        UpdateGlobalTransformIfNeeded();
        _cachedInverseGlobalTransform = _globalTransform.Inverse();
        _cachedInverseVersion = _worldVersion;
#if FIXEDNODE3D_PROFILE
        Stat_InverseCacheMiss++;
#endif
    }
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
        {
            _dirtyMask |= DIRTY_GLOBAL_TRANSFORM;
            _localVersion++;
        }
    }
    private void MarkGlobalDirtyDownwards() { /* versioning model: intentionally no-op in pre-fastPath baseline */ }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool IsDirty(uint bits) => (_dirtyMask & bits) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void ClearDirty(uint bits) { _dirtyMask &= ~bits; }
    #endregion

    #region Local Properties
    public TSVector localPosition
    {
        get => new TSVector(_localTransform.m03, _localTransform.m13, _localTransform.m23);
        set { var cur = localPosition; if (cur != value) { _localTransform.m03 = value.x; _localTransform.m13 = value.y; _localTransform.m23 = value.z; SetDirty(DIRTY_GLOBAL_TRANSFORM); } }
    }
    public TSQuaternion localRotation
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _localRotationQuat; }
        set {
            if (!TSQuaternion.ValueEquals(_localRotationQuat, value)) {
                _localRotationQuat = value; _localRotationQuat.Normalize();
                // 同步欧拉缓存，避免后续基于 localEuler 的增量使用陈旧基线
                _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }
    public TSVector localScale
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _localScale; }
        set { var sc = SanitizeScale(value); if (_localScale != sc) { _localScale = sc; SetDirty(DIRTY_LOCAL_TRANSFORM); } }
    }
    public TSVector localEuler
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _eulerRotation; }
        set { if (_eulerRotation != value) { _eulerRotation = value; _localRotationQuat = EulerToQuaternion(_eulerRotation, _rotationOrder); SetDirty(DIRTY_LOCAL_TRANSFORM); } }
    }
    public RotationOrder rotationOrder
    {
        get => _rotationOrder;
        set { if (_rotationOrder != value) { UpdateLocalEulerScaleIfNeeded(); _rotationOrder = value; _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder); } }
    }
    public bool hasShear => _hasShear;
    public bool isTopLevel => _isTopLevel;
    public uint worldVersion => _worldVersion;
    public uint localVersion => _localVersion;
    #endregion

    #region World Properties
    public TSVector worldPosition { get { UpdateGlobalTransformIfNeeded(); return new TSVector(_globalTransform.m03, _globalTransform.m13, _globalTransform.m23); } }
    public TSQuaternion worldRotation { get { UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldRotationQuat; } }
    public TSVector worldScale { get { UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldScale; } }
    public TSVector worldRight { get { UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldRight; } }
    public TSVector worldUp { get { UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldUp; } }
    public TSVector worldForward { get { UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldForward; } }
    public TMatrix3x4 localMatrix { get { UpdateLocalMatrixIfNeeded(); return _localTransform; } }
    public TMatrix3x4 globalMatrix { get { UpdateGlobalTransformIfNeeded(); return _globalTransform; } }
    public TMatrix3x4 inverseGlobalMatrix { get { UpdateInverseCacheIfNeeded(); return _cachedInverseGlobalTransform; } }

    public void GetWorldSnapshot(out TSVector pos, out TSQuaternion rot, out TSVector scale, out TMatrix3x4 mat)
    {
        UpdateGlobalTransformIfNeeded();
        UpdateWorldRotScaleCacheIfNeeded();
        mat = _globalTransform;
        pos = new TSVector(mat.m03, mat.m13, mat.m23);
        rot = _cachedWorldRotationQuat;
        scale = _cachedWorldScale;
    }
    #endregion

    #region Parent / Reparent
    public FixedNode3D parent => _parent;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool WouldIntroduceCycle(FixedNode3D newParent)
    {
        for (var p = newParent; p != null; p = p._parent) if (p == this) return true; return false;
    }

    private static int _cyclePrevented = 0;
    public static int cyclePreventedCount => _cyclePrevented;
    public static void ResetCycleStats() { _cyclePrevented = 0; }

    public void SetParent(FixedNode3D newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;
        if (newParent != null && WouldIntroduceCycle(newParent)) { _cyclePrevented++; return; }
        UpdateGlobalTransformIfNeeded();
        var oldWorld = _globalTransform;
        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);
        if (!_isTopLevel && keepWorld)
        {
            if (_parent == null) _localTransform = oldWorld; else { _parent.UpdateGlobalTransformIfNeeded(); var invP = Inverse3x4(_parent._globalTransform); _localTransform = invP * oldWorld; }
            SetDirty(DIRTY_EULER_SCALE); ClearDirty(DIRTY_LOCAL_TRANSFORM); _globalTransform = oldWorld; _worldVersion++; _cachedParentWorldVersion = _parent != null ? _parent._worldVersion : 0; ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
        else
        {
            SetDirty(DIRTY_GLOBAL_TRANSFORM); _cachedParentWorldVersion = 0;
        }
    }

    public void SetAsTopLevel(bool enabled, bool keepGlobal = true)
    {
        if (_isTopLevel == enabled) return;
        if (enabled)
        {
            UpdateGlobalTransformIfNeeded();
            if (keepGlobal) { _localTransform = _globalTransform; SetDirty(DIRTY_EULER_SCALE); ClearDirty(DIRTY_LOCAL_TRANSFORM); }
        }
        else
        {
            if (_parent != null && keepGlobal)
            {
                UpdateGlobalTransformIfNeeded(); _parent.UpdateGlobalTransformIfNeeded(); var invP = Inverse3x4(_parent._globalTransform); _localTransform = invP * _globalTransform; SetDirty(DIRTY_EULER_SCALE); ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
        _isTopLevel = enabled; SetDirty(DIRTY_GLOBAL_TRANSFORM); _cachedParentWorldVersion = _parent != null ? _parent._worldVersion : 0;
    }
    #endregion

    #region Set World
    public void SetWorldPosition(in TSVector wPos)
    {
        if (_isTopLevel || _parent == null) { localPosition = wPos; return; }
        _parent.UpdateGlobalTransformIfNeeded(); var invP = Inverse3x4(_parent._globalTransform); localPosition = invP.MultiplyPoint(wPos);
    }
    public void SetWorldRotation(in TSQuaternion wRot)
    {
        if (_isTopLevel || _parent == null) localRotation = wRot; else { TSQuaternion pRot = _parent.worldRotation; var inv = TSQuaternion.Inverse(pRot); var lr = inv * wRot; lr.Normalize(); localRotation = lr; }
    }
    public void SetWorldTRS(in TSVector wPos, in TSQuaternion wRot, in TSVector wScale)
    {
        FixedNode3DMath.ComposeMatrix(wRot, wScale, wPos, out var w);
        if (_isTopLevel || _parent == null) _localTransform = w; else { _parent.UpdateGlobalTransformIfNeeded(); var invP = Inverse3x4(_parent._globalTransform); _localTransform = invP * w; }
        SetDirty(DIRTY_EULER_SCALE); ClearDirty(DIRTY_LOCAL_TRANSFORM); SetDirty(DIRTY_GLOBAL_TRANSFORM);
    }
    #endregion

    #region Direct Local Setters
    public void SetLocalQuaternion(TSQuaternion q, in TSVector? optScale = null)
    {
        q.Normalize(); UpdateLocalEulerScaleIfNeeded(); TSVector sc = optScale.HasValue ? SanitizeScale(optScale.Value) : _localScale; _localRotationQuat = q; _eulerRotation = QuaternionToEuler(q, _rotationOrder); _localScale = sc; SetDirty(DIRTY_LOCAL_TRANSFORM);
    }
    public void SetLocalTRS(in TSVector pos, TSQuaternion rot, in TSVector scale)
    {
        rot.Normalize(); _localRotationQuat = rot; _eulerRotation = QuaternionToEuler(rot, _rotationOrder); _localScale = SanitizeScale(scale); _localTransform.m03 = pos.x; _localTransform.m13 = pos.y; _localTransform.m23 = pos.z; SetDirty(DIRTY_LOCAL_TRANSFORM);
    }
    public void SetLocalMatrix(in TMatrix3x4 m)
    {
        _localTransform = m; SetDirty(DIRTY_EULER_SCALE); ClearDirty(DIRTY_LOCAL_TRANSFORM); SetDirty(DIRTY_GLOBAL_TRANSFORM);
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
                FixedNode3DMath.ApplyNewRotScaleWithOptionalShear(_localTransform, _localRotationQuat, _localScale, pos, out _localTransform);
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
            if (_retainShearOnEdit) FixedNode3DMath.DetectShear(_localTransform, out _hasShear); else _hasShear = false;
#if FIXEDNODE3D_PROFILE
            Stat_LocalMatrixRecomputeCount++;
#endif
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
        }
    }
    private void UpdateLocalEulerScaleIfNeeded()
    {
        if (IsDirty(DIRTY_EULER_SCALE))
        {
            FixedNode3DMath.ExtractScale(_localTransform, out FP sx, out FP sy, out FP sz); _localScale = new TSVector(sx, sy, sz);
            FixedNode3DMath.ExtractRotationColumnsNormalized(_localTransform, sx, sy, sz,
                out FP ir00, out FP ir01, out FP ir02,
                out FP ir10, out FP ir11, out FP ir12,
                out FP ir20, out FP ir21, out FP ir22);
            FixedNode3DMath.RotationMatrixToQuaternion(ir00, ir01, ir02, ir10, ir11, ir12, ir20, ir21, ir22, out _localRotationQuat);
            _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);
            if (_retainShearOnEdit) FixedNode3DMath.DetectShear(_localTransform, out _hasShear); else { FixedNode3DMath.DetectShear(_localTransform, out _hasShear); if (_hasShear && !_retainShearOnEdit) { /* will be cleared on rebuild */ } }
#if FIXEDNODE3D_PROFILE
            Stat_EulerScaleExtractCount++;
#endif
            ClearDirty(DIRTY_EULER_SCALE);
        }
    }
    private void UpdateGlobalTransformIfNeeded()
    {
#if FIXEDNODE3D_PROFILE
        Stat_WorldQueryCount++;
#endif
        UpdateLocalMatrixIfNeeded();
        if (!_isTopLevel && _parent != null)
        {
            if (!IsDirty(DIRTY_GLOBAL_TRANSFORM) && _cachedParentWorldVersion == _parent._worldVersion) return;
        }
        else
        {
            if (!IsDirty(DIRTY_GLOBAL_TRANSFORM)) return;
        }
        var stack = _tlsUpdateStack ??= new List<FixedNode3D>(16); stack.Clear();
        FixedNode3D cur = this;
        while (true)
        {
            cur.UpdateLocalMatrixIfNeeded();
            bool need = cur.IsDirty(DIRTY_GLOBAL_TRANSFORM);
            uint pwv = 0; var p = cur._parent;
            if (!cur._isTopLevel && p != null)
            {
                p.UpdateLocalMatrixIfNeeded(); pwv = p._worldVersion;
                if (cur._cachedParentWorldVersion != pwv) need = true;
            }
            if (need)
            {
                stack.Add(cur);
                if (p != null) { cur = p; continue; }
            }
            break;
        }
        if (stack.Count == 0) return;
#if FIXEDNODE3D_PROFILE
        if (stack.Count > Stat_MaxBatchRecomputeDepth) Stat_MaxBatchRecomputeDepth = stack.Count;
#endif
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var n = stack[i];
            if (n._isTopLevel || n._parent == null) n._globalTransform = n._localTransform; else n._globalTransform = n._parent._globalTransform * n._localTransform;
            n._worldVersion++; n._cachedParentWorldVersion = n._parent != null ? n._parent._worldVersion : 0;
#if FIXEDNODE3D_PROFILE
            Stat_WorldRecomputeCount++;
#endif
            n.ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
        stack.Clear();
    }
    #endregion

    #region Math Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static TSVector SanitizeScale(TSVector s) { FP min = FP.EN6; if (FP.Abs(s.x) < min) s.x = s.x >= (FP)0 ? min : (FP)0 - min; if (FP.Abs(s.y) < min) s.y = s.y >= (FP)0 ? min : (FP)0 - min; if (FP.Abs(s.z) < min) s.z = s.z >= (FP)0 ? min : (FP)0 - min; return s; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static TSQuaternion EulerToQuaternion(in TSVector eulerRad, in RotationOrder order)
    {
        switch (order)
        {
            case RotationOrder.YXZ: FixedNode3DMath.EulerToQuaternion_YXZ(eulerRad, out TSQuaternion qyxz); return qyxz;
            case RotationOrder.ZXY: FixedNode3DMath.EulerToQuaternion_UnityZXY(eulerRad, out TSQuaternion quzxy); return quzxy;
            case RotationOrder.XYZ:
            default: FixedNode3DMath.EulerToQuaternion_XYZ(eulerRad, out TSQuaternion qxyz); return qxyz;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static TSVector QuaternionToEuler(in TSQuaternion q, in RotationOrder order)
    {
        switch (order)
        {
            case RotationOrder.YXZ: FixedNode3DMath.QuaternionToEuler_YXZ(q, out TSVector eYXZ); return eYXZ;
            case RotationOrder.ZXY: FixedNode3DMath.QuaternionToEuler_UnityZXY(q, out TSVector eZXY); return eZXY;
            case RotationOrder.XYZ:
            default: FixedNode3DMath.QuaternionToEuler_XYZ(q, out TSVector eXYZ); return eXYZ;
        }
    }
    private static TMatrix3x4 Inverse3x4(TMatrix3x4 m) => m.Inverse();
    #endregion

    #region Space Conversion
    public TSVector TransformPointLocalToWorld(in TSVector p) { UpdateGlobalTransformIfNeeded(); return _globalTransform.MultiplyPoint(p); }
    public TSVector TransformPointWorldToLocal(in TSVector p) { UpdateInverseCacheIfNeeded(); return _cachedInverseGlobalTransform.MultiplyPoint(p); }
    public TSVector LocalVectorToWorld(in TSVector v) { UpdateGlobalTransformIfNeeded(); return _globalTransform.MultiplyVector(v); }
    public TSVector WorldVectorToLocal(in TSVector v) { UpdateInverseCacheIfNeeded(); return _cachedInverseGlobalTransform.MultiplyVector(v); }
    public void TranslateLocal(in TSVector delta)
    {
        UpdateLocalMatrixIfNeeded(); UpdateLocalEulerScaleIfNeeded();
        FixedNode3DMath.ExtractScale(_localTransform, out FP sx, out FP sy, out FP sz);
        FixedNode3DMath.ExtractRotationColumnsNormalized(_localTransform, sx, sy, sz,
            out FP r00, out FP r01, out FP r02,
            out FP r10, out FP r11, out FP r12,
            out FP r20, out FP r21, out FP r22);
        TSVector d = new TSVector(r00 * delta.x + r01 * delta.y + r02 * delta.z, r10 * delta.x + r11 * delta.y + r12 * delta.z, r20 * delta.x + r21 * delta.y + r22 * delta.z);
        localPosition = localPosition + d;
    }
    public void TranslateWorld(in TSVector delta) { localPosition = localPosition + (_isTopLevel || _parent == null ? delta : WorldVectorToLocal(delta)); }
    #endregion

    #region Debug
    public override string ToString() => $"FixedNode3D LocalPos={localPosition}, Euler={localEuler}, Scale={localScale}, Dirty=0x{_dirtyMask:X}, worldV={_worldVersion}, parentWVCache={_cachedParentWorldVersion}";
    #endregion
}
