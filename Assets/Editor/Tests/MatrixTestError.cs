using NUnit.Framework;
using Core.TrueSync;
using UnityEditor.VersionControl;

public static class TSMatrixTestHelper {
    // 允许的定点误差（按你 FP 精度调整）
    private static readonly FP Eps = FP.FromRaw(1); // 或 (FP)(1e-4f)

    public static void AssertMatrixApprox(TSMatrix a, TSMatrix b, FP eps) {
        for (int row = 1; row <= 3; row++) {
            for (int col = 1; col <= 3; col++) {
                FP diff = FP.Abs((FP)(a.GetType().GetField($"M{row}{col}").GetValue(a)) - (FP)(b.GetType().GetField($"M{row}{col}").GetValue(b)));
                Assert.True(diff <= eps, $"Matrix mismatch at M{row}{col}: a={a.GetType().GetField($"M{row}{col}").GetValue(a)}, b={b.GetType().GetField($"M{row}{col}").GetValue(b)}, diff={diff}, eps={eps}\nA={a}\nB={b}");
            }
        }
    }

    public static FP RowDot(TSMatrix m, int r, int c) {
        // row r · row/col c (col when checking M * M^T)
        switch (r) {
            case 0: switch (c) {
                case 0: return m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13;
                case 1: return m.M11 * m.M21 + m.M12 * m.M22 + m.M13 * m.M23;
                case 2: return m.M11 * m.M31 + m.M12 * m.M32 + m.M13 * m.M33;
            } break;
            case 1: switch (c) {
                case 0: return m.M21 * m.M11 + m.M22 * m.M12 + m.M23 * m.M13;
                case 1: return m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23;
                case 2: return m.M21 * m.M31 + m.M22 * m.M32 + m.M23 * m.M33;
            } break;
            case 2: switch (c) {
                case 0: return m.M31 * m.M11 + m.M32 * m.M12 + m.M33 * m.M13;
                case 1: return m.M31 * m.M21 + m.M32 * m.M22 + m.M33 * m.M23;
                case 2: return m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33;
            } break;
        }
        return FP.Zero;
    }

    public static void AssertOrthonormal(TSMatrix m, FP eps) {
        // 行向量单位且相互垂直
        for (int i = 0; i < 3; i++) {
            FP len2 = RowDot(m, i, i);
            Assert.True(FP.Abs(len2 - FP.One) <= eps, "Row not unit");
        }
        Assert.True(FP.Abs(RowDot(m,0,1)) <= eps);
        Assert.True(FP.Abs(RowDot(m,0,2)) <= eps);
        Assert.True(FP.Abs(RowDot(m,1,2)) <= eps);
        // det ≈ 1
        FP det = m.Determinant();
        Assert.True(FP.Abs(det - FP.One) <= eps, "Det not 1");
    }
}

[TestFixture]
public class TSMatrixTests {

    [Test]
    public void Determinant_Identity_IsOne() {
        Assert.AreEqual(FP.One, TSMatrix.Identity.Determinant());
    }

    [Test]
    public void Determinant_KnownValue() {
        // 构造矩阵:
        // |2 3 1|
        // |0 1 4|
        // |5 -2 2|
        TSMatrix m = new TSMatrix(
            (2), (3), (1),
            FP.Zero, FP.One, (4),
            (5), (-2), (2));
        // 手工: 2*(1*2-4*-2) -3*(0*2-4*5) +1*(0*-2-1*5) = 2*(2+8) -3*(0-20) +1*(0-5)= 2*10 -3*(-20) -5 =20+60-5=75
        FP expected = (75);
        Assert.AreEqual(expected, m.Determinant());
    }

    [Test]
    public void Inverse_Mul_ReturnsIdentity() {
        TSMatrix rot = TSMatrix.CreateRotationX((FP)(0.75f)) *
                       TSMatrix.CreateRotationY((FP)(-1.1f)) *
                       TSMatrix.CreateRotationZ((FP)(0.33f));

        TSMatrix inv = TSMatrix.Inverse(rot);
        TSMatrix prod = rot * inv;

        // 验证接近单位阵
        TSMatrixTestHelper.AssertMatrixApprox(prod, TSMatrix.Identity, FP.FromRaw(5));
    }

    [Test]
    public void Invert_Unscaled_Equals_Scaled() {
        TSMatrix rot = TSMatrix.AngleAxis((FP)(0.6f), new TSVector(FP.One, FP.Zero, FP.Zero));
        TSMatrix a; TSMatrix.Inverse(ref rot, out a);  // 现有(含1024缩放)
        TSMatrix b; TSMatrix.Invert(ref rot, out b);   // 无缩放版本
        TSMatrixTestHelper.AssertMatrixApprox(a, b, FP.FromRaw(2));
    }

    [Test]
    public void FromQuaternion_IsOrthonormal() {
        TSQuaternion.CreateFromYawPitchRoll(
            (FP)(0.3f), (FP)(-1.2f), (FP)(0.9f),out TSQuaternion q);
        TSMatrix m = TSMatrix.CreateFromQuaternion(q);
        TSMatrixTestHelper.AssertOrthonormal(m, 1e-04);
    }

    [Test]
    public void Transpose_SymmetricCheck() {
        TSMatrix m = TSMatrix.CreateRotationY((FP)(0.4f));
        TSMatrix mt = TSMatrix.Transpose(m);
        TSMatrix shouldIdentity = m * mt;
        TSMatrixTestHelper.AssertMatrixApprox(shouldIdentity, TSMatrix.Identity, FP.FromRaw(6));
    }

    [Test]
    public void LookAt_ProducesOrthonormal() {
        TSVector forward = new TSVector((FP)(0.3f), (FP)(1.0f), (FP)(2.2f));
        TSVector up = TSVector.up;
        TSMatrix view = TSMatrix.LookAt(forward, up);
        TSMatrixTestHelper.AssertOrthonormal(view, FP.FromRaw(10));
    }

    [Test]
    public void Euler_RoundTrip_Basic() {
        // 仅在确认欧拉约定后使用；此处示例
        TSMatrix r = TSMatrix.CreateFromYawPitchRoll((FP)(0.2f), (FP)(0.1f), (FP)(-0.3f));
        TSVector e = r.eulerAngles;
        TSMatrix r2 = TSMatrix.CreateFromYawPitchRoll(
            e.y * FP.Deg2Rad, e.x * FP.Deg2Rad, e.z * FP.Deg2Rad); // 需按你的欧拉顺序调整
        TSMatrixTestHelper.AssertMatrixApprox(r, r2, 1e-01); // 允许较大误差（欧拉易有万向节和顺序差异）
    }
}
