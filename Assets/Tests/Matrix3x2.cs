using System;
using UnityEngine;

/// <summary>
/// Unity 风格（列向量语义）的 2D 3x3 仿射矩阵:
/// | m00 m01 m02 |
/// | m10 m11 m12 |
/// |  0   0   1  |
/// v' = M * v (v = (x,y,1)^T)
/// 组合：M = T * R * S (与 Unity Matrix4x4.TRS 语义一致；先 S 再 R 再 T)
/// 现支持从矩阵反解“带符号”的缩放（单轴反射），算法详见 Decompose。
/// 限制：无法唯一还原“双轴同时为负”与“旋转+正缩放”之间的等价模糊，这是数学上信息的不可逆性。
/// </summary>
[Serializable]
public struct Matrix2DUnity : IEquatable<Matrix2DUnity>
{
    public float m00, m01, m02;
    public float m10, m11, m12;

    public static readonly Matrix2DUnity Identity = new Matrix2DUnity(1, 0, 0, 0, 1, 0);

    public Matrix2DUnity(float m00, float m01, float m02,
                         float m10, float m11, float m12)
    {
        this.m00 = m00; this.m01 = m01; this.m02 = m02;
        this.m10 = m10; this.m11 = m11; this.m12 = m12;
    }

    public Vector2 MultiplyPoint(Vector2 p)
    {
        // 列向量: v' = M * v
        return new Vector2(
            m00 * p.x + m01 * p.y + m02,
            m10 * p.x + m11 * p.y + m12
        );
    }

    public static Matrix2DUnity Translation(Vector2 t) =>
        new Matrix2DUnity(1, 0, t.x, 0, 1, t.y);

    public static Matrix2DUnity Rotation(float radians)
    {
        float c = Mathf.Cos(radians);
        float s = Mathf.Sin(radians);
        // 列向量旋转矩阵列: [ [ c -s 0 ], [ s c 0 ], [0 0 1] ]
        return new Matrix2DUnity(c, -s, 0,
                                 s,  c, 0);
    }

    public static Matrix2DUnity Scale(Vector2 s) =>
        new Matrix2DUnity(s.x, 0, 0,
                          0,   s.y, 0);

    /// <summary>
    /// M = T * R * S
    /// </summary>
    public static Matrix2DUnity TRS(Vector2 position, float rotationRadians, Vector2 scale)
    {
        var T = Translation(position);
        var R = Rotation(rotationRadians);
        var S = Scale(scale);
        return T * R * S;
    }

    /// <summary>
    /// 逆矩阵（仿射 2D），不可逆时返回 Identity。
    /// </summary>
    public Matrix2DUnity Inverse()
    {
        float det = m00 * m11 - m01 * m10;
        if (Mathf.Approximately(det, 0))
            return Identity;

        float inv = 1f / det;
        // 线性部分逆
        float nm00 =  m11 * inv;
        float nm01 = -m01 * inv;
        float nm10 = -m10 * inv;
        float nm11 =  m00 * inv;

        // 平移逆： -R^{-1} * t
        float tx = m02;
        float ty = m12;
        float nm02 = -(nm00 * tx + nm01 * ty);
        float nm12 = -(nm10 * tx + nm11 * ty);

        return new Matrix2DUnity(nm00, nm01, nm02,
                                 nm10, nm11, nm12);
    }

    /// <summary>
    /// 分解出 (translation, rotationRadians, signedScale)。
    /// 支持单轴反射：如果线性部分行列式为负，则 sy 会为负（或 sx 为 0 的特例处理）。
    /// 算法：
    /// 线性部分 L = R * S （R 正交 det=+1，S=diag(sx, sy) 允许 sy<0）
    /// sx = |第一列|；r0 = 第一列 / sx；r1 = (-r0.y, r0.x)；
    /// sy = dot(第二列, r1) （自然带符号）。
    /// rotation = atan2(r0.y, r0.x)
    /// 注意：双轴都为负与 (rotation+π, 正缩放) 等价，无法区分。
    /// </summary>
    public void Decompose(out Vector2 translation, out float rotation, out Vector2 scale)
    {
        translation = new Vector2(m02, m12);

        // 第一列
        float a = m00;
        float c = m10;
        float sx = Mathf.Sqrt(a * a + c * c);

        float epsilon = 1e-7f;
        float r0x, r0y;

        if (sx > epsilon)
        {
            r0x = a / sx;
            r0y = c / sx;
        }
        else
        {
            // 退化：把旋转视为 0，sx = 0
            sx = 0f;
            r0x = 1f;
            r0y = 0f;
        }

        // 正交第二列基向量
        float r1x = -r0y;
        float r1y =  r0x;

        // 第二列
        float b = m01;
        float d = m11;

        // 有符号 sy
        float sy = b * r1x + d * r1y;

        // 旋转
        rotation = Mathf.Atan2(r0y, r0x);

        scale = new Vector2(sx, sy);
    }

    public static Matrix2DUnity operator *(Matrix2DUnity A, Matrix2DUnity B)
    {
        // 列向量数学: C = A * B (先应用 B 再 A)
        Matrix2DUnity C;
        C.m00 = A.m00 * B.m00 + A.m01 * B.m10;
        C.m01 = A.m00 * B.m01 + A.m01 * B.m11;
        C.m02 = A.m00 * B.m02 + A.m01 * B.m12 + A.m02;

        C.m10 = A.m10 * B.m00 + A.m11 * B.m10;
        C.m11 = A.m10 * B.m01 + A.m11 * B.m11;
        C.m12 = A.m10 * B.m02 + A.m11 * B.m12 + A.m12;
        return C;
    }

    public bool Equals(Matrix2DUnity other)
    {
        return Mathf.Approximately(m00, other.m00) &&
               Mathf.Approximately(m01, other.m01) &&
               Mathf.Approximately(m02, other.m02) &&
               Mathf.Approximately(m10, other.m10) &&
               Mathf.Approximately(m11, other.m11) &&
               Mathf.Approximately(m12, other.m12);
    }

    public override bool Equals(object obj) => obj is Matrix2DUnity o && Equals(o);

    public override int GetHashCode() => HashCode.Combine(m00, m01, m02, m10, m11, m12);

    public override string ToString() =>
        $"[{m00},{m01},{m02}; {m10},{m11},{m12}; 0,0,1]";

    /// <summary>
    /// 转为 Unity Matrix4x4。平移 -> m03, m13
    /// </summary>
    public Matrix4x4 ToMatrix4x4()
    {
        Matrix4x4 M = Matrix4x4.identity;
        M.m00 = m00; M.m01 = m01; M.m03 = m02;
        M.m10 = m10; M.m11 = m11; M.m13 = m12;
        // m22 = 1, m33 = 1
        return M;
    }
}