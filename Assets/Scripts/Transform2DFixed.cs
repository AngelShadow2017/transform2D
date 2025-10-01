
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using Unity.Burst;
using Unity.Mathematics;
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
public class Transform2DFixed
{
    #region 字段
    private Transform2DFixed _parent;
    private readonly HashSet<Transform2DFixed> _children = new();

    // Local
    private TSVector2 _localPosition = TSVector2.zero;
    private FP   _localRotation = 0f;          // radians
    private TSVector2 _localScale    = TSVector2.one; // 可为负

    // Cached matrices
    private TMatrix2x3 _localMatrix = TMatrix2x3.Identity;
    private TMatrix2x3 _worldMatrix = TMatrix2x3.Identity;

    // World (cached)
    private TSVector2 _worldPosition = TSVector2.zero;
    private FP   _worldRotation = 0f;          // radians (with reflection rule)
    private TSVector2 _worldScale    = TSVector2.one; // 公式法得到的 lossyScale (read-only)

    // 纯旋转累积矩阵 R_world (2x2)
    private FP _rw00 = 1f, _rw01 = 0f, _rw10 = 0f, _rw11 = 1f;

    // Abs 线性累积 W_world_abs (2x2) = Π (R_i * |S_i|)
    private FP _aw00 = 1f, _aw01 = 0f, _aw10 = 0f, _aw11 = 1f;

    // 符号链
    private int2 _signAccum = new int2(1, 1);

    private bool _localDirty = true;
    private bool _worldDirty = true;

    private static readonly FP MIN_ABS_SCALE = FP.EN6;
    #endregion

    #region Local 属性
    public TSVector2 localPosition
    {
        get => _localPosition;
        set { if (_localPosition != value) { _localPosition = value; MarkLocalDirty(); } }
    }

    public FP localRotationDegrees
    {
        get => _localRotation * FP.Rad2Deg;
        set => LocalRotationRad = value * FP.Deg2Rad;
    }

    public FP LocalRotationRad
    {
        get => _localRotation;
        set
        {
            value = NormalizeRad(value);
            if (_localRotation!=value)
            {
                _localRotation = value;
                MarkLocalDirty();
            }
        }
    }

    public TSVector2 localScale
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
    public TSVector2 worldPosition    { get { UpdateWorld(); return _worldPosition; } }
    public FP   worldRotationDeg { get { UpdateWorld(); return _worldRotation * FP.Rad2Deg; } }
    public FP   WorldRotationRad { get { UpdateWorld(); return _worldRotation; } }
    
    //超过2级的应该实现是错的，和unity不一致，尽量不要用这个
    public TSVector2 worldScale       { get { UpdateWorld(); return _worldScale; } } // (公式法 lossyScale)
    public TSVector2 lossyScale       => worldScale;
    public int2 signAccum        { get { UpdateWorld(); return _signAccum; } }

    // Unity-like 只允许 position/rotation 改
    public TSVector2 position   { get => worldPosition;   set => SetWorldPosition(value); }
    public FP   rotationDeg{ get => worldRotationDeg; set => SetWorldRotationDegrees(value); }

    public TMatrix2x3 worldMatrix { get { UpdateWorld(); return _worldMatrix; } }
    public TMatrix2x3 localMatrix { get { UpdateLocal(); return _localMatrix; } }
    #endregion

    #region 层级
    public Transform2DFixed parent => _parent;

    public void SetParent(Transform2DFixed newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;

        // 保存当前世界矩阵
        UpdateWorld();
        TMatrix2x3 oldWorld = _worldMatrix;

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
        foreach (var c in _children)
            c.MarkWorldDirty();
    }
    #endregion

    #region 更新
    private void UpdateLocal()
    {
        if (!_localDirty) return;

        FP r = _localRotation;
        FP cosR = FP.FastCos(r);
        FP sinR = FP.FastSin(r);
        FP sx = _localScale.x;
        FP sy = _localScale.y;

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
            _worldPosition = new TSVector2(_localMatrix.m02, _localMatrix.m12);

            // 纯旋转 R_world = R_local
            FP r = _localRotation;
            FP cosR = FP.FastCos(r);
            FP sinR = FP.FastSin(r);
            _rw00 = cosR; _rw01 = -sinR;
            _rw10 = sinR; _rw11 =  cosR;

            // W_world_abs = R_local * |S_local|
            FP ax = FP.Abs(_localScale.x);
            FP ay = FP.Abs(_localScale.y);
            _aw00 = cosR * ax;
            _aw10 = sinR * ax;
            _aw01 = -sinR * ay;
            _aw11 =  cosR * ay;

            // 符号链
            _signAccum = new int2(SignNonZero(_localScale.x), SignNonZero(_localScale.y));

            // worldRotation（根节点，无反射干扰）
            _worldRotation = _localRotation;

            // 公式法 lossyScale:
            // S_world_abs = R_world^T * W_world_abs
            // diag = (rw00*aw00 + rw10*aw10, rw01*aw01 + rw11*aw11)
            FP s00 = _rw00 * _aw00 + _rw10 * _aw10;
            FP s11 = _rw01 * _aw01 + _rw11 * _aw11;
            _worldScale = new TSVector2(_signAccum.x * s00, _signAccum.y * s11);
        }
        else
        {
            _parent.UpdateWorld();

            // 世界矩阵
            _worldMatrix = _parent._worldMatrix * _localMatrix;
            _worldPosition = new TSVector2(_worldMatrix.m02, _worldMatrix.m12);

            // 局部旋转纯矩阵
            FP r = _localRotation;
            FP cosR = FP.FastCos(r);
            FP sinR = FP.FastSin(r);

            // 父纯旋转矩阵
            FP pr00 = _parent._rw00;
            FP pr01 = _parent._rw01;
            FP pr10 = _parent._rw10;
            FP pr11 = _parent._rw11;

            // R_world = R_parent * R_local
            _rw00 = pr00 * cosR + pr01 * sinR;
            _rw01 = pr00 * (-sinR) + pr01 * cosR;
            _rw10 = pr10 * cosR + pr11 * sinR;
            _rw11 = pr10 * (-sinR) + pr11 * cosR;

            // W_world_abs = W_parent_abs * (R_local * |S_local|)
            FP ax = FP.Abs(_localScale.x);
            FP ay = FP.Abs(_localScale.y);
            FP rl00 = cosR * ax;
            FP rl10 = sinR * ax;
            FP rl01 = -sinR * ay;
            FP rl11 =  cosR * ay;

            FP paw00 = _parent._aw00;
            FP paw01 = _parent._aw01;
            FP paw10 = _parent._aw10;
            FP paw11 = _parent._aw11;

            FP newAw00 = paw00 * rl00 + paw01 * rl10;
            FP newAw01 = paw00 * rl01 + paw01 * rl11;
            FP newAw10 = paw10 * rl00 + paw11 * rl10;
            FP newAw11 = paw10 * rl01 + paw11 * rl11;
            _aw00 = newAw00; _aw01 = newAw01; _aw10 = newAw10; _aw11 = newAw11;

            // 符号链
            _signAccum = new int2(
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
            FP s00 = _rw00 * _aw00 + _rw10 * _aw10;
            FP s11 = _rw01 * _aw01 + _rw11 * _aw11;
            _worldScale = new TSVector2(_signAccum.x * s00, _signAccum.y * s11);
        }

        _worldDirty = false;
    }
    #endregion

    #region 世界操作（仅位置/旋转）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(in TSVector2 wpos)
    {
        if (_parent == null)
            localPosition = wpos;
        else
        {
            var invParent = _parent.worldMatrix.Inverse();
            localPosition = invParent.MultiplyPoint(wpos);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotationDegrees(in FP deg) => SetWorldRotationRad(deg * FP.Deg2Rad);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotationRad(in FP desiredWorldRot)
    {
        if (_parent == null)
        {
            LocalRotationRad = desiredWorldRot;
        }
        else
        {
            _parent.UpdateWorld();
            bool parentReflected = (_parent._signAccum.x * _parent._signAccum.y) < 0f;
            FP lr = parentReflected
                ? NormalizeRad(_parent.WorldRotationRad - desiredWorldRot)
                : NormalizeRad(desiredWorldRot - _parent.WorldRotationRad);
            LocalRotationRad = lr;
        }
    }
    #endregion

    #region Local 批量设置
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLocalTRSDegrees(in TSVector2 pos, in FP rotDeg, in TSVector2 scale)
        => SetLocalTRSRad(pos, rotDeg * FP.Deg2Rad, scale);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLocalTRSRad(in TSVector2 pos, FP rotRad, in TSVector2 scale)
    {
        bool changed = false;
        if (_localPosition != pos) { _localPosition = pos; changed = true; }

        rotRad = NormalizeRad(rotRad);
        if (_localRotation!=rotRad) { _localRotation = rotRad; changed = true; }

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
    public (TSVector2 position, FP rotationRad, TSVector2 scale) GetWorldTRSRad()
    {
        UpdateWorld();
        return (_worldPosition, _worldRotation, _worldScale);
    }

    public (TSVector2 position, FP rotationDeg, TSVector2 scale) GetWorldTRSDegrees()
    {
        var w = GetWorldTRSRad();
        return (w.position, w.rotationRad * FP.Rad2Deg, w.scale);
    }
    
    #endregion

    #region 变换函数
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 TransformPoint(in TSVector2 p)             { UpdateWorld(); return _worldMatrix.MultiplyPoint(p); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 InverseTransformPoint(in TSVector2 p)      { UpdateWorld(); return _worldMatrix.Inverse().MultiplyPoint(p); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 TransformDirection(in TSVector2 d)
    {
        // 仅旋转，不受缩放与反射符号影响（与 Translate 自空间逻辑保持一致）
        UpdateWorld();
        FP r = _worldRotation;
        FP c = FP.FastCos(r);
        FP s = FP.FastSin(r);
        return new TSVector2(c * d.x - s * d.y, s * d.x + c * d.y);
    }
    
// 原先含缩放/反射的行为若仍需要，可改名保留
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 TransformVector(in TSVector2 v)
    {
        UpdateWorld();
        return _worldMatrix.MultiplyVector(v); // 含缩放与反射
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 InverseTransformDirection(in TSVector2 d)
    {
        // 逆纯旋转：使用 -worldRotation
        UpdateWorld();
        FP r = -_worldRotation;
        FP c = FP.FastCos(r);
        FP s = FP.FastSin(r);
        return new TSVector2(c * d.x - s * d.y, s * d.x + c * d.y);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector2 InverseTransformVector(in TSVector2 v)
    {
        // 含缩放/反射逆
        UpdateWorld();
        var inv = _worldMatrix.Inverse();
        return inv.MultiplyVector(v);
    }
    public void Translate(in TSVector2 delta, in Space space = Space.Self)
    {
        if (space == Space.Self)
        {
            UpdateWorld();
            // 修复：当存在负 scale（反射）时，之前通过 worldMatrix 列向量得到的方向会被符号翻转，
            // 导致自空间平移方向与期望（与 worldRotation 视觉一致）不符。
            // 这里改为使用“世界旋转角”构造纯旋转基向量，从而忽略缩放与反射符号。
            FP r = _worldRotation;
            FP cosR = FP.FastCos(r);
            FP sinR = FP.FastSin(r);
            TSVector2 right = new TSVector2(cosR, sinR);        // 纯旋转后的 x 轴
            TSVector2 up    = new TSVector2(-sinR, cosR);       // 纯旋转后的 y 轴 (左手系: R*[0,1])
            SetWorldPosition(_worldPosition + right * delta.x + up * delta.y);
        }
        else
        {
            SetWorldPosition(worldPosition + delta);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Rotate(in FP deltaDegrees, in Space space = Space.Self)
    {
        if (space == Space.Self)
            localRotationDegrees = localRotationDegrees + deltaDegrees;
        else
            rotationDeg = rotationDeg + deltaDegrees;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LookAt(in TSVector2 worldPoint)
    {
        TSVector2 dir = worldPoint - worldPosition;
        if (dir.LengthSquared() < FP.EN7) return;
        FP ang = FP.Atan2(dir.y, dir.x);
        SetWorldRotationRad(ang);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FP GetAngleTo(in TSVector2 worldPoint)
    {
        TSVector2 dir = worldPoint - worldPosition;
        if (dir.LengthSquared() < FP.EN7) return FP.Zero;
        FP ang = FP.Atan2(dir.y, dir.x);
        return TSMath.DeltaAngle(worldRotationDeg, ang * FP.Rad2Deg);
    }
    #endregion

    #region 递归刷新
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecalculateWorldRecursive()
    {
        UpdateWorld();
        foreach (var c in _children)
            c.RecalculateWorldRecursive();
    }
    #endregion

    #region 分解 & 工具
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecomposePureRS(TMatrix2x3 m, out TSVector2 pos, out FP rot, out TSVector2 scale)
    {
        Transform2DFixedHelper.DecomposePureRS(m, out pos, out rot, out scale);
        /*pos = new TSVector2(m.m02, m.m12);

        // 线性部分 A = [a b; c d] = R * S (S 对角)
        FP a = m.m00; FP b = m.m01;
        FP c = m.m10; FP d = m.m11;

        rot = FP.Atan2(c, a); // R 的角度
        FP cosR = FP.FastCos(rot);
        FP sinR = FP.FastSin(rot);

        // S = R^T * A
        FP s00 =  cosR * a + sinR * c;
        FP s11 = -sinR * b + cosR * d;

        // 防止极小
        if (FP.Abs(s00) < MIN_ABS_SCALE) s00 = (s00 >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (FP.Abs(s11) < MIN_ABS_SCALE) s11 = (s11 >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);

        scale = new TSVector2(s00, s11);

        rot = NormalizeRad(rot);*/
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FP NormalizeRad(FP a)
    {
        /*
        a %= (FP.PiTimes2);
        if (a <= -FP.Pi) a += FP.PiTimes2;
        if (a >  FP.Pi)  a -= FP.PiTimes2;
        */
        Transform2DFixedHelper.NormalizeRad(ref a);
        return a;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSVector2 SanitizeScale(TSVector2 s)
    {
        //if (FP.Abs(s.x) < MIN_ABS_SCALE) s.x = (s.x >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        //if (FP.Abs(s.y) < MIN_ABS_SCALE) s.y = (s.y >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        Transform2DFixedHelper.SanitizeScale(ref s);
        return s;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignNonZero(in FP v) => v < 0 ? -1 : 1;
    #endregion

    #region 调试
    public override string ToString()
    {
        var w = GetWorldTRSRad();
        return $"Local(pos={_localPosition}, rot={localRotationDegrees:F2}°, scale={_localScale}) | World(pos={w.position}, rot={w.rotationRad * FP.Rad2Deg:F2}°, formulaLossyScale={w.scale}, signAccum={_signAccum})";
    }

    public Matrix4x4 WorldMatrix4x4
    {
        get
        {
            var m = worldMatrix;
            return new Matrix4x4(
                new Vector4((float)m.m00, (float)m.m10, 0, 0),
                new Vector4((float)m.m01, (float)m.m11, 0, 0),
                new Vector4(0,      0,     1, 0),
                new Vector4((float)m.m02, (float)m.m12,  0, 1)
            );
        }
    }

    public Matrix4x4 LocalMatrix4x4
    {
        get
        {
            var m = localMatrix;
            return new Matrix4x4(
                new Vector4((float)m.m00, (float)m.m10, 0, 0),
                new Vector4((float)m.m01, (float)m.m11, 0, 0),
                new Vector4(0,      0,     1, 0),
                new Vector4((float)m.m02, (float)m.m12,  0, 1)
            );
        }
    }
    #endregion

    #region 构造
    public Transform2DFixed() { }
    public Transform2DFixed(Transform2DFixed parent, bool keepWorld = true)
    {
        SetParent(parent, keepWorld);
    }
    #endregion
}

[BurstCompile(OptimizeFor = OptimizeFor.Performance)]
public static class Transform2DFixedHelper
{
    private static readonly FP MIN_ABS_SCALE = FP.EN6;
    
    
    
    [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SanitizeScale(ref TSVector2 s)
    {
        if (FP.Abs(s.x) < MIN_ABS_SCALE) s.x = (s.x >= 0 ? MIN_ABS_SCALE : 0-MIN_ABS_SCALE);
        if (FP.Abs(s.y) < MIN_ABS_SCALE) s.y = (s.y >= 0 ? MIN_ABS_SCALE : 0-MIN_ABS_SCALE);
    }
    [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mod(ref long a,in long b)
    {
        a = a == FP.MIN_VALUE & b == -1 ?
            0 :
            a % b;
    }

    [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormalizeRad(ref FP a)
    {
        Mod(ref a._serializedValue,FP.PI_TIMES_2);
        if (a._serializedValue <= -FP.PI) a._serializedValue+=FP.PI_TIMES_2;
        if (a._serializedValue >  FP.PI)  a._serializedValue -= FP.PI_TIMES_2;
        //a %= (FP.PiTimes2);
        //if (a <= 0-FP.Pi) a += FP.PiTimes2;
        //if (a >  FP.Pi)  a -= FP.PiTimes2;
    }
    [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecomposePureRS(in TMatrix2x3 m, out TSVector2 pos, out FP rot, out TSVector2 scale)
    {
        pos = new TSVector2(m.m02, m.m12);

        // 线性部分 A = [a b; c d] = R * S (S 对角)
        FP a = m.m00; FP b = m.m01;
        FP c = m.m10; FP d = m.m11;

        rot = FP.Atan2(c, a); // R 的角度
        FP cosR = FP.FastCos(rot);
        FP sinR = FP.FastSin(rot);

        // S = R^T * A
        FP s00 =  cosR * a + sinR * c;
        FP s11 = (0-sinR) * b + cosR * d;

        // 防止极小
        if (FP.Abs(s00) < MIN_ABS_SCALE) s00 = (s00 >= 0 ? MIN_ABS_SCALE : 0-MIN_ABS_SCALE);
        if (FP.Abs(s11) < MIN_ABS_SCALE) s11 = (s11 >= 0 ? MIN_ABS_SCALE : 0-MIN_ABS_SCALE);

        scale = new TSVector2(s00, s11);

        NormalizeRad(ref rot);
    }
}