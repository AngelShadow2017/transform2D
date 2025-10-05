using System;
using System.Runtime.CompilerServices;
using Core.TrueSync;

public class Transform3DFixedSkew
{
    #region Config
    private const bool RETAIN_SHEAR_ON_EDIT = true; // 是否在用户修改 rot/scale 时保留 shear
    private static readonly FP MIN_ABS_SCALE = FP.EN6;
    #endregion

    #region Hierarchy
    private Transform3DFixedSkew _parent;
    private readonly System.Collections.Generic.HashSet<Transform3DFixedSkew> _children = new();
    #endregion

    #region Local Data (Position + Hidden Full Linear)
    private TSVector _localPosition = TSVector.zero;

    // 内部局部线性矩阵 L_local_full (3x3)
    private FP _ll00=FP.One,_ll01=FP.Zero,_ll02=FP.Zero;
    private FP _ll10=FP.Zero,_ll11=FP.One,_ll12=FP.Zero;
    private FP _ll20=FP.Zero,_ll21=FP.Zero,_ll22=FP.One;

    // 暴露给外部的 TRS 缓存
    private TSQuaternion _localRotExposed = TSQuaternion.identity;
    private TSVector     _localScaleExposed = TSVector.one;
    private bool _exposedTRSDirty = true;

    // 世界矩阵（3x4）与线性部分缓存
    private TMatrix3x4 _worldMatrix = TMatrix3x4.Identity;
    private bool _worldDirty = true;

    // 世界 rotation 累积（纯 Q 乘）: R_world
    private FP _rw00=FP.One,_rw01=FP.Zero,_rw02=FP.Zero;
    private FP _rw10=FP.Zero,_rw11=FP.One,_rw12=FP.Zero;
    private FP _rw20=FP.Zero,_rw21=FP.Zero,_rw22=FP.One;

    // W_world = Π L_i_full
    private FP _ww00=FP.One,_ww01=FP.Zero,_ww02=FP.Zero;
    private FP _ww10=FP.Zero,_ww11=FP.One,_ww12=FP.Zero;
    private FP _ww20=FP.Zero,_ww21=FP.Zero,_ww22=FP.One;

    private TSVector _worldPosition = TSVector.zero;
    private TSQuaternion _worldRotation = TSQuaternion.identity;
    private TSVector _worldScale = TSVector.one;

    // 记录本地是否被“用户直接编辑过 TRS”
    private bool _localEditedTRS = false;
    #endregion

    #region Public Local Properties (只暴露 TRS)
    public TSVector localPosition
    {
        get => _localPosition;
        set { if (_localPosition != value){ _localPosition = value; MarkWorldDirty(); } }
    }
    public TSQuaternion localRotation
    {
        get
        {
            UpdateExposedTRS();
            return _localRotExposed;
        }
        set
        {
            UpdateExposedTRS();
            if (!TSQuaternion.ValueEquals(_localRotExposed, value))
            {
                ApplyNewLocalRotScale(value, _localScaleExposed, retainShear: RETAIN_SHEAR_ON_EDIT);
            }
        }
    }
    public TSVector localScale
    {
        get
        {
            UpdateExposedTRS();
            return _localScaleExposed;
        }
        set
        {
            UpdateExposedTRS();
            TSVector sanitized = SanitizeScale(value);
            if (_localScaleExposed != sanitized)
            {
                ApplyNewLocalRotScale(_localRotExposed, sanitized, retainShear: RETAIN_SHEAR_ON_EDIT);
            }
        }
    }
    #endregion

    #region Public World Readonly
    public TSVector worldPosition { get { UpdateWorld(); return _worldPosition; } }
    public TSQuaternion worldRotation { get { UpdateWorld(); return _worldRotation; } }
    public TSVector worldScale { get { UpdateWorld(); return _worldScale; } }
    public TSVector lossyScale => worldScale;
    #endregion

    #region Parent
    public Transform3DFixedSkew parent => _parent;

    public void SetParent(Transform3DFixedSkew newParent, bool keepWorld = true)
    {
        if (_parent == newParent) return;
        UpdateWorld();
        TMatrix3x4 oldWorld = _worldMatrix;

        if (_parent != null) _parent._children.Remove(this);
        _parent = newParent;
        if (_parent != null) _parent._children.Add(this);

        if (!keepWorld)
        {
            MarkWorldDirty();
            return;
        }

        // 线性部分：L_local_new = invParentLinear * L_world_old
        if (_parent == null)
        {
            // 直接用旧的世界矩阵作为新的局部
            CopyLinear(oldWorld, ref _ll00, ref _ll01, ref _ll02,
                                 ref _ll10, ref _ll11, ref _ll12,
                                 ref _ll20, ref _ll21, ref _ll22);
            _localPosition = new TSVector(oldWorld.m03, oldWorld.m13, oldWorld.m23);
        }
        else
        {
            _parent.UpdateWorld();
            // parent 世界线性 (3x3)
            FP p00=_parent._ww00, p01=_parent._ww01, p02=_parent._ww02;
            FP p10=_parent._ww10, p11=_parent._ww11, p12=_parent._ww12;
            FP p20=_parent._ww20, p21=_parent._ww21, p22=_parent._ww22;

            // inverse(parent linear) * worldLinear
            // 先求 parentLinear 的逆 (正交+shear 上三角，可用一般 3x3 逆)
            Invert3x3(p00,p01,p02,p10,p11,p12,p20,p21,p22,
                      out FP ip00,out FP ip01,out FP ip02,
                      out FP ip10,out FP ip11,out FP ip12,
                      out FP ip20,out FP ip21,out FP ip22);

            // world linear
            FP w00=_ww00, w01=_ww01, w02=_ww02;
            FP w10=_ww10, w11=_ww11, w12=_ww12;
            FP w20=_ww20, w21=_ww21, w22=_ww22;

            _ll00 = ip00*w00 + ip01*w10 + ip02*w20;
            _ll01 = ip00*w01 + ip01*w11 + ip02*w21;
            _ll02 = ip00*w02 + ip01*w12 + ip02*w22;

            _ll10 = ip10*w00 + ip11*w10 + ip12*w20;
            _ll11 = ip10*w01 + ip11*w11 + ip12*w21;
            _ll12 = ip10*w02 + ip11*w12 + ip12*w22;

            _ll20 = ip20*w00 + ip21*w10 + ip22*w20;
            _ll21 = ip20*w01 + ip21*w11 + ip22*w21;
            _ll22 = ip20*w02 + ip21*w12 + ip22*w22;

            // 位置： invParentWorldMatrix * oldWorld.position
            var invParentWorld = _parent.WorldMatrixInverse(); // 你可缓存逆
            _localPosition = invParentWorld.MultiplyPoint(new TSVector(oldWorld.m03, oldWorld.m13, oldWorld.m23));
        }

        _exposedTRSDirty = true;
        _localEditedTRS = false;
        MarkWorldDirty();
    }
    #endregion

    #region Internal: Apply New Local Rot+Scale
    private void ApplyNewLocalRotScale(TSQuaternion newRot, TSVector newScale, bool retainShear)
    {
        // 需要先拿到当前 shear（通过 QR）
        UpdateExposedTRS(); // 确保我们已经把 _ll** 分解成 _localRotExposed/_localScaleExposed
        // 现在我们需要：L = Q * R
        // 之前分解时并未显式缓存 R 的上三角；这里可以即时再算一次 QR
        FP q00,q01,q02,q10,q11,q12,q20,q21,q22;
        FP r00,r01,r02,r11,r12,r22;
        QRDecompose(_ll00,_ll01,_ll02,_ll10,_ll11,_ll12,_ll20,_ll21,_ll22,
                    out q00,out q01,out q02,
                    out q10,out q11,out q12,
                    out q20,out q21,out q22,
                    out r00,out r01,out r02,out r11,out r12,out r22);

        // 新旋转矩阵 (from newRot)
        QuaternionToMatrix(newRot,
                           out FP nr00,out FP nr01,out FP nr02,
                           out FP nr10,out FP nr11,out FP nr12,
                           out FP nr20,out FP nr21,out FP nr22);

        // 新缩放
        FP nsx = (FP)newScale.x;
        FP nsy = (FP)newScale.y;
        FP nsz = (FP)newScale.z;

        if (!retainShear)
        {
            // 丢弃 shear: L = R_new * diag(newScale)
            _ll00 = nr00 * nsx; _ll01 = nr01 * nsy; _ll02 = nr02 * nsz;
            _ll10 = nr10 * nsx; _ll11 = nr11 * nsy; _ll12 = nr12 * nsz;
            _ll20 = nr20 * nsx; _ll21 = nr21 * nsy; _ll22 = nr22 * nsz;
        }
        else
        {
            // 保留 shear: 旧 R 上三角的非对角(r01,r02,r12) 需要归一化策略
            // 方案 1：直接把旧 r01,r02,r12 “映射”到新缩放
            // 原 R = | r00 r01 r02 |
            //        | 0   r11 r12 |
            //        | 0   0   r22 |
            // 其中 r00,r11,r22 ~ 旧 scale_x,y,z（正）; offDiag = shear * 对应尺度
            // shearXy = r01 / r11, shearXz = r02 / r22, shearYz = r12 / r22
            FP old_sx = r00;
            FP old_sy = r11;
            FP old_sz = r22;
            if (old_sx == FP.Zero) old_sx = MIN_ABS_SCALE;
            if (old_sy == FP.Zero) old_sy = MIN_ABS_SCALE;
            if (old_sz == FP.Zero) old_sz = MIN_ABS_SCALE;

            FP sh_xy = r01 / old_sy;
            FP sh_xz = r02 / old_sz;
            FP sh_yz = r12 / old_sz;

            // 重建新的上三角：
            FP nr00u = nsx;
            FP nr11u = nsy;
            FP nr22u = nsz;
            FP nr01u = sh_xy * nr11u;
            FP nr02u = sh_xz * nr22u;
            FP nr12u = sh_yz * nr22u;

            // L_new = R_new * R_upper_new
            _ll00 = nr00 * nr00u + nr01 * 0     + nr02 * 0;
            _ll01 = nr00 * nr01u + nr01 * nr11u + nr02 * 0;
            _ll02 = nr00 * nr02u + nr01 * nr12u + nr02 * nr22u;

            _ll10 = nr10 * nr00u + nr11 * 0     + nr12 * 0;
            _ll11 = nr10 * nr01u + nr11 * nr11u + nr12 * 0;
            _ll12 = nr10 * nr02u + nr11 * nr12u + nr12 * nr22u;

            _ll20 = nr20 * nr00u + nr21 * 0     + nr22 * 0;
            _ll21 = nr20 * nr01u + nr21 * nr11u + nr22 * 0;
            _ll22 = nr20 * nr02u + nr21 * nr12u + nr22 * nr22u;
        }

        _exposedTRSDirty = true;
        _localEditedTRS = true;
        MarkWorldDirty();
    }
    #endregion

    #region Update Exposed TRS (QR)
    private void UpdateExposedTRS()
    {
        if (!_exposedTRSDirty) return;
        // QR 分解
        FP q00,q01,q02,q10,q11,q12,q20,q21,q22;
        FP r00,r01,r02,r11,r12,r22;
        QRDecompose(_ll00,_ll01,_ll02,_ll10,_ll11,_ll12,_ll20,_ll21,_ll22,
                    out q00,out q01,out q02,
                    out q10,out q11,out q12,
                    out q20,out q21,out q22,
                    out r00,out r01,out r02,out r11,out r12,out r22);

        // rotation from Q
        _localRotExposed = RotationFromColumns(q00,q10,q20, q01,q11,q21, q02,q12,q22);
        // 轴向缩放取对角
        _localScaleExposed = new TSVector(r00, r11, r22);
        _exposedTRSDirty = false;
    }
    #endregion

    #region World Update
    private void UpdateWorld()
    {
        if (!_worldDirty) return;

        if (_parent == null)
        {
            // worldLinear = localLinear
            _ww00=_ll00; _ww01=_ll01; _ww02=_ll02;
            _ww10=_ll10; _ww11=_ll11; _ww12=_ll12;
            _ww20=_ll20; _ww21=_ll21; _ww22=_ll22;

            // 取局部 Q（需 QR，保证 R_world）
            UpdateExposedTRS(); // 确保有分解（会做 QR）
            QuaternionToMatrix(_localRotExposed,
                               out _rw00,out _rw01,out _rw02,
                               out _rw10,out _rw11,out _rw12,
                               out _rw20,out _rw21,out _rw22);

            _worldPosition = _localPosition;
            _worldRotation = _localRotExposed;
        }
        else
        {
            _parent.UpdateWorld();

            // 世界线性 = parentLinear * localLinear
            FP pw00=_parent._ww00, pw01=_parent._ww01, pw02=_parent._ww02;
            FP pw10=_parent._ww10, pw11=_parent._ww11, pw12=_parent._ww12;
            FP pw20=_parent._ww20, pw21=_parent._ww21, pw22=_parent._ww22;

            _ww00 = pw00*_ll00 + pw01*_ll10 + pw02*_ll20;
            _ww01 = pw00*_ll01 + pw01*_ll11 + pw02*_ll21;
            _ww02 = pw00*_ll02 + pw01*_ll12 + pw02*_ll22;

            _ww10 = pw10*_ll00 + pw11*_ll10 + pw12*_ll20;
            _ww11 = pw10*_ll01 + pw11*_ll11 + pw12*_ll21;
            _ww12 = pw10*_ll02 + pw11*_ll12 + pw12*_ll22;

            _ww20 = pw20*_ll00 + pw21*_ll10 + pw22*_ll20;
            _ww21 = pw20*_ll01 + pw21*_ll11 + pw22*_ll21;
            _ww22 = pw20*_ll02 + pw21*_ll12 + pw22*_ll22;

            // 累计旋转 R_world = R_parent * R_local(Q)
            UpdateExposedTRS();
            QuaternionToMatrix(_localRotExposed,
                               out FP lr00,out FP lr01,out FP lr02,
                               out FP lr10,out FP lr11,out FP lr12,
                               out FP lr20,out FP lr21,out FP lr22);

            FP pr00=_parent._rw00, pr01=_parent._rw01, pr02=_parent._rw02;
            FP pr10=_parent._rw10, pr11=_parent._rw11, pr12=_parent._rw12;
            FP pr20=_parent._rw20, pr21=_parent._rw21, pr22=_parent._rw22;

            _rw00 = pr00*lr00 + pr01*lr10 + pr02*lr20;
            _rw01 = pr00*lr01 + pr01*lr11 + pr02*lr21;
            _rw02 = pr00*lr02 + pr01*lr12 + pr02*lr22;

            _rw10 = pr10*lr00 + pr11*lr10 + pr12*lr20;
            _rw11 = pr10*lr01 + pr11*lr11 + pr12*lr21;
            _rw12 = pr10*lr02 + pr11*lr12 + pr12*lr22;

            _rw20 = pr20*lr00 + pr21*lr10 + pr22*lr20;
            _rw21 = pr20*lr01 + pr21*lr11 + pr22*lr21;
            _rw22 = pr20*lr02 + pr21*lr12 + pr22*lr22;

            _worldPosition = _parent.TransformPoint(_localPosition);
            _worldRotation = _parent._worldRotation * _localRotExposed;
        }

        // worldScale (lossy) = diag(R_world^T * W_world)
        FP wsx = _rw00*_ww00 + _rw10*_ww10 + _rw20*_ww20;
        FP wsy = _rw01*_ww01 + _rw11*_ww11 + _rw21*_ww21;
        FP wsz = _rw02*_ww02 + _rw12*_ww12 + _rw22*_ww22;
        _worldScale = new TSVector(wsx,wsy,wsz);

        // worldMatrix（3x4）
        _worldMatrix.m00=_ww00; _worldMatrix.m01=_ww01; _worldMatrix.m02=_ww02; _worldMatrix.m03=_worldPosition.x;
        _worldMatrix.m10=_ww10; _worldMatrix.m11=_ww11; _worldMatrix.m12=_ww12; _worldMatrix.m13=_worldPosition.y;
        _worldMatrix.m20=_ww20; _worldMatrix.m21=_ww21; _worldMatrix.m22=_ww22; _worldMatrix.m23=_worldPosition.z;

        _worldDirty = false;
    }
    #endregion

    #region Utilities

    private void MarkWorldDirty()
    {
        if (_worldDirty) return;
        _worldDirty = true;
        foreach (var c in _children) c.MarkWorldDirty();
    }

    private static TSVector SanitizeScale(TSVector v)
    {
        if (FP.Abs(v.x) < MIN_ABS_SCALE) v.x = v.x>=0?MIN_ABS_SCALE:-MIN_ABS_SCALE;
        if (FP.Abs(v.y) < MIN_ABS_SCALE) v.y = v.y>=0?MIN_ABS_SCALE:-MIN_ABS_SCALE;
        if (FP.Abs(v.z) < MIN_ABS_SCALE) v.z = v.z>=0?MIN_ABS_SCALE:-MIN_ABS_SCALE;
        return v;
    }

    private static void CopyLinear(in TMatrix3x4 m,
        ref FP a00,ref FP a01,ref FP a02,
        ref FP a10,ref FP a11,ref FP a12,
        ref FP a20,ref FP a21,ref FP a22)
    {
        a00=m.m00; a01=m.m01; a02=m.m02;
        a10=m.m10; a11=m.m11; a12=m.m12;
        a20=m.m20; a21=m.m21; a22=m.m22;
    }

    // 3x3 逆
    private static void Invert3x3(FP a00,FP a01,FP a02,
                                  FP a10,FP a11,FP a12,
                                  FP a20,FP a21,FP a22,
                                  out FP i00,out FP i01,out FP i02,
                                  out FP i10,out FP i11,out FP i12,
                                  out FP i20,out FP i21,out FP i22)
    {
        FP c00 = a11*a22 - a12*a21;
        FP c01 = a02*a21 - a01*a22;
        FP c02 = a01*a12 - a02*a11;
        FP c10 = a12*a20 - a10*a22;
        FP c11 = a00*a22 - a02*a20;
        FP c12 = a02*a10 - a00*a12;
        FP c20 = a10*a21 - a11*a20;
        FP c21 = a01*a20 - a00*a21;
        FP c22 = a00*a11 - a01*a10;

        FP det = a00*c00 + a01*c10 + a02*c20;
        if (FP.Abs(det) < FP.EN7) det = (det>=0?FP.EN7:-FP.EN7);
        FP invDet = FP.One / det;

        i00 = c00*invDet; i01 = c01*invDet; i02 = c02*invDet;
        i10 = c10*invDet; i11 = c11*invDet; i12 = c12*invDet;
        i20 = c20*invDet; i21 = c21*invDet; i22 = c22*invDet;
    }

    // Modified Gram-Schmidt QR (确保对角正，若 det<0 调整符号)
    private static void QRDecompose(
        FP a00,FP a01,FP a02,
        FP a10,FP a11,FP a12,
        FP a20,FP a21,FP a22,
        out FP q00,out FP q01,out FP q02,
        out FP q10,out FP q11,out FP q12,
        out FP q20,out FP q21,out FP q22,
        out FP r00,out FP r01,out FP r02,
        out FP r11,out FP r12,out FP r22)
    {
        // c0
        FP v00=a00, v10=a10, v20=a20;
        r00 = FP.Sqrt(v00*v00 + v10*v10 + v20*v20);
        if (FP.Abs(r00) < FP.EN7) r00 = FP.EN7;
        q00 = v00 / r00; q10 = v10 / r00; q20 = v20 / r00;

        // c1
        FP dot01 = q00*a01 + q10*a11 + q20*a21;
        r01 = dot01;
        v00 = a01 - r01*q00;
        v10 = a11 - r01*q10;
        v20 = a21 - r01*q20;
        r11 = FP.Sqrt(v00*v00 + v10*v10 + v20*v20);
        if (FP.Abs(r11) < FP.EN7) r11 = FP.EN7;
        q01 = v00 / r11; q11 = v10 / r11; q21 = v20 / r11;

        // c2
        FP dot02 = q00*a02 + q10*a12 + q20*a22;
        FP dot12 = q01*a02 + q11*a12 + q21*a22;
        r02 = dot02;
        r12 = dot12;
        v00 = a02 - r02*q00 - r12*q01;
        v10 = a12 - r02*q10 - r12*q11;
        v20 = a22 - r02*q20 - r12*q21;
        r22 = FP.Sqrt(v00*v00 + v10*v10 + v20*v20);
        if (FP.Abs(r22) < FP.EN7) r22 = FP.EN7;
        q02 = v00 / r22; q12 = v10 / r22; q22 = v20 / r22;

        // 保证右手系（det>0），否则翻第一列 & r00 取负
        FP det = q00*(q11*q22 - q12*q21) - q01*(q10*q22 - q12*q20) + q02*(q10*q21 - q11*q20);
        if (det < FP.Zero)
        {
            q00 = -q00; q10 = -q10; q20 = -q20;
            r00 = -r00; r01 = -r01; r02 = -r02;
        }
    }

    private static TSQuaternion RotationFromColumns(FP c00,FP c10,FP c20,
                                                     FP c01,FP c11,FP c21,
                                                     FP c02,FP c12,FP c22)
    {
        // same as FromRotationColumns earlier
        FP trace = c00 + c11 + c22;
        TSQuaternion q;
        if (trace > FP.Zero)
        {
            FP s = FP.Sqrt(trace + FP.One) * 2;
            FP invS = FP.One / s;
            q = new TSQuaternion(
                (c21 - c12)*invS,
                (c02 - c20)*invS,
                (c10 - c01)*invS,
                s*(FP)0.25
            );
        }
        else if (c00 > c11 && c00 > c22)
        {
            FP s = FP.Sqrt(FP.One + c00 - c11 - c22)*2;
            FP invS = FP.One/s;
            q = new TSQuaternion(
                s*(FP)0.25,
                (c01 + c10)*invS,
                (c02 + c20)*invS,
                (c21 - c12)*invS
            );
        }
        else if (c11 > c22)
        {
            FP s = FP.Sqrt(FP.One + c11 - c00 - c22)*2;
            FP invS = FP.One/s;
            q = new TSQuaternion(
                (c01 + c10)*invS,
                s*(FP)0.25,
                (c12 + c21)*invS,
                (c02 - c20)*invS
            );
        }
        else
        {
            FP s = FP.Sqrt(FP.One + c22 - c00 - c11)*2;
            FP invS = FP.One/s;
            q = new TSQuaternion(
                (c02 + c20)*invS,
                (c12 + c21)*invS,
                s*(FP)0.25,
                (c10 - c01)*invS
            );
        }
        q.Normalize();
        return q;
    }

    private static void QuaternionToMatrix(TSQuaternion q,
                                           out FP m00,out FP m01,out FP m02,
                                           out FP m10,out FP m11,out FP m12,
                                           out FP m20,out FP m21,out FP m22)
    {
        FP xx=q.x*q.x, yy=q.y*q.y, zz=q.z*q.z;
        FP xy=q.x*q.y, xz=q.x*q.z, yz=q.y*q.z;
        FP wx=q.w*q.x, wy=q.w*q.y, wz=q.w*q.z;
        FP two=(FP)2;

        m00 = FP.One - two*(yy+zz);
        m01 = two*(xy - wz);
        m02 = two*(xz + wy);

        m10 = two*(xy + wz);
        m11 = FP.One - two*(xx+zz);
        m12 = two*(yz - wx);

        m20 = two*(xz - wy);
        m21 = two*(yz + wx);
        m22 = FP.One - two*(xx+yy);
    }

    public TSVector TransformPoint(in TSVector p)
    {
        UpdateWorld();
        return _worldMatrix.MultiplyPoint(p);
    }

    private TMatrix3x4 WorldMatrixInverse()
    {
        // 直接调用你已有的 3x4 逆实现或者临时写一个
        // 略（与原类同理）
        return _worldMatrix.Inverse();
    }
    #endregion
}