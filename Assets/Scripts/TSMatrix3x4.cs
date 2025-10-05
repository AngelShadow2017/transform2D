using System;
using System.Runtime.CompilerServices;
using Core.TrueSync;
using Unity.Burst;

[BurstCompile(OptimizeFor = OptimizeFor.Performance)]
#region 3D 仿射矩阵 3x4
/// <summary>
/// 3D 仿射矩阵 (3x4)：
/// 行主形式与 TMatrix2x3 的乘法规则一致：
///   结果线性部分：A = A_a * A_b
///   结果平移：t = A_a * t_b + t_a
/// 字段命名：
///   行 r，列 c ： m{r}{c}
///   线性： m00..m02 / m10..m12 / m20..m22
///   平移： m03, m13, m23
/// </summary>
public struct TMatrix3x4
{
    public FP m00, m01, m02, m03;
    public FP m10, m11, m12, m13;
    public FP m20, m21, m22, m23;

    public static readonly TMatrix3x4 Identity;

    static TMatrix3x4()
    {
        // 单位：线性=I，平移=0
        Identity = new TMatrix3x4
        {
            m00 = FP.One, m01 = FP.Zero, m02 = FP.Zero, m03 = FP.Zero,
            m10 = FP.Zero, m11 = FP.One, m12 = FP.Zero, m13 = FP.Zero,
            m20 = FP.Zero, m21 = FP.Zero, m22 = FP.One, m23 = FP.Zero
        };
    }

    #region 乘法
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMatrix3x4 operator *(in TMatrix3x4 a, in TMatrix3x4 b)
    {
        TMatrix3x4 r = new TMatrix3x4();
        Multiply(a, b, ref r);
        return r;
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Multiply(in TMatrix3x4 a, in TMatrix3x4 b, ref TMatrix3x4 r)
    {
        // 线性 = A_a * A_b
        r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20;
        r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21;
        r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22;
        r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03;

        r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20;
        r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21;
        r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22;
        r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13;

        r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20;
        r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21;
        r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22;
        r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23;
    }
    #endregion

    #region Inverse
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TMatrix3x4 Inverse()
    {
        TMatrix3x4 r = new TMatrix3x4();
        Inverse(this, ref r);
        return r;
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Inverse(in TMatrix3x4 a, ref TMatrix3x4 r)
    {
        // 线性部分 A
        FP a00 = a.m00; FP a01 = a.m01; FP a02 = a.m02;
        FP a10 = a.m10; FP a11 = a.m11; FP a12 = a.m12;
        FP a20 = a.m20; FP a21 = a.m21; FP a22 = a.m22;

        // Cofactors（带符号代数余子式）
        FP c00 = a11 * a22 - a12 * a21;
        FP c01 = (0 - (a10 * a22 - a12 * a20)); // - (a10*a22 - a12*a20)
        FP c02 = a10 * a21 - a11 * a20;

        FP c10 = (0 - (a01 * a22 - a02 * a21));
        FP c11 = a00 * a22 - a02 * a20;
        FP c12 = (0 - (a00 * a21 - a01 * a20));

        FP c20 = a01 * a12 - a02 * a11;
        FP c21 = (0 - (a00 * a12 - a02 * a10));
        FP c22 = a00 * a11 - a01 * a10;

        FP det = a00 * c00 + a01 * c01 + a02 * c02; // 行展开
        if (det == 0)
            throw new InvalidOperationException("Matrix not invertible (det=0).");

        FP invDet = 1 / det;

        // 逆矩阵线性部分 = adj(A)/det = transpose(C)/det
        r.m00 = c00 * invDet;
        r.m01 = c10 * invDet;
        r.m02 = c20 * invDet;

        r.m10 = c01 * invDet;
        r.m11 = c11 * invDet;
        r.m12 = c21 * invDet;

        r.m20 = c02 * invDet;
        r.m21 = c12 * invDet;
        r.m22 = c22 * invDet;

        // 平移 t = (a.m03, a.m13, a.m23)
        FP tx = a.m03;
        FP ty = a.m13;
        FP tz = a.m23;

        // 新平移 = -R * t
        r.m03 = (0 - (r.m00 * tx + r.m01 * ty + r.m02 * tz));
        r.m13 = (0 - (r.m10 * tx + r.m11 * ty + r.m12 * tz));
        r.m23 = (0 - (r.m20 * tx + r.m21 * ty + r.m22 * tz));
    }
    #endregion

    #region MultiplyPoint / MultiplyVector
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector MultiplyPoint(in TSVector p)
    {
        TSVector r = new TSVector();
        MulPoint(this, p, ref r);
        return r;
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MulPoint(in TMatrix3x4 m, in TSVector p, ref TSVector r)
    {
        r.x = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03;
        r.y = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
        r.z = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSVector MultiplyVector(in TSVector v)
    {
        TSVector r = new TSVector();
        MulVector(this, v, ref r);
        return r;
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MulVector(in TMatrix3x4 m, in TSVector v, ref TSVector r)
    {
        r.x = m.m00 * v.x + m.m01 * v.y + m.m02 * v.z;
        r.y = m.m10 * v.x + m.m11 * v.y + m.m12 * v.z;
        r.z = m.m20 * v.x + m.m21 * v.y + m.m22 * v.z;
    }
    #endregion

    #region 工具函数 (可扩展)
    /*[MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMatrix3x4 FromLinearTranslation2(
        FP a00, FP a01, FP a02,
        FP a10, FP a11, FP a12,
        FP a20, FP a21, FP a22,
        FP tx, FP ty, FP tz)
    {
        return new TMatrix3x4
        {
            m00 = a00, m01 = a01, m02 = a02, m03 = tx,
            m10 = a10, m11 = a11, m12 = a12, m13 = ty,
            m20 = a20, m21 = a21, m22 = a22, m23 = tz
        };
    }*/
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMatrix3x4 FromLinearTranslation(
        FP a00, FP a01, FP a02,FP tx,
        FP a10, FP a11, FP a12,FP ty,
        FP a20, FP a21, FP a22,FP tz
          )
    {
        return new TMatrix3x4
        {
            m00 = a00, m01 = a01, m02 = a02, m03 = tx,
            m10 = a10, m11 = a11, m12 = a12, m13 = ty,
            m20 = a20, m21 = a21, m22 = a22, m23 = tz
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMatrix3x4 Translate(in TSVector t)
    {
        var r = Identity;
        r.m03 = t.x;
        r.m13 = t.y;
        r.m23 = t.z;
        return r;
    }
    #endregion

    public override string ToString()
    {
        return $"[\n" +
               $"  {m00}, {m01}, {m02}, {m03},\n" +
               $"  {m10}, {m11}, {m12}, {m13},\n" +
               $"  {m20}, {m21}, {m22}, {m23}\n]";
    }
}
#endregion