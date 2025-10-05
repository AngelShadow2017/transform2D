#if false
//
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
// 追加说明（新增 Unity 兼容旋转顺序支持 + Shear 保留策略补充）:
// - Unity 内部的 Transform.eulerAngles 使用内在顺序 ZXY（intrinsic Z->X->Y）。
// - 增加 RotationOrder.UnityZXY。
// - Shear 支持：现在 localTransform 线性部分可包含 shear（与 Godot Node3D Basis 一样）。
//   * 当通过 reparent / SetWorldTRS / SetLocalMatrix 写入时，不主动去除 shear。
//   * 当通过设置 rotation/scale/quaternion 重建时：
//       - 若 _retainShearOnEdit = false (默认，与 Godot 行为一致)，重新生成纯 R*S，shear 消失。
//       - 若 _retainShearOnEdit = true，则尝试将旧矩阵分解为 (R_old * S_old * Shear) 并把 shear 映射到新尺度后再合成。
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
    // 本地仿射矩阵（可包含 shear）
    private TMatrix3x4 _localTransform = TMatrix3x4.Identity;

    // Euler + Scale + Quaternion
    private TSVector _eulerRotation = TSVector.zero;          // 弧度
    private TSVector _localScale = TSVector.one;
    private TSQuaternion _localRotationQuat = TSQuaternion.identity;

    private const RotationOrder _rotationOrder = RotationOrder.YXZ;
    private uint _dirtyMask = DIRTY_NONE;

    // --- Shear Support Additions ---
    // hasShear：分解时检测列向量是否非正交
    private bool _hasShear = false;
    // retainShearOnEdit：是否在用户重新设置 rotation/scale 时把已有 shear 保留（默认 false 模拟 Godot）
    private const bool _retainShearOnEdit = false;

    // 极小阈值用于正交判断
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
            var cur = localPosition;
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
                // 修改旋转 => 需要重建 localTransform（决定是否保留 shear）
                SetDirty(DIRTY_LOCAL_TRANSFORM);
            }
        }
    }

    public TSVector localScale
    {
        get { UpdateLocalEulerScaleIfNeeded(); return _localScale; }
        set
        {
            var sanitized = SanitizeScale(value);
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

    /*public RotationOrder rotationOrder
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
    }*/

    // --- Shear Support Additions ---
    public bool hasShear => _hasShear;
    /*public bool retainShearOnEdit
    {
        get => _retainShearOnEdit;
        set => _retainShearOnEdit = value;
    }*/
    #endregion

    #region World Properties
    public TSVector worldPosition { get { UpdateGlobalTransformIfNeeded(); return new TSVector(_globalTransform.m03, _globalTransform.m13, _globalTransform.m23); } }

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
            FP sx = ColumnLength(_globalTransform.m00, _globalTransform.m10, _globalTransform.m20);
            FP sy = ColumnLength(_globalTransform.m01, _globalTransform.m11, _globalTransform.m21);
            FP sz = ColumnLength(_globalTransform.m02, _globalTransform.m12, _globalTransform.m22);
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

        // 缓存当前世界矩阵（含 shear）
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
                var invP = Inverse3x4(_parent._globalTransform);
                _localTransform = invP * oldWorld;
            }
            // 矩阵路径进入 => Euler/Scale 脏
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
        var w = ComposeMatrix(wRot, wScale, wPos); // 纯 TRS 输入
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
        var sc = optScale.HasValue ? SanitizeScale(optScale.Value) : _localScale;
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
            // 需要从 (rot, scale + 可能保留的 shear) 重建
            TSVector pos = localPosition;
            if (_retainShearOnEdit && _hasShear)
            {
                // 旧矩阵含 shear：尝试保留
                ApplyNewRotScaleWithOptionalShear(pos);
            }
            else
            {
                // 直接构建纯 R*S
                ComposeRotationScale(_localRotationQuat, _localScale,
                    out FP r00, out FP r01, out FP r02,
                    out FP r10, out FP r11, out FP r12,
                    out FP r20, out FP r21, out FP r22);
                _localTransform.m00 = r00; _localTransform.m01 = r01; _localTransform.m02 = r02;
                _localTransform.m10 = r10; _localTransform.m11 = r11; _localTransform.m12 = r12;
                _localTransform.m20 = r20; _localTransform.m21 = r21; _localTransform.m22 = r22;
                _localTransform.m03 = pos.x; _localTransform.m13 = pos.y; _localTransform.m23 = pos.z;
            }

            // 重建后再检测是否仍有 shear（retain=false 时应该为 false）
            _hasShear = DetectShear(_localTransform);
            ClearDirty(DIRTY_LOCAL_TRANSFORM);
        }
    }

    private void UpdateLocalEulerScaleIfNeeded()
    {
        if (IsDirty(DIRTY_EULER_SCALE))
        {
            FP sx = ColumnLength(_localTransform.m00, _localTransform.m10, _localTransform.m20); if (sx == FP.Zero) sx = FP.EN6;
            FP sy = ColumnLength(_localTransform.m01, _localTransform.m11, _localTransform.m21); if (sy == FP.Zero) sy = FP.EN6;
            FP sz = ColumnLength(_localTransform.m02, _localTransform.m12, _localTransform.m22); if (sz == FP.Zero) sz = FP.EN6;
            _localScale = new TSVector(sx, sy, sz);

            FP ir00 = _localTransform.m00 / sx; FP ir01 = _localTransform.m01 / sy; FP ir02 = _localTransform.m02 / sz;
            FP ir10 = _localTransform.m10 / sx; FP ir11 = _localTransform.m11 / sy; FP ir12 = _localTransform.m12 / sz;
            FP ir20 = _localTransform.m20 / sx; FP ir21 = _localTransform.m21 / sy; FP ir22 = _localTransform.m22 / sz;

            _localRotationQuat = RotationMatrixToQuat(ir00, ir01, ir02, ir10, ir11, ir12, ir20, ir21, ir22);
            _eulerRotation = QuaternionToEuler(_localRotationQuat, _rotationOrder);

            _hasShear = DetectShear(_localTransform);
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

    #region Shear Helpers
    // 检测列向量是否近似非正交 => 存在 shear
    private bool DetectShear(in TMatrix3x4 m)
    {
        if (!_retainShearOnEdit)
        {
            return false;
        }
        // 取列向量
        TSVector c0 = new TSVector(m.m00, m.m10, m.m20);
        TSVector c1 = new TSVector(m.m01, m.m11, m.m21);
        TSVector c2 = new TSVector(m.m02, m.m12, m.m22);

        FP l0 = ColumnLength(c0.x, c0.y, c0.z); if (l0 == FP.Zero) l0 = FP.EN6;
        FP l1 = ColumnLength(c1.x, c1.y, c1.z); if (l1 == FP.Zero) l1 = FP.EN6;
        FP l2 = ColumnLength(c2.x, c2.y, c2.z); if (l2 == FP.Zero) l2 = FP.EN6;

        TSVector n0 = new TSVector(c0.x / l0, c0.y / l0, c0.z / l0);
        TSVector n1 = new TSVector(c1.x / l1, c1.y / l1, c1.z / l1);
        TSVector n2 = new TSVector(c2.x / l2, c2.y / l2, c2.z / l2);

        FP d01 = TSVector.Dot(n0, n1);
        FP d02 = TSVector.Dot(n0, n2);
        FP d12 = TSVector.Dot(n1, n2);

        return (FP.Abs(d01) > SHEAR_EPS) || (FP.Abs(d02) > SHEAR_EPS) || (FP.Abs(d12) > SHEAR_EPS);
    }

    // 当 retainShearOnEdit = true 且原本有 shear，修改 rotation/scale 时保持 shear 逻辑
    // 这里用简化的“提取纯旋再乘旧 shear”的思路：
    // 1) 旧 L = R_old_s * S_old * ShearUpper（近似：通过列正交化获得 R_old_s，再构建 R_old_s * S_old）
    // 2) 目标 R_new*S_new 用 (localRotationQuat + _localScale)
    // 3) 组合：L_new = (R_new * diag(S_new)) * ShearUpperMapped
    private void ApplyNewRotScaleWithOptionalShear(TSVector pos)
    {
        // 分解旧线性
        FP sx_old = ColumnLength(_localTransform.m00, _localTransform.m10, _localTransform.m20); if (sx_old == FP.Zero) sx_old = FP.EN6;
        FP sy_old = ColumnLength(_localTransform.m01, _localTransform.m11, _localTransform.m21); if (sy_old == FP.Zero) sy_old = FP.EN6;
        FP sz_old = ColumnLength(_localTransform.m02, _localTransform.m12, _localTransform.m22); if (sz_old == FP.Zero) sz_old = FP.EN6;

        // 归一化列 => 近似旧旋转
        FP ir00 = _localTransform.m00 / sx_old; FP ir01 = _localTransform.m01 / sy_old; FP ir02 = _localTransform.m02 / sz_old;
        FP ir10 = _localTransform.m10 / sx_old; FP ir11 = _localTransform.m11 / sy_old; FP ir12 = _localTransform.m12 / sz_old;
        FP ir20 = _localTransform.m20 / sx_old; FP ir21 = _localTransform.m21 / sy_old; FP ir22 = _localTransform.m22 / sz_old;

        // 用归一化列 -> 旧旋转矩阵
        // 构造目标新纯 R*S
        ComposeRotationScale(_localRotationQuat, _localScale,
            out FP nr00, out FP nr01, out FP nr02,
            out FP nr10, out FP nr11, out FP nr12,
            out FP nr20, out FP nr21, out FP nr22);

        // 提取 shear 上三角（简单 Gram 型：旧的列在旧旋转下的投影）
        // shearUpper 近似：
        // ShearXY = dot(nr? 用旧R? 为简化我们用旧正交基 ir??)
        // 简化：旧 shear = R_old^T * (L_old * S_old^-1) - I
        // 但我们只有列或许更简单：在旧基下:
        // 列0 = sx_old * e0
        // 列1 = R_old * (sy_old * e1 + shear_xy * sx_old * e0)...
        // 为避免复杂推导，这里走近似映射：用列间投影占比。
        FP proj01 = (ir00 * ir01 + ir10 * ir11 + ir20 * ir21); // n0·n1
        FP proj02 = (ir00 * ir02 + ir10 * ir12 + ir20 * ir22);
        FP proj12 = (ir01 * ir02 + ir11 * ir12 + ir21 * ir22);

        // 将 shear 投影按新尺度比例映射
        // 新列:
        // c0' = (nr00,nr10,nr20)*Sx_new
        // c1' = (nr01,nr11,nr21)*Sy_new + proj01 * (nr00,nr10,nr20)*Sx_new
        // c2' = (nr02,nr12,nr22)*Sz_new + proj02 * (nr00,nr10,nr20)*Sx_new + proj12 * (nr01,nr11,nr21)*Sy_new
        FP Sx = _localScale.x; FP Sy = _localScale.y; FP Sz = _localScale.z;

        FP c0x = nr00 * Sx; FP c0y = nr10 * Sx; FP c0z = nr20 * Sx;
        FP c1x = nr01 * Sy + proj01 * c0x;
        FP c1y = nr11 * Sy + proj01 * c0y;
        FP c1z = nr21 * Sy + proj01 * c0z;

        FP c2x = nr02 * Sz + proj02 * c0x + proj12 * (nr01 * Sy);
        FP c2y = nr12 * Sz + proj02 * c0y + proj12 * (nr11 * Sy);
        FP c2z = nr22 * Sz + proj02 * c0z + proj12 * (nr21 * Sy);

        _localTransform.m00 = c0x; _localTransform.m01 = c1x; _localTransform.m02 = c2x;
        _localTransform.m10 = c0y; _localTransform.m11 = c1y; _localTransform.m12 = c2y;
        _localTransform.m20 = c0z; _localTransform.m21 = c1z; _localTransform.m22 = c2z;

        _localTransform.m03 = pos.x; _localTransform.m13 = pos.y; _localTransform.m23 = pos.z;
    }
    #endregion

    #region Math Helpers (与前版一致 + 扩展)
    private static TSVector SanitizeScale(TSVector s)
    {
        FP min = FP.EN6;
        if (FP.Abs(s.x) < min) s.x = s.x >= 0 ? min : -min;
        if (FP.Abs(s.y) < min) s.y = s.y >= 0 ? min : -min;
        if (FP.Abs(s.z) < min) s.z = s.z >= 0 ? min : -min;
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FP ColumnLength(FP x, FP y, FP z) => FP.Sqrt(x * x + y * y + z * z);

    private static void ComposeRotationScale(TSQuaternion q, TSVector s,
        out FP m00, out FP m01, out FP m02,
        out FP m10, out FP m11, out FP m12,
        out FP m20, out FP m21, out FP m22)
    {
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

    private static TSQuaternion EulerToQuaternion(TSVector eulerRad, RotationOrder order)
    {
        TSQuaternion qx = TSQuaternion.AngleAxis(eulerRad.x * FP.Rad2Deg, new TSVector(1, 0, 0));
        TSQuaternion qy = TSQuaternion.AngleAxis(eulerRad.y * FP.Rad2Deg, new TSVector(0, 1, 0));
        TSQuaternion qz = TSQuaternion.AngleAxis(eulerRad.z * FP.Rad2Deg, new TSVector(0, 0, 1));
        TSQuaternion res;
        switch (order)
        {
            case RotationOrder.YXZ:      res = qy * qx * qz; break;
            case RotationOrder.UnityZXY: res = qz * qx * qy; break;
            case RotationOrder.XYZ:
            default: res = qx * qy * qz; break;
        }
        res.Normalize();
        return res;
    }

    private static TSVector QuaternionToEuler(TSQuaternion q, RotationOrder order)
    {
        FP xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
        FP xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
        FP wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;
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
                FP clamp = r21; if (clamp > FP.One) clamp = FP.One; if (clamp < -FP.One) clamp = -FP.One;
                FP x = -FP.Asin(clamp);
                FP y = FP.Atan2(r20, r22);
                FP z = FP.Atan2(r01, r11);
                return new TSVector(x, y, z);
            }
            case RotationOrder.UnityZXY:
            {
                FP sx = r21; if (sx > FP.One) sx = FP.One; if (sx < -FP.One) sx = -FP.One;
                FP x = FP.Asin(sx);
                FP cx = FP.Cos(x);
                FP EPS = FP.EN6;
                FP y, z;
                if (FP.Abs(cx) > EPS)
                {
                    y = FP.Atan2(-r20, r22);
                    z = FP.Atan2(r01, r11);
                }
                else
                {
                    z = FP.Zero;
                    y = FP.Atan2(r10, r00);
                }
                return new TSVector(x, y, z);
            }
            case RotationOrder.XYZ:
            default:
            {
                FP syVal = r02; if (syVal > FP.One) syVal = FP.One; if (syVal < -FP.One) syVal = -FP.One;
                FP y = FP.Asin(syVal);
                FP x = FP.Atan2(-r12, r22);
                FP z = FP.Atan2(-r01, r00);
                return new TSVector(x, y, z);
            }
        }
    }

    private static TMatrix3x4 Inverse3x4(in TMatrix3x4 m) => m.Inverse();

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
#endif