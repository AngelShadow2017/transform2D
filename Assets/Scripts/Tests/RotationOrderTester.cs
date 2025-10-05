using UnityEngine;
using System;
using System.Collections.Generic;

public class RotationOrderTester : MonoBehaviour
{
    [Tooltip("随机样本数量")]
    public int samples = 50000;

    [Tooltip("角度范围(度)")]
    public Vector3 minEuler = new Vector3(-180,-180,-180);
    public Vector3 maxEuler = new Vector3( 180, 180, 180);

    [Tooltip("四元数点乘偏差阈值 (1 - dot)")]
    public float quatDiffEps = 1e-5f;

    [Tooltip("是否在 Start 自动执行")]
    public bool runOnStart = true;

    private struct OrderStat
    {
        public string name;
        public int matchCount;
        public double totalDiff;
    }

    // 六种常见内旋顺序（Intrinsic）：依次对局部轴做旋转：q = q * R(axis)
    private readonly string[] orders = {
        "XYZ","XZY","YXZ","YZX","ZXY","ZYX"
    };

    void Start()
    {
        if (runOnStart) RunTest();
    }

    [ContextMenu("Run Test")]
    public void RunTest()
    {
        var stats = new Dictionary<string, OrderStat>();
        foreach (var o in orders)
            stats[o] = new OrderStat{ name = o, matchCount = 0, totalDiff = 0 };

        System.Random rng = new System.Random();

        for (int i = 0; i < samples; i++)
        {
            Vector3 euler = new Vector3(
                Rand(minEuler.x, maxEuler.x, rng),
                Rand(minEuler.y, maxEuler.y, rng),
                Rand(minEuler.z, maxEuler.z, rng)
            );

            Quaternion unityQ = Quaternion.Euler(euler);

            foreach (var ord in orders)
            {
                Quaternion q = BuildByOrder(ord, euler);
                // 由于四元数存在正负等价，比较用 |dot|
                float d = 1f - Mathf.Abs(Quaternion.Dot(unityQ, q));
                var st = stats[ord];
                st.totalDiff += d;
                if (d <= quatDiffEps) st.matchCount++;
                stats[ord] = st;
            }
        }

        // 选出匹配次数最多 + 平均误差最小
        string best = null;
        foreach (var kv in stats)
        {
            if (best == null) best = kv.Key;
            else
            {
                var a = stats[best];
                var b = kv.Value;
                if (b.matchCount > a.matchCount ||
                   (b.matchCount == a.matchCount && b.totalDiff < a.totalDiff))
                    best = kv.Key;
            }
        }

        Debug.Log(BuildReport(stats, best));
    }

    private static float Rand(float min, float max, System.Random r)
    {
        return (float)(min + (max - min) * r.NextDouble());
    }

    private static Quaternion BuildByOrder(string order, Vector3 eulerDeg)
    {
        Quaternion q = Quaternion.identity;
        for (int i = 0; i < order.Length; i++)
        {
            char c = order[i];
            float angle = 0f;
            Vector3 axis = Vector3.right;
            switch (c)
            {
                case 'X': angle = eulerDeg.x; axis = Vector3.right; break;
                case 'Y': angle = eulerDeg.y; axis = Vector3.up;    break;
                case 'Z': angle = eulerDeg.z; axis = Vector3.forward; break;
            }
            q = q * Quaternion.AngleAxis(angle, axis); // 内旋：依次乘以局部轴旋转
        }
        return q;
    }

    private string BuildReport(Dictionary<string, OrderStat> stats, string best)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("==== Unity Euler Rotation Order Test ====");
        sb.AppendLine($"Samples: {samples}  Eps(quat diff): {quatDiffEps:E}");
        foreach (var kv in stats)
        {
            double avg = kv.Value.totalDiff / samples;
            sb.AppendLine(
                $"Order {kv.Key}  Match={kv.Value.matchCount}  MatchRate={(kv.Value.matchCount/(double)samples):P2}  AvgDiff={avg:E6}"
            );
        }
        sb.AppendLine($"BEST MATCH (推测 Unity 内部欧拉顺序) = {best}");
        sb.AppendLine("说明：这里指内旋顺序(局部轴)，即代码中依次 q = q * R(axis)。");
        sb.AppendLine("若 best=ZXY，则表示 Quaternion.Euler(x,y,z) 等价于按 Z->X->Y 内旋。");
        return sb.ToString();
    }
}
