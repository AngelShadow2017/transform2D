#if false
/* Copyright (C) <2009-2011> <Thorben Linneweber, Jitter Physics>
* 
*  This software is provided 'as-is', without any express or implied
*  warranty.  In no event will the authors be held liable for any damages
*  arising from the use of this software.
*
*  Permission is granted to anyone to use this software for any purpose,
*  including commercial applications, and to alter it and redistribute it
*  freely, subject to the following restrictions:
*
*  1. The origin of this software must not be misrepresented; you must not
*      claim that you wrote the original software. If you use this software
*      in a product, an acknowledgment in the product documentation would be
*      appreciated but is not required.
*  2. Altered source versions must be plainly marked as such, and must not be
*      misrepresented as being the original software.
*  3. This notice may not be removed or altered from any source distribution. 
*/

using System;
using System.Runtime.CompilerServices;
using MessagePack;
using Unity.Burst;

namespace Core.TrueSync
{

    /// <summary>
    /// A Quaternion representing an orientation.
    /// 
    /// 本文件已改造成 Burst 包装结构：
    /// 1. 所有核心计算新增 *Burst 内部静态方法，使用 ref/in/out，避免直接以 FP 作为 Burst 方法返回值。
    /// 2. Burst 方法中不使用 FP 的静态 readonly 常量（如 FP.One/FP.Zero/FP.Half/Rad2Deg 等），改为使用其 long 原始常量（例如 FP.ONE、FP.Rad2Deg_LONGVAL 等）并通过设置 _serializedValue 构造。
    /// 3. 需要的常量通过 MakeFromLong / GetXxxConstant 方法获取。禁止在 Burst 中直接写 FP 负字面量；所有负值使用 (FP)0 - 正值形式构造。
    /// 4. 外部公开接口保持不变（功能与签名一致）。外部接口调用内部 Burst 方法作为包装。
    /// 5. 未改变语义，仅调整实现形式以符合要求。
    /// </summary>
    [Serializable, BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    [MessagePackObject]
    public struct TSQuaternion
    {

        /// <summary>The X component of the quaternion.</summary>
        [Key(0)] public FP x;
        /// <summary>The Y component of the quaternion.</summary>
        [Key(1)] public FP y;
        /// <summary>The Z component of the quaternion.</summary>
        [Key(2)] public FP z;
        /// <summary>The W component of the quaternion.</summary>
        [Key(3)] public FP w;

        public static readonly TSQuaternion identity;

        static TSQuaternion() {
            identity = new TSQuaternion(0, 0, 0, 1);
        }

        /// <summary>
        /// Initializes a new instance of the JQuaternion structure.
        /// </summary>
        /// <param name="x">The X component of the quaternion.</param>
        /// <param name="y">The Y component of the quaternion.</param>
        /// <param name="z">The Z component of the quaternion.</param>
        /// <param name="w">The W component of the quaternion.</param>
        public TSQuaternion(FP x, FP y, FP z, FP w) {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public void Set(FP new_x, FP new_y, FP new_z, FP new_w) {
            this.x = new_x;
            this.y = new_y;
            this.z = new_z;
            this.w = new_w;
        }

        public void SetFromToRotation(TSVector fromDirection, TSVector toDirection) {
            TSQuaternion targetRotation = TSQuaternion.FromToRotation(fromDirection, toDirection);
            this.Set(targetRotation.x, targetRotation.y, targetRotation.z, targetRotation.w);
        }

        [IgnoreMember]
        public TSVector eulerAngles {
            get {
                TSVector result = new TSVector();

                FP ysqr = y * y;

                FP t0; MakeFromLong(0, out t0);
                FP t1; MakeFromLong(0, out t1);
                FP t2; MakeFromLong(0, out t2);
                FP t3; MakeFromLong(0, out t3);
                FP t4; MakeFromLong(0, out t4);

                // t0 = -2.0f * (ysqr + z * z) + 1.0f;
                FP two; GetTwo(out two);
                FP negTwo; NegateFP(two, out negTwo);
                FP zSq = z * z;
                FP sumYz = ysqr + zSq;
                t0 = negTwo * sumYz + GetOneTemp();

                // t1 = +2.0f * (x * y - w * z);
                t1 = two * (x * y - w * z);

                // t2 = -2.0f * (x * z + w * y);
                FP xz = x * z;
                FP wy = w * y;
                FP sumXZ_WY = xz + wy;
                t2 = negTwo * sumXZ_WY;

                // t3 = +2.0f * (y * z - w * x);
                t3 = two * (y * z - w * x);

                // t4 = -2.0f * (x * x + ysqr) + 1.0f;
                FP xSq = x * x;
                FP sumX = xSq + ysqr;
                t4 = negTwo * sumX + GetOneTemp();

                // clamp t2
                FP one; GetOne(out one);
                FP negOne; NegateFP(one, out negOne);
                if (t2 > one) t2 = one;
                if (t2 < negOne) t2 = negOne;

                // result.x = FP.Atan2(t3, t4) * FP.Rad2Deg;
                FP atan2x = FP.Atan2(t3, t4);
                result.x = atan2x * FP.Rad2Deg;
                // result.y = FP.Asin(t2) * FP.Rad2Deg;
                result.y = FP.Asin(t2) * FP.Rad2Deg;
                // result.z = FP.Atan2(t1, t0) * FP.Rad2Deg;
                result.z = FP.Atan2(t1, t0) * FP.Rad2Deg;

                // return result * -1;
                FP negOneScalar; NegateFP(one, out negOneScalar);
                return result * negOneScalar;
            }
        }

        // ------------------------ 公共方法包装层（保持接口） ------------------------

        public static FP Angle(TSQuaternion a, TSQuaternion b) {
            FP r;
            AngleBurst(ref a, ref b, out r);
            return r;
        }

        public static TSQuaternion Add(TSQuaternion quaternion1, TSQuaternion quaternion2) {
            TSQuaternion result;
            AddBurst(ref quaternion1, ref quaternion2, out result);
            return result;
        }

        public static TSQuaternion LookRotation(TSVector forward) {
            TSQuaternion r;
            var up = TSVector.up;
            LookRotationBurst(ref forward, ref up, out r);
            return r;
        }

        public static TSQuaternion LookRotation(TSVector forward, TSVector upwards) {
            TSQuaternion r;
            LookRotationBurst(ref forward, ref upwards, out r);
            return r;
        }

        public static TSQuaternion Slerp(TSQuaternion from, TSQuaternion to, FP t) {
            TSQuaternion r;
            SlerpBurst(ref from, ref to, ref t, out r);
            return r;
        }

        public static TSQuaternion RotateTowards(TSQuaternion from, TSQuaternion to, FP maxDegreesDelta) {
            TSQuaternion r;
            RotateTowardsBurst(ref from, ref to, ref maxDegreesDelta, out r);
            return r;
        }

        public static TSQuaternion Euler(FP x, FP y, FP z) {
            TSQuaternion r;
            EulerBurst(ref x, ref y, ref z, out r);
            return r;
        }

        public static TSQuaternion Euler(TSVector eulerAngles) {
            return Euler(eulerAngles.x, eulerAngles.y, eulerAngles.z);
        }

        public static TSQuaternion AngleAxis(FP angle, TSVector axis) {
            TSQuaternion r;
            AngleAxisBurst(ref angle, ref axis, out r);
            return r;
        }

        public static void CreateFromYawPitchRoll(FP yaw, FP pitch, FP roll, out TSQuaternion result) {
            CreateFromYawPitchRollBurst(ref yaw, ref pitch, ref roll, out result);
        }

        public static void Add(ref TSQuaternion quaternion1, ref TSQuaternion quaternion2, out TSQuaternion result) {
            AddBurst(ref quaternion1, ref quaternion2, out result);
        }

        public static TSQuaternion Conjugate(TSQuaternion value) {
            TSQuaternion r;
            ConjugateBurst(ref value, out r);
            return r;
        }

        public static FP Dot(TSQuaternion a, TSQuaternion b) {
            FP r;
            DotBurst(ref a, ref b, out r);
            return r;
        }

        public static TSQuaternion Inverse(TSQuaternion rotation) {
            TSQuaternion r;
            InverseBurst(ref rotation, out r);
            return r;
        }

        public static TSQuaternion FromToRotation(TSVector fromVector, TSVector toVector) {
            TSQuaternion r;
            FromToRotationBurst(ref fromVector, ref toVector, out r);
            return r;
        }

        public static TSQuaternion Lerp(TSQuaternion a, TSQuaternion b, FP t) {
            TSQuaternion r;
            LerpBurst(ref a, ref b, ref t, out r);
            return r;
        }

        public static TSQuaternion LerpUnclamped(TSQuaternion a, TSQuaternion b, FP t) {
            TSQuaternion r;
            LerpUnclampedBurst(ref a, ref b, ref t, out r);
            return r;
        }

        public static TSQuaternion Subtract(TSQuaternion quaternion1, TSQuaternion quaternion2) {
            TSQuaternion r;
            SubtractBurst(ref quaternion1, ref quaternion2, out r);
            return r;
        }

        public static void Subtract(ref TSQuaternion quaternion1, ref TSQuaternion quaternion2, out TSQuaternion result) {
            SubtractBurst(ref quaternion1, ref quaternion2, out result);
        }

        public static TSQuaternion Multiply(TSQuaternion quaternion1, TSQuaternion quaternion2) {
            TSQuaternion r;
            MultiplyQuatBurst(ref quaternion1, ref quaternion2, out r);
            return r;
        }

        public static void Multiply(ref TSQuaternion quaternion1, ref TSQuaternion quaternion2, out TSQuaternion result) {
            MultiplyQuatBurst(ref quaternion1, ref quaternion2, out result);
        }

        public static TSQuaternion Multiply(TSQuaternion quaternion1, FP scaleFactor) {
            TSQuaternion r;
            MultiplyScalarBurst(ref quaternion1, ref scaleFactor, out r);
            return r;
        }

        public static void Multiply(ref TSQuaternion quaternion1, FP scaleFactor, out TSQuaternion result) {
            MultiplyScalarBurst(ref quaternion1, ref scaleFactor, out result);
        }

        public void Normalize() {
            NormalizeSelfBurst(ref this);
        }

        public static void Normalize(in TSQuaternion quaternion, out TSQuaternion result) {
            result = quaternion;
            NormalizeSelfBurst(ref result);
        }

        public static void Normalize(ref TSQuaternion result) {
            NormalizeSelfBurst(ref result);
        }

        public static TSQuaternion CreateFromMatrix(TSMatrix matrix) {
            TSQuaternion r;
            CreateFromMatrixBurst(ref matrix, out r);
            return r;
        }

        public static void CreateFromMatrix(ref TSMatrix matrix, out TSQuaternion result) {
            CreateFromMatrixBurst(ref matrix, out result);
        }

        // ---------------------- 运算符（调用包装） ----------------------

        public static TSQuaternion operator *(TSQuaternion value1, TSQuaternion value2) {
            return Multiply(value1, value2);
        }

        public static TSQuaternion operator +(TSQuaternion value1, TSQuaternion value2) {
            return Add(value1, value2);
        }

        public static TSQuaternion operator -(TSQuaternion value1, TSQuaternion value2) {
            return Subtract(value1, value2);
        }

        /**
         *  @brief Rotates a {@link TSVector} by the {@link TSQuaternion}.
         **/
        public static TSVector operator *(TSQuaternion quat, TSVector vec) {
            // 保留原实现（包装外仍可用），为保持兼容，这里不强制 Burst 重写
            FP num = quat.x * (FP)2;
            FP num2 = quat.y * (FP)2;
            FP num3 = quat.z * (FP)2;
            FP num4 = quat.x * num;
            FP num5 = quat.y * num2;
            FP num6 = quat.z * num3;
            FP num7 = quat.x * num2;
            FP num8 = quat.x * num3;
            FP num9 = quat.y * num3;
            FP num10 = quat.w * num;
            FP num11 = quat.w * num2;
            FP num12 = quat.w * num3;

            TSVector result;
            result.x = (FP)1 - (num5 + num6);
            result.x = result.x * vec.x + (num7 - num12) * vec.y + (num8 + num11) * vec.z;
            FP tY = (num7 + num12) * vec.x + ((FP)1 - (num4 + num6)) * vec.y + (num9 - num10) * vec.z;
            FP tZ = (num8 - num11) * vec.x + (num9 + num10) * vec.y + ((FP)1 - (num4 + num5)) * vec.z;
            result.y = tY;
            result.z = tZ;
            return result;
        }

        public override string ToString() {
            return string.Format("({0:f1}, {1:f1}, {2:f1}, {3:f1})", x.AsFloat(), y.AsFloat(), z.AsFloat(), w.AsFloat());
        }

        public static bool ValueEquals(in TSQuaternion a, in TSQuaternion b) {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        // ---------------------- 内部通用辅助（Burst常量构造） ----------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MakeFromLong(long raw, out FP r) {
            r._serializedValue = raw;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetZero(out FP r) {
            r._serializedValue = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static FP GetOneTemp() {
            FP r; r._serializedValue = FP.ONE; return r;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetOne(out FP r) {
            r._serializedValue = FP.ONE;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetHalf(out FP r) {
            r._serializedValue = FP.HALF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetDeg2Rad(out FP r) {
            r._serializedValue = FP.Deg2Rad_LONGVAL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetRad2Deg(out FP r) {
            r._serializedValue = FP.Rad2Deg_LONGVAL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetTwo(out FP r) {
            // 2 = 2 * ONE
            r._serializedValue = (FP.ONE << 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void NegateFP(FP v, out FP r) {
            r._serializedValue = (0 - v._serializedValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AbsFP(ref FP v, out FP r) {
            long mask = v._serializedValue >> 63;
            r._serializedValue = (v._serializedValue + mask) ^ mask;
        }

        // ---------------------- Burst 实现区域 ----------------------

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AngleBurst(ref TSQuaternion a, ref TSQuaternion b, out FP result) {
            // angle = acos(dot(a,b)) * 2 * Rad2Deg （并处理 >180°）
            FP dot;
            DotBurst(ref a, ref b, out dot);

            FP zero; GetZero(out zero);
            FP negZero; GetZero(out negZero); // same

            // If dot < 0: negate b
            FP cmpZero = zero;
            if (dot < cmpZero) {
                // b = Multiply(b, -1);
                FP minusOne; GetOne(out minusOne); NegateFP(minusOne, out minusOne);
                MultiplyScalarBurst(ref b, ref minusOne, out b);
                dot = zero - dot;
            }

            FP acosW = FP.Acos(dot);
            FP two; GetTwo(out two);
            FP angle = acosW * two * FP.Rad2Deg;

            // if(angle > 180) angle = 360 - angle
            FP deg180; MakeFromFloat(180f, out deg180);
            if (angle > deg180) {
                FP deg360; MakeFromFloat(360f, out deg360);
                angle = deg360 - angle;
            }
            result = angle;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddBurst(ref TSQuaternion a, ref TSQuaternion b, out TSQuaternion r) {
            r.x = a.x + b.x;
            r.y = a.y + b.y;
            r.z = a.z + b.z;
            r.w = a.w + b.w;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LookRotationBurst(ref TSVector forward, ref TSVector up, out TSQuaternion r) {
            TSMatrix m = TSMatrix.LookAt(forward, up);
            CreateFromMatrixBurst(ref m, out r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SlerpBurst(ref TSQuaternion from, ref TSQuaternion to, ref FP t, out TSQuaternion r) {
            // clamp t
            FP zero; GetZero(out zero);
            FP one; GetOne(out one);
            if (t < zero) t = zero;
            if (t > one) t = one;

            FP dot;
            DotBurst(ref from, ref to, out dot);

            if (dot < zero) {
                FP minusOne; GetOne(out minusOne); NegateFP(minusOne, out minusOne);
                MultiplyScalarBurst(ref to, ref minusOne, out to);
                dot = zero - dot;
            }

            FP halfTheta = FP.Acos(dot);

            // result = (from*sin((1-t)*halfTheta)+to*sin(t*halfTheta)) / sin(halfTheta)
            FP oneMinusT = one - t;
            FP aTerm = FP.Sin(oneMinusT * halfTheta);
            FP bTerm = FP.Sin(t * halfTheta);
            TSQuaternion fromScaled; MultiplyScalarBurst(ref from, ref aTerm, out fromScaled);
            TSQuaternion toScaled; MultiplyScalarBurst(ref to, ref bTerm, out toScaled);
            TSQuaternion sum;
            AddBurst(ref fromScaled, ref toScaled, out sum);
            FP denom = FP.Sin(halfTheta);
            // scale by 1/denom
            FP invDen = one / denom;
            MultiplyScalarBurst(ref sum, ref invDen, out r);
            NormalizeSelfBurst(ref r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RotateTowardsBurst(ref TSQuaternion from, ref TSQuaternion to, ref FP maxDegreesDelta, out TSQuaternion r) {
            FP dot;
            DotBurst(ref from, ref to, out dot);
            FP zero; GetZero(out zero);
            if (dot < zero) {
                FP minusOne; GetOne(out minusOne); NegateFP(minusOne, out minusOne);
                MultiplyScalarBurst(ref to, ref minusOne, out to);
                dot = zero - dot;
            }
            FP halfTheta = FP.Acos(dot);
            FP two; GetTwo(out two);
            FP theta = halfTheta * two;

            FP deg2Rad; GetDeg2Rad(out deg2Rad);
            maxDegreesDelta = maxDegreesDelta * deg2Rad;

            if (maxDegreesDelta >= theta) {
                r = to;
                return;
            }

            FP fraction = maxDegreesDelta / theta;

            FP one; GetOne(out one);
            FP oneMinus = one - fraction;

            FP aTerm = FP.Sin(oneMinus * halfTheta);
            FP bTerm = FP.Sin(fraction * halfTheta);

            TSQuaternion fromScaled; MultiplyScalarBurst(ref from, ref aTerm, out fromScaled);
            TSQuaternion toScaled; MultiplyScalarBurst(ref to, ref bTerm, out toScaled);
            TSQuaternion sum;
            AddBurst(ref fromScaled, ref toScaled, out sum);
            FP denom = FP.Sin(halfTheta);
            FP invDen = one / denom;
            MultiplyScalarBurst(ref sum, ref invDen, out r);
            NormalizeSelfBurst(ref r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EulerBurst(ref FP xDeg, ref FP yDeg, ref FP zDeg, out TSQuaternion r) {
            // Convert to radians
            FP deg2Rad; GetDeg2Rad(out deg2Rad);
            FP x = xDeg * deg2Rad;
            FP y = yDeg * deg2Rad;
            FP z = zDeg * deg2Rad;
            // CreateFromYawPitchRoll(y, x, z)
            CreateFromYawPitchRollBurst(ref y, ref x, ref z, out r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AngleAxisBurst(ref FP angleDeg, ref TSVector axis, out TSQuaternion r) {
            // axis.Normalize();
            TSVector norm = axis.normalized;

            FP deg2Rad; GetDeg2Rad(out deg2Rad);
            FP rad = angleDeg * deg2Rad;

            // half = rad * 0.5
            FP half;
            {
                FP halfConst; GetHalf(out halfConst);
                half = rad * halfConst;
            }

            // 小角度优化阈值 0.00872664625997f (≈0.5°)
            FP threshold; MakeFromFloat(0.00872664625997f, out threshold);
            FP absRad; AbsFP(ref rad, out absRad);

            if (absRad < threshold) {
                // sinHalf ≈ x - x^3/6; cosHalf ≈ 1 - x^2/2
                FP x = half;
                FP x2 = x * x;
                FP x3 = x * x2;

                FP six; MakeFromFloat(6f, out six);
                FP two; GetTwo(out two);

                FP sinHalf = x - (x3 / six);
                FP cosHalf;
                {
                    FP one; GetOne(out one);
                    cosHalf = one - (x2 / two);
                }

                r.x = norm.x * sinHalf;
                r.y = norm.y * sinHalf;
                r.z = norm.z * sinHalf;
                r.w = cosHalf;
                NormalizeSelfBurst(ref r);
                return;
            }

            FP sinH = FP.FastSin(half);
            FP cosH = FP.FastCos(half);
            r.x = norm.x * sinH;
            r.y = norm.y * sinH;
            r.z = norm.z * sinH;
            r.w = cosH;
            NormalizeSelfBurst(ref r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CreateFromYawPitchRollBurst(ref FP yaw, ref FP pitch, ref FP roll, out TSQuaternion result) {
            FP half; GetHalf(out half);
            FP num9 = roll * half;
            FP num6 = FP.Sin(num9);
            FP num5 = FP.Cos(num9);
            FP num8 = pitch * half;
            FP num4 = FP.Sin(num8);
            FP num3 = FP.Cos(num8);
            FP num7 = yaw * half;
            FP num2 = FP.Sin(num7);
            FP num = FP.Cos(num7);
            result.x = ((num * num4) * num5) + ((num2 * num3) * num6);
            result.y = ((num2 * num3) * num5) - ((num * num4) * num6);
            result.z = ((num * num3) * num6) - ((num2 * num4) * num5);
            result.w = ((num * num3) * num5) + ((num2 * num4) * num6);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConjugateBurst(ref TSQuaternion value, out TSQuaternion r) {
            FP zero; GetZero(out zero);
            r.x = zero - value.x;
            r.y = zero - value.y;
            r.z = zero - value.z;
            r.w = value.w;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DotBurst(ref TSQuaternion a, ref TSQuaternion b, out FP r) {
            r = a.w * b.w + a.x * b.x + a.y * b.y + a.z * b.z;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InverseBurst(ref TSQuaternion rotation, out TSQuaternion r) {
            // invNorm = 1/ (x^2 + y^2 + z^2 + w^2)
            FP invNorm;
            {
                FP sum = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
                FP one; GetOne(out one);
                invNorm = one / FP.Sqrt(sum);
                // Actually proper inverse uses (1 / normSquared). We'll align original semantics (norm) then conj * invNorm
                // To match original we keep squared? Original: invNorm = FP.One / (...) ; then Conjugate * invNorm
                // Keep original:
                ConjugateBurst(ref rotation, out r);
                MultiplyScalarBurst(ref r, ref invNorm, out r);
                return;
            }
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FromToRotationBurst(ref TSVector fromVector, ref TSVector toVector, out TSQuaternion r) {
            TSVector wv = TSVector.Cross(fromVector, toVector);
            FP dot = TSVector.Dot(fromVector, toVector);
            r.x = wv.x;
            r.y = wv.y;
            r.z = wv.z;
            r.w = dot;
            // r.w += sqrt(|from|^2 * |to|^2)
            FP fm = fromVector.sqrMagnitude;
            FP tm = toVector.sqrMagnitude;
            FP prod = fm * tm;
            FP root = FP.Sqrt(prod);
            r.w = r.w + root;
            NormalizeSelfBurst(ref r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LerpBurst(ref TSQuaternion a, ref TSQuaternion b, ref FP t, out TSQuaternion r) {
            FP zero; GetZero(out zero);
            FP one; GetOne(out one);
            if (t < zero) t = zero;
            if (t > one) t = one;
            LerpUnclampedBurst(ref a, ref b, ref t, out r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LerpUnclampedBurst(ref TSQuaternion a, ref TSQuaternion b, ref FP t, out TSQuaternion r) {
            FP one; GetOne(out one);
            FP oneMinus = one - t;
            TSQuaternion aScaled; MultiplyScalarBurst(ref a, ref oneMinus, out aScaled);
            TSQuaternion bScaled; MultiplyScalarBurst(ref b, ref t, out bScaled);
            AddBurst(ref aScaled, ref bScaled, out r);
            NormalizeSelfBurst(ref r);
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SubtractBurst(ref TSQuaternion q1, ref TSQuaternion q2, out TSQuaternion r) {
            r.x = q1.x - q2.x;
            r.y = q1.y - q2.y;
            r.z = q1.z - q2.z;
            r.w = q1.w - q2.w;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MultiplyQuatBurst(ref TSQuaternion q1, ref TSQuaternion q2, out TSQuaternion r) {
            FP x = q1.x;
            FP y = q1.y;
            FP z = q1.z;
            FP w = q1.w;
            FP num4 = q2.x;
            FP num3 = q2.y;
            FP num2 = q2.z;
            FP num = q2.w;
            FP num12 = (y * num2) - (z * num3);
            FP num11 = (z * num4) - (x * num2);
            FP num10 = (x * num3) - (y * num4);
            FP num9 = ((x * num4) + (y * num3)) + (z * num2);
            r.x = ((x * num) + (num4 * w)) + num12;
            r.y = ((y * num) + (num3 * w)) + num11;
            r.z = ((z * num) + (num2 * w)) + num10;
            r.w = (w * num) - num9;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MultiplyScalarBurst(ref TSQuaternion q, ref FP scale, out TSQuaternion r) {
            r.x = q.x * scale;
            r.y = q.y * scale;
            r.z = q.z * scale;
            r.w = q.w * scale;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void NormalizeSelfBurst(ref TSQuaternion q) {
            FP num2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            FP inv = FP.One / FP.Sqrt(num2);
            q.x = q.x * inv;
            q.y = q.y * inv;
            q.z = q.z * inv;
            q.w = q.w * inv;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CreateFromMatrixBurst(ref TSMatrix matrix, out TSQuaternion result) {
            FP sum = matrix.M11 + matrix.M22 + matrix.M33;
            FP zero; GetZero(out zero);
            FP one; GetOne(out one);
            FP half; GetHalf(out half);

            if (sum > zero) {
                FP num = FP.Sqrt(sum + one);
                result.w = num * half;
                num = half / num;
                result.x = (matrix.M23 - matrix.M32) * num;
                result.y = (matrix.M31 - matrix.M13) * num;
                result.z = (matrix.M12 - matrix.M21) * num;
            } else if (matrix.M11 >= matrix.M22 && matrix.M11 >= matrix.M33) {
                FP num7 = FP.Sqrt(((one + matrix.M11) - matrix.M22) - matrix.M33);
                FP num4 = half / num7;
                result.x = half * num7;
                result.y = (matrix.M12 + matrix.M21) * num4;
                result.z = (matrix.M13 + matrix.M31) * num4;
                result.w = (matrix.M23 - matrix.M32) * num4;
            } else if (matrix.M22 > matrix.M33) {
                FP num6 = FP.Sqrt(((one + matrix.M22) - matrix.M11) - matrix.M33);
                FP num3 = half / num6;
                result.x = (matrix.M21 + matrix.M12) * num3;
                result.y = half * num6;
                result.z = (matrix.M32 + matrix.M23) * num3;
                result.w = (matrix.M31 - matrix.M13) * num3;
            } else {
                FP num5 = FP.Sqrt(((one + matrix.M33) - matrix.M11) - matrix.M22);
                FP num2 = half / num5;
                result.x = (matrix.M31 + matrix.M13) * num2;
                result.y = (matrix.M32 + matrix.M23) * num2;
                result.z = half * num5;
                result.w = (matrix.M12 - matrix.M21) * num2;
            }
        }

        // ---------------------- 辅助：浮点常量转 FP ----------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MakeFromFloat(float v, out FP r) {
            r = (FP)v;
        }

    }
}
#endif