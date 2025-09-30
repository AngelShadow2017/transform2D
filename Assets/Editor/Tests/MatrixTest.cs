using System;
using Core.TrueSync;
using NUnit.Framework;
using UnityEngine;

public class TMatrix2x3Tests
{
    private static TMatrix2x3 ManualMultiply(in TMatrix2x3 a, in TMatrix2x3 b)
    {
        // 参考实现（与源码 Multiply 保持一致）
        TMatrix2x3 r = new TMatrix2x3
        {
            m00 = a.m00 * b.m00 + a.m01 * b.m10,
            m01 = a.m00 * b.m01 + a.m01 * b.m11,
            m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02,

            m10 = a.m10 * b.m00 + a.m11 * b.m10,
            m11 = a.m10 * b.m01 + a.m11 * b.m11,
            m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12
        };
        return r;
    }

    private static bool MatrixEqual(in TMatrix2x3 a, in TMatrix2x3 b)
    {
        Debug.Log("Comparing:\n" + a.ToString() + "\n" + b.ToString());
        FP epsilon = (FP)1e-5f;
        return FP.Abs(a.m00 - b.m00) < epsilon && FP.Abs(a.m01 - b.m01) < epsilon && FP.Abs(a.m02 - b.m02) < epsilon
               && FP.Abs(a.m10 - b.m10) < epsilon && FP.Abs(a.m11 - b.m11) < epsilon && FP.Abs(a.m12 - b.m12) < epsilon;

    }

    private static TMatrix2x3 RandomNonSingular(int seed)
    {
        var rand = new System.Random(seed);
        while (true)
        {
            int a00 = rand.Next(1, 6);
            int a01 = rand.Next(-3, 4);
            int a10 = rand.Next(-3, 4);
            int a11 = rand.Next(1, 6);
            FP det = (FP)a00 * (FP)a11 - (FP)a01 * (FP)a10;
            if (det != FP.Zero)
            {
                return new TMatrix2x3
                {
                    m00 = (FP)a00,
                    m01 = (FP)a01,
                    m02 = (FP)rand.Next(-20, 21),
                    m10 = (FP)a10,
                    m11 = (FP)a11,
                    m12 = (FP)rand.Next(-20, 21)
                };
            }
        }
    }

    [Test]
    public void Identity_Multiplication()
    {
        var I = TMatrix2x3.Identity;
        var m = RandomNonSingular(123);
        var left = I * m;
        var right = m * I;
        Assert.IsTrue(MatrixEqual(m, left), "I * M 应等于 M");
        Assert.IsTrue(MatrixEqual(m, right), "M * I 应等于 M");
    }

    [Test]
    public void Multiplication_Correctness()
    {
        var a = RandomNonSingular(11);
        var b = RandomNonSingular(22);
        var expected = ManualMultiply(a, b);
        var actual = a * b;
        Assert.IsTrue(MatrixEqual(expected, actual), "矩阵乘法结果不匹配实现公式");
    }

    [Test]
    public void Inverse_Correct()
    {
        var m = RandomNonSingular(4567);
        var inv = m.Inverse();
        var prod1 = m * inv;
        var prod2 = inv * m;
        Assert.IsTrue(MatrixEqual(TMatrix2x3.Identity, prod1), "M * M^{-1} 不为单位矩阵");
        Assert.IsTrue(MatrixEqual(TMatrix2x3.Identity, prod2), "M^{-1} * M 不为单位矩阵");
    }

    [Test]
    public void Inverse_Exception_On_Singular()
    {
        // 构造奇异矩阵：第二行是第一行的 0.5 倍 => det = 0
        var singular = new TMatrix2x3
        {
            m00 = (FP)2,
            m01 = (FP)4,
            m02 = (FP)3,
            m10 = (FP)1,
            m11 = (FP)2,
            m12 = (FP)5
        };
        Assert.Throws<InvalidOperationException>(() => singular.Inverse());
    }

    [Test]
    public void MultiplyPoint_And_MultiplyVector()
    {
        var m = RandomNonSingular(999);
        var p = new TSVector2((FP)3, (FP)(-2));
        var vp = m.MultiplyPoint(p);
        var vv = m.MultiplyVector(p);

        // 手工
        TSVector2 expectedP = new TSVector2
        {
            x = m.m00 * p.x + m.m01 * p.y + m.m02,
            y = m.m10 * p.x + m.m11 * p.y + m.m12
        };
        TSVector2 expectedV = new TSVector2
        {
            x = m.m00 * p.x + m.m01 * p.y,
            y = m.m10 * p.x + m.m11 * p.y
        };

        Assert.AreEqual(expectedP.x, vp.x);
        Assert.AreEqual(expectedP.y, vp.y);
        Assert.AreEqual(expectedV.x, vv.x);
        Assert.AreEqual(expectedV.y, vv.y);
    }

    [Test]
    public void Inverse_RoundTrip_Point()
    {
        var m = RandomNonSingular(321);
        var inv = m.Inverse();
        var p = new TSVector2((FP)7, (FP)5);
        var tp = m.MultiplyPoint(p);
        var back = inv.MultiplyPoint(tp);
        Assert.AreEqual(p.x, back.x);
        Assert.AreEqual(p.y, back.y);
    }
}
