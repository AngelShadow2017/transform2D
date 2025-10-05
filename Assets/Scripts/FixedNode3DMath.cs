using System.Runtime.CompilerServices;
using Unity.Burst;
using Core.TrueSync;

/*
 * FixedNode3DMath
 * - 仅放“纯数学 + 定点”操作，全部静态方法。
 * - 受 BurstCompile 约束：不返回 FP，所有 FP 结果通过 out/ref。
 * - 不使用 FP 的静态 readonly 常量；用 (FP)0 / (FP)1 / (FP)2 ...
 * - 负数用 (FP)0 - xxx
 * - Quaternion / Vector / Matrix 的写入全通过 ref/out。
 * - 外层 FixedNode3D 调用时只负责脏标记与层级管理。
 */

namespace FixedNode3DInternal
{
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    public static class FixedNode3DMath
    {
        // 极小阈值 (对应 EN6)
        // 直接写 (FP)0 + raw 也可以，这里使用 (FP)0 + (FP)0.000001 方式避免静态 readonly。
        // 但为了稳定，用内部方法最小值写死：EN6_LONGVAL = 4295 (参见 FP.EN6 定义)。
        private const long EN6_LONGVAL = 4295;
        [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EpsilonSmallFunc(out FP val)
        {
            val._serializedValue = EN6_LONGVAL; // 约 1e-6
        }

        #region 基础：列长度

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ColumnLength(in FP x, in FP y, in FP z, out FP len)
        {
            // len = sqrt(x^2 + y^2 + z^2)
            FP xx = x * x;
            FP yy = y * y;
            FP zz = z * z;
            FP sum = xx + yy + zz;
            len = FP.Sqrt(sum);
        }

        #endregion

        #region 组合：Quaternion + Scale => 3x3 (线性部分)

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ComposeRotationScale(
            in TSQuaternion q,
            in TSVector s,
            out FP m00, out FP m01, out FP m02,
            out FP m10, out FP m11, out FP m12,
            out FP m20, out FP m21, out FP m22)
        {
            // 与原实现一致，只是保持 Burst 约定
            FP two = (FP)2;

            FP xx = q.x * q.x;
            FP yy = q.y * q.y;
            FP zz = q.z * q.z;
            FP xy = q.x * q.y;
            FP xz = q.x * q.z;
            FP yz = q.y * q.z;
            FP wx = q.w * q.x;
            FP wy = q.w * q.y;
            FP wz = q.w * q.z;

            FP r00 = (FP)1 - two * (yy + zz);
            FP r01 = two * (xy - wz);
            FP r02 = two * (xz + wy);

            FP r10 = two * (xy + wz);
            FP r11 = (FP)1 - two * (xx + zz);
            FP r12 = two * (yz - wx);

            FP r20 = two * (xz - wy);
            FP r21 = two * (yz + wx);
            FP r22 = (FP)1 - two * (xx + yy);

            m00 = r00 * s.x; m01 = r01 * s.y; m02 = r02 * s.z;
            m10 = r10 * s.x; m11 = r11 * s.y; m12 = r12 * s.z;
            m20 = r20 * s.x; m21 = r21 * s.y; m22 = r22 * s.z;
        }

        #endregion

        #region 从 3x3(含缩放) 提取 纯旋 + Scale

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ExtractScale(
            in TMatrix3x4 m,
            out FP sx, out FP sy, out FP sz)
        {
            EpsilonSmallFunc(out FP EpsilonSmall);
            ColumnLength(m.m00, m.m10, m.m20, out sx);
            if (sx == (FP)0) sx = EpsilonSmall;
            ColumnLength(m.m01, m.m11, m.m21, out sy);
            if (sy == (FP)0) sy = EpsilonSmall;
            ColumnLength(m.m02, m.m12, m.m22, out sz);
            if (sz == (FP)0) sz = EpsilonSmall;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ExtractRotationColumnsNormalized(
            in TMatrix3x4 m,
            in FP sx, in FP sy, in FP sz,
            out FP r00, out FP r01, out FP r02,
            out FP r10, out FP r11, out FP r12,
            out FP r20, out FP r21, out FP r22)
        {
            FP sxinv = (FP)1 / sx;
            FP syinv = (FP)1 / sy;
            FP szinv = (FP)1 / sz;
            r00 = m.m00 * sxinv; r01 = m.m01 * syinv; r02 = m.m02 * szinv;
            r10 = m.m10 * sxinv; r11 = m.m11 * syinv; r12 = m.m12 * szinv;
            r20 = m.m20 * sxinv; r21 = m.m21 * syinv; r22 = m.m22 * szinv;
        }

        #endregion

        #region RotationMatrix -> Quaternion

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RotationMatrixToQuaternion(
            in FP r00, in FP r01, in FP r02,
            in FP r10, in FP r11, in FP r12,
            in FP r20, in FP r21, in FP r22,
            out TSQuaternion q)
        {
            FP trace = r00 + r11 + r22;
            if (trace > (FP)0)
            {
                FP s = FP.Sqrt(trace + (FP)1) * (FP)2;
                FP invS = (FP)1 / s;
                q.x = (r21 - r12) * invS;
                q.y = (r02 - r20) * invS;
                q.z = (r10 - r01) * invS;
                q.w = s * (FP)0.25;
            }
            else if (r00 > r11 && r00 > r22)
            {
                FP s = FP.Sqrt(((FP)1 + r00 - r11 - r22)) * (FP)2;
                FP invS = (FP)1 / s;
                q.x = s * (FP)0.25;
                q.y = (r01 + r10) * invS;
                q.z = (r02 + r20) * invS;
                q.w = (r21 - r12) * invS;
            }
            else if (r11 > r22)
            {
                FP s = FP.Sqrt(((FP)1 + r11 - r00 - r22)) * (FP)2;
                FP invS = (FP)1 / s;
                q.x = (r01 + r10) * invS;
                q.y = s * (FP)0.25;
                q.z = (r12 + r21) * invS;
                q.w = (r02 - r20) * invS;
            }
            else
            {
                FP s = FP.Sqrt(((FP)1 + r22 - r00 - r11)) * (FP)2;
                FP invS = (FP)1 / s;
                q.x = (r02 + r20) * invS;
                q.y = (r12 + r21) * invS;
                q.z = s * (FP)0.25;
                q.w = (r10 - r01) * invS;
            }
            NormalizeQuaternion(ref q);
        }

        #endregion

        #region Quaternion Normalize

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NormalizeQuaternion(ref TSQuaternion q)
        {
            FP mag = (q.x * q.x) + (q.y * q.y) + (q.z * q.z) + (q.w * q.w);
            FP inv = (FP)1 / FP.Sqrt(mag);
            q.x = q.x * inv;
            q.y = q.y * inv;
            q.z = q.z * inv;
            q.w = q.w * inv;
        }

        #endregion

        #region Euler <-> Quaternion (支持 YXZ / XYZ / UnityZXY)

        // 注意：AngleAxis 里原本把角度乘 FP.Deg2Rad；这里直接使用弧度输入，不再转度
        // 传入的 eulerRad 即为弧度向量
        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EulerToQuaternion_YXZ(in TSVector eulerRad, out TSQuaternion q)
        {
            // Y * X * Z intrinsic
            QuaternionFromAxisAngle((FP)0, (FP)1, (FP)0, eulerRad.y, out TSQuaternion qy);
            QuaternionFromAxisAngle((FP)1, (FP)0, (FP)0, eulerRad.x, out TSQuaternion qx);
            QuaternionFromAxisAngle((FP)0, (FP)0, (FP)1, eulerRad.z, out TSQuaternion qz);
            MultiplyQuaternion(qy, qx, out TSQuaternion t);
            MultiplyQuaternion(t, qz, out q);
            NormalizeQuaternion(ref q);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EulerToQuaternion_XYZ(in TSVector eulerRad, out TSQuaternion q)
        {
            QuaternionFromAxisAngle((FP)1, (FP)0, (FP)0, eulerRad.x, out TSQuaternion qx);
            QuaternionFromAxisAngle((FP)0, (FP)1, (FP)0, eulerRad.y, out TSQuaternion qy);
            QuaternionFromAxisAngle((FP)0, (FP)0, (FP)1, eulerRad.z, out TSQuaternion qz);
            MultiplyQuaternion(qx, qy, out TSQuaternion t);
            MultiplyQuaternion(t, qz, out q);
            NormalizeQuaternion(ref q);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EulerToQuaternion_UnityZXY(in TSVector eulerRad, out TSQuaternion q)
        {
            // Z * X * Y
            QuaternionFromAxisAngle((FP)0, (FP)0, (FP)1, eulerRad.z, out TSQuaternion qz);
            QuaternionFromAxisAngle((FP)1, (FP)0, (FP)0, eulerRad.x, out TSQuaternion qx);
            QuaternionFromAxisAngle((FP)0, (FP)1, (FP)0, eulerRad.y, out TSQuaternion qy);
            MultiplyQuaternion(qz, qx, out TSQuaternion t);
            MultiplyQuaternion(t, qy, out q);
            NormalizeQuaternion(ref q);
        }

        // 旋转轴必须是单位轴；这里给的是 X/Y/Z 标准轴
        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QuaternionFromAxisAngle(in FP ax, in FP ay, in FP az, in FP angleRad, out TSQuaternion q)
        {
            FP half = angleRad * (FP)0.5;
            FP s = FP.FastSin(half);
            FP c = FP.FastCos(half);
            q.x = ax * s;
            q.y = ay * s;
            q.z = az * s;
            q.w = c;
        }

        // 与 TSQuaternion.Multiply 一致
        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MultiplyQuaternion(in TSQuaternion a, in TSQuaternion b, out TSQuaternion r)
        {
            FP ax = a.x; FP ay = a.y; FP az = a.z; FP aw = a.w;
            FP bx = b.x; FP by = b.y; FP bz = b.z; FP bw = b.w;

            FP num12 = (ay * bz) - (az * by);
            FP num11 = (az * bx) - (ax * bz);
            FP num10 = (ax * by) - (ay * bx);
            FP num9 = ((ax * bx) + (ay * by)) + (az * bz);

            r.x = ((ax * bw) + (bx * aw)) + num12;
            r.y = ((ay * bw) + (by * aw)) + num11;
            r.z = ((az * bw) + (bz * aw)) + num10;
            r.w = (aw * bw) - num9;
        }

        #endregion

        #region Quaternion -> Euler (三个顺序)

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QuaternionToEuler_YXZ(in TSQuaternion q, out TSVector euler)
        {
            // 构造 3x3
            FP two = (FP)2;
            FP xx = q.x * q.x;
            FP yy = q.y * q.y;
            FP zz = q.z * q.z;
            FP xy = q.x * q.y;
            FP xz = q.x * q.z;
            FP yz = q.y * q.z;
            FP wx = q.w * q.x;
            FP wy = q.w * q.y;
            FP wz = q.w * q.z;

            FP r00 = (FP)1 - two * (yy + zz);
            FP r01 = two * (xy - wz);
            FP r02 = two * (xz + wy);
            FP r10 = two * (xy + wz);
            FP r11 = (FP)1 - two * (xx + zz);
            FP r12 = two * (yz - wx);
            FP r20 = two * (xz - wy);
            FP r21 = two * (yz + wx);
            FP r22 = (FP)1 - two * (xx + yy);

            // clamp r21
            FP clamp = r21;
            if (clamp > (FP)1) clamp = (FP)1;
            if (clamp < (FP)0 - (FP)1) clamp = (FP)0 - (FP)1;

            FP x = (FP)0 - FP.Asin(clamp);
            FP y = FP.Atan2(r20, r22);
            FP z = FP.Atan2(r01, r11);
            euler.x = x; euler.y = y; euler.z = z;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QuaternionToEuler_XYZ(in TSQuaternion q, out TSVector euler)
        {
            FP two = (FP)2;
            FP xx = q.x * q.x;
            FP yy = q.y * q.y;
            FP zz = q.z * q.z;
            FP xy = q.x * q.y;
            FP xz = q.x * q.z;
            FP yz = q.y * q.z;
            FP wx = q.w * q.x;
            FP wy = q.w * q.y;
            FP wz = q.w * q.z;

            FP r00 = (FP)1 - two * (yy + zz);
            FP r01 = two * (xy - wz);
            FP r02 = two * (xz + wy);
            FP r12 = two * (yz - wx);
            FP r22 = (FP)1 - two * (xx + yy);

            FP syVal = r02;
            if (syVal > (FP)1) syVal = (FP)1;
            if (syVal < (FP)0 - (FP)1) syVal = (FP)0 - (FP)1;

            FP y = FP.Asin(syVal);
            FP x = FP.Atan2((FP)0 - r12, r22);
            FP z = FP.Atan2((FP)0 - r01, r00);
            euler.x = x; euler.y = y; euler.z = z;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QuaternionToEuler_UnityZXY(in TSQuaternion q, out TSVector euler)
        {
            EpsilonSmallFunc(out FP EpsilonSmall);
            FP two = (FP)2;
            FP xx = q.x * q.x;
            FP yy = q.y * q.y;
            FP zz = q.z * q.z;
            FP xy = q.x * q.y;
            FP xz = q.x * q.z;
            FP yz = q.y * q.z;
            FP wx = q.w * q.x;
            FP wy = q.w * q.y;
            FP wz = q.w * q.z;

            FP r00 = (FP)1 - two * (yy + zz);
            FP r01 = two * (xy - wz);
            FP r10 = two * (xy + wz);
            FP r11 = (FP)1 - two * (xx + zz);
            FP r12 = two * (yz - wx);
            FP r20 = two * (xz - wy);
            FP r21 = two * (yz + wx);
            FP r22 = (FP)1 - two * (xx + yy);

            // ZXY
            FP sx = r21;
            if (sx > (FP)1) sx = (FP)1;
            if (sx < (FP)0 - (FP)1) sx = (FP)0 - (FP)1;

            FP x = FP.Asin(sx);
            FP cx = FP.Cos(x);
            FP eps = EpsilonSmall;
            FP y, z;
            if (FP.Abs(cx) > eps)
            {
                y = FP.Atan2((FP)0 - r20, r22);
                z = FP.Atan2(r01, r11);
            }
            else
            {
                z = (FP)0;
                y = FP.Atan2(r10, r00);
            }
            euler.x = x; euler.y = y; euler.z = z;
        }

        #endregion

        #region Shear 检测 + 保留

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DetectShear(
            in TMatrix3x4 m,
            out bool hasShear)
        {
            EpsilonSmallFunc(out FP EpsilonSmall);
            // 这里调用者会先判断需不需要保留 shear，如果不需要可直接返回 false。
            // 取列向量
            ColumnLength(m.m00, m.m10, m.m20, out FP l0); if (l0 == (FP)0) l0 = EpsilonSmall;
            ColumnLength(m.m01, m.m11, m.m21, out FP l1); if (l1 == (FP)0) l1 = EpsilonSmall;
            ColumnLength(m.m02, m.m12, m.m22, out FP l2); if (l2 == (FP)0) l2 = EpsilonSmall;
            FP l0inv = (FP)1 / l0;
            FP l1inv = (FP)1 / l1;
            FP l2inv = (FP)1 / l2;
            FP n00 = m.m00 * l0inv; FP n10 = m.m10 * l0inv; FP n20 = m.m20 * l0inv;
            FP n01 = m.m01 * l1inv; FP n11 = m.m11 * l1inv; FP n21 = m.m21 * l1inv;
            FP n02 = m.m02 * l2inv; FP n12 = m.m12 * l2inv; FP n22 = m.m22 * l2inv;

            FP d01 = n00 * n01 + n10 * n11 + n20 * n21;
            FP d02 = n00 * n02 + n10 * n12 + n20 * n22;
            FP d12 = n01 * n02 + n11 * n12 + n21 * n22;

            FP eps = EpsilonSmall;
            hasShear = (FP.Abs(d01) > eps) || (FP.Abs(d02) > eps) || (FP.Abs(d12) > eps);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyNewRotScaleWithOptionalShear(
            in TMatrix3x4 oldLocal,
            in TSQuaternion newQuat,
            in TSVector newScale,
            in TSVector pos,
            out TMatrix3x4 outLocal)
        {
            EpsilonSmallFunc(out FP EpsilonSmall);
            // 1. 旧 scale
            ColumnLength(oldLocal.m00, oldLocal.m10, oldLocal.m20, out FP sx_old); if (sx_old == (FP)0) sx_old = EpsilonSmall;
            ColumnLength(oldLocal.m01, oldLocal.m11, oldLocal.m21, out FP sy_old); if (sy_old == (FP)0) sy_old = EpsilonSmall;
            ColumnLength(oldLocal.m02, oldLocal.m12, oldLocal.m22, out FP sz_old); if (sz_old == (FP)0) sz_old = EpsilonSmall;
            FP sx_oldinv = (FP)1 / sx_old;
            FP sy_oldinv = (FP)1 / sy_old;
            FP sz_oldinv = (FP)1 / sz_old;
            // 2. 归一列得到旧的近似旋转
            FP ir00 = oldLocal.m00 * sx_oldinv; FP ir01 = oldLocal.m01 * sy_oldinv; FP ir02 = oldLocal.m02 * sz_oldinv;
            FP ir10 = oldLocal.m10 * sx_oldinv; FP ir11 = oldLocal.m11 * sy_oldinv; FP ir12 = oldLocal.m12 * sz_oldinv;
            FP ir20 = oldLocal.m20 * sx_oldinv; FP ir21 = oldLocal.m21 * sy_oldinv; FP ir22 = oldLocal.m22 * sz_oldinv;

            // 3. 新的纯 R*S
            ComposeRotationScale(
                newQuat, newScale,
                out FP nr00, out FP nr01, out FP nr02,
                out FP nr10, out FP nr11, out FP nr12,
                out FP nr20, out FP nr21, out FP nr22);

            // 4. 投影系数（旧 shear）
            FP proj01 = ir00 * ir01 + ir10 * ir11 + ir20 * ir21;
            FP proj02 = ir00 * ir02 + ir10 * ir12 + ir20 * ir22;
            FP proj12 = ir01 * ir02 + ir11 * ir12 + ir21 * ir22;

            FP Sx = newScale.x;
            FP Sy = newScale.y;
            FP Sz = newScale.z;

            FP c0x = nr00 * Sx; FP c0y = nr10 * Sx; FP c0z = nr20 * Sx;
            FP c1x = nr01 * Sy + proj01 * c0x;
            FP c1y = nr11 * Sy + proj01 * c0y;
            FP c1z = nr21 * Sy + proj01 * c0z;

            FP c2x = nr02 * Sz + proj02 * c0x + proj12 * (nr01 * Sy);
            FP c2y = nr12 * Sz + proj02 * c0y + proj12 * (nr11 * Sy);
            FP c2z = nr22 * Sz + proj02 * c0z + proj12 * (nr21 * Sy);

            outLocal = TMatrix3x4.FromLinearTranslation(
                c0x, c1x, c2x, pos.x,
                c0y, c1y, c2y, pos.y,
                c0z, c1z, c2z, pos.z
            );
        }

        #endregion

        #region 组合 TRS (纯 R*S + T)

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ComposeMatrix(
            in TSQuaternion q,
            in TSVector s,
            in TSVector pos,
            out TMatrix3x4 m)
        {
            ComposeRotationScale(q, s,
                out FP m00, out FP m01, out FP m02,
                out FP m10, out FP m11, out FP m12,
                out FP m20, out FP m21, out FP m22);

            m = TMatrix3x4.FromLinearTranslation(
                m00, m01, m02, pos.x,
                m10, m11, m12, pos.y,
                m20, m21, m22, pos.z
            );
        }

        #endregion
    }
}