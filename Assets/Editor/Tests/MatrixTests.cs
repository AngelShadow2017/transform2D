using System;
using NUnit.Framework;
using Unity.Mathematics;
using Core.TrueSync;
using UnityEngine;
using Random = System.Random;
public class TMatrix3x4Tests_Float4x4
{
    const float Epsilon = 1e-4f;
    readonly Random _rand = new Random(20250101);

    // ====== 基础辅助 ======

    private FP FPFromFloat(float v) => FP.FromFloat(v); // 若没有此方法，请调整为你的 FP 构造

    private float Rand(float min, float max)
        => (float)(_rand.NextDouble() * (max - min) + min);

    private void AssertNear(float a, float b, float eps = Epsilon)
        => Assert.IsTrue(math.abs(a - b) <= eps, $"Expected {b}, got {a}, Δ={math.abs(a - b)}");

    private void AssertFloat4x4IsIdentity(float4x4 m, float eps = Epsilon)
    {
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                float expected = (r == c) ? 1f : 0f;
                float v = Get(m, r, c);
                AssertNear(v, expected, eps);
            }
        }
    }

    private float Get(in float4x4 m, int row, int col)
    {
        switch (col)
        {
            case 0: return col == 0 ? m.c0[row] : 0;
            case 1: return m.c1[row];
            case 2: return m.c2[row];
            case 3: return m.c3[row];
        }
        return 0;
    }

    // 将 TMatrix3x4 转为 float4x4
    private float4x4 ToFloat4x4(in TMatrix3x4 m)
    {
        return new float4x4(
            new float4((float)m.m00, (float)m.m10, (float)m.m20, 0f),
            new float4((float)m.m01, (float)m.m11, (float)m.m21, 0f),
            new float4((float)m.m02, (float)m.m12, (float)m.m22, 0f),
            new float4((float)m.m03, (float)m.m13, (float)m.m23, 1f)
        );
    }

    // 构造随机仿射矩阵（旋转 + 非零缩放 + 平移）
    private TMatrix3x4 BuildRandomMatrix()
    {
        float3 axis = math.normalize(new float3(Rand(-1,1), Rand(-1,1), Rand(-1,1)) + new float3(0.001f));
        float angle = Rand(-math.PI, math.PI);
        quaternion q = quaternion.AxisAngle(axis, angle);
        float3x3 R = new float3x3(q);

        float3 scale = new float3(Rand(0.3f, 2.0f), Rand(0.3f, 2.0f), Rand(0.3f, 2.0f));
        R.c0 *= scale.x;
        R.c1 *= scale.y;
        R.c2 *= scale.z;

        float3 t = new float3(Rand(-10f, 10f), Rand(-10f, 10f), Rand(-10f, 10f));

        return TMatrix3x4.FromLinearTranslation(
            FPFromFloat(R.c0.x), FPFromFloat(R.c1.x), FPFromFloat(R.c2.x), FPFromFloat(t.x),
            FPFromFloat(R.c0.y), FPFromFloat(R.c1.y), FPFromFloat(R.c2.y), FPFromFloat(t.y),
            FPFromFloat(R.c0.z), FPFromFloat(R.c1.z), FPFromFloat(R.c2.z), FPFromFloat(t.z)
        );
    }

    // 构造纯平移
    private TMatrix3x4 BuildTranslation(float3 t)
    {
        return TMatrix3x4.FromLinearTranslation(
            FP.One, FP.Zero, FP.Zero, FPFromFloat(t.x),
            FP.Zero, FP.One, FP.Zero, FPFromFloat(t.y),
            FP.Zero, FP.Zero, FP.One, FPFromFloat(t.z)
        );
    }

    // 构造纯缩放
    private TMatrix3x4 BuildScaling(float3 s)
    {
        return TMatrix3x4.FromLinearTranslation(
            FPFromFloat(s.x), FP.Zero, FP.Zero, FP.Zero,
            FP.Zero, FPFromFloat(s.y), FP.Zero, FP.Zero,
            FP.Zero, FP.Zero, FPFromFloat(s.z), FP.Zero
        );
    }

    // 构造绕任意轴旋转
    private TMatrix3x4 BuildRotation(float3 axis, float angle)
    {
        axis = math.normalize(axis);
        quaternion q = quaternion.AxisAngle(axis, angle);
        float3x3 R = new float3x3(q);
        return TMatrix3x4.FromLinearTranslation(
            FPFromFloat(R.c0.x), FPFromFloat(R.c1.x), FPFromFloat(R.c2.x), FP.Zero,
            FPFromFloat(R.c0.y), FPFromFloat(R.c1.y), FPFromFloat(R.c2.y), FP.Zero,
            FPFromFloat(R.c0.z), FPFromFloat(R.c1.z), FPFromFloat(R.c2.z), FP.Zero
        );
    }

    // ====== 测试用例 ======

    [Test]
    public void Identity_ToFloat4x4_Layout()
    {
        var I = TMatrix3x4.Identity;
        var f = ToFloat4x4(I);
        // 期待为标准仿射 4x4
        AssertFloat4x4IsIdentity(f);
    }

    [Test]
    public void Multiplication_Equals_Float4x4()
    {
        for (int i = 0; i < 64; i++)
        {
            var A = BuildRandomMatrix();
            var B = BuildRandomMatrix();
            var C = A * B;

            float4x4 Af = ToFloat4x4(A);
            float4x4 Bf = ToFloat4x4(B);
            float4x4 Cf = math.mul(Af, Bf);

            // 对比线性 3x3 和平移
            CompareLinearAndTranslation(C, Cf);
        }
    }

    private void CompareLinearAndTranslation(in TMatrix3x4 m, in float4x4 f)
    {
        // 线性部分
        AssertNear((float)m.m00, f.c0.x);
        AssertNear((float)m.m01, f.c1.x);
        AssertNear((float)m.m02, f.c2.x);

        AssertNear((float)m.m10, f.c0.y);
        AssertNear((float)m.m11, f.c1.y);
        AssertNear((float)m.m12, f.c2.y);

        AssertNear((float)m.m20, f.c0.z);
        AssertNear((float)m.m21, f.c1.z);
        AssertNear((float)m.m22, f.c2.z);

        // 平移列
        AssertNear((float)m.m03, f.c3.x);
        AssertNear((float)m.m13, f.c3.y);
        AssertNear((float)m.m23, f.c3.z);
    }

    [Test]
    public void Inverse_Correct_Float4x4()
    {
        for (int i = 0; i < 32; i++)
        {
            var A = BuildRandomMatrix();
            var Ainv = A.Inverse();

            var Af = ToFloat4x4(A);
            var Ainvf = ToFloat4x4(Ainv);

            var If = math.mul(Af, Ainvf);
            var If2 = math.mul(Ainvf, Af);

            // 两侧都应接近 Identity
            AssertFloat4x4IsIdentity(If);
            AssertFloat4x4IsIdentity(If2);
        }
    }

    [Test]
    public void Inverse_NonInvertible_Throws()
    {
        // 构造行依赖：第二行复制第一行
        var singular = TMatrix3x4.FromLinearTranslation(
            FP.One, FP.FromFloat(2f), FP.FromFloat(3f), FP.Zero,
            FP.One, FP.FromFloat(2f), FP.FromFloat(3f), FP.Zero,
            FP.Zero, FP.Zero, FP.Zero, FP.Zero
        );

        Assert.Throws<InvalidOperationException>(() => singular.Inverse());
    }

    [Test]
    public void MultiplyPoint_Matches_Float4x4()
    {
        for (int i = 0; i < 64; i++)
        {
            var M = BuildRandomMatrix();
            TSVector p = new TSVector
            {
                x = FPFromFloat(Rand(-5,5)),
                y = FPFromFloat(Rand(-5,5)),
                z = FPFromFloat(Rand(-5,5))
            };

            var rFP = M.MultiplyPoint(p);
            var Mf = ToFloat4x4(M);
            float4 pf = new float4((float)p.x, (float)p.y, (float)p.z, 1f);
            float4 rf = math.mul(Mf, pf);

            AssertNear((float)rFP.x, rf.x);
            AssertNear((float)rFP.y, rf.y);
            AssertNear((float)rFP.z, rf.z);
        }
    }

    [Test]
    public void MultiplyVector_Matches_Float4x4()
    {
        for (int i = 0; i < 64; i++)
        {
            var M = BuildRandomMatrix();
            TSVector v = new TSVector
            {
                x = FPFromFloat(Rand(-5,5)),
                y = FPFromFloat(Rand(-5,5)),
                z = FPFromFloat(Rand(-5,5))
            };

            var rFP = M.MultiplyVector(v);
            var Mf = ToFloat4x4(M);
            float4 vf = new float4((float)v.x, (float)v.y, (float)v.z, 0f);
            float4 rf = math.mul(Mf, vf);

            AssertNear((float)rFP.x, rf.x);
            AssertNear((float)rFP.y, rf.y);
            AssertNear((float)rFP.z, rf.z);
        }
    }

    [Test]
    public void PureTranslation_Test()
    {
        var t = new float3(3.2f, -5f, 7.7f);
        var M = BuildTranslation(t);
        var Mf = ToFloat4x4(M);

        // 点测试
        var p = new TSVector { x = FPFromFloat(1), y = FPFromFloat(2), z = FPFromFloat(3) };
        var rFP = M.MultiplyPoint(p);
        float4 rf = math.mul(Mf, new float4(1,2,3,1));

        AssertNear((float)rFP.x, rf.x);
        AssertNear((float)rFP.y, rf.y);
        AssertNear((float)rFP.z, rf.z);

        // 线性部分应为单位
        AssertNear((float)M.m00, 1f); AssertNear((float)M.m11, 1f); AssertNear((float)M.m22, 1f);
        AssertNear((float)M.m01, 0f); AssertNear((float)M.m02, 0f);
        AssertNear((float)M.m10, 0f); AssertNear((float)M.m12, 0f);
        AssertNear((float)M.m20, 0f); AssertNear((float)M.m21, 0f);
    }

    [Test]
    public void PureScaling_Test()
    {
        var s = new float3(2f, -3f, 0.5f);
        var M = BuildScaling(s);
        var Mf = ToFloat4x4(M);

        var v = new TSVector { x = FPFromFloat(1), y = FPFromFloat(-1), z = FPFromFloat(4) };
        var rVec = M.MultiplyVector(v);
        float4 rv = math.mul(Mf, new float4(1,-1,4,0));

        AssertNear((float)rVec.x, rv.x);
        AssertNear((float)rVec.y, rv.y);
        AssertNear((float)rVec.z, rv.z);

        // 点也要加平移(无平移 所以一致)
        var p = v;
        var rPoint = M.MultiplyPoint(p);
        float4 rp = math.mul(Mf, new float4(1,-1,4,1));
        AssertNear((float)rPoint.x, rp.x);
        AssertNear((float)rPoint.y, rp.y);
        AssertNear((float)rPoint.z, rp.z);
    }

    [Test]
    public void PureRotation_Test()
    {
        var axis = new float3(0.3f, 1f, -0.2f);
        var angle = 1.2345f;
        var M = BuildRotation(axis, angle);
        var Mf = float4x4.TRS(float3.zero,quaternion.AxisAngle(math.normalizesafe(axis),angle),new float3(1,1,1));//ToFloat4x4(M);
        Debug.Log(M);
        Debug.Log(Mf);
        // 验证长度保持（旋转无缩放）
        var v = new TSVector { x = FPFromFloat(2), y = FPFromFloat(-5), z = FPFromFloat(1) };
        var rFP = M.MultiplyVector(v);
        float4 rv = math.mul(Mf, new float4(2,-5,1,0));
        Debug.Log(rv);
        float lenIn = math.length(new float3(2,-5,1));
        float lenOut = math.length(new float3(rv.x, rv.y, rv.z));

        AssertNear(lenIn, lenOut, 1e-3f);
        AssertNear((float)rFP.x, rv.x);
        AssertNear((float)rFP.y, rv.y);
        AssertNear((float)rFP.z, rv.z);
    }

    [Test]
    public void Inverse_RotationTranslation()
    {
        var R = BuildRotation(new float3(0,1,0.5f), 0.75f);
        var T = BuildTranslation(new float3(5,-2,3));
        var A = R * T; // 先 R 后 T
        var Af = ToFloat4x4(A);

        var Ainv = A.Inverse();
        var Ainvf = ToFloat4x4(Ainv);

        var If = math.mul(Af, Ainvf);
        AssertFloat4x4IsIdentity(If, 2e-4f);
    }

    [Test]
    public void Multiply_Associativity_Approx()
    {
        var A = BuildRandomMatrix();
        var B = BuildRandomMatrix();
        var C = BuildRandomMatrix();

        var AB_C = (A * B) * C;
        var A_BC = A * (B * C);

        // 比对
        AssertNear((float)AB_C.m00, (float)A_BC.m00, 2e-4f);
        AssertNear((float)AB_C.m13, (float)A_BC.m13, 2e-4f);
        AssertNear((float)AB_C.m22, (float)A_BC.m22, 2e-4f);
        AssertNear((float)AB_C.m03, (float)A_BC.m03, 2e-4f);
        AssertNear((float)AB_C.m23, (float)A_BC.m23, 2e-4f);
    }
}