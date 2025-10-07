
#if false
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
 *
 * 2025-10 结构性优化：
 * 引入 "版本号惰性刷新"：不再在本地变化时向子树递归标记 DIRTY_GLOBAL_TRANSFORM，
 * 子节点在查询 worldMatrix 时检测父节点 worldVersion 是否变更，若不一致再局部重算，
 * 大幅降低大子树频繁局部修改时的标记放大成本。
 * 可在编译定义 FIXEDNODE3D_PROFILE 下采样统计命中/重算次数。
 */

public class FixedNode3D
{
    [ThreadStatic] private static List<FixedNode3D> _tlsUpdateStack; // thread local shared stack (was per-instance)
    #region Dirty Flags
    private const uint DIRTY_NONE             = 0;
    private const uint DIRTY_LOCAL_TRANSFORM  = 1 << 0; // 需要重建本地矩阵 (由欧拉/四元数/scale 等引起)
    private const uint DIRTY_EULER_SCALE      = 1 << 1; // 需要从矩阵反解出 euler / scale
    private const uint DIRTY_GLOBAL_TRANSFORM = 1 << 2; // 自身 world 需要重算（不再级联向下传播）
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

    // 版本号惰性刷新新增字段：
    // _localVersion : 每次本地 TRS 结构性变化 +1（仅用于调试/统计，可不依赖）
    // _worldVersion : 每次自身 worldMatrix 真实重建 +1
    // _cachedParentWorldVersion : 上次成功重建自身 worldMatrix 时记录的父 worldVersion（用于发现父已变化）
    private uint _localVersion = 1;
    private uint _worldVersion = 1;
    private uint _cachedParentWorldVersion = 0; // root/toplevel 用 0

#if FIXEDNODE3D_PROFILE
    public static long Stat_WorldQueryCount = 0;        // 访问 worldPosition/worldRotation/worldScale/globalMatrix 次数
    public static long Stat_WorldRecomputeCount = 0;    // 实际重建 worldMatrix 次数
    public static long Stat_LocalMatrixRecomputeCount = 0; // 重建本地矩阵次数
    public static long Stat_EulerScaleExtractCount = 0; // 反解 euler/scale 次数
    public static long Stat_WorldRotScaleCacheMiss = 0; // worldRotation/worldScale 缓存未命中次数
    public static long Stat_InverseCacheMiss = 0;       // 逆矩阵缓存未命中次数
    public static long Stat_MaxBatchRecomputeDepth = 0; // 最大一次批量链长
    public static long Stat_WorldFastPathHit = 0;       // 快速路径命中（无需进入完整更新）
    public static long Stat_WorldFastPathMiss = 0;      // 快速路径失效（需进入更新）
    public static long Stat_ReparentCalls = 0;          // SetParent 调用次数
    public static long Stat_ReparentKeepWorld = 0;      // keepWorld=true 次数
    public static long Stat_ReparentSkipWorld = 0;      // keepWorld=false 且跳过旧 world 抓取次数
    public static void ResetStats()
    {
        Stat_WorldQueryCount = 0;
        Stat_WorldRecomputeCount = 0;
        Stat_LocalMatrixRecomputeCount = 0;
        Stat_EulerScaleExtractCount = 0;
        Stat_WorldRotScaleCacheMiss = 0;
        Stat_InverseCacheMiss = 0;
        Stat_MaxBatchRecomputeDepth = 0;
        Stat_WorldFastPathHit = 0;
        Stat_WorldFastPathMiss = 0;
        Stat_ReparentCalls = 0;
        Stat_ReparentKeepWorld = 0;
        Stat_ReparentSkipWorld = 0;
    }
#endif
    #endregion

    #region Constructors
    public FixedNode3D() { }
    public FixedNode3D(FixedNode3D parent, bool keepWorld = true) { SetParent(parent, keepWorld); }
    #endregion

    #region Dirty Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDirty(uint bits)
    {
        // 设置自身脏位。与旧实现不同：不再将 DIRTY_GLOBAL_TRANSFORM 级联向下。
        _dirtyMask |= bits;

        // 本地结构变化必然影响自身 world，因此也置 DIRTY_GLOBAL_TRANSFORM（但不传播）。
        if ((bits & (DIRTY_LOCAL_TRANSFORM | DIRTY_EULER_SCALE)) != 0)
        {
            _dirtyMask |= DIRTY_GLOBAL_TRANSFORM;
            _localVersion++; // 仅记录本地修改次数
        }
    }

    // 旧的递归向下标记逻辑被弃用（保留空实现以避免外部潜在反射调用崩溃）
    private void MarkGlobalDirtyDownwards() { /* no-op after versioning optimization */ }

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

    #region World Cached Decomposition
    // 为减少多次访问 worldRotation/worldScale 时重复从矩阵提取的成本，引入基于 worldVersion 的缓存
    private TSQuaternion _cachedWorldRotationQuat = TSQuaternion.identity;
    private TSVector _cachedWorldScale = TSVector.one;
    private uint _cachedWorldRSVersion = 0; // 与 worldVersion 对齐

    // 方向向量缓存（归一化列，包含负 scale 符号）
    private TSVector _cachedWorldRight = TSVector.right; // 需要 TSVector.right，如无可用则改为 new TSVector(1,0,0)
    private TSVector _cachedWorldUp = TSVector.up;
    private TSVector _cachedWorldForward = TSVector.forward;

    // 按需缓存逆矩阵（只在 World->Local 向量/点转换或外部需要时构建）
    private TMatrix3x4 _cachedInverseGlobalTransform;
    private uint _cachedInverseVersion = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateWorldRotScaleCacheIfNeeded()
    {
        if (_cachedWorldRSVersion == _worldVersion) return;
        UpdateGlobalTransformIfNeeded();
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
        // 方向列（已归一化）
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

    #region World Properties
    public TSVector worldPosition
    {
        get { EnsureGlobalUpToDateForQuery(); return new TSVector(_globalTransform.m03, _globalTransform.m13, _globalTransform.m23); }
    }

    // 新增：一次性获取世界姿态快照，避免多次属性访问的重复 Update/拆分成本
    public void GetWorldSnapshot(out TSVector position, out TSQuaternion rotation, out TSVector scale, out TMatrix3x4 matrix)
    {
        EnsureGlobalUpToDateForQuery();
        UpdateWorldRotScaleCacheIfNeeded();
        matrix = _globalTransform;
        position = new TSVector(matrix.m03, matrix.m13, matrix.m23);
        rotation = _cachedWorldRotationQuat;
        scale = _cachedWorldScale;
    }

    public TSQuaternion worldRotation
    {
        get
        {
            EnsureGlobalUpToDateForQuery();
            UpdateWorldRotScaleCacheIfNeeded();
            return _cachedWorldRotationQuat;
        }
    }

    public TSVector worldScale
    {
        get
        {
            EnsureGlobalUpToDateForQuery();
            UpdateWorldRotScaleCacheIfNeeded();
            return _cachedWorldScale;
        }
    }

    // 便捷方向（单位向量，基于 worldRotation 缓存）
    public TSVector worldRight { get { EnsureGlobalUpToDateForQuery(); UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldRight; } }
    public TSVector worldUp    { get { EnsureGlobalUpToDateForQuery(); UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldUp; } }
    public TSVector worldForward { get { EnsureGlobalUpToDateForQuery(); UpdateWorldRotScaleCacheIfNeeded(); return _cachedWorldForward; } }

    public TMatrix3x4 localMatrix { get { UpdateLocalMatrixIfNeeded(); return _localTransform; } }
    public TMatrix3x4 globalMatrix { get { EnsureGlobalUpToDateForQuery(); return _globalTransform; } }
    public TMatrix3x4 inverseGlobalMatrix { get { EnsureGlobalUpToDateForQuery(); UpdateInverseCacheIfNeeded(); return _cachedInverseGlobalTransform; } }

    // 公开只读 worldVersion 便于外部调试缓存命中
    public uint worldVersion => _worldVersion;
    public uint localVersion => _localVersion;
    #endregion

    #region Parent / Reparent
    public FixedNode3D parent => _parent;

    // 防环检测：向上遍历 newParent 链，若出现 this 则会形成环
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool WouldIntroduceCycle(FixedNode3D newParent)
    {
        for (var p = newParent; p != null; p = p._parent)
        {
            if (p == this) return true;
        }
        return false;
    }

    // 调试：统计被阻止的成环尝试次数
    private static int _cyclePreventedCount = 0;
    public static int cyclePreventedCount => _cyclePreventedCount;
    public static void ResetCycleStats() { _cyclePreventedCount = 0; }

    public void SetParent(FixedNode3D newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;
        if (newParent != null && WouldIntroduceCycle(newParent))
        {
            _cyclePreventedCount++;
#if UNITY_ENGINE || UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogWarning("[FixedNode3D] Prevented cycle while setting parent.");
#endif
            return;
        }
#if FIXEDNODE3D_PROFILE
        Stat_ReparentCalls++;
        if (keepWorld) Stat_ReparentKeepWorld++; // 记录是否保持 world
#endif
        TMatrix3x4 oldWorld = TMatrix3x4.Identity;
        if (keepWorld)
        {
            // 需要保持 world：只在必要时才拿旧 world
            UpdateGlobalTransformIfNeeded();
            oldWorld = _globalTransform;
        }
        else
        {
#if FIXEDNODE3D_PROFILE
            Stat_ReparentSkipWorld++; // 记录跳过旧 world 抓取（优化路径）
#endif
        }

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
                var invP = Inverse3x4(_parent._globalTransform);
                _localTransform = invP * oldWorld;
            }
            SetDirty(DIRTY_EULER_SCALE);
            ClearDirty(DIRTY_LOCAL_TRANSFORM); // 直接用 localMatrix 当前值，不需要重新 Compose

            // 既然 keepWorld，我们立即保持 world 缓存一致，避免首次查询再重算
            _globalTransform = oldWorld;
            _worldVersion++; // 视为一次结构变化
            _cachedParentWorldVersion = _parent != null ? _parent._worldVersion : 0;
            ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
        else
        {
            SetDirty(DIRTY_GLOBAL_TRANSFORM); // 自身 world 后续延迟重算
            _cachedParentWorldVersion = 0; // 强制后续刷新
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
        _cachedParentWorldVersion = _parent != null ? _parent._worldVersion : 0;
    }

    public bool isTopLevel => _isTopLevel;
    #endregion

    #region Set World
    public void SetWorldPosition(in TSVector wPos)
    {
        if (_isTopLevel || _parent == null) { localPosition = wPos; return; }
        _parent.UpdateGlobalTransformIfNeeded();
        var invP = Inverse3x4(_parent._globalTransform);
        localPosition = invP.MultiplyPoint(wPos);
    }

    public void SetWorldRotation(in TSQuaternion wRot)
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

    public void SetWorldTRS(in TSVector wPos, in TSQuaternion wRot, in TSVector wScale)
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
    public void SetLocalQuaternion(TSQuaternion q, in TSVector? optScale = null)
    {
        q.Normalize();
        UpdateLocalEulerScaleIfNeeded();
        TSVector sc = optScale.HasValue ? SanitizeScale(optScale.Value) : _localScale;
        _localRotationQuat = q;
        _eulerRotation = QuaternionToEuler(q, _rotationOrder);
        _localScale = sc;
        SetDirty(DIRTY_LOCAL_TRANSFORM);
    }

    public void SetLocalTRS(in TSVector pos, TSQuaternion rot, in TSVector scale)
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

    public void SetLocalMatrix(in TMatrix3x4 m)
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
#if FIXEDNODE3D_PROFILE
            Stat_EulerScaleExtractCount++;
#endif
            ClearDirty(DIRTY_EULER_SCALE);
        }
    }

    private void UpdateGlobalTransformIfNeeded()
    {
        // 快速本地矩阵保证
        UpdateLocalMatrixIfNeeded();

        // 判定是否需要：若自身不脏且父版本一致即可直接返回
        if (!_isTopLevel && _parent != null)
        {
            if (!IsDirty(DIRTY_GLOBAL_TRANSFORM) && _cachedParentWorldVersion == _parent._worldVersion)
                return;
        }
        else
        {
            if (!IsDirty(DIRTY_GLOBAL_TRANSFORM)) return; // 顶层且非脏
        }

        var stack = _tlsUpdateStack ??= new List<FixedNode3D>(16);
        stack.Clear();

        FixedNode3D cur = this;
        while (true)
        {
            cur.UpdateLocalMatrixIfNeeded();
            bool need = cur.IsDirty(DIRTY_GLOBAL_TRANSFORM);
            uint parentWV = 0;
            var p = cur._parent;
            if (!cur._isTopLevel && p != null)
            {
                p.UpdateLocalMatrixIfNeeded();
                parentWV = p._worldVersion;
                if (cur._cachedParentWorldVersion != parentWV)
                    need = true;
            }

            if (need)
            {
                stack.Add(cur);
                if (p != null)
                {
                    cur = p;
                    continue; // 继续往上
                }
            }
            break;
        }

        if (stack.Count == 0) return; // 已 up-to-date
#if FIXEDNODE3D_PROFILE
        if (stack.Count > Stat_MaxBatchRecomputeDepth) Stat_MaxBatchRecomputeDepth = stack.Count;
#endif
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var n = stack[i];
            if (n._isTopLevel || n._parent == null)
            {
                n._globalTransform = n._localTransform;
            }
            else
            {
                n._globalTransform = n._parent._globalTransform * n._localTransform;
            }
            n._worldVersion++;
            n._cachedParentWorldVersion = n._parent != null ? n._parent._worldVersion : 0;
#if FIXEDNODE3D_PROFILE
            Stat_WorldRecomputeCount++;
#endif
            n.ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
        stack.Clear();
    }
    #endregion

    #region Math Wrappers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSVector SanitizeScale(TSVector s)
    {
        FP min = FP.EN6;
        if (FP.Abs(s.x) < min) s.x = s.x >= (FP)0 ? min : (FP)0 - min;
        if (FP.Abs(s.y) < min) s.y = s.y >= (FP)0 ? min : (FP)0 - min;
        if (FP.Abs(s.z) < min) s.z = s.z >= (FP)0 ? min : (FP)0 - min;
        return s;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSQuaternion RotationMatrixToQuat(
        in FP r00, in FP r01, in FP r02,
        in FP r10, in FP r11, in FP r12,
        in FP r20, in FP r21, in FP r22)
    {
        FixedNode3DMath.RotationMatrixToQuaternion(
            r00, r01, r02,
            r10, r11, r12,
            r20, r21, r22,
            out TSQuaternion q);
        return q;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSQuaternion EulerToQuaternion(in TSVector eulerRad, in RotationOrder order)
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSVector QuaternionToEuler(in TSQuaternion q, in RotationOrder order)
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

    private static TMatrix3x4 Inverse3x4(TMatrix3x4 m) => m.Inverse();

    #endregion

    #region Space Conversion Helpers
    public void TranslateLocal(in TSVector delta)
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

    public void TranslateWorld(in TSVector delta)
    {
        localPosition = localPosition + (_isTopLevel || _parent == null
            ? delta
            : WorldVectorToLocal(delta));
    }

    public TSVector TransformPointLocalToWorld(in TSVector p)
    {
        EnsureGlobalUpToDateForQuery();
        return _globalTransform.MultiplyPoint(p);
    }

    public TSVector TransformPointWorldToLocal(in TSVector p)
    {
        EnsureGlobalUpToDateForQuery();
        UpdateInverseCacheIfNeeded();
        return _cachedInverseGlobalTransform.MultiplyPoint(p);
    }

    public TSVector LocalVectorToWorld(in TSVector v)
    {
        EnsureGlobalUpToDateForQuery();
        return _globalTransform.MultiplyVector(v);
    }

    public TSVector WorldVectorToLocal(in TSVector v)
    {
        EnsureGlobalUpToDateForQuery();
        UpdateInverseCacheIfNeeded();
        return _cachedInverseGlobalTransform.MultiplyVector(v);
    }
    #endregion

    #region Debug / Validation Helpers
    // 供测试/调试：校验从给定节点出发的子树是否存在环 (DFS)
    public static bool ValidateNoCycles(FixedNode3D root, out string error)
    {
        error = null;
        if (root == null) { error = "Root null"; return false; }
        var visiting = new HashSet<FixedNode3D>();
        var visited = new HashSet<FixedNode3D>();
        string localError = null;
        bool Dfs(FixedNode3D n)
        {
            if (visiting.Contains(n)) { localError = "Cycle detected at node"; return false; }
            if (visited.Contains(n)) return true;
            visiting.Add(n);
            for (int i = 0; i < n._children.Count; i++)
            {
                if (!Dfs(n._children[i])) return false;
            }
            visiting.Remove(n);
            visited.Add(n);
            return true;
        }
        bool ok = Dfs(root);
        if (!ok) error = localError;
        return ok;
    }
    #endregion

    #region Debug
    public override string ToString()
    {
        return $"FixedNode3D LocalPos={localPosition}, Euler={localEuler}, Scale={localScale}, Shear={_hasShear}, Order={_rotationOrder}, Dirty=0x{_dirtyMask:X}, worldV={_worldVersion}, parentWVCache={_cachedParentWorldVersion}, RSv={_cachedWorldRSVersion}";
    }
    #endregion

    #region Fast Path Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWorldUpToDateFast()
    {
        if (IsDirty(DIRTY_GLOBAL_TRANSFORM)) return false;
        if (!_isTopLevel && _parent != null && _cachedParentWorldVersion != _parent._worldVersion) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureGlobalUpToDateForQuery()
    {
#if FIXEDNODE3D_PROFILE
        Stat_WorldQueryCount++;
#endif
        if (!IsWorldUpToDateFast())
        {
#if FIXEDNODE3D_PROFILE
            Stat_WorldFastPathMiss++;
#endif
            UpdateGlobalTransformIfNeeded();
        }
        else
        {
#if FIXEDNODE3D_PROFILE
            Stat_WorldFastPathHit++;
#endif
        }
    }
    #endregion
}

public class FixedNode3D : FixedNode3D
{
    public FixedNode3D() { }
    public FixedNode3D(FixedNode3D parent, bool keepWorld = true) { SetParent(parent, keepWorld); }

}
#endif