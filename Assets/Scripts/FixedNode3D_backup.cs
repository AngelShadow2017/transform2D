#if false//
// Fixed-point Node3D-like hierarchical transform with Godot-style lazy dirty bits.
//
// Key goals:
// 1. 保留原有 localPosition/localRotation/localScale/worldPosition 等接口。
// 2. 引入与 Godot Node3D 类似的双表示与脏标记：
//      - 局部矩阵(localTransform) 与 (eulerRotation + scale) 之间的懒同步。
//      - 全局矩阵(globalTransform) 懒计算，父链递归。
// 3. 支持“只在需要时”重建：
//      - 当设置 localRotation/localScale/euler 时标记 LOCAL_TRANSFORM 脏；
//      - 当直接对矩阵进行写入(例如 SetLocalMatrix / SetLocalBasis / SetLocalQuaternion) 时标记 EULER_SCALE 脏；
//      - 读取时按需更新。
// 4. 使用固定点类型 (FP / TSVector / TSQuaternion / TMatrix3x4)。
// 5. 可选择支持 Euler 旋转顺序（默认 YXZ 与 Godot 相同）。
// 6. 纯逻辑类，不依赖 UnityEngine（除了您已有的一些结构，如果要放纯 C# 项目可移除 UNITY 相关）。
//
// 注意：
// - 若需要物理/帧插值，可在此基础上加 prevLocal / prevGlobal 以及插值接口。
// - 若需要保留 Shear，可拓展为在 localTransform 中允许出现非正交，但这里保持纯 TRS（不保留 shear）。
// - Euler <-> Matrix 转换只严选了常用顺序（YXZ + 备选 XYZ），可按需扩展。
// - 线程安全未实现（与 Godot 的 MTNumeric 不同）。如需多线程读，请加锁或拆分读缓存。
// - 若想引入分层批量更新队列，可再增加一个集中调度器，这里先做简单递归。
// 
// Dirty 位定义：
//   DIRTY_NONE = 0
//   DIRTY_LOCAL_TRANSFORM = 1        // 需要用 euler+scale 重建 localTransform
//   DIRTY_EULER_SCALE = 2            // 需要用 localTransform 分解出 euler+scale
//   DIRTY_GLOBAL_TRANSFORM = 4       // 需要用 parent.global * local 计算 global
//
// 用法示例：
//   var a = new FixedNode3D();
//   a.localPosition = new TSVector(1,0,0);
//   var b = new FixedNode3D(); b.SetParent(a);
//   b.localRotation = TSQuaternion.Euler(0, 30, 0);
//   var wp = b.worldPosition;   // 触发递归更新
//
// -------------------------------------------------------------------------
// 追加说明（新增 Unity 兼容旋转顺序支持）:
// - Unity 内部的 Transform.eulerAngles 使用内在顺序 ZXY（相当于 intrinsic Z→X→Y）。
// - 为了兼容，新增 RotationOrder.ZXY，并在 EulerToQuaternion / QuaternionToEuler 中加入分支。
// - 保留本文件第一版的所有原始注释，不删除、不改动，只增加必要扩展注释与代码。
// -------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.TrueSync;

public class FixedNode3D
{
    #region Dirty Flags
    private const uint DIRTY_NONE             = 0;
    private const uint DIRTY_LOCAL_TRANSFORM  = 1 << 0; // Need rebuild localTransform from (euler+scale+quat)
    private const uint DIRTY_EULER_SCALE      = 1 << 1; // Need rebuild euler+scale from localTransform
    private const uint DIRTY_GLOBAL_TRANSFORM = 1 << 2; // Need rebuild globalTransform (parent change or local change)
    #endregion

    #region Rotation Order
    // 仅实现 Godot 默认 YXZ，与常用 XYZ。可自行扩展 (XZY,YZX,ZXY,ZYX)。
    // 扩展：新增 ZXY = Unity transform.eulerAngles 使用的内在顺序 Z→X→Y。
    public enum RotationOrder : byte
    {
        YXZ = 0,
        XYZ = 1,
        ZXY = 2 // 新增：Unity 内部 eulerAngles 顺序
    }
    #endregion

    #region Hierarchy
    private FixedNode3D _parent;
    private readonly List<FixedNode3D> _children = new List<FixedNode3D>(4);
    #endregion

    #region Local Representations
    // 本地矩阵（3x4），线性=旋转*缩放（纯 TRS，不包含 shear）
    private TMatrix3x4 _localTransform = TMatrix3x4.Identity;

    // Euler + Scale + Quaternion（与 Node3D 类似做双向懒同步）
    // 说明：保留 quaternion 是为了避免频繁从 Euler 反推，再由 quaternion 构造矩阵可控精度。
    private TSVector _eulerRotation = TSVector.zero;          // 弧度制（内部使用弧度，接口可选扩展 degrees）
    private TSVector _localScale = TSVector.one;
    private TSQuaternion _localRotationQuat = TSQuaternion.identity;

    private RotationOrder _rotationOrder = RotationOrder.YXZ;

    // 哪一组是来源取决于脏标记：如果 LOCAL_TRANSFORM 脏 => (euler+scale+quat) 是真实来源；
    // 如果 EULER_SCALE 脏 => localTransform 是来源。
    private uint _dirtyMask = DIRTY_NONE;
    #endregion

    #region Global Cache
    private TMatrix3x4 _globalTransform = TMatrix3x4.Identity;
    private bool _isTopLevel = false; // 模仿 Node3D set_as_top_level 概念（忽略父变换）
    #endregion

    #region Constructors
    public FixedNode3D() { }
    public FixedNode3D(FixedNode3D parent, bool keepWorld = true) { SetParent(parent, keepWorld); }
    #endregion

    #region Internal Dirty Helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDirty(uint bits)
    {
        _dirtyMask |= bits;
        // 任何本地改变会让全局也需要更新
        if ((bits & (DIRTY_LOCAL_TRANSFORM | DIRTY_EULER_SCALE)) != 0)
            _dirtyMask |= DIRTY_GLOBAL_TRANSFORM;

        // 向下传播全局脏（与 Godot 一样）
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
        get
        {
            // localTransform 永远保存了位移 (即使变换脏，这部分仍然是正确的来源)
            return new TSVector(_localTransform.m03, _localTransform.m13, _localTransform.m23);
        }
        set
        {
            TSVector curr = localPosition;
            if (curr != value)
            {
                _localTransform.m03 = value.x;
                _localTransform.m13 = value.y;
                _localTransform.m23 = value.z;
                SetDirty(DIRTY_GLOBAL_TRANSFORM); // 仅位置改变不破坏旋转缩放表示
            }
        }
    }

    public TSQuaternion localRotation
    {
        get
        {
            UpdateLocalEulerScaleIfNeeded(); // 确保 quaternion 有效
            return _localRotationQuat;
        }
        set
        {
            if (!TSQuaternion.ValueEquals(_localRotationQuat, value))
            {
                // 以 quaternion 为真实来源 => 置 LOCAL_TRANSFORM 脏
                _localRotationQuat = value;
                _localRotationQuat.Normalize();
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }

    public TSVector localScale
    {
        get
        {
            UpdateLocalEulerScaleIfNeeded();
            return _localScale;
        }
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

    // Euler (radians)
    public TSVector localEuler
    {
        get
        {
            UpdateLocalEulerScaleIfNeeded();
            return _eulerRotation;
        }
        set
        {
            if (_eulerRotation != value)
            {
                _eulerRotation = value;
                // 用 euler 作为来源，同步 quaternion
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
                // 需要先确保我们有当前的 euler，从 localTransform 分解
                UpdateLocalEulerScaleIfNeeded();
                // 重新解释当前旋转为新顺序最接近的欧拉
                // 简化：直接将 quaternion 再次分解到新顺序
                _rotationOrder = value;
                _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);
                // 不需要设置 EULER_SCALE 脏，因为我们刚刚更新了表现
            }
        }
    }
    #endregion

    #region World Read-Only Properties
    public TSVector worldPosition
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            return new TSVector(_globalTransform.m03, _globalTransform.m13, _globalTransform.m23);
        }
    }

    public TSQuaternion worldRotation
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            // 分解 global 旋转（仅需正交化 + 去 scale），因为 globalTransform 是 (R*Scale) 累乘
            // 我们构造一个无缩放的旋转矩阵，再转 quaternion
            ExtractRotationFromLinear(_globalTransform,
                out FP r00, out FP r01, out FP r02,
                out FP r10, out FP r11, out FP r12,
                out FP r20, out FP r21, out FP r22);

            TSQuaternion q = RotationMatrixToQuat(r00, r01, r02, r10, r11, r12, r20, r21, r22);
            return q;
        }
    }

    public TSVector worldScale
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            // 近似：列向量长度（与 Node3D Basis::get_scale 相同）
            // col0 = (m00,m10,m20), col1 = (m01,m11,m21), col2 = (m02,m12,m22)
            FP sx = ColumnLength(_globalTransform.m00, _globalTransform.m10, _globalTransform.m20);
            FP sy = ColumnLength(_globalTransform.m01, _globalTransform.m11, _globalTransform.m21);
            FP sz = ColumnLength(_globalTransform.m02, _globalTransform.m12, _globalTransform.m22);
            return new TSVector(sx, sy, sz);
        }
    }

    public TMatrix3x4 localMatrix
    {
        get
        {
            UpdateLocalMatrixIfNeeded();
            return _localTransform;
        }
    }

    public TMatrix3x4 globalMatrix
    {
        get
        {
            UpdateGlobalTransformIfNeeded();
            return _globalTransform;
        }
    }
    #endregion

    #region Public API - Parent / Reparent
    public FixedNode3D parent => _parent;

    public void SetParent(FixedNode3D newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;

        // 缓存当前世界
        UpdateGlobalTransformIfNeeded();
        TMatrix3x4 oldWorld = _globalTransform;

        // Detach
        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        if (!_isTopLevel && keepWorld)
        {
            if (_parent == null)
            {
                // 世界 = 本地
                _localTransform = oldWorld;
                // 由矩阵反解 euler+scale
                SetDirty(DIRTY_EULER_SCALE);  // 本地矩阵是来源，需要分解
                ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
            else
            {
                _parent.UpdateGlobalTransformIfNeeded();
                // local = parent^-1 * world
                TMatrix3x4 invParent = Inverse3x4(_parent._globalTransform);
                _localTransform = invParent * oldWorld;
                SetDirty(DIRTY_EULER_SCALE);
                ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
        else
        {
            // 不保持世界 => 只需要标记全局脏即可
            // 如果 topLevel 状态变化时，可考虑特殊处理
            SetDirty(DIRTY_GLOBAL_TRANSFORM);
        }
    }

    public void SetAsTopLevel(bool enabled, bool keepGlobal = true)
    {
        if (_isTopLevel == enabled) return;
        if (enabled)
        {
            UpdateGlobalTransformIfNeeded();
            if (!keepGlobal && _parent != null)
            {
                // 直接保留当前 localMatrix（不变换）
            }
            else
            {
                // local = global
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
                TMatrix3x4 invParent = Inverse3x4(_parent._globalTransform);
                _localTransform = invParent * _globalTransform;
                SetDirty(DIRTY_EULER_SCALE);
                ClearDirty(DIRTY_LOCAL_TRANSFORM);
            }
            else
            {
                // 保留 local 不变
            }
        }
        _isTopLevel = enabled;
        SetDirty(DIRTY_GLOBAL_TRANSFORM);
    }

    public bool isTopLevel => _isTopLevel;
    #endregion

    #region Public API - Set World
    public void SetWorldPosition(TSVector worldPos)
    {
        if (_isTopLevel || _parent == null)
        {
            localPosition = worldPos;
            return;
        }
        _parent.UpdateGlobalTransformIfNeeded();
        // localPosition = invParent * worldPos
        TMatrix3x4 invParent = Inverse3x4(_parent._globalTransform);
        // MultiplyPoint:
        TSVector lp = invParent.MultiplyPoint(worldPos);
        localPosition = lp;
    }

    public void SetWorldRotation(TSQuaternion worldRot)
    {
        if (_isTopLevel || _parent == null)
        {
            localRotation = worldRot;
        }
        else
        {
            // localRot = parentWorldRot^-1 * worldRot
            TSQuaternion pRot = _parent.worldRotation; // triggers update
            TSQuaternion inv = TSQuaternion.Inverse(pRot);
            TSQuaternion lr = inv * worldRot;
            lr.Normalize();
            localRotation = lr;
        }
    }

    public void SetWorldTRS(TSVector wPos, TSQuaternion wRot, TSVector wScale)
    {
        // Compose a world matrix (R*S + t)
        TMatrix3x4 w = ComposeMatrix(wRot, wScale, wPos);
        if (_isTopLevel || _parent == null)
        {
            _localTransform = w;
            SetDirty(DIRTY_EULER_SCALE);
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
            SetDirty(DIRTY_GLOBAL_TRANSFORM);
        }
        else
        {
            _parent.UpdateGlobalTransformIfNeeded();
            TMatrix3x4 invP = Inverse3x4(_parent._globalTransform);
            _localTransform = invP * w;
            SetDirty(DIRTY_EULER_SCALE);
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
            SetDirty(DIRTY_GLOBAL_TRANSFORM);
        }
    }
    #endregion

    #region Public API - Direct local setters (matrix / quaternion)
    public void SetLocalQuaternion(TSQuaternion q, TSVector? optScale = null)
    {
        q.Normalize();
        UpdateLocalEulerScaleIfNeeded(); // ensure _localScale valid if not provided
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

    // 若外部直接提供一个 3x4（假设纯 TRS），那我们当它是来源 => 需要分解 Euler/Scale
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
            // Rebuild localTransform from (quat + scale + position)
            // 保留当前 position
            TSVector pos = new TSVector(_localTransform.m03, _localTransform.m13, _localTransform.m23);
            ComposeRotationScale(_localRotationQuat, _localScale,
                out FP r00, out FP r01, out FP r02,
                out FP r10, out FP r11, out FP r12,
                out FP r20, out FP r21, out FP r22);

            _localTransform.m00 = r00; _localTransform.m01 = r01; _localTransform.m02 = r02;
            _localTransform.m10 = r10; _localTransform.m11 = r11; _localTransform.m12 = r12;
            _localTransform.m20 = r20; _localTransform.m21 = r21; _localTransform.m22 = r22;
            _localTransform.m03 = pos.x; _localTransform.m13 = pos.y; _localTransform.m23 = pos.z;

            ClearDirty(DIRTY_LOCAL_TRANSFORM);
        }
    }

    private void UpdateLocalEulerScaleIfNeeded()
    {
        if (IsDirty(DIRTY_EULER_SCALE))
        {
            // 从 localTransform 分解 quaternion + scale + euler
            // Step 1: 提取列向量长度 => scale
            FP sx = ColumnLength(_localTransform.m00, _localTransform.m10, _localTransform.m20);
            FP sy = ColumnLength(_localTransform.m01, _localTransform.m11, _localTransform.m21);
            FP sz = ColumnLength(_localTransform.m02, _localTransform.m12, _localTransform.m22);
            if (sx == FP.Zero) sx = FP.EN6;
            if (sy == FP.Zero) sy = FP.EN6;
            if (sz == FP.Zero) sz = FP.EN6;
            _localScale = new TSVector(sx, sy, sz);

            // Step 2: 构造“纯旋转”矩阵
            FP ir00 = _localTransform.m00 / sx;
            FP ir01 = _localTransform.m01 / sy;
            FP ir02 = _localTransform.m02 / sz;
            FP ir10 = _localTransform.m10 / sx;
            FP ir11 = _localTransform.m11 / sy;
            FP ir12 = _localTransform.m12 / sz;
            FP ir20 = _localTransform.m20 / sx;
            FP ir21 = _localTransform.m21 / sy;
            FP ir22 = _localTransform.m22 / sz;

            // Step 3: 由旋转矩阵 → quaternion
            _localRotationQuat = RotationMatrixToQuat(ir00, ir01, ir02, ir10, ir11, ir12, ir20, ir21, ir22);

            // Step 4: quaternion → euler (指定顺序)
            _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);

            ClearDirty(DIRTY_EULER_SCALE);
        }
    }

    private void UpdateGlobalTransformIfNeeded()
    {
        // 先确保本地矩阵可用
        UpdateLocalMatrixIfNeeded();

        if (IsDirty(DIRTY_GLOBAL_TRANSFORM))
        {
            if (_isTopLevel || _parent == null)
            {
                _globalTransform = _localTransform;
            }
            else
            {
                _parent.UpdateGlobalTransformIfNeeded();
                _globalTransform = _parent._globalTransform * _localTransform;
            }
            ClearDirty(DIRTY_GLOBAL_TRANSFORM);
        }
    }
    #endregion

    #region Math Helpers
    private static TSVector SanitizeScale(TSVector s)
    {
        FP min = FP.EN6;
        if (FP.Abs(s.x) < min) s.x = s.x >= 0 ? min : -min;
        if (FP.Abs(s.y) < min) s.y = s.y >= 0 ? min : -min;
        if (FP.Abs(s.z) < min) s.z = s.z >= 0 ? min : -min;
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FP ColumnLength(FP x, FP y, FP z)
    {
        // sqrt(x^2 + y^2 + z^2)
        return FP.Sqrt(x * x + y * y + z * z);
    }

    // Compose rotation*scale (no shear) into 3x3
    private static void ComposeRotationScale(TSQuaternion q, TSVector s,
        out FP m00, out FP m01, out FP m02,
        out FP m10, out FP m11, out FP m12,
        out FP m20, out FP m21, out FP m22)
    {
        // Quaternion -> rotation
        FP xx = q.x * q.x;
        FP yy = q.y * q.y;
        FP zz = q.z * q.z;
        FP xy = q.x * q.y;
        FP xz = q.x * q.z;
        FP yz = q.y * q.z;
        FP wx = q.w * q.x;
        FP wy = q.w * q.y;
        FP wz = q.w * q.z;
        FP two = (FP)2;

        FP r00 = FP.One - two * (yy + zz);
        FP r01 = two * (xy - wz);
        FP r02 = two * (xz + wy);

        FP r10 = two * (xy + wz);
        FP r11 = FP.One - two * (xx + zz);
        FP r12 = two * (yz - wx);

        FP r20 = two * (xz - wy);
        FP r21 = two * (yz + wx);
        FP r22 = FP.One - two * (xx + yy);

        m00 = r00 * s.x; m01 = r01 * s.y; m02 = r02 * s.z;
        m10 = r10 * s.x; m11 = r11 * s.y; m12 = r12 * s.z;
        m20 = r20 * s.x; m21 = r21 * s.y; m22 = r22 * s.z;
    }

    // Extract normalized rotation (removing non-uniform scale).
    private static void ExtractRotationFromLinear(
        in TMatrix3x4 m,
        out FP r00, out FP r01, out FP r02,
        out FP r10, out FP r11, out FP r12,
        out FP r20, out FP r21, out FP r22)
    {
        FP sx = ColumnLength(m.m00, m.m10, m.m20); if (sx == FP.Zero) sx = FP.EN6;
        FP sy = ColumnLength(m.m01, m.m11, m.m21); if (sy == FP.Zero) sy = FP.EN6;
        FP sz = ColumnLength(m.m02, m.m12, m.m22); if (sz == FP.Zero) sz = FP.EN6;

        r00 = m.m00 / sx; r01 = m.m01 / sy; r02 = m.m02 / sz;
        r10 = m.m10 / sx; r11 = m.m11 / sy; r12 = m.m12 / sz;
        r20 = m.m20 / sx; r21 = m.m21 / sy; r22 = m.m22 / sz;
    }

    private static TSQuaternion RotationMatrixToQuat(
        FP r00, FP r01, FP r02,
        FP r10, FP r11, FP r12,
        FP r20, FP r21, FP r22)
    {
        // 标准矩阵->四元数
        FP trace = r00 + r11 + r22;
        TSQuaternion q;
        if (trace > FP.Zero)
        {
            FP s = FP.Sqrt(trace + FP.One) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r21 - r12) * invS,
                (r02 - r20) * invS,
                (r10 - r01) * invS,
                s * (FP)0.25
            );
        }
        else if (r00 > r11 && r00 > r22)
        {
            FP s = FP.Sqrt(FP.One + r00 - r11 - r22) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                s * (FP)0.25,
                (r01 + r10) * invS,
                (r02 + r20) * invS,
                (r21 - r12) * invS
            );
        }
        else if (r11 > r22)
        {
            FP s = FP.Sqrt(FP.One + r11 - r00 - r22) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r01 + r10) * invS,
                s * (FP)0.25,
                (r12 + r21) * invS,
                (r02 - r20) * invS
            );
        }
        else
        {
            FP s = FP.Sqrt(FP.One + r22 - r00 - r11) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r02 + r20) * invS,
                (r12 + r21) * invS,
                s * (FP)0.25,
                (r10 - r01) * invS
            );
        }
        q.Normalize();
        return q;
    }

    // Euler -> Quaternion by order
    private static TSQuaternion EulerToQuaternion(TSVector eulerRad, RotationOrder order)
    {
        // 直接通过单轴 quaternion 乘积，避免精度损失
        TSQuaternion qx = TSQuaternion.AngleAxis(eulerRad.x * FP.Rad2Deg, new TSVector(1, 0, 0));
        TSQuaternion qy = TSQuaternion.AngleAxis(eulerRad.y * FP.Rad2Deg, new TSVector(0, 1, 0));
        TSQuaternion qz = TSQuaternion.AngleAxis(eulerRad.z * FP.Rad2Deg, new TSVector(0, 0, 1));

        TSQuaternion res;
        switch (order)
        {
            case RotationOrder.YXZ:      // Godot 默认：Ry * Rx * Rz
                res = qy * qx * qz;
                break;
            case RotationOrder.ZXY: // Unity: Rz * Rx * Ry
                res = qz * qx * qy;
                break;
            case RotationOrder.XYZ:
            default:
                res = qx * qy * qz;
                break;
        }
        res.Normalize();
        return res;
    }

    // Quaternion -> Euler (matching order)
    private static TSVector QuaternionToEuler(TSQuaternion q, RotationOrder order)
    {
        // 简化方案：将 quaternion -> rotation matrix -> 再用特定顺序的反解。
        // 这里只实现 YXZ / XYZ / ZXY。

        // 生成旋转矩阵（无缩放）
        FP xx = q.x * q.x;
        FP yy = q.y * q.y;
        FP zz = q.z * q.z;
        FP xy = q.x * q.y;
        FP xz = q.x * q.z;
        FP yz = q.y * q.z;
        FP wx = q.w * q.x;
        FP wy = q.w * q.y;
        FP wz = q.w * q.z;
        FP two = (FP)2;

        FP r00 = FP.One - two * (yy + zz);
        FP r01 = two * (xy - wz);
        FP r02 = two * (xz + wy);

        FP r10 = two * (xy + wz);
        FP r11 = FP.One - two * (xx + zz);
        FP r12 = two * (yz - wx);

        FP r20 = two * (xz - wy);
        FP r21 = two * (yz + wx);
        FP r22 = FP.One - two * (xx + yy);

        switch (order)
        {
            case RotationOrder.YXZ:
            {
                FP clamp = r21;
                if (clamp > FP.One) clamp = FP.One;
                if (clamp < -FP.One) clamp = -FP.One;
                FP x = -FP.Asin(clamp);
                FP y = FP.Atan2(r20, r22);
                FP z = FP.Atan2(r01, r11);
                return new TSVector(x, y, z);
            }
            case RotationOrder.ZXY:
            {
                // 内在顺序 Z -> X -> Y (R = Rz * Rx * Ry)
                // 推导关键：中间轴 X，出现 gimbal 时需要 fallback
                // 对应关系（参考推导 / 与之前解释一致）:
                // sx = r21
                FP sx = r21;
                if (sx > FP.One) sx = FP.One;
                if (sx < -FP.One) sx = -FP.One;
                FP x = FP.Asin(sx);
                FP cx = FP.Cos(x);
                FP EPS = FP.EN6;
                FP y, z;
                if (FP.Abs(cx) > EPS)
                {
                    // y = atan2(-r20, r22)
                    y = FP.Atan2(-r20, r22);
                    // z = atan2(r01, r11)
                    z = FP.Atan2(r01, r11);
                }
                else
                {
                    // Gimbal：cx 近 0，设 z=0，y = atan2(r10, r00)
                    z = FP.Zero;
                    y = FP.Atan2(r10, r00);
                }
                return new TSVector(x, y, z);
            }
            case RotationOrder.XYZ:
            default:
            {
                FP syVal = r02;
                if (syVal > FP.One) syVal = FP.One;
                if (syVal < -FP.One) syVal = -FP.One;
                FP y = FP.Asin(syVal);
                FP x = FP.Atan2(-r12, r22);
                FP z = FP.Atan2(-r01, r00);
                return new TSVector(x, y, z);
            }
        }
    }

    private static TMatrix3x4 Inverse3x4(in TMatrix3x4 m)
    {
        // 与 TMatrix3x4.Inverse 类似，这里直接调用您的实现更好。为了内聚再写一份（可替换）。
        return m.Inverse();
    }

    private static TMatrix3x4 ComposeMatrix(TSQuaternion q, TSVector s, TSVector pos)
    {
        ComposeRotationScale(q, s,
            out FP m00, out FP m01, out FP m02,
            out FP m10, out FP m11, out FP m12,
            out FP m20, out FP m21, out FP m22);
        return TMatrix3x4.FromLinearTranslation(
            m00, m01, m02, pos.x,
            m10, m11, m12, pos.y,
            m20, m21, m22, pos.z
        );
    }
    #endregion

    #region Public Helpers
    public void TranslateLocal(TSVector delta)
    {
        // local space translation = R * delta
        UpdateLocalMatrixIfNeeded();
        // 取出纯旋转（列已含缩放，需除 scale）
        UpdateLocalEulerScaleIfNeeded(); // ensure scale
        ExtractRotationFromLinear(_localTransform,
            out FP r00, out FP r01, out FP r02,
            out FP r10, out FP r11, out FP r12,
            out FP r20, out FP r21, out FP r22);

        TSVector translated = new TSVector(
            r00 * delta.x + r01 * delta.y + r02 * delta.z,
            r10 * delta.x + r11 * delta.y + r12 * delta.z,
            r20 * delta.x + r21 * delta.y + r22 * delta.z
        );

        localPosition = localPosition + translated;
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
        TMatrix3x4 inv = Inverse3x4(_globalTransform);
        return inv.MultiplyPoint(p);
    }

    public TSVector LocalVectorToWorld(TSVector v)
    {
        UpdateGlobalTransformIfNeeded();
        // 不加平移
        return _globalTransform.MultiplyVector(v);
    }

    public TSVector WorldVectorToLocal(TSVector v)
    {
        UpdateGlobalTransformIfNeeded();
        TMatrix3x4 inv = Inverse3x4(_globalTransform);
        return inv.MultiplyVector(v);
    }
    #endregion

    #region Debug / Introspection
    public override string ToString()
    {
        TSVector lp = localPosition;
        TSVector le = localEuler;
        TSVector ls = localScale;
        TSVector wp = worldPosition;
        return $"FixedNode3D Local(P={lp}, Euler={le}, Scale={ls}, Order={_rotationOrder}) World(P={wp}) Dirty=0x{_dirtyMask:X}";
    }
    #endregion
}
#endif