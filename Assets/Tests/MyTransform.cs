using System;
using UnityEngine;

/// <summary>
/// Unity 风格（列向量语义）的 2D Transform（自定义，可独立于 UnityEngine.Transform 组织层级）
/// 支持：Position / Rotation(弧度 & 角度) / Scale(含负号) / Parent / Local & World 矩阵缓存。
/// 负缩放支持策略：
/// 1. 世界缩放不再依赖矩阵分解的长度，而是递归按分量相乘（父.WorldScale * 本地Scale）保留符号。
/// 2. 世界旋转采用递归相加（父旋转 + 本地旋转），避免单轴反射情况下从矩阵“反推出”出现的歧义。
///    若你需要基于最终线性部分的“几何朝向”可再提供一个 DecomposedWorldRotation。
/// </summary>
[Serializable]
public class Transform2UnityStyle
{
    [SerializeField] private Vector2 _position = Vector2.zero;
    [SerializeField] private float _rotationRadians = 0f; // 内部存弧度
    [SerializeField] private Vector2 _scale = Vector2.one; // 允许负值（反射/镜像）

    private Transform2UnityStyle _parent;

    [Flags]
    private enum DirtyFlags : byte
    {
        None       = 0,
        LocalDirty = 1 << 0,
        WorldDirty = 1 << 1,
        All        = LocalDirty | WorldDirty
    }

    [Flags]
    public enum LocalChangeFlags : byte
    {
        None     = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
        Scale    = 1 << 2,
        All      = Position | Rotation | Scale
    }

    private DirtyFlags _dirty = DirtyFlags.All;
    private Matrix2DUnity _localMatrix;
    private Matrix2DUnity _worldMatrix;

    public event Action OnLocalMatrixRecalculated;
    public event Action OnWorldMatrixRecalculated;
    public event Action OnWorldDirty;

    public Transform2UnityStyle Parent
    {
        get => _parent;
        set
        {
            if (_parent == value) return;
            if (_parent != null)
                _parent.OnWorldDirty -= ParentWorldDirtyHandler;

            _parent = value;

            if (_parent != null)
                _parent.OnWorldDirty += ParentWorldDirtyHandler;

            MarkWorldDirty();
        }
    }

    private void ParentWorldDirtyHandler() => MarkWorldDirty();

    public Vector2 Position => _position;
    public float Rotation => _rotationRadians;
    public float RotationDegrees => _rotationRadians * Mathf.Rad2Deg;
    public Vector2 Scale => _scale;

    public Matrix2DUnity LocalMatrix
    {
        get { RecalcLocalIfNeeded(); return _localMatrix; }
    }

    public Matrix2DUnity WorldMatrix
    {
        get { RecalcWorldIfNeeded(); return _worldMatrix; }
    }

    /// <summary>
    /// 世界位置：直接分解矩阵或通过 WorldMatrix.m02,m12
    /// </summary>
    public Vector2 WorldPosition
    {
        get
        {
            WorldMatrix.Decompose(out var t, out _, out _);
            return t;
        }
    }

    /// <summary>
    /// 世界旋转（递归相加得到逻辑旋转，不受负缩放反射歧义影响）
    /// </summary>
    public float WorldRotation => (Parent != null ? Parent.WorldRotation : 0f) + _rotationRadians;
    public float WorldRotationDegrees => WorldRotation * Mathf.Rad2Deg;

    /// <summary>
    /// 世界缩放（带符号）。递归按分量相乘，保留所有层级设置的符号。
    /// </summary>
    public Vector2 WorldScale => (Parent != null ? Parent.WorldScale : Vector2.one) * _scale;

    /// <summary>
    /// 如果你仍需要“从最终矩阵分解出的”旋转（它会把单轴反射折叠进 scale 符号，不推荐用于逻辑朝向）可暴露此属性。
    /// </summary>
    public float DecomposedWorldRotation
    {
        get
        {
            WorldMatrix.Decompose(out _, out var r, out _);
            return r;
        }
    }

    /// <summary>
    /// 如果你仍需要“从最终矩阵分解出的”世界缩放（其第二轴符号可反映奇数次反射），
    /// 注意：与递归乘法得到的 WorldScale 在双轴都为负时可能出现 180°旋转 + 正缩放的等价模糊。
    /// </summary>
    public Vector2 DecomposedWorldScale
    {
        get
        {
            WorldMatrix.Decompose(out _, out _, out var s);
            return s;
        }
    }

    private void MarkLocalDirty()
    {
        _dirty |= DirtyFlags.LocalDirty | DirtyFlags.WorldDirty;
        OnWorldDirty?.Invoke();
    }

    private void MarkWorldDirty()
    {
        _dirty |= DirtyFlags.WorldDirty;
        OnWorldDirty?.Invoke();
    }

    private void RecalcLocalIfNeeded()
    {
        if ((_dirty & DirtyFlags.LocalDirty) == 0) return;
        _localMatrix = Matrix2DUnity.TRS(_position, _rotationRadians, _scale);
        _dirty &= ~DirtyFlags.LocalDirty;
        OnLocalMatrixRecalculated?.Invoke();
    }

    private void RecalcWorldIfNeeded()
    {
        if ((_dirty & DirtyFlags.WorldDirty) == 0) return;
        RecalcLocalIfNeeded();
        _worldMatrix = Parent != null ? Parent.WorldMatrix * _localMatrix : _localMatrix;
        _dirty &= ~DirtyFlags.WorldDirty;
        OnWorldMatrixRecalculated?.Invoke();
    }

    // --- 设置接口 ---

    public bool SetPosition(in Vector2 position)
    {
        if (_position == position) return false;
        _position = position;
        MarkLocalDirty();
        return true;
    }

    public bool SetRotation(float rotationRadians)
    {
        if (Mathf.Approximately(_rotationRadians, rotationRadians)) return false;
        _rotationRadians = rotationRadians;
        MarkLocalDirty();
        return true;
    }

    public bool SetRotationDegrees(float rotationDegrees) =>
        SetRotation(rotationDegrees * Mathf.Deg2Rad);

    public bool SetScale(in Vector2 scale)
    {
        if (_scale == scale) return false;
        _scale = scale;
        MarkLocalDirty();
        return true;
    }

    /// <summary>
    /// 批量更新（只对 flags 指定的分量写入，一次置脏）
    /// </summary>
    public bool SetLocal(in Vector2 position, float rotationRadians, in Vector2 scale, LocalChangeFlags flags)
    {
        bool changed = false;

        if ((flags & LocalChangeFlags.Position) != 0 && _position != position)
        {
            _position = position;
            changed = true;
        }

        if ((flags & LocalChangeFlags.Rotation) != 0 && !Mathf.Approximately(_rotationRadians, rotationRadians))
        {
            _rotationRadians = rotationRadians;
            changed = true;
        }

        if ((flags & LocalChangeFlags.Scale) != 0 && _scale != scale)
        {
            _scale = scale;
            changed = true;
        }

        if (changed) MarkLocalDirty();
        return changed;
    }

    public bool SetLocalDegrees(in Vector2 position, float rotationDegrees, in Vector2 scale, LocalChangeFlags flags)
        => SetLocal(in position, rotationDegrees * Mathf.Deg2Rad, in scale, flags);

    [Obsolete("Use SetLocal / SetLocalDegrees instead.")]
    public void SetTRS(Vector2 position, float rotationRadians, Vector2 scale)
        => SetLocal(in position, rotationRadians, in scale, LocalChangeFlags.All);

    [Obsolete("Use SetLocalDegrees instead.")]
    public void SetTRSDegrees(Vector2 position, float rotationDegrees, Vector2 scale)
        => SetLocalDegrees(in position, rotationDegrees, in scale, LocalChangeFlags.All);

    public Vector2 TransformPointLocalToWorld(Vector2 localPoint) => WorldMatrix.MultiplyPoint(localPoint);

    public Vector2 TransformPointWorldToLocal(Vector2 worldPoint)
    {
        var inv = WorldMatrix.Inverse();
        return inv.MultiplyPoint(worldPoint);
    }

    public Matrix4x4 WorldMatrix4x4 => WorldMatrix.ToMatrix4x4();

    public override string ToString() =>
        $"(Pos:{_position}, Rot:{RotationDegrees:0.##}°, Scale:{_scale})";
}