using System;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using Unity.Burst;

[BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    #region 2D 仿射矩阵 2x3 (列式)
    public struct TMatrix2x3
    {
        public FP m00, m01, m02;
        public FP m10, m11, m12;

        public static TMatrix2x3 Identity;
        static TMatrix2x3()
        {

            Identity = new TMatrix2x3();
            Identity.m00 = FP.One;
            Identity.m11 = FP.One;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TMatrix2x3 operator *(in TMatrix2x3 a,in TMatrix2x3 b)
        {
            TMatrix2x3 r = new TMatrix2x3();
            Multiply(a,b,ref r);
            return r;
        }
        [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Multiply(in TMatrix2x3 a, in TMatrix2x3 b, ref TMatrix2x3 r)
        {
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02;

            r.m10 = a.m10 * b.m00 + a.m11 * b.m10;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TMatrix2x3 Inverse()
        {
            /*FP det = m00 * m11 - m01 * m10;
            if (det==FP.Zero) throw new InvalidOperationException("Matrix not invertible.");
            FP inv = FP.One / det;*/
            TMatrix2x3 r = new TMatrix2x3();
            Inverse(this, ref r);
            /*r.m00 =  m11 * inv;
            r.m01 = -m01 * inv;
            r.m02 = -(r.m00 * m02 + r.m01 * m12);

            r.m10 = -m10 * inv;
            r.m11 =  m00 * inv;
            r.m12 = -(r.m10 * m02 + r.m11 * m12);*/
            return r;
        }
        [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Inverse(in TMatrix2x3 a, ref TMatrix2x3 r)
        {
            FP det = a.m00 * a.m11 - a.m01 * a.m10;
            if (det==FP.Zero) throw new InvalidOperationException("Matrix not invertible.");
            FP inv = FP.One / det;
            r.m00 =  a.m11 * inv;
            r.m01 = -a.m01 * inv;
            r.m02 = -(r.m00 * a.m02 + r.m01 * a.m12);

            r.m10 = -a.m10 * inv;
            r.m11 =  a.m00 * inv;
            r.m12 = -(r.m10 * a.m02 + r.m11 * a.m12);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TSVector2 MultiplyPoint(in TSVector2 p)
        {
            TSVector2 r = new TSVector2();
            MulPoint(this,p,ref r);
            return r;
            /*return new TSVector2(m00 * p.x + m01 * p.y + m02,
                m10 * p.x + m11 * p.y + m12);*/
        }
        [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void MulPoint(in TMatrix2x3 m, in TSVector2 p, ref TSVector2 r)
        {
            r.x = m.m00 * p.x + m.m01 * p.y + m.m02;
            r.y = m.m10 * p.x + m.m11 * p.y + m.m12;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TSVector2 MultiplyVector(in TSVector2 v)
        {
            TSVector2 r = new TSVector2();
            MulVector(this,v,ref r);
            return r;
            /*=>
            new TSVector2(m00 * v.x + m01 * v.y,
                m10 * v.x + m11 * v.y);*/
        }
        [BurstCompile,MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void MulVector(in TMatrix2x3 m, in TSVector2 v, ref TSVector2 r)
        {
            r.x = m.m00 * v.x + m.m01 * v.y;
            r.y = m.m10 * v.x + m.m11 * v.y;
        }

        public override string ToString()
        {
            return $"[\n  {m00}, {m01}, {m02},\n  {m10}, {m11}, {m12}\n]";
        }
    }
    #endregion