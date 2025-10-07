using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Core.TrueSync
{
    [BurstCompile]
    public partial struct MathBurstedFix
    {
        private const bool disable = false;//要生成一些静态预计算的东西的时候,记得在这里调成true
        public const long MAX_VALUE = long.MaxValue;
        public const long MIN_VALUE = long.MinValue;
        public const long MaxValue = long.MaxValue-1;
        public const long MinValue = long.MinValue + 2;
        public const int NUM_BITS = 64;
        public const int FRACTIONAL_PLACES = 32;
        public const long ONE = 1L << FRACTIONAL_PLACES;
        public const long TEN = 10L << FRACTIONAL_PLACES;
        public const long HALF = 1L << (FRACTIONAL_PLACES - 1);
        public const long PI_TIMES_2 = 0x6487ED511;
        public const long PI = 0x3243F6A88;
        public const long PI_OVER_2 = 0x1921FB544;
        public const long LN2 = 0xB17217F7;
        public const long LOG2MAX = 0x1F00000000;
        public const long LOG2MIN = -0x2000000000;
        public const int LUT_SIZE = (int)(PI_OVER_2 >> 15);
        [BurstCompile(DisableDirectCall = disable,OptimizeFor = OptimizeFor.Performance)]
        public static int CountLeadingZeroes(ulong x)
        {
            int result = 0;
            while ((x & 0xF000000000000000) == 0) { result += 4; x <<= 4; }
            while ((x & 0x8000000000000000) == 0) { result += 1; x <<= 1; }
            return result;
        }
        [BurstCompile(DisableDirectCall=disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Division(in long xl,in long yl)
        {

            if (yl == 0)
            {
                return MAX_VALUE;
                //throw new DivideByZeroException();
            }

            var remainder = (ulong)(xl >= 0 ? xl : -xl);
            var divider = (ulong)(yl >= 0 ? yl : -yl);
            var quotient = 0UL;
            var bitPos = NUM_BITS / 2 + 1;


            // If the divider is divisible by 2^n, take advantage of it.
            while ((divider & 0xF) == 0 && bitPos >= 4)
            {
                divider >>= 4;
                bitPos -= 4;
            }

            while (remainder != 0 && bitPos >= 0)
            {
                int shift = CountLeadingZeroes(remainder);
                if (shift > bitPos)
                {
                    shift = bitPos;
                }
                remainder <<= shift;
                bitPos -= shift;

                var div = remainder / divider;
                remainder = remainder % divider;
                quotient += div << bitPos;

                // Detect overflow
                if ((div & ~(0xFFFFFFFFFFFFFFFF >> bitPos)) != 0)
                {
                    return ((xl ^ yl) & MIN_VALUE) == 0 ? MaxValue : MinValue;
                }

                remainder <<= 1;
                --bitPos;
            }

            // rounding
            ++quotient;
            var result = (long)(quotient >> 1);
            if (((xl ^ yl) & MIN_VALUE) != 0)
            {
                result = -result;
            }
            return result;
            //return new FP(result);
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Multiply(in long xl,in long yl) {
            var xlo = (ulong)(xl & 0x00000000FFFFFFFF);
            var xhi = xl >> FRACTIONAL_PLACES;
            var ylo = (ulong)(yl & 0x00000000FFFFFFFF);
            var yhi = yl >> FRACTIONAL_PLACES;

            var lolo = xlo * ylo;
            var lohi = (long)xlo * yhi;
            var hilo = xhi * (long)ylo;
            var hihi = xhi * yhi;

            var loResult = lolo >> FRACTIONAL_PLACES;
            var midResult1 = lohi;
            var midResult2 = hilo;
            var hiResult = hihi << FRACTIONAL_PLACES;

            var sum = (long)loResult + midResult1 + midResult2 + hiResult;
            return sum;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long AddOverflowHelper(in long x, in long y, ref bool overflow)
        {
            var sum = x + y;
            // x + y overflows if sign(x) ^ sign(y) != sign(sum)
            overflow |= ((x ^ y ^ sum) & MIN_VALUE) != 0;
            return sum;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long OverflowMultiply(in long xl,in long yl) {
            var xlo = (ulong)(xl & 0x00000000FFFFFFFF);
            var xhi = xl >> FRACTIONAL_PLACES;
            var ylo = (ulong)(yl & 0x00000000FFFFFFFF);
            var yhi = yl >> FRACTIONAL_PLACES;

            var lolo = xlo * ylo;
            var lohi = (long)xlo * yhi;
            var hilo = xhi * (long)ylo;
            var hihi = xhi * yhi;

            var loResult = lolo >> FRACTIONAL_PLACES;
            var midResult1 = lohi;
            var midResult2 = hilo;
            var hiResult = hihi << FRACTIONAL_PLACES;

            bool overflow = false;
            var sum = AddOverflowHelper((long)loResult, midResult1, ref overflow);
            sum = AddOverflowHelper(sum, midResult2, ref overflow);
            sum = AddOverflowHelper(sum, hiResult, ref overflow);

            bool opSignsEqual = ((xl ^ yl) & MIN_VALUE) == 0;

            // if signs of operands are equal and sign of result is negative,
            // then multiplication overflowed positively
            // the reverse is also true
            if (opSignsEqual)
            {
                if (sum < 0 || (overflow && xl > 0))
                {
                    return MaxValue;
                }
            }
            else
            {
                if (sum > 0)
                {
                    return MinValue;
                }
            }

            // if the top 32 bits of hihi (unused in the result) are neither all 0s or 1s,
            // then this means the result overflowed.
            var topCarry = hihi >> FRACTIONAL_PLACES;
            if (topCarry != 0 && topCarry != -1 /*&& xl != -17 && yl != -17*/)
            {
                return opSignsEqual ? MaxValue : MinValue;
            }

            // If signs differ, both operands' magnitudes are greater than 1,
            // and the result is greater than the negative operand, then there was negative overflow.
            if (!opSignsEqual)
            {
                long posOp, negOp;
                if (xl > yl)
                {
                    posOp = xl;
                    negOp = yl;
                }
                else
                {
                    posOp = yl;
                    negOp = xl;
                }
                if (sum > negOp && negOp < -ONE && posOp > ONE)
                {
                    return MinValue;
                }
            }
            return sum;

        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Sqrt(ref long xl) {
            if (xl < 0)
            {
                // We cannot represent infinities like Single and Double, and Sqrt is
                // mathematically undefined for x < 0. So we just throw an exception.
                //throw new ArgumentOutOfRangeException("Negative value passed to Sqrt", "x");
                xl = long.MaxValue;
            }

            var num = (ulong)xl;
            var result = 0UL;

            // second-to-top bit
            var bit = 1UL << (NUM_BITS - 2);

            while (bit > num)
            {
                bit >>= 2;
            }

            // The main part is executed twice, in order to avoid
            // using 128 bit values in computations.
            for (var i = 0; i < 2; ++i)
            {
                // First we get the top 48 bits of the answer.
                while (bit != 0)
                {
                    if (num >= result + bit)
                    {
                        num -= result + bit;
                        result = (result >> 1) + bit;
                    }
                    else
                    {
                        result = result >> 1;
                    }
                    bit >>= 2;
                }

                if (i == 0)
                {
                    // Then process it again to get the lowest 16 bits.
                    if (num > (1UL << (NUM_BITS / 2)) - 1)
                    {
                        // The remainder 'num' is too large to be shifted left
                        // by 32, so we have to add 1 to result manually and
                        // adjust 'num' accordingly.
                        // num = a - (result + 0.5)^2
                        //       = num + result^2 - (result + 0.5)^2
                        //       = num - result - 0.5
                        num -= result;
                        num = (num << (NUM_BITS / 2)) - 0x80000000UL;
                        result = (result << (NUM_BITS / 2)) + 0x80000000UL;
                    }
                    else
                    {
                        num <<= (NUM_BITS / 2);
                        result <<= (NUM_BITS / 2);
                    }

                    bit = 1UL << (NUM_BITS / 2 - 2);
                }
            }
            // Finally, if next bit would have been 1, round the result upwards.
            if (num > result)
            {
                ++result;
            }
            return result;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ClampSinValue(long angle, out bool flipHorizontal, out bool flipVertical)
        {
            // Clamp value to 0 - 2*PI using modulo; this is very slow but there's no better way AFAIK
            var clamped2Pi = angle % PI_TIMES_2;
            if (angle < 0)
            {
                clamped2Pi += PI_TIMES_2;
            }

            // The LUT contains values for 0 - PiOver2; every other value must be obtained by
            // vertical or horizontal mirroring
            flipVertical = clamped2Pi >= PI;
            // obtain (angle % PI) from (angle % 2PI) - much faster than doing another modulo
            var clampedPi = clamped2Pi;
            while (clampedPi >= PI)
            {
                clampedPi -= PI;
            }
            flipHorizontal = clampedPi >= PI_OVER_2;
            // obtain (angle % PI_OVER_2) from (angle % PI) - much faster than doing another modulo
            var clampedPiOver2 = clampedPi;
            if (clampedPiOver2 >= PI_OVER_2)
            {
                clampedPiOver2 -= PI_OVER_2;
            }
            return clampedPiOver2;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long SinLutCalc(in long x) {
            bool flipHorizontal, flipVertical;
            var clampedL = MathBurstedFix.ClampSinValue(x, out flipHorizontal, out flipVertical);

            // Here we use the fact that the SinLut table has a number of entries
            // equal to (PI_OVER_2 >> 15) to use the angle to index directly into it
            var rawIndex = (uint)(clampedL >> 15);
            if (rawIndex >= LUT_SIZE)
            {
                rawIndex = LUT_SIZE - 1;
            }
            var nearestValue = SinLut[flipHorizontal ?
                SinLut.Length - 1 - (int)rawIndex :
                (int)rawIndex];
            return flipVertical ? -nearestValue : nearestValue;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long DistanceSquared(in long x,in long y) {
            return Multiply(x, x)+Multiply(y,y);
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Distance(in long x, in long y)
        {
            long res = DistanceSquared(x, y);
            res = (long)Sqrt(ref res);
            return res;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long DistanceSquared3D(in long x, in long y, in long z)
        {
            return Multiply(x, x) + Multiply(y, y) + Multiply(z, z);
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Distance3D(in long x, in long y, in long z)
        {
            long res = DistanceSquared3D(x, y, z);
            res = (long)Sqrt(ref res);
            return res;
        }
    }
    //三角函数部分
    public partial struct MathBurstedFix
    {
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AtanRaw(in long zRaw)
        {
            // 0 直接返回
            if (zRaw == 0) return 0;
        
            // 取符号（不能直接写负数字面量，用 0 - v）
            bool neg = zRaw < 0;
            long x = neg ? (0 - zRaw) : zRaw;
        
            // invert：x>1 时使用 atan(x)=PI/2 - atan(1/x)
            bool invert = x > ONE;
            if (invert)
            {
                x = Division(ONE, x);
            }
        
            // 以下复刻 FP.Atan 的级数形式（与原 FP.Atan 行为一致）
            // 常量 2,3
            long TWO = ONE << 1;                // 2.0
            long THREE = (3L << FRACTIONAL_PLACES); // 3.0
        
            long result = ONE;
            long term = ONE;
        
            long zSq = Multiply(x, x);          // x^2
            long zSq2 = zSq << 1;               // 2 * x^2
            long zSqPlusOne = zSq + ONE;        // x^2 + 1
            long zSq12 = zSqPlusOne << 1;       // 2*(x^2+1)
            long dividend = zSq2;
            long divisor = Multiply(zSqPlusOne, THREE);
        
            for (int i = 2; i < 30; i++)
            {
                // term *= dividend / divisor
                long frac = Division(dividend, divisor);
                term = Multiply(term, frac);
                result += term;
        
                dividend += zSq2;
                divisor += zSq12;
        
                if (term == 0) break;
            }
        
            // result = result * x / (x^2 + 1)
            result = Division(Multiply(result, x), zSqPlusOne);
        
            if (invert)
            {
                result = PI_OVER_2 - result;
            }
        
            if (neg)
            {
                result = 0 - result;
            }
        
            return result;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Atan2Raw(in long yRaw, in long xRaw)
        {
            // (0,0) -> 0
            if (xRaw == 0)
            {
                if (yRaw > 0) return PI_OVER_2;
                if (yRaw < 0) return (0 - PI_OVER_2);
                return 0;
            }
            if (yRaw == 0)
            {
                return xRaw >= 0 ? 0 : PI;
            }

            // 先算 atan(y/x)
            long ratio = Division(yRaw, xRaw);
            long angle = AtanRaw(ratio); // 范围 (-PI/2, PI/2)

            if (xRaw < 0)
            {
                if (yRaw >= 0)
                {
                    angle += PI;
                }
                else
                {
                    angle -= PI;
                }
            }

            long NEG_PI = (0 - PI);
            if (angle > PI)
            {
                angle -= PI_TIMES_2;
            }
            else if (angle < NEG_PI)
            {
                angle += PI_TIMES_2;
            }
            return angle;
        }
        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static long Atan2RawFast(in long yRaw, in long xRaw)
        {
            // 特殊点与轴处理
            if (xRaw == 0)
            {
                if (yRaw > 0) return PI_OVER_2;
                if (yRaw < 0) return (0 - PI_OVER_2);
                return 0; // (0,0)
            }
            if (yRaw == 0)
            {
                return xRaw >= 0 ? 0 : PI;
            }

            // z = y / x
            long z = Division(yRaw, xRaw); // 定点 (Q32)

            // sm = EN2 * 28  (EN2 原始常量  = ONE / 100 = 42949673)
            // 28 的定点表示 = 28 << FRACTIONAL_PLACES
            long twentyEight = (28L << FRACTIONAL_PLACES);
            long EN2_RAW = 42949673;                  // 与 FP.EN2_LONGVAL 保持一致
            long sm = Multiply(EN2_RAW, twentyEight); // ≈ 0.28 (Q32)

            // 预计算 z^2 与 sm*z*z
            long zSq = Multiply(z, z);
            long sm_z_z = Multiply(sm, zSq);

            // 溢出检测：与原实现一致逻辑 (One + sm*z*z == MaxValue)
            long overflowCheck = ONE + sm_z_z;
            if (overflowCheck == MaxValue)
            {
                return yRaw < 0 ? (0 - PI_OVER_2) : PI_OVER_2;
            }

            long absZ = z < 0 ? (0 - z) : z;
            long atan;

            if (absZ < ONE)
            {
                // atan ≈ z / (1 + sm*z*z)
                long denom = ONE + sm_z_z;
                atan = Division(z, denom);

                if (xRaw < 0)
                {
                    if (yRaw < 0) atan -= PI;
                    else atan += PI;
                }
            }
            else
            {
                // atan ≈ PI/2 - z / (z*z + sm)
                long denom = zSq + sm;
                long frac = Division(z, denom);
                atan = PI_OVER_2 - frac;

                if (yRaw < 0)
                {
                    atan -= PI;
                }
            }

            // 归一化到 [-PI, PI]
            /*long NEG_PI = (0 - PI);
            if (atan > PI) atan -= PI_TIMES_2;
            else if (atan < NEG_PI) atan += PI_TIMES_2;
            */
            return atan;
        }


        [BurstCompile(DisableDirectCall = disable, OptimizeFor = OptimizeFor.Performance)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AcosRaw(in long xRaw)
        {
            // 域裁剪到 [-1,1]
            long x = xRaw;
            long NEG_ONE = (0 - ONE);
            if (x > ONE) x = ONE;
            if (x < NEG_ONE) x = NEG_ONE;
        
            if (x == 0) return PI_OVER_2;
        
            // t = 1 - x*x
            long t = ONE - Multiply(x, x);
            if (t < 0) t = 0;
        
            // 开方（沿用现有 Sqrt；其缩放与全局 FP.Sqrt 保持一致，保持行为一致）
            long sqrtVal = (long)Sqrt(ref t); // 注意：沿用现有实现的缩放特性
        
            // ratio = sqrt(1 - x^2) / x
            long ratio = Division(sqrtVal, x);
        
            long atanPart = AtanRaw(ratio);
        
            // x < 0 ? atan + PI : atan
            if (x < 0)
            {
                atanPart += PI;
            }
            return atanPart;
        }
        
        [BurstCompile(DisableDirectCall = disable)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AsinRaw(in long xRaw)
        {
            // asin(x) = PI/2 - acos(x)
            long a = AcosRaw(xRaw);
            return PI_OVER_2 - a;
        }
        
        
        // =============================
        // 文件: Assets/TrueSync/Fix64.cs  修改 Asin / Acos 调用 Raw 版本
        // 用原有 FP 外壳，保持对外接口不变
        // =============================
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Asin(FP value)
        {
            FP r;
            r._serializedValue = MathBurstedFix.AsinRaw(value._serializedValue);
            return r;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Acos(FP x)
        {
            FP r;
            r._serializedValue = MathBurstedFix.AcosRaw(x._serializedValue);
            return r;
        }
        
    }
}
