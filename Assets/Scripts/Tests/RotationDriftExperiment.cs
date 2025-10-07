using System;
using System.Collections.Generic;
using UnityEngine;
using Core.TrueSync;

// 放在任意场景里运行，或通过在 Inspector 上点 Context Menu 来执行。
// 作用：对比 FixedNode3D 在不同使用方式下与 Unity Transform 旋转累计误差，定位 localRotation setter 未同步 _eulerRotation 的潜在问题。
// 变体说明：
//  VariantA_EulerIncrement      : 仅通过 localEuler 增量（基线）
//  VariantB_LocalRotationThenEuler : 先通过 localRotation(四元数乘) 再做欧拉增量（触发 bug 的组合）
//  VariantC_SetLocalQuaternionThenEuler : 使用 SetLocalQuaternion(内部同步欧拉) 再做欧拉增量（应显著更小漂移）
//  VariantD_PureQuaternionChain : 纯四元数链式（不访问欧拉）作为漂移参考
// 输出：每隔 logInterval 迭代打印角度差（度），结束后打印 JSON 风格总结。
// 使用方法：把脚本挂到空物体，勾选需要的变体，运行场景。
// 注意：此脚本不修改 FixedNode3D，本身只观察现状行为。

public class RotationDriftExperiment : MonoBehaviour
{
    [Header("General Settings")] public int iterations = 10000; // 总迭代次数
    public float deltaAngleDeg = 0.1f;         // 每步期望自旋角度（绕 Y）
    public int logInterval = 1000;             // 日志间隔
    public int seed = 1234;                    // 随机种子（用于可能拓展）

    
    [Header("Enable Variants")] public bool runVariantA = true;  // 仅欧拉增量
    public bool runVariantB = true;  // localRotation (q) 后再欧拉增量
    public bool runVariantC = true;  // SetLocalQuaternion 后欧拉增量
    public bool runVariantD = true;  // 纯左乘四元数链式 (delta * rot)
    public bool runVariantE = true;  // 纯右乘四元数链式 (rot * delta)
    public bool runVariantF = true;  // 单步增量精度对比（只比较 delta 本身）
    public bool runVariantG = true;  // 使用较大步长 (1 deg) 做链式，观察量化差异
    public bool runVariantH = true;  // 对比: 直接累乘缓存的本地四元数 vs 通过矩阵提取的 worldRotation

    [Header("Diagnostics")] public bool logPerStepYawSample = false; public int yawSampleInterval = 2000;

    [Header("Misc")] public bool autoRunOnStart = true; public bool printFinalSummary = true;

    [Header("Precision / Extra Logs")] public bool enableTheoreticalYaw = true; public bool highPrecisionQuaternionLog = false;

    private struct ResultRow { public string name; public float finalAngle; public float maxAngle; public float avgAngle; public int samples; }

    private List<ResultRow> _results = new List<ResultRow>();

    private TSQuaternion TSAngleAxis(float angleDeg, TSVector axis)
    {
        return TSQuaternion.AngleAxis((FP)angleDeg, axis); // TSQuaternion.AngleAxis 角度是度
    }

    private float AngleBetween(TSQuaternion a, Quaternion b)
    {
        TSQuaternion bt = new TSQuaternion((FP)b.x, (FP)b.y, (FP)b.z, (FP)b.w);
        return (float)TSQuaternion.Angle(a, bt);
    }

    private void LogHeader()
    {
        Debug.Log($"[RotationDrift] iterations={iterations} deltaDeg={deltaAngleDeg} orderBaseline=YXZ (FixedNode3D default)");
    }

    private void RunVariant(string variantName, Action<FixedNode3D, Transform, TSQuaternion, Quaternion> stepFn)
    {
        var go = new GameObject($"Unity_{variantName}");
        var tr = go.transform; tr.position = Vector3.zero; tr.rotation = Quaternion.identity; tr.localScale = Vector3.one;

        var node = new FixedNode3D();
        node.SetLocalTRS(new TSVector(0,0,0), TSQuaternion.identity, TSVector.one);

        TSQuaternion qDeltaTS = TSAngleAxis(deltaAngleDeg, new TSVector(0,1,0));
        Quaternion qDeltaUnity = Quaternion.AngleAxis(deltaAngleDeg, Vector3.up);

        float maxAngle = 0f; float sum = 0f; int sampleCount = 0; float lastAngle = 0f;

        for (int i=1;i<=iterations;i++)
        {
            stepFn(node, tr, qDeltaTS, qDeltaUnity);
            // 对齐 worldRotation 与 Unity transform.rotation
            TSQuaternion nRot = node.worldRotation; // 只读缓存
            float diff = AngleBetween(nRot, tr.rotation);
            if (diff > maxAngle) maxAngle = diff;
            sum += diff; sampleCount++; lastAngle = diff;
            if (i % logInterval == 0)
            {
                Debug.Log($"[RotationDrift][{variantName}] step={i} angleDiffDeg={diff:F6} (max={maxAngle:F6})");
            }
        }

        _results.Add(new ResultRow{ name=variantName, finalAngle=lastAngle, maxAngle=maxAngle, avgAngle=sum/sampleCount, samples=sampleCount });
        DestroyImmediate(go);
    }

    private float ExtractYawDegreeTS(TSQuaternion q)
    {
        // 针对仅绕 Y 旋转的理想情况：yaw = 2 * atan2(y, w)
        // 若存在数值噪声 (x,z != 0) 仍然使用此估计，偏差很小
        double yw = (double)q.y; double ww = (double)q.w; // 转 double 精度
        double yawRad = 2.0 * Math.Atan2(yw, ww);
        double deg = yawRad * (180.0 / Math.PI);
        // 归一化到 [0,360)
        deg %= 360.0; if (deg < 0) deg += 360.0;
        return (float)deg;
    }
    private float ExtractYawDegreeUnity(Quaternion q)
    {
        // Unity 绕 Y 纯旋: q.y, q.w 同理
        double yawRad = 2.0 * Math.Atan2(q.y, q.w);
        double deg = yawRad * (180.0 / Math.PI);
        deg %= 360.0; if (deg < 0) deg += 360.0;
        return (float)deg;
    }

    private void RunVariantDeltaCheck(string variantName)
    {
        TSQuaternion qDeltaTS = TSAngleAxis(deltaAngleDeg, new TSVector(0,1,0));
        Quaternion qDeltaUnity = Quaternion.AngleAxis(deltaAngleDeg, Vector3.up);
        float singleDiff = AngleBetween(qDeltaTS, qDeltaUnity);
        Debug.Log($"[RotationDrift][{variantName}] SingleStep AngleDiffDeg={singleDiff:F9} (期望≈0) qTS={qDeltaTS} qU={qDeltaUnity}");
    }

    private void RunVariantLargeStep(string variantName, float stepDeg)
    {
        var go = new GameObject($"Unity_{variantName}");
        var tr = go.transform; tr.rotation = Quaternion.identity;
        var node = new FixedNode3D(); node.SetLocalTRS(new TSVector(0,0,0), TSQuaternion.identity, TSVector.one);
        TSQuaternion qDeltaTS = TSAngleAxis(stepDeg, new TSVector(0,1,0));
        Quaternion qDeltaUnity = Quaternion.AngleAxis(stepDeg, Vector3.up);
        float last = 0, max = 0, sum = 0; int samples=0; float firstDiff = -1;
        for (int i=1;i<=iterations;i++)
        {
            node.localRotation = qDeltaTS * node.localRotation; // 保持与 VariantD 同模式
            tr.rotation = qDeltaUnity * tr.rotation;
            float diff = AngleBetween(node.worldRotation, tr.rotation);
            if (firstDiff < 0) firstDiff = diff;
            if (diff > max) max = diff; sum += diff; samples++; last = diff;
        }
        Debug.Log($"[RotationDrift][{variantName}] stepDeg={stepDeg} first={firstDiff:F9} final={last:F9} max={max:F9} avg={(sum/samples):F9}");
        DestroyImmediate(go);
    }

    // 新增 VariantH: 在 TrueSync 内部保留一个独立累乘的四元数 qAcc，不走矩阵->反解路径；比较 qAcc 与 node.worldRotation 之间角度，以及与 Unity。
    private void RunVariantH()
    {
        string variantName = "VariantH_LocalQuatVsExtracted";
        var go = new GameObject($"Unity_{variantName}"); var tr = go.transform; tr.rotation = Quaternion.identity;
        var node = new FixedNode3D(); node.SetLocalTRS(new TSVector(0,0,0), TSQuaternion.identity, TSVector.one);
        TSQuaternion qDeltaTS = TSAngleAxis(deltaAngleDeg, new TSVector(0,1,0)); Quaternion qDeltaUnity = Quaternion.AngleAxis(deltaAngleDeg, Vector3.up);
        TSQuaternion qAcc = TSQuaternion.identity; // 内部期望累积
        float maxDiffExtract = 0, maxDiffUnity = 0; float sumExtract=0, sumUnity=0; float lastExtract=0, lastUnity=0; int samples=0;
        for (int i=1;i<=iterations;i++)
        {
            // 直接在节点上累乘（和原 VariantD 一样）
            node.localRotation = qDeltaTS * node.localRotation;
            // Unity 同步
            tr.rotation = qDeltaUnity * tr.rotation;
            // 内部理想累乘器（完全绕 Y）
            qAcc = qDeltaTS * qAcc; qAcc.Normalize();
            // 提取 worldRotation（矩阵->拆）
            TSQuaternion wrot = node.worldRotation;
            float diffExtract = (float)TSQuaternion.Angle(qAcc, wrot);
            float diffUnity = AngleBetween(wrot, tr.rotation);
            if (diffExtract > maxDiffExtract) maxDiffExtract = diffExtract;
            if (diffUnity > maxDiffUnity) maxDiffUnity = diffUnity;
            sumExtract += diffExtract; sumUnity += diffUnity; samples++; lastExtract = diffExtract; lastUnity = diffUnity;
            if (i % logInterval == 0)
            {
                float yawAcc = ExtractYawDegreeTS(qAcc);
                float yawW = ExtractYawDegreeTS(wrot);
                float yawU = ExtractYawDegreeUnity(tr.rotation);
                float theo = enableTheoreticalYaw ? TheoreticalYawAtStep(i) : -1f;
                Debug.Log($"[RotationDrift][{variantName}] step={i} diffAcc_vs_Extract={diffExtract:F6} diffExtract_vs_Unity={diffUnity:F6} yawAcc={yawAcc:F5} yawW={yawW:F5} yawU={yawU:F5} yawTheo={theo:F5}");
                LogQuat("Acc", qAcc); LogQuat("World", wrot);
            }
        }
        Debug.Log($"[RotationDrift][{variantName}][Summary] finalDiffAccExtract={lastExtract:F9} maxAccExtract={maxDiffExtract:F9} avgAccExtract={(sumExtract/samples):F9} finalDiffExtractUnity={lastUnity:F9} maxExtractUnity={maxDiffUnity:F9} avgExtractUnity={(sumUnity/samples):F9}");
        DestroyImmediate(go);
    }

    private float TheoreticalYawAtStep(int step)
    {
        double yaw = (double)step * deltaAngleDeg; yaw %= 360.0; if (yaw < 0) yaw += 360.0; return (float)yaw;
    }

    private void LogQuat(string tag, TSQuaternion q)
    {
        if (!highPrecisionQuaternionLog) return;
        Debug.Log($"[RotationDrift][QuatDump] {tag} q=({(float)q.x.AsFloat():F9},{(float)q.y.AsFloat():F9},{(float)q.z.AsFloat():F9},{(float)q.w.AsFloat():F9})");
    }

    // 修改 RunVariant 以支持 yaw 诊断输出
    private void RunVariant(string variantName, Action<FixedNode3D, Transform, TSQuaternion, Quaternion> stepFn, bool withYawDiag=false)
    {
        var go = new GameObject($"Unity_{variantName}");
        var tr = go.transform; tr.position = Vector3.zero; tr.rotation = Quaternion.identity; tr.localScale = Vector3.one;
        var node = new FixedNode3D(); node.SetLocalTRS(new TSVector(0,0,0), TSQuaternion.identity, TSVector.one);
        TSQuaternion qDeltaTS = TSAngleAxis(deltaAngleDeg, new TSVector(0,1,0));
        Quaternion qDeltaUnity = Quaternion.AngleAxis(deltaAngleDeg, Vector3.up);
        float maxAngle = 0f; float sum = 0f; int sampleCount = 0; float lastAngle = 0f;
        for (int i=1;i<=iterations;i++)
        {
            stepFn(node, tr, qDeltaTS, qDeltaUnity);
            TSQuaternion nRot = node.worldRotation;
            float diff = AngleBetween(nRot, tr.rotation);
            if (diff > maxAngle) maxAngle = diff; sum += diff; sampleCount++; lastAngle = diff;
            if (i % logInterval == 0)
            {
                float yawTS = withYawDiag ? ExtractYawDegreeTS(nRot) : 0f;
                float yawU = withYawDiag ? ExtractYawDegreeUnity(tr.rotation) : 0f;
                float yawTheo = enableTheoreticalYaw ? TheoreticalYawAtStep(i) : -1f;
                if (withYawDiag && logPerStepYawSample)
                    Debug.Log($"[RotationDrift][{variantName}] step={i} diff={diff:F6} yawTS={yawTS:F4} yawU={yawU:F4} yawTheo={yawTheo:F4} yawDiffTSTheo={(yawTS - yawTheo):F4} yawDiffUTheo={(yawU - yawTheo):F4}");
                else
                    Debug.Log($"[RotationDrift][{variantName}] step={i} diff={diff:F6} (max={maxAngle:F6}) yawTheo={yawTheo:F4}");
            }
        }
        _results.Add(new ResultRow{ name=variantName, finalAngle=lastAngle, maxAngle=maxAngle, avgAngle=sum/sampleCount, samples=sampleCount });
        DestroyImmediate(go);
    }

    private void RunAll()
    {
        _results.Clear(); LogHeader();
        if (runVariantF) RunVariantDeltaCheck("VariantF_DeltaSingleStep");
        if (runVariantA) RunVariant("VariantA_EulerIncrement", (node, tr, qDeltaTS, qDeltaUnity) => { tr.Rotate(0f, deltaAngleDeg, 0f, Space.Self); var e = node.localEuler; e.y += (FP)(deltaAngleDeg * Mathf.Deg2Rad); node.localEuler = e; }, withYawDiag:true);
        if (runVariantD) RunVariant("VariantD_PureQuaternionChain_LeftMult", (node, tr, qDeltaTS, qDeltaUnity) => { node.localRotation = qDeltaTS * node.localRotation; tr.rotation = qDeltaUnity * tr.rotation; }, withYawDiag:true);
        if (runVariantE) RunVariant("VariantE_PureQuaternionChain_RightMult", (node, tr, qDeltaTS, qDeltaUnity) => { node.localRotation = node.localRotation * qDeltaTS; tr.rotation = tr.rotation * qDeltaUnity; }, withYawDiag:true);
        if (runVariantB) RunVariant("VariantB_LocalRotationThenEuler", (node, tr, qDeltaTS, qDeltaUnity) => { node.localRotation = qDeltaTS * node.localRotation; tr.rotation = qDeltaUnity * tr.rotation; var e = node.localEuler; e.y += (FP)(deltaAngleDeg * Mathf.Deg2Rad); node.localEuler = e; tr.Rotate(0f, deltaAngleDeg, 0f, Space.Self); });
        if (runVariantC) RunVariant("VariantC_SetLocalQuaternionThenEuler", (node, tr, qDeltaTS, qDeltaUnity) => { var newQ = qDeltaTS * node.localRotation; node.SetLocalQuaternion(newQ); tr.rotation = qDeltaUnity * tr.rotation; var e = node.localEuler; e.y += (FP)(deltaAngleDeg * Mathf.Deg2Rad); node.localEuler = e; tr.Rotate(0f, deltaAngleDeg, 0f, Space.Self); });
        if (runVariantG) { RunVariantLargeStep("VariantG_LargeStep_1deg", 1f); RunVariantLargeStep("VariantG_LargeStep_5deg", 5f); }
        if (runVariantH) RunVariantH();
        if (printFinalSummary) { PrintSummary(); }
    }

    private void PrintSummary()
    {
        // 打印一个简单的 JSON 风格总结，方便复制分析
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("[RotationDrift][Summary] \n");
        sb.Append("[\n");
        for (int i=0;i<_results.Count;i++)
        {
            var r = _results[i];
            sb.AppendFormat("  {{ \"name\": \"{0}\", \"finalAngleDeg\": {1:F8}, \"maxAngleDeg\": {2:F8}, \"avgAngleDeg\": {3:F8}, \"samples\": {4} }}{5}\n",
                r.name, r.finalAngle, r.maxAngle, r.avgAngle, r.samples, i==_results.Count-1?"":" ,");
        }
        sb.Append("]");
        Debug.Log(sb.ToString());
    }

    private void Start()
    {
        if (autoRunOnStart)
        {
            RunAll();
        }
    }

    [ContextMenu("Run Drift Experiment Now")] private void RunFromContextMenu() { RunAll(); }
}
