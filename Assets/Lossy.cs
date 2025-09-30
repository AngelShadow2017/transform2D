using UnityEngine;


public static class PreciseLossyScaleLegacy
{
    public static Vector3 ComputeFormulaLossyScale(Transform leaf, bool normalizeReflection = true)
    {
        // 收集链（根到叶）
        int depth = 0;
        for (Transform t = leaf; t != null; t = t.parent) depth++;
        Transform[] chain = new Transform[depth];
        {
            Transform t = leaf;
            for (int i = depth - 1; i >= 0; --i)
            {
                chain[i] = t;
                t = t.parent;
            }
        }

        Matrix4x4 R_world = Matrix4x4.identity;
        Matrix4x4 W_world = Matrix4x4.identity;

        foreach (var tr in chain)
        {
            Matrix4x4 R_i = Matrix4x4.Rotate(tr.localRotation);
            Vector3 ls = tr.localScale;
            Matrix4x4 S_i = Matrix4x4.Scale(ls);

            // (R_i * S_i) 只需要线性部分，平移可忽略
            Matrix4x4 R_i_S_i = R_i * S_i;

            W_world = W_world * R_i_S_i;
            R_world = R_world * R_i;
        }

        // R_world 纯旋转 ⇒ 逆 = 转置
        Matrix4x4 R_world_T = R_world.transpose;
        Matrix4x4 S_world = R_world_T * W_world;

        Vector3 scale = new Vector3(S_world.m00, S_world.m11, S_world.m22);

        if (normalizeReflection)
        {
            float detW = Determinant3x3(W_world);
            if (detW < 0)
            {
                Vector3 absS = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                int maxAxis = 0;
                if (absS.y > absS[maxAxis]) maxAxis = 1;
                if (absS.z > absS[maxAxis]) maxAxis = 2;
                scale = absS;
                if (maxAxis == 0) scale.x = -scale.x;
                else if (maxAxis == 1) scale.y = -scale.y;
                else scale.z = -scale.z;
            }
        }

        return scale;
    }

    private static float Determinant3x3(Matrix4x4 m)
    {
        // 只看左上角 3×3
        Vector3 c0 = new Vector3(m.m00, m.m10, m.m20);
        Vector3 c1 = new Vector3(m.m01, m.m11, m.m21);
        Vector3 c2 = new Vector3(m.m02, m.m12, m.m22);
        return Vector3.Dot(c0, Vector3.Cross(c1, c2));
    }
}
public class Lossy : MonoBehaviour
{
    void Start()
    {
        Vector3 mul = MultiplyParentLossyScales(transform);
        Debug.Log(transform.parent.localToWorldMatrix+" "+transform.localToWorldMatrix);
        Debug.Log(transform.root.lossyScale+" "+transform.root.localToWorldMatrix);
        //Debug.Log(transform.parent.worldToLocalMatrix.lossyScale);
        Debug.Log(transform.localToWorldMatrix.lossyScale);
        //Debug.Log(transform.worldToLocalMatrix.lossyScale);
        Vector3 self = transform.lossyScale;

        Debug.Log("GetLossy: " + PreciseLossyScaleLegacy.ComputeFormulaLossyScale(transform));
        Debug.Log("自身 localToWorldMatrix.lossyScale: " + self);
    }
    public static Vector3 GetLossyScaleLikeUnity(Transform t)
    {
        Matrix4x4 m = t.localToWorldMatrix;
        Vector3 c0 = new Vector3(m.m00, m.m10, m.m20);
        Vector3 c1 = new Vector3(m.m01, m.m11, m.m21);
        Vector3 c2 = new Vector3(m.m02, m.m12, m.m22);

        float sx = c0.magnitude;
        float sy = c1.magnitude;
        float sz = c2.magnitude;

        // 处理整体反射（行列式 < 0）
        // Unity 里常见做法是把一个分量取负（通常是和 原来的 localScale 符号 或 叉乘朝向 对齐）
        float det = Vector3.Dot(Vector3.Cross(c0, c1), c2);
        if (det < 0f)
        {
            // 习惯上把最大的那个轴或第一个轴取负，Unity 具体策略内部实现，但效果类似：
            sx = -sx;
        }

        return new Vector3(sx, sy, sz);
    }
    Vector3 MultiplyParentLossyScales(Transform t)
    {
        if (t.parent == null)
            return t.localToWorldMatrix.lossyScale;
        Vector3 parent = MultiplyParentLossyScales(t.parent);
        Vector3 current = t.localToWorldMatrix.lossyScale;
        return new Vector3(
            parent.x * current.x,
            parent.y * current.y,
            parent.z * current.z
        );
    }

    // 方法1：直接取列向量模长
    Vector3 ColumnLength(Matrix4x4 m)
    {
        Vector3 x = new Vector3(m.m00, m.m10, m.m20);
        Vector3 y = new Vector3(m.m01, m.m11, m.m21);
        Vector3 z = new Vector3(m.m02, m.m12, m.m22);

        return new Vector3(x.magnitude, y.magnitude, z.magnitude);
    }

    // 方法2：Gram-Schmidt 正交化 + 符号修正
    Vector3 GramSchmidtScale(Matrix4x4 m)
    {
        Vector3 x = new Vector3(m.m00, m.m10, m.m20);
        Vector3 y = new Vector3(m.m01, m.m11, m.m21);
        Vector3 z = new Vector3(m.m02, m.m12, m.m22);

        float scaleX = x.magnitude;
        x.Normalize();

        y = y - Vector3.Dot(y, x) * x;
        float scaleY = y.magnitude;
        y.Normalize();

        z = z - Vector3.Dot(z, x) * x - Vector3.Dot(z, y) * y;
        float scaleZ = z.magnitude;

        // 符号修正：如果行列式为负，说明有镜像，需要翻转一个轴
        float det = Matrix4x4.identity.determinant; // 这里不能直接用 identity，要用 m 的 3x3 部分
        det = m.m00 * (m.m11 * m.m22 - m.m12 * m.m21)
              - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20)
              + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        if (det < 0)
        {
            scaleX = -scaleX; // Unity 内部通常翻转 X 轴
        }

        return new Vector3(scaleX, scaleY, scaleZ);
    }
}