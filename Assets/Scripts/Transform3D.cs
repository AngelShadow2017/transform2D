#if false
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine; // 仅用于调试的 Matrix4x4（可去掉）
using Core.TrueSync; // 假设你的 FP / 向量 / 四元数 / 矩阵都在此命名空间

/// <summary>
/// 3D 固定点层级变换节点：
/// - 显式存储 localPosition / localRotation(Quaternion) / localScale
/// - world：仅缓存 worldPosition / worldRotation / worldMatrix（以及其逆矩阵）
/// - 不缓存 worldScale（有需要时请通过矩阵即时分解）
/// - 不做 2D 版的“奇反射”旋转加减法规则；直接使用四元数乘法：worldRot = parentRot * localRot
/// - SetParent(keepWorld = true) 时用矩阵分解反推 local TRS
/// - Translate(Space.Self) 使用纯旋转基向量（不受缩放或反射影响）
/// </summary>
public class Transform3DFixed
{
    #region Fields
    private Transform3DFixed _parent;
    private readonly HashSet<Transform3DFixed> _children = new();

    // Local
    private TSVector _localPosition = TSVector.zero;
    private TSQuaternion _localRotation = TSQuaternion.identity;
    private TSVector _localScale = TSVector.one;

    // Cached matrices (3x4 仿射：线性3x3 + 平移)
    private TMatrix3x4 _localMatrix = TMatrix3x4.Identity;
    private TMatrix3x4 _worldMatrix = TMatrix3x4.Identity;

    // Inverse (lazy)
    private TMatrix3x4 _worldMatrixInv = TMatrix3x4.Identity;
    private bool _worldInvDirty = true;

    // World (cached)
    private TSVector _worldPosition = TSVector.zero;
    private TSQuaternion _worldRotation = TSQuaternion.identity;

    private bool _localDirty = true;
    private bool _worldDirty = true;

    private static readonly FP MIN_ABS_SCALE = FP.EN6;
    #endregion

    #region Local Properties
    public TSVector localPosition
    {
        get => _localPosition;
        set
        {
            if (_localPosition != value)
            {
                _localPosition = value;
                MarkLocalDirty();
            }
        }
    }

    public TSQuaternion localRotation
    {
        get => _localRotation;
        set
        {
            // Assumes caller provides normalized or near-normalized quaternion
            if (_localRotation != value)
            {
                _localRotation = value; // 可在此 Normalize
                MarkLocalDirty();
            }
        }
    }

    public TSVector localScale
    {
        get => _localScale;
        set
        {
            var v = SanitizeScale(value);
            if (_localScale != v)
            {
                _localScale = v;
                MarkLocalDirty();
            }
        }
    }
    #endregion

    #region World Readonly Properties
    public TSVector worldPosition { get { UpdateWorld(); return _worldPosition; } }
    public TSQuaternion worldRotation { get { UpdateWorld(); return _worldRotation; } }

    // Unity-like shortcuts
    public TSVector position { get => worldPosition; set => SetWorldPosition(value); }
    public TSQuaternion rotation { get => worldRotation; set => SetWorldRotation(value); }

    public TMatrix3x4 worldMatrix { get { UpdateWorld(); return _worldMatrix; } }
    public TMatrix3x4 localMatrix { get { UpdateLocal(); return _localMatrix; } }

    public TMatrix3x4 worldMatrixInverse { get { UpdateWorldInverse(); return _worldMatrixInv; } }
    #endregion

    #region Hierarchy
    public Transform3DFixed parent => _parent;

    public void SetParent(Transform3DFixed newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;

        // Snapshot current world
        UpdateWorld();
        TMatrix3x4 oldWorld = _worldMatrix;

        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        MarkWorldDirty();

        if (keepWorld)
        {
            if (_parent == null)
            {
                // Decompose oldWorld → new local
                Transform3DFixedHelper.DecomposeTRS(oldWorld, out _localPosition, out _localRotation, out _localScale);
            }
            else
            {
                var invParent = _parent.worldMatrixInverse;
                var newLocal = invParent * oldWorld;
                Transform3DFixedHelper.DecomposeTRS(newLocal, out _localPosition, out _localRotation, out _localScale);
            }
            _localDirty = true;
            MarkWorldDirty();
        }
    }
    #endregion

    #region Dirty Flags
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkLocalDirty()
    {
        _localDirty = true;
        MarkWorldDirty();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkWorldDirty()
    {
        if (_worldDirty) return;
        _worldDirty = true;
        _worldInvDirty = true;
        foreach (var c in _children)
            c.MarkWorldDirty();
    }
    #endregion

    #region Update Routines
    private void UpdateLocal()
    {
        if (!_localDirty) return;

        // Build localMatrix = R * S (linear) + T
        // Rotation matrix from quaternion
        TSQuaternion q = _localRotation; // 确保已归一
        FP xx = q.x + q.x;
        FP yy = q.y + q.y;
        FP zz = q.z + q.z;
        FP xy = q.x * yy;
        FP xz = q.x * zz;
        FP yz = q.y * zz;
        FP wx = q.w * xx;
        FP wy = q.w * yy;
        FP wz = q.w * zz;
        FP xx2 = q.x * xx;
        FP yy2 = q.y * yy;
        FP zz2 = q.z * zz;

        // 标准四元数→矩阵（行主 / 列主要与 TMatrix3x4 的定义匹配，这里假设与 2D 一致：m00 m01 m02 为第一列的 x,y,z 分量? 
        // 由于未知 TMatrix3x4 内部布局，你需按自己的实现调整。
        // 下列写法假设与 Unity 一样：第一列是 X 轴，m00 m10 m20；第二列 Y 轴；第三列 Z 轴。
        FP r00 = FP.One - (yy2 + zz2);
        FP r11 = FP.One - (xx2 + zz2);
        FP r22 = FP.One - (xx2 + yy2);
        FP r01 = xy + wz;
        FP r10 = xy - wz;
        FP r02 = xz - wy;
        FP r20 = xz + wy;
        FP r12 = yz + wx;
        FP r21 = yz - wx;

        FP sx = _localScale.x;
        FP sy = _localScale.y;
        FP sz = _localScale.z;

        // 线性部分 = R * S（按列乘对应轴缩放）
        _localMatrix.m00 = r00 * sx;
        _localMatrix.m10 = r10 * sx;
        _localMatrix.m20 = r20 * sx;

        _localMatrix.m01 = r01 * sy;
        _localMatrix.m11 = r11 * sy;
        _localMatrix.m21 = r21 * sy;

        _localMatrix.m02 = r02 * sz;
        _localMatrix.m12 = r12 * sz;
        _localMatrix.m22 = r22 * sz;

        // 平移
        _localMatrix.m03 = _localPosition.x;
        _localMatrix.m13 = _localPosition.y;
        _localMatrix.m23 = _localPosition.z;

        _localDirty = false;
    }

    private void UpdateWorld()
    {
        if (!_worldDirty) return;

        UpdateLocal();

        if (_parent == null)
        {
            _worldMatrix = _localMatrix;
            _worldPosition = _localPosition;
            _worldRotation = _localRotation;
        }
        else
        {
            _parent.UpdateWorld();
            _worldMatrix = _parent._worldMatrix * _localMatrix; // 需要实现矩阵乘（仿射）
            // world position 来自矩阵
            _worldPosition = new TSVector(_worldMatrix.m03, _worldMatrix.m13, _worldMatrix.m23);
            _worldRotation = _parent._worldRotation * _localRotation;
            // 若需确保单位，可 Normalize
        }

        _worldDirty = false;
        _worldInvDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateWorldInverse()
    {
        UpdateWorld();
        if (!_worldInvDirty) return;
        _worldMatrixInv = _worldMatrix.Inverse(); // 需你在 TMatrix3x4 中实现
        _worldInvDirty = false;
    }
    #endregion

    #region World Mutators (Position / Rotation)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(in TSVector wpos)
    {
        if (_parent == null)
            localPosition = wpos;
        else
        {
            var invParent = _parent.worldMatrixInverse;
            localPosition = invParent.MultiplyPoint(wpos);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotation(in TSQuaternion wrot)
    {
        if (_parent == null)
            localRotation = wrot;
        else
        {
            _parent.UpdateWorld();
            // local = parent^-1 * world
            var invParent = _parent._worldRotation.Inverse(); // 假设有 Inverse()
            localRotation = invParent * wrot;
        }
    }
    #endregion

    #region Batch Local Set
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLocalTRS(in TSVector pos, in TSQuaternion rot, in TSVector scale)
    {
        bool changed = false;
        if (_localPosition != pos) { _localPosition = pos; changed = true; }
        if (_localRotation != rot) { _localRotation = rot; changed = true; }

        var sc = SanitizeScale(scale);
        if (_localScale != sc) { _localScale = sc; changed = true; }

        if (changed)
        {
            _localDirty = true;
            MarkWorldDirty();
        }
    }
    #endregion

    #region World TRS Accessors
    public (TSVector position, TSQuaternion rotation, TSVector scaleApprox) GetWorldTRSApprox()
    {
        // 仅返回 position & rotation；scaleApprox 临时用 1,1,1（或可做一次分解）
        UpdateWorld();
        return (_worldPosition, _worldRotation, TSVector.one);
    }
    #endregion

    #region Transform Functions
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector TransformPoint(in TSVector p) { UpdateWorld(); return _worldMatrix.MultiplyPoint(p); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector InverseTransformPoint(in TSVector p) => worldMatrixInverse.MultiplyPoint(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector TransformVector(in TSVector v) { UpdateWorld(); return _worldMatrix.MultiplyVector(v); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector InverseTransformVector(in TSVector v) => worldMatrixInverse.MultiplyVector(v);

    // Direction: 只考虑旋转，不考虑缩放
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector TransformDirection(in TSVector d)
    {
        UpdateWorld();
        return _worldRotation * d; // 假设 quaternion*vector 已实现纯旋转
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector InverseTransformDirection(in TSVector d)
    {
        UpdateWorld();
        var inv = _worldRotation.Inverse();
        return inv * d;
    }

    public void Translate(in TSVector delta, Space space = Space.Self)
    {
        if (space == Space.World)
        {
            SetWorldPosition(worldPosition + delta);
        }
        else
        {
            UpdateWorld();
            // 取纯旋转基
            GetRotationAxes(_worldRotation, out TSVector right, out TSVector up, out TSVector fwd);
            SetWorldPosition(_worldPosition + right * delta.x + up * delta.y + fwd * delta.z);
        }
    }

    public void Rotate(in TSQuaternion deltaRot, Space space = Space.Self)
    {
        if (space == Space.Self)
        {
            localRotation = _localRotation * deltaRot;
        }
        else
        {
            // world = delta * world
            SetWorldRotation(deltaRot * worldRotation);
        }
    }

    public void LookAt(in TSVector worldTarget, in TSVector worldUp)
    {
        TSVector dir = worldTarget - worldPosition;
        if (dir.LengthSquared() < FP.EN7) return;

        var lookRot = Transform3DFixedHelper.LookRotation(dir, worldUp);
        SetWorldRotation(lookRot);
    }
    #endregion

    #region Recursive Refresh
    public void RecalculateWorldRecursive()
    {
        UpdateWorld();
        foreach (var c in _children)
            c.RecalculateWorldRecursive();
    }
    #endregion

    #region Utilities
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSVector SanitizeScale(TSVector s)
    {
        if (FP.Abs(s.x) < MIN_ABS_SCALE) s.x = (s.x >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (FP.Abs(s.y) < MIN_ABS_SCALE) s.y = (s.y >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (FP.Abs(s.z) < MIN_ABS_SCALE) s.z = (s.z >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        return s;
    }

    // 从 quaternion 获取基向量（假设 * 运算可旋转向量）
    private static void GetRotationAxes(in TSQuaternion q, out TSVector right, out TSVector up, out TSVector forward)
    {
        right   = q * TSVector.right;
        up      = q * TSVector.up;
        forward = q * TSVector.forward;
    }
    #endregion

    #region Debug / Interop (可选)
    public override string ToString()
    {
        var w = GetWorldTRSApprox();
        return $"Local(pos={_localPosition}, rot={_localRotation}, scale={_localScale}) | World(pos={w.position}, rot={w.rotation})";
    }

    public Matrix4x4 WorldMatrix4x4
    {
        get
        {
            var m = worldMatrix;
            return new Matrix4x4(
                new Vector4((float)m.m00, (float)m.m10, (float)m.m20, 0),
                new Vector4((float)m.m01, (float)m.m11, (float)m.m21, 0),
                new Vector4((float)m.m02, (float)m.m12, (float)m.m22, 0),
                new Vector4((float)m.m03, (float)m.m13, (float)m.m23, 1)
            );
        }
    }

    public Matrix4x4 LocalMatrix4x4
    {
        get
        {
            var m = localMatrix;
            return new Matrix4x4(
                new Vector4((float)m.m00, (float)m.m10, (float)m.m20, 0),
                new Vector4((float)m.m01, (float)m.m11, (float)m.m21, 0),
                new Vector4((float)m.m02, (float)m.m12, (float)m.m22, 0),
                new Vector4((float)m.m03, (float)m.m13, (float)m.m23, 1)
            );
        }
    }
    #endregion

    #region Constructors
    public Transform3DFixed() { }
    public Transform3DFixed(Transform3DFixed parent, bool keepWorld = true)
    {
        SetParent(parent, keepWorld);
    }
    #endregion
}

/// <summary>
/// 3D 辅助，含 TRS 分解与 LookRotation。根据你的固定点数学库适配。
/// </summary>
public static class Transform3DFixedHelper
{
    private static readonly FP MIN_ABS_SCALE = FP.EN6;

    /// <summary>
    /// 分解 3x4 仿射矩阵为 pos / rot / scale（无 skew 假设）。
    /// 若行列式为负，放到一个轴的 scale 上（这里选择 X 轴）。
    /// </summary>
    public static void DecomposeTRS(in TMatrix3x4 m, out TSVector pos, out TSQuaternion rot, out TSVector scale)
    {
        pos = new TSVector(m.m03, m.m13, m.m23);

        // 线性列向量
        TSVector col0 = new TSVector(m.m00, m.m10, m.m20);
        TSVector col1 = new TSVector(m.m01, m.m11, m.m21);
        TSVector col2 = new TSVector(m.m02, m.m12, m.m22);

        FP sx = col0.Magnitude();
        FP sy = col1.Magnitude();
        FP sz = col2.Magnitude();

        if (sx < MIN_ABS_SCALE) sx = (sx >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (sy < MIN_ABS_SCALE) sy = (sy >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (sz < MIN_ABS_SCALE) sz = (sz >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);

        // 归一化形成旋转矩阵列
        var invSx = FP.One / sx;
        var invSy = FP.One / sy;
        var invSz = FP.One / sz;

        TSVector r0 = col0 * invSx;
        TSVector r1 = col1 * invSy;
        TSVector r2 = col2 * invSz;

        // 检查是否反射（行列式 < 0）
        FP det = Dot(r0, Cross(r1, r2));
        if (det < 0)
        {
            // 把反射吸收进 X 轴缩放
            sx = -sx;
            r0 = r0 * -FP.One;
        }

        // 从正交矩阵 (r0,r1,r2) 构造四元数
        rot = FromRotationColumns(r0, r1, r2);

        scale = new TSVector(sx, sy, sz);
    }

    public static TSQuaternion LookRotation(in TSVector forward, in TSVector up)
    {
        // 构造正交基
        TSVector f = forward.Normalized();
        TSVector r = Cross(up, f).Normalized();
        TSVector u = Cross(f, r);

        return FromRotationColumns(r, u, f);
    }

    #region Math Helpers (需与你的向量/四元数实现一致)
    private static FP Dot(in TSVector a, in TSVector b) => a.x * b.x + a.y * b.y + a.z * b.z;

    private static TSVector Cross(in TSVector a, in TSVector b)
        => new TSVector(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x
        );

    // 从旋转矩阵列向量生成四元数（列为基向量 x=r0, y=r1, z=r2）
    private static TSQuaternion FromRotationColumns(in TSVector r0, in TSVector r1, in TSVector r2)
    {
        // 旋转矩阵：
        // [ r0.x r1.x r2.x ]
        // [ r0.y r1.y r2.y ]
        // [ r0.z r1.z r2.z ]
        FP trace = r0.x + r1.y + r2.z;
        TSQuaternion q;
        if (trace > FP.Zero)
        {
            FP s = FP.Sqrt(trace + FP.One) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r2.y - r1.z) * invS,
                (r0.z - r2.x) * invS,
                (r1.x - r0.y) * invS,
                s * FP._0_25 // (1/4)*s
            );
        }
        else if (r0.x > r1.y && r0.x > r2.z)
        {
            FP s = FP.Sqrt(FP.One + r0.x - r1.y - r2.z) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                s * FP._0_25,
                (r0.y + r1.x) * invS,
                (r0.z + r2.x) * invS,
                (r2.y - r1.z) * invS
            );
        }
        else if (r1.y > r2.z)
        {
            FP s = FP.Sqrt(FP.One + r1.y - r0.x - r2.z) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r0.y + r1.x) * invS,
                s * FP._0_25,
                (r1.z + r2.y) * invS,
                (r0.z - r2.x) * invS
            );
        }
        else
        {
            FP s = FP.Sqrt(FP.One + r2.z - r0.x - r1.y) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (r0.z + r2.x) * invS,
                (r1.z + r2.y) * invS,
                s * FP._0_25,
                (r1.x - r0.y) * invS
            );
        }
        // 归一化（根据你的 TSQuaternion 是否需要）
        // q = q.Normalized();
        return q;
    }
    #endregion
}
#endif