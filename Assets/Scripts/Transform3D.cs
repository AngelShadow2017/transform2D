using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using UnityEngine;

/*
 * 3D 固定点 Transform（纯 TRS，无显式 shear 存储）。
 * 实现要点：
 *   R_world = Π R_i
 *   W_world = Π (R_i * S_i)      // 不再取 abs
 *   S_world = R_world^T * W_world
 *   worldScale(lossyScale) = diag(S_world)
 *
 * 若有非均匀缩放 + 下级旋转 => W_world 包含 shear，使 S_world 非对角，diag 只是近似（与 Unity 文档一致）。
 * 可选：SetParent 保持世界时两种策略：
 *   1) USE_PURE_RS_DECOMPOSE = true  (通过 worldMatrix 反解 local，吸收 shear)
 *   2) 否则用公式近似（保持 worldRotation / worldScale 近似）
 *
 * 仍保留 signAccum（如果不要，可删除），对世界缩放值不再必需。
 */

public class Transform3DFixed
{
    #region Config
    private const bool USE_PURE_RS_DECOMPOSE = true;
    private const bool SCALE_RETAIN_ITERATE = false; // 仅在公式回推模式时用于一次迭代修正
    #endregion

    #region Fields
    private Transform3DFixed _parent;
    private readonly HashSet<Transform3DFixed> _children = new();

    // Local TRS
    private TSVector _localPosition = TSVector.zero;
    private TSQuaternion _localRotation = TSQuaternion.identity;
    private TSVector _localScale = TSVector.one; // 可含负

    // Matrices
    private TMatrix3x4 _localMatrix = TMatrix3x4.Identity;
    private TMatrix3x4 _worldMatrix = TMatrix3x4.Identity;
    private TMatrix3x4 _worldInv = TMatrix3x4.Identity;
    private bool _worldInvDirty = true;

    // World cached
    private TSVector _worldPosition = TSVector.zero;
    private TSQuaternion _worldRotation = TSQuaternion.identity;
    private TSVector _worldScale = TSVector.one;

    // 纯旋转累积 R_world
    private FP _rw00=FP.One,_rw01=FP.Zero,_rw02=FP.Zero;
    private FP _rw10=FP.Zero,_rw11=FP.One,_rw12=FP.Zero;
    private FP _rw20=FP.Zero,_rw21=FP.Zero,_rw22=FP.One;

    // W_world = Π (R_i * S_i)
    private FP _ww00=FP.One,_ww01=FP.Zero,_ww02=FP.Zero;
    private FP _ww10=FP.Zero,_ww11=FP.One,_ww12=FP.Zero;
    private FP _ww20=FP.Zero,_ww21=FP.Zero,_ww22=FP.One;

    // 符号链（调试用，可移除）
    private int _signX = 1, _signY = 1, _signZ = 1;

    private bool _localDirty = true;
    private bool _worldDirty = true;

    private static readonly FP MIN_ABS_SCALE = FP.EN6;
    #endregion

    #region Local Properties
    public TSVector localPosition
    {
        get => _localPosition;
        set { if (_localPosition != value) { _localPosition = value; MarkLocalDirty(); } }
    }
    public TSQuaternion localRotation
    {
        get => _localRotation;
        set { if (!TSQuaternion.ValueEquals(_localRotation,value)) { _localRotation = value; MarkLocalDirty(); } }
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

    #region World Readonly
    public TSVector worldPosition { get { UpdateWorld(); return _worldPosition; } }
    public TSQuaternion worldRotation { get { UpdateWorld(); return _worldRotation; } }
    public TSVector worldScale { get { UpdateWorld(); return _worldScale; } }
    public TSVector lossyScale => worldScale;

    public TMatrix3x4 worldMatrix { get { UpdateWorld(); return _worldMatrix; } }
    public TMatrix3x4 localMatrix { get { UpdateLocal(); return _localMatrix; } }
    public TMatrix3x4 worldMatrixInverse { get { UpdateWorldInverse(); return _worldInv; } }

    public (int x,int y,int z) signAccum { get { UpdateWorld(); return (_signX,_signY,_signZ); } }
    #endregion

    #region Hierarchy
    public Transform3DFixed parent => _parent;

    public void SetParent(Transform3DFixed newParent, bool keepWorld = true, bool keepWorldScale = true)
    {
        if (_parent == newParent) return;

        UpdateWorld();
        TSVector cachedPos = _worldPosition;
        TSQuaternion cachedRot = _worldRotation;
        TSVector cachedWorldScale = _worldScale;
        TMatrix3x4 oldWorld = _worldMatrix;

        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        if (!keepWorld)
        {
            MarkWorldDirty();
            return;
        }

        if (USE_PURE_RS_DECOMPOSE)
        {
            if (_parent == null)
            {
                DecomposePureRS3D(oldWorld, out _localPosition, out _localRotation, out _localScale);
            }
            else
            {
                var invParent = _parent.worldMatrixInverse;
                var newLocal = invParent * oldWorld;
                DecomposePureRS3D(newLocal, out _localPosition, out _localRotation, out _localScale);
            }
            if (!keepWorldScale)
            {
                // 可在此恢复原 localScale，如果想忽略世界缩放保持
                // _localScale = _localScale;
            }
        }
        else
        {
            if (_parent == null)
            {
                _localPosition = cachedPos;
                _localRotation = cachedRot;
                if (keepWorldScale)
                    _localScale = SanitizeScale(cachedWorldScale);
            }
            else
            {
                _parent.UpdateWorld();
                var invParentRot = TSQuaternion.Inverse(_parent._worldRotation);
                _localRotation = invParentRot * cachedRot;
                _localRotation.Normalize();

                var invPMat = _parent.worldMatrixInverse;
                _localPosition = invPMat.MultiplyPoint(cachedPos);

                if (keepWorldScale)
                {
                    _localScale = InitialLocalScaleFromWorld(cachedWorldScale, _parent._worldScale);
                    _localScale = SanitizeScale(_localScale);

                    if (SCALE_RETAIN_ITERATE)
                    {
                        MarkLocalDirty();
                        UpdateWorld();
                        TSVector predicted = _worldScale;
                        FP px = FP.Abs(predicted.x) < MIN_ABS_SCALE ? MIN_ABS_SCALE : predicted.x;
                        FP py = FP.Abs(predicted.y) < MIN_ABS_SCALE ? MIN_ABS_SCALE : predicted.y;
                        FP pz = FP.Abs(predicted.z) < MIN_ABS_SCALE ? MIN_ABS_SCALE : predicted.z;
                        TSVector ratio = new TSVector(cachedWorldScale.x / px,
                                                      cachedWorldScale.y / py,
                                                      cachedWorldScale.z / pz);
                        _localScale = SanitizeScale(new TSVector(_localScale.x * ratio.x,
                                                                 _localScale.y * ratio.y,
                                                                 _localScale.z * ratio.z));
                        MarkLocalDirty();
                    }
                }
            }
        }

        _localDirty = true;
        MarkWorldDirty();
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
        _worldInvDirty = true;
        foreach (var c in _children)
            c.MarkWorldDirty();
    }
    #endregion

    #region UpdateLocal
    private void UpdateLocal()
    {
        if (!_localDirty) return;

        TSQuaternion q = _localRotation;
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

        FP sx = _localScale.x;
        FP sy = _localScale.y;
        FP sz = _localScale.z;

        _localMatrix.m00 = r00 * sx; _localMatrix.m01 = r01 * sy; _localMatrix.m02 = r02 * sz; _localMatrix.m03 = _localPosition.x;
        _localMatrix.m10 = r10 * sx; _localMatrix.m11 = r11 * sy; _localMatrix.m12 = r12 * sz; _localMatrix.m13 = _localPosition.y;
        _localMatrix.m20 = r20 * sx; _localMatrix.m21 = r21 * sy; _localMatrix.m22 = r22 * sz; _localMatrix.m23 = _localPosition.z;

        _localDirty = false;
    }
    #endregion

    #region UpdateWorld (R_world / W_world / worldScale)
    private void UpdateWorld()
    {
        if (!_worldDirty) return;

        UpdateLocal();

        // 提取本地旋转列 (R_local)
        TSQuaternion q = _localRotation;
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

        FP lr00 = FP.One - two * (yy + zz);
        FP lr10 = two * (xy + wz);
        FP lr20 = two * (xz - wy);

        FP lr01 = two * (xy - wz);
        FP lr11 = FP.One - two * (xx + zz);
        FP lr21 = two * (yz + wx);

        FP lr02 = two * (xz + wy);
        FP lr12 = two * (yz - wx);
        FP lr22 = FP.One - two * (xx + yy);

        FP sx = _localScale.x;
        FP sy = _localScale.y;
        FP sz = _localScale.z;

        // (R_local * S_local)
        FP ls00 = lr00 * sx; FP ls01 = lr01 * sy; FP ls02 = lr02 * sz;
        FP ls10 = lr10 * sx; FP ls11 = lr11 * sy; FP ls12 = lr12 * sz;
        FP ls20 = lr20 * sx; FP ls21 = lr21 * sy; FP ls22 = lr22 * sz;

        if (_parent == null)
        {
            _worldMatrix = _localMatrix;
            _worldPosition = _localPosition;
            _worldRotation = _localRotation;

            // R_world = R_local
            _rw00 = lr00; _rw01 = lr01; _rw02 = lr02;
            _rw10 = lr10; _rw11 = lr11; _rw12 = lr12;
            _rw20 = lr20; _rw21 = lr21; _rw22 = lr22;

            // W_world = R_local * S_local
            _ww00 = ls00; _ww01 = ls01; _ww02 = ls02;
            _ww10 = ls10; _ww11 = ls11; _ww12 = ls12;
            _ww20 = ls20; _ww21 = ls21; _ww22 = ls22;

            // signAccum (仅调试)
            _signX = sx < 0 ? -1 : 1;
            _signY = sy < 0 ? -1 : 1;
            _signZ = sz < 0 ? -1 : 1;

            // worldScale = diag(R_world^T * W_world) = dot(Rw.col(i), Ww.col(i))
            FP wsx =  _rw00 * _ww00 + _rw10 * _ww10 + _rw20 * _ww20;
            FP wsy =  _rw01 * _ww01 + _rw11 * _ww11 + _rw21 * _ww21;
            FP wsz =  _rw02 * _ww02 + _rw12 * _ww12 + _rw22 * _ww22;
            _worldScale = new TSVector(wsx, wsy, wsz);
        }
        else
        {
            _parent.UpdateWorld();
            _worldMatrix = _parent._worldMatrix * _localMatrix;
            _worldPosition = new TSVector(_worldMatrix.m03, _worldMatrix.m13, _worldMatrix.m23);
            _worldRotation = _parent._worldRotation * _localRotation;

            // R_world = R_parent * R_local
            FP pr00=_parent._rw00, pr01=_parent._rw01, pr02=_parent._rw02;
            FP pr10=_parent._rw10, pr11=_parent._rw11, pr12=_parent._rw12;
            FP pr20=_parent._rw20, pr21=_parent._rw21, pr22=_parent._rw22;

            _rw00 = pr00 * lr00 + pr01 * lr10 + pr02 * lr20;
            _rw01 = pr00 * lr01 + pr01 * lr11 + pr02 * lr21;
            _rw02 = pr00 * lr02 + pr01 * lr12 + pr02 * lr22;

            _rw10 = pr10 * lr00 + pr11 * lr10 + pr12 * lr20;
            _rw11 = pr10 * lr01 + pr11 * lr11 + pr12 * lr21;
            _rw12 = pr10 * lr02 + pr11 * lr12 + pr12 * lr22;

            _rw20 = pr20 * lr00 + pr21 * lr10 + pr22 * lr20;
            _rw21 = pr20 * lr01 + pr21 * lr11 + pr22 * lr21;
            _rw22 = pr20 * lr02 + pr21 * lr12 + pr22 * lr22;

            // W_world = W_parent * (R_local * S_local)
            FP pw00=_parent._ww00, pw01=_parent._ww01, pw02=_parent._ww02;
            FP pw10=_parent._ww10, pw11=_parent._ww11, pw12=_parent._ww12;
            FP pw20=_parent._ww20, pw21=_parent._ww21, pw22=_parent._ww22;

            _ww00 = pw00 * ls00 + pw01 * ls10 + pw02 * ls20;
            _ww01 = pw00 * ls01 + pw01 * ls11 + pw02 * ls21;
            _ww02 = pw00 * ls02 + pw01 * ls12 + pw02 * ls22;

            _ww10 = pw10 * ls00 + pw11 * ls10 + pw12 * ls20;
            _ww11 = pw10 * ls01 + pw11 * ls11 + pw12 * ls21;
            _ww12 = pw10 * ls02 + pw11 * ls12 + pw12 * ls22;

            _ww20 = pw20 * ls00 + pw21 * ls10 + pw22 * ls20;
            _ww21 = pw20 * ls01 + pw21 * ls11 + pw22 * ls21;
            _ww22 = pw20 * ls02 + pw21 * ls12 + pw22 * ls22;

            // signAccum（调试）
            int lx = sx < 0 ? -1 : 1;
            int ly = sy < 0 ? -1 : 1;
            int lz = sz < 0 ? -1 : 1;
            _signX = _parent._signX * lx;
            _signY = _parent._signY * ly;
            _signZ = _parent._signZ * lz;

            // worldScale
            FP wsx =  _rw00 * _ww00 + _rw10 * _ww10 + _rw20 * _ww20;
            FP wsy =  _rw01 * _ww01 + _rw11 * _ww11 + _rw21 * _ww21;
            FP wsz =  _rw02 * _ww02 + _rw12 * _ww12 + _rw22 * _ww22;
            _worldScale = new TSVector(wsx, wsy, wsz);
        }

        _worldDirty = false;
        _worldInvDirty = true;
    }

    private void UpdateWorldInverse()
    {
        UpdateWorld();
        if (!_worldInvDirty) return;
        _worldInv = _worldMatrix.Inverse();
        _worldInvDirty = false;
    }
    #endregion

    #region Helpers
    private static TSVector SanitizeScale(TSVector v)
    {
        if (FP.Abs(v.x) < MIN_ABS_SCALE) v.x = (v.x >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (FP.Abs(v.y) < MIN_ABS_SCALE) v.y = (v.y >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        if (FP.Abs(v.z) < MIN_ABS_SCALE) v.z = (v.z >= 0 ? MIN_ABS_SCALE : -MIN_ABS_SCALE);
        return v;
    }
    private static TSVector InitialLocalScaleFromWorld(in TSVector desiredWorldScale, in TSVector parentWorldScale)
    {
        FP px = FP.Abs(parentWorldScale.x) < MIN_ABS_SCALE ? MIN_ABS_SCALE : parentWorldScale.x;
        FP py = FP.Abs(parentWorldScale.y) < MIN_ABS_SCALE ? MIN_ABS_SCALE : parentWorldScale.y;
        FP pz = FP.Abs(parentWorldScale.z) < MIN_ABS_SCALE ? MIN_ABS_SCALE : parentWorldScale.z;
        return new TSVector(desiredWorldScale.x / px,
                            desiredWorldScale.y / py,
                            desiredWorldScale.z / pz);
    }
    #endregion

    #region API
    public TSVector TransformPoint(in TSVector p) { UpdateWorld(); return _worldMatrix.MultiplyPoint(p); }
    public TSVector InverseTransformPoint(in TSVector p) { return worldMatrixInverse.MultiplyPoint(p); }
    public TSVector TransformVector(in TSVector v) { UpdateWorld(); return _worldMatrix.MultiplyVector(v); }
    public TSVector InverseTransformVector(in TSVector v) => worldMatrixInverse.MultiplyVector(v);
    public TSVector TransformDirection(in TSVector d) { UpdateWorld(); return _worldRotation * d; }
    public TSVector InverseTransformDirection(in TSVector d) { UpdateWorld(); var inv = TSQuaternion.Inverse(_worldRotation); return inv * d; }

    public void Translate(in TSVector delta, Space space = Space.Self)
    {
        if (space == Space.World) localPosition = worldPosition + delta;
        else
        {
            UpdateWorld();
            var right = _worldRotation * TSVector.right;
            var up    = _worldRotation * TSVector.up;
            var fwd   = _worldRotation * TSVector.forward;
            localPosition = worldPosition + right * delta.x + up * delta.y + fwd * delta.z;
        }
    }
    public void Rotate(in TSQuaternion dq, Space space = Space.Self)
    {
        if (space == Space.Self) localRotation = localRotation * dq;
        else SetWorldRotation(worldRotation * dq);
    }
    public void SetWorldPosition(in TSVector wp)
    {
        if (_parent == null) localPosition = wp;
        else
        {
            var invParent = _parent.worldMatrixInverse;
            localPosition = invParent.MultiplyPoint(wp);
        }
    }
    public void SetWorldRotation(in TSQuaternion wr)
    {
        if (_parent == null) localRotation = wr;
        else
        {
            _parent.UpdateWorld();
            var inv = TSQuaternion.Inverse(_parent._worldRotation);
            localRotation = inv * wr;
        }
    }
    #endregion

    #region Debug
    public override string ToString()
    {
        return $"Local(P={_localPosition}, R={_localRotation}, S={_localScale}) | World(P={_worldPosition}, R={_worldRotation}, Scale={_worldScale}, sign=({_signX},{_signY},{_signZ}))";
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

    #region Recursive
    public void RecalculateWorldRecursive()
    {
        UpdateWorld();
        foreach (var c in _children)
            c.RecalculateWorldRecursive();
    }
    #endregion

    #region Batch Local
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLocalTRS(in TSVector pos, in TSQuaternion rot, in TSVector scale)
    {
        bool changed = false;
        if (_localPosition != pos) { _localPosition = pos; changed = true; }
        if (!TSQuaternion.ValueEquals(_localRotation, rot)) { _localRotation = rot; changed = true; }
        var sc = SanitizeScale(scale);
        if (_localScale != sc) { _localScale = sc; changed = true; }
        if (changed) { _localDirty = true; MarkWorldDirty(); }
    }
    #endregion

    #region Ctor
    public Transform3DFixed() { }
    public Transform3DFixed(Transform3DFixed parent, bool keepWorld = true) { SetParent(parent, keepWorld); }
    #endregion

    #region Pure RS
    public static void DecomposePureRS3D(in TMatrix3x4 m,
                                         out TSVector pos,
                                         out TSQuaternion rot,
                                         out TSVector scale)
    {
        Transform3DFixedRSHelper.DecomposePureRS(in m, out pos, out rot, out scale);
    }
    #endregion
}

/// <summary>
/// 3D 纯 RS 分解（假设线性 = R * S）。若含 shear，会把 shear 吸收入旋转/缩放——与 2D 行为一致。
/// 行列式 < 0 ：翻第一列 + sx 取负。
/// </summary>
public static class Transform3DFixedRSHelper
{
    private static readonly FP MIN_ABS_SCALE = FP.EN6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecomposePureRS(in TMatrix3x4 m,
                                       out TSVector pos,
                                       out TSQuaternion rot,
                                       out TSVector scale)
    {
        pos = new TSVector(m.m03, m.m13, m.m23);

        TSVector c0 = new TSVector(m.m00, m.m10, m.m20);
        TSVector c1 = new TSVector(m.m01, m.m11, m.m21);
        TSVector c2 = new TSVector(m.m02, m.m12, m.m22);

        FP sx = c0.magnitude;
        FP sy = c1.magnitude;
        FP sz = c2.magnitude;

        if (sx < MIN_ABS_SCALE) sx = MIN_ABS_SCALE;
        if (sy < MIN_ABS_SCALE) sy = MIN_ABS_SCALE;
        if (sz < MIN_ABS_SCALE) sz = MIN_ABS_SCALE;

        TSVector r0 = c0 / sx;
        TSVector r1 = c1 / sy;
        TSVector r2 = c2 / sz;

        FP det = Dot(r0, Cross(r1, r2));
        if (det < FP.Zero)
        {
            sx = -sx;
            r0 = TSVector.zero-r0;
        }

        rot = FromRotationColumns(r0, r1, r2);
        rot.Normalize();
        scale = new TSVector(sx, sy, sz);
    }

    #region Math helpers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FP Dot(in TSVector a, in TSVector b) => a.x * b.x + a.y * b.y + a.z * b.z;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TSVector Cross(in TSVector a, in TSVector b)
        => new TSVector(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x
        );

    private static TSQuaternion FromRotationColumns(in TSVector r0, in TSVector r1, in TSVector r2)
    {
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
                s * (FP)0.25
            );
        }
        else if (r0.x > r1.y && r0.x > r2.z)
        {
            FP s = FP.Sqrt(FP.One + r0.x - r1.y - r2.z) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                s * (FP)0.25,
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
                s * (FP)0.25,
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
                s * (FP)0.25,
                (r1.x - r0.y) * invS
            );
        }
        q.Normalize();
        return q;
    }
    #endregion
}

public static class Transform3DFixedHelper
{
    // 如需再加其它工具函数，可放这里
}