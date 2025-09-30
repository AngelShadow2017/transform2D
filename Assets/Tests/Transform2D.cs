using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 2D 变换节点（无 skew），显式存储 local Position / Rotation / Scale。
/// 特性：
/// 1. worldMatrix = parent.worldMatrix * localMatrix
/// 2. worldRotation 处理“奇反射”时采用 parentRot - localRot，否则 parentRot + localRot（匹配之前你的需求）
/// 3. lossyScale(worldScale) 使用公式法：
///      R_world = Π R_i
///      W_world_abs = Π (R_i * |S_i|)         (双反射：所有局部缩放取绝对值)
///      S_world_abs = R_world^T * W_world_abs
///      lossyScale = signAccum ⊙ diag(S_world_abs)
///    signAccum = 逐轴符号连乘（包含当前节点自身）
/// 4. 禁止外部修改世界缩放：无任何 SetWorldScale / lossyScale setter。
/// 5. SetParent(keepWorld=true) 时通过矩阵反求 local（精确，不估算）
/// </summary>
public class CachedTransform2DNode
{
    #region 2D 仿射矩阵 2x3 (列式)
    public struct Mat3
    {
        public float m00, m01, m02;
        public float m10, m11, m12;

        public static Mat3 Identity => new Mat3 { m00 = 1, m01 = 0, m02 = 0, m10 = 0, m11 = 1, m12 = 0 };

        public static Mat3 operator *(Mat3 a, Mat3 b)
        {
            Mat3 r;
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02;

            r.m10 = a.m10 * b.m00 + a.m11 * b.m10;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12;
            return r;
        }

        public Mat3 Inverse()
        {
            float det = m00 * m11 - m01 * m10;
            if (Mathf.Abs(det) < 1e-18f) throw new InvalidOperationException("Matrix not invertible.");
            float inv = 1f / det;
            Mat3 r;
            r.m00 =  m11 * inv;
            r.m01 = -m01 * inv;
            r.m02 = -(r.m00 * m02 + r.m01 * m12);

            r.m10 = -m10 * inv;
            r.m11 =  m00 * inv;
            r.m12 = -(r.m10 * m02 + r.m11 * m12);
            return r;
        }

        public Vector2 MultiplyPoint(Vector2 p) =>
            new Vector2(m00 * p.x + m01 * p.y + m02,
                        m10 * p.x + m11 * p.y + m12);

        public Vector2 MultiplyVector(Vector2 v) =>
            new Vector2(m00 * v.x + m01 * v.y,
                        m10 * v.x + m11 * v.y);
    }
    #endregion

    #region 字段
    private CachedTransform2DNode _parent;
    private readonly HashSet<CachedTransform2DNode> _children = new();

    // Local
    private Vector2 _localPosition = Vector2.zero;
    private float   _localRotation = 0f;          // radians
    private Vector2 _localScale    = Vector2.one; // 可为负

    // Cached matrices
    private Mat3 _localMatrix = Mat3.Identity;
    private Mat3 _worldMatrix = Mat3.Identity;

    // World (cached)
    private Vector2 _worldPosition = Vector2.zero;
    private float   _worldRotation = 0f;          // radians (with reflection rule)
    private Vector2 _worldScale    = Vector2.one; // 公式法得到的 lossyScale (read-only)

    // 纯旋转累积矩阵 R_world (2x2)
    private float _rw00 = 1f, _rw01 = 0f, _rw10 = 0f, _rw11 = 1f;

    // Abs 线性累积 W_world_abs (2x2) = Π (R_i * |S_i|)
    private float _aw00 = 1f, _aw01 = 0f, _aw10 = 0f, _aw11 = 1f;

    // 符号链
    private Vector2 _signAccum = new Vector2(1, 1);

    private bool _localDirty = true;
    private bool _worldDirty = true;

    private const float MIN_ABS_SCALE = 1e-6f;
    #endregion

    #region Local 属性
    public Vector2 localPosition
    {
        get => _localPosition;
        set { if (_localPosition != value) { _localPosition = value; MarkLocalDirty(); } }
    }

    public float localRotationDegrees
    {
        get => _localRotation * Mathf.Rad2Deg;
        set => LocalRotationRad = value * Mathf.Deg2Rad;
    }

    public float LocalRotationRad
    {
        get => _localRotation;
        set
        {
            value = NormalizeRad(value);
            if (!Mathf.Approximately(_localRotation, value))
            {
                _localRotation = value;
                MarkLocalDirty();
            }
        }
    }

    public Vector2 localScale
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

    #region World 只读属性
    public Vector2 worldPosition    { get { UpdateWorld(); return _worldPosition; } }
    public float   worldRotationDeg { get { UpdateWorld(); return _worldRotation * Mathf.Rad2Deg; } }
    public float   WorldRotationRad { get { UpdateWorld(); return _worldRotation; } }
    public Vector2 worldScale       { get { UpdateWorld(); return _worldScale; } } // (公式法 lossyScale)
    public Vector2 lossyScale       => worldScale;
    public Vector2 signAccum        { get { UpdateWorld(); return _signAccum; } }

    // Unity-like 只允许 position/rotation 改
    public Vector2 position   { get => worldPosition;   set => SetWorldPosition(value); }
    public float   rotationDeg{ get => worldRotationDeg; set => SetWorldRotationDegrees(value); }

    public Mat3 worldMatrix { get { UpdateWorld(); return _worldMatrix; } }
    public Mat3 localMatrix { get { UpdateLocal(); return _localMatrix; } }
    #endregion

    #region 层级
    public CachedTransform2DNode parent => _parent;

    public void SetParent(CachedTransform2DNode newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;

        // 保存当前世界矩阵
        UpdateWorld();
        Mat3 oldWorld = _worldMatrix;

        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        MarkWorldDirty();

        if (keepWorld)
        {
            // 重新计算 localMatrix = parent^-1 * oldWorld
            if (_parent == null)
            {
                // 直接分解 oldWorld
                DecomposePureRS(oldWorld, out _localPosition, out _localRotation, out _localScale);
            }
            else
            {
                var invParent = _parent.worldMatrix.Inverse();
                var newLocal = invParent * oldWorld;
                DecomposePureRS(newLocal, out _localPosition, out _localRotation, out _localScale);
            }
            _localDirty = true;
            MarkWorldDirty();
        }
    }
    #endregion

    #region Dirty
    private void MarkLocalDirty()
    {
        _localDirty = true;
        MarkWorldDirty();
    }

    private void MarkWorldDirty()
    {
        if (_worldDirty) return;
        _worldDirty = true;
        foreach (var c in _children)
            c.MarkWorldDirty();
    }
    #endregion

    #region 更新
    private void UpdateLocal()
    {
        if (!_localDirty) return;

        float r = _localRotation;
        float cosR = Mathf.Cos(r);
        float sinR = Mathf.Sin(r);
        float sx = _localScale.x;
        float sy = _localScale.y;

        // localMatrix = R * S
        _localMatrix.m00 = cosR * sx;
        _localMatrix.m10 = sinR * sx;
        _localMatrix.m01 = -sinR * sy;
        _localMatrix.m11 =  cosR * sy;
        _localMatrix.m02 = _localPosition.x;
        _localMatrix.m12 = _localPosition.y;

        _localDirty = false;
    }

    private void UpdateWorld()
    {
        if (!_worldDirty) return;

        UpdateLocal();

        if (_parent == null)
        {
            _worldMatrix = _localMatrix;
            _worldPosition = new Vector2(_localMatrix.m02, _localMatrix.m12);

            // 纯旋转 R_world = R_local
            float r = _localRotation;
            float cosR = Mathf.Cos(r);
            float sinR = Mathf.Sin(r);
            _rw00 = cosR; _rw01 = -sinR;
            _rw10 = sinR; _rw11 =  cosR;

            // W_world_abs = R_local * |S_local|
            float ax = Mathf.Abs(_localScale.x);
            float ay = Mathf.Abs(_localScale.y);
            _aw00 = cosR * ax;
            _aw10 = sinR * ax;
            _aw01 = -sinR * ay;
            _aw11 =  cosR * ay;

            // 符号链
            _signAccum = new Vector2(SignNonZero(_localScale.x), SignNonZero(_localScale.y));

            // worldRotation（根节点，无反射干扰）
            _worldRotation = _localRotation;

            // 公式法 lossyScale:
            // S_world_abs = R_world^T * W_world_abs
            // diag = (rw00*aw00 + rw10*aw10, rw01*aw01 + rw11*aw11)
            float s00 = _rw00 * _aw00 + _rw10 * _aw10;
            float s11 = _rw01 * _aw01 + _rw11 * _aw11;
            _worldScale = new Vector2(_signAccum.x * s00, _signAccum.y * s11);
        }
        else
        {
            _parent.UpdateWorld();

            // 世界矩阵
            _worldMatrix = _parent._worldMatrix * _localMatrix;
            _worldPosition = new Vector2(_worldMatrix.m02, _worldMatrix.m12);

            // 局部旋转纯矩阵
            float r = _localRotation;
            float cosR = Mathf.Cos(r);
            float sinR = Mathf.Sin(r);

            // 父纯旋转矩阵
            float pr00 = _parent._rw00;
            float pr01 = _parent._rw01;
            float pr10 = _parent._rw10;
            float pr11 = _parent._rw11;

            // R_world = R_parent * R_local
            _rw00 = pr00 * cosR + pr01 * sinR;
            _rw01 = pr00 * (-sinR) + pr01 * cosR;
            _rw10 = pr10 * cosR + pr11 * sinR;
            _rw11 = pr10 * (-sinR) + pr11 * cosR;

            // W_world_abs = W_parent_abs * (R_local * |S_local|)
            float ax = Mathf.Abs(_localScale.x);
            float ay = Mathf.Abs(_localScale.y);
            float rl00 = cosR * ax;
            float rl10 = sinR * ax;
            float rl01 = -sinR * ay;
            float rl11 =  cosR * ay;

            float paw00 = _parent._aw00;
            float paw01 = _parent._aw01;
            float paw10 = _parent._aw10;
            float paw11 = _parent._aw11;

            float newAw00 = paw00 * rl00 + paw01 * rl10;
            float newAw01 = paw00 * rl01 + paw01 * rl11;
            float newAw10 = paw10 * rl00 + paw11 * rl10;
            float newAw11 = paw10 * rl01 + paw11 * rl11;
            _aw00 = newAw00; _aw01 = newAw01; _aw10 = newAw10; _aw11 = newAw11;

            // 符号链
            _signAccum = new Vector2(
                _parent._signAccum.x * SignNonZero(_localScale.x),
                _parent._signAccum.y * SignNonZero(_localScale.y)
            );

            // worldRotation（含奇反射调整）
            bool parentReflected = (_parent._signAccum.x * _parent._signAccum.y) < 0f;
            _worldRotation = parentReflected
                ? NormalizeRad(_parent._worldRotation - _localRotation)
                : NormalizeRad(_parent._worldRotation + _localRotation);

            // 公式法 lossyScale:
            // S_world_abs = R_world^T * W_world_abs
            // R_world^T = [ rw00 rw10; rw01 rw11 ]
            float s00 = _rw00 * _aw00 + _rw10 * _aw10;
            float s11 = _rw01 * _aw01 + _rw11 * _aw11;
            _worldScale = new Vector2(_signAccum.x * s00, _signAccum.y * s11);
        }

        _worldDirty = false;
    }
    #endregion

    #region 世界操作（仅位置/旋转）
    public void SetWorldPosition(Vector2 wpos)
    {
        if (_parent == null)
            localPosition = wpos;
        else
        {
            var invParent = _parent.worldMatrix.Inverse();
            localPosition = invParent.MultiplyPoint(wpos);
        }
    }

    public void SetWorldRotationDegrees(float deg) => SetWorldRotationRad(deg * Mathf.Deg2Rad);
    public void SetWorldRotationRad(float desiredWorldRot)
    {
        if (_parent == null)
        {
            LocalRotationRad = desiredWorldRot;
        }
        else
        {
            _parent.UpdateWorld();
            bool parentReflected = (_parent._signAccum.x * _parent._signAccum.y) < 0f;
            float lr = parentReflected
                ? NormalizeRad(_parent.WorldRotationRad - desiredWorldRot)
                : NormalizeRad(desiredWorldRot - _parent.WorldRotationRad);
            LocalRotationRad = lr;
        }
    }
    #endregion

    #region Local 批量设置
    public void SetLocalTRSDegrees(Vector2 pos, float rotDeg, Vector2 scale)
        => SetLocalTRSRad(pos, rotDeg * Mathf.Deg2Rad, scale);

    public void SetLocalTRSRad(Vector2 pos, float rotRad, Vector2 scale)
    {
        bool changed = false;
        if (_localPosition != pos) { _localPosition = pos; changed = true; }

        rotRad = NormalizeRad(rotRad);
        if (!Mathf.Approximately(_localRotation, rotRad)) { _localRotation = rotRad; changed = true; }

        var sc = SanitizeScale(scale);
        if (_localScale != sc) { _localScale = sc; changed = true; }

        if (changed)
        {
            _localDirty = true;
            MarkWorldDirty();
        }
    }
    #endregion

    #region 获取 world TRS
    public (Vector2 position, float rotationRad, Vector2 scale) GetWorldTRSRad()
    {
        UpdateWorld();
        return (_worldPosition, _worldRotation, _worldScale);
    }

    public (Vector2 position, float rotationDeg, Vector2 scale) GetWorldTRSDegrees()
    {
        var w = GetWorldTRSRad();
        return (w.position, w.rotationRad * Mathf.Rad2Deg, w.scale);
    }
    #endregion

    #region 变换函数
    public Vector2 TransformPoint(Vector2 p)             { UpdateWorld(); return _worldMatrix.MultiplyPoint(p); }
    public Vector2 InverseTransformPoint(Vector2 p)      { UpdateWorld(); return _worldMatrix.Inverse().MultiplyPoint(p); }
    public Vector2 TransformDirection(Vector2 d)         { UpdateWorld(); return _worldMatrix.MultiplyVector(d); }
    public Vector2 InverseTransformDirection(Vector2 d)  { UpdateWorld(); return _worldMatrix.Inverse().MultiplyVector(d); }

    public void Translate(Vector2 delta, Space space = Space.Self)
    {
        if (space == Space.Self)
        {
            UpdateWorld();
            // 修复：当存在负 scale（反射）时，之前通过 worldMatrix 列向量得到的方向会被符号翻转，
            // 导致自空间平移方向与期望（与 worldRotation 视觉一致）不符。
            // 这里改为使用“世界旋转角”构造纯旋转基向量，从而忽略缩放与反射符号。
            float r = _worldRotation;
            float cosR = Mathf.Cos(r);
            float sinR = Mathf.Sin(r);
            Vector2 right = new Vector2(cosR, sinR);        // 纯旋转后的 x 轴
            Vector2 up    = new Vector2(-sinR, cosR);       // 纯旋转后的 y 轴 (左手系: R*[0,1])
            SetWorldPosition(_worldPosition + right * delta.x + up * delta.y);
        }
        else
        {
            SetWorldPosition(worldPosition + delta);
        }
    }

    public void Rotate(float deltaDegrees, Space space = Space.Self)
    {
        if (space == Space.Self)
            localRotationDegrees = localRotationDegrees + deltaDegrees;
        else
            rotationDeg = rotationDeg + deltaDegrees;
    }

    public void LookAt(Vector2 worldPoint)
    {
        Vector2 dir = worldPoint - worldPosition;
        if (dir.sqrMagnitude < 1e-12f) return;
        float ang = Mathf.Atan2(dir.y, dir.x);
        SetWorldRotationRad(ang);
    }

    public float GetAngleTo(Vector2 worldPoint)
    {
        Vector2 dir = worldPoint - worldPosition;
        if (dir.sqrMagnitude < 1e-12f) return 0f;
        float ang = Mathf.Atan2(dir.y, dir.x);
        return Mathf.DeltaAngle(worldRotationDeg, ang * Mathf.Rad2Deg);
    }
    #endregion

    #region 递归刷新
    public void RecalculateWorldRecursive()
    {
        UpdateWorld();
        foreach (var c in _children)
            c.RecalculateWorldRecursive();
    }
    #endregion

    #region 分解 & 工具
    private static void DecomposePureRS(Mat3 m, out Vector2 pos, out float rot, out Vector2 scale)
    {
        pos = new Vector2(m.m02, m.m12);

        // 线性部分 A = [a b; c d] = R * S (S 对角)
        float a = m.m00; float b = m.m01;
        float c = m.m10; float d = m.m11;

        rot = Mathf.Atan2(c, a); // R 的角度
        float cosR = Mathf.Cos(rot);
        float sinR = Mathf.Sin(rot);

        // S = R^T * A
        float s00 =  cosR * a + sinR * c;
        float s11 = -sinR * b + cosR * d;

        // 防止极小
        if (Mathf.Abs(s00) < MIN_ABS_SCALE) s00 = (s00 >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (Mathf.Abs(s11) < MIN_ABS_SCALE) s11 = (s11 >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);

        scale = new Vector2(s00, s11);

        rot = NormalizeRad(rot);
    }

    private static float NormalizeRad(float a)
    {
        a %= (2 * Mathf.PI);
        if (a <= -Mathf.PI) a += 2 * Mathf.PI;
        if (a >  Mathf.PI)  a -= 2 * Mathf.PI;
        return a;
    }

    private static Vector2 SanitizeScale(Vector2 s)
    {
        if (Mathf.Abs(s.x) < MIN_ABS_SCALE) s.x = (s.x >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (Mathf.Abs(s.y) < MIN_ABS_SCALE) s.y = (s.y >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        return s;
    }

    private static float SignNonZero(float v) => v < 0 ? -1f : 1f;
    #endregion

    #region 调试
    public override string ToString()
    {
        var w = GetWorldTRSRad();
        return $"Local(pos={_localPosition}, rot={localRotationDegrees:F2}°, scale={_localScale}) | World(pos={w.position}, rot={w.rotationRad * Mathf.Rad2Deg:F2}°, formulaLossyScale={w.scale}, signAccum={_signAccum})";
    }

    public Matrix4x4 WorldMatrix4x4
    {
        get
        {
            var m = worldMatrix;
            return new Matrix4x4(
                new Vector4(m.m00, m.m10, 0, 0),
                new Vector4(m.m01, m.m11, 0, 0),
                new Vector4(0,      0,     1, 0),
                new Vector4(m.m02, m.m12,  0, 1)
            );
        }
    }

    public Matrix4x4 LocalMatrix4x4
    {
        get
        {
            var m = localMatrix;
            return new Matrix4x4(
                new Vector4(m.m00, m.m10, 0, 0),
                new Vector4(m.m01, m.m11, 0, 0),
                new Vector4(0,      0,     1, 0),
                new Vector4(m.m02, m.m12,  0, 1)
            );
        }
    }
    #endregion

    #region 构造
    public CachedTransform2DNode() { }
    public CachedTransform2DNode(CachedTransform2DNode parent, bool keepWorld = true)
    {
        SetParent(parent, keepWorld);
    }
    #endregion
}