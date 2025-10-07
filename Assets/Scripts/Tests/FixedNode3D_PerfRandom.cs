using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Core.TrueSync;
using Debug = UnityEngine.Debug;
#if UNITY_2020_2_OR_NEWER
using Unity.Profiling;
#endif

/*
 * FixedNode3D 随机操作性能测试 (精确版)：
 * 目标：
 *  - 随机交织各种操作，不按批次分段。
 *  - 数据随机值准备阶段不计入操作耗时。
 *  - 可选按概率抽样测量单个操作类型的平均耗时，降低计时扰动。
 *  - 支持权重配置，控制不同操作出现频率。
 *  - Reparent 含成环防护；可开关。
 *  - 仅使用均匀缩放，避免 shear；可选允许非均匀（默认关闭）。
 * 用法：
 *  1. 挂到一个空物体。
 *  2. 调整 nodeCount / opsPerIteration / iterations 与各操作权重。
 *  3. 点击 Play 或 autoRun。
 *  4. 结束后查看 Console 输出。
 */
public class FixedNode3D_PerfRandom : MonoBehaviour
{
    [Header("Hierarchy")]
    public int nodeCount = 5000;
    public enum BuildMode { RandomTree, Chain, Wide }
    public BuildMode buildMode = BuildMode.RandomTree;
    [Range(0f,1f)] public float randomParentBias = 0.7f; // RandomTree 父选择倾向较靠前
    public int wideBranching = 32;

    [Header("Iterations / Ops")]
    public int iterations = 120; // 帧数
    public int opsPerIteration = 20000; // 每帧操作数（越大越重）

    [Serializable]
    public class OperationWeights
    {
        [Range(0,1)] public float setPosition = 0.10f;
        [Range(0,1)] public float setRotationEuler = 0.10f;
        [Range(0,1)] public float setScaleUniform = 0.05f;
        [Range(0,1)] public float setTRS = 0.05f; // 同时改 pos+rot+scale
        [Range(0,1)] public float queryWorld = 0.30f; // worldPosition/Rotation/Scale/Matrix
        [Range(0,1)] public float transformPoint = 0.15f;
        [Range(0,1)] public float transformVector = 0.15f;
        [Range(0,1)] public float reparent = 0.10f;
        // 可选：只测 globalMatrix (baseline)；若启用，则 QueryWorld 操作只访问 globalMatrix
        public bool queryWorldMatrixOnly = false;
        public float Sum() => setPosition + setRotationEuler + setScaleUniform + setTRS + queryWorld + transformPoint + transformVector + reparent;
    }
    public OperationWeights weights = new OperationWeights();

    [Header("Scale Settings")]
    public float[] uniformScaleSet = new float[] { 0.5f, 1f, 2f, 4f };
    public bool randomPickScale = true;
    public float fallbackUniformScale = 1f;
    public bool allowNonUniformScale = false; // 若开启，仍用于 setScaleUniform 操作，但生成随机 xyz (仍可导致 shear，默认 false)。

    [Header("Random Ranges (Local TRS)")]
    public Vector3 posMin = new Vector3(-5,-3,-5);
    public Vector3 posMax = new Vector3( 5, 3, 5);
    public Vector3 eulerMinDeg = Vector3.zero;
    public Vector3 eulerMaxDeg = new Vector3(360,360,360);

    [Header("Space Convert Test Data")]
    public Vector3 testPointMin = new Vector3(-1,-1,-1);
    public Vector3 testPointMax = new Vector3( 1, 1, 1);

    [Header("Reparent")]
    public bool enableReparent = true;
    public float reparentToNullProbability = 0.10f;
    public float reparentKeepWorldProbability = 0.5f;

    [Header("Sampling Timing")]
    public bool enablePerOpSampling = true;
    [Range(0f,1f)] public float perOpSampleRate = 0.01f; // 抽样比例

    [Header("Run Control")]
    public bool autoRun = true;
    public KeyCode triggerKey = KeyCode.R;
    public bool logPerIteration = false;

    [Header("Seed")]
    public int randomSeed = 12345;
    public bool useSeed = true;

    [Header("Shear Check (Optional)")]
    public bool enableShearSample = false;
    [Range(0,1)] public float shearSampleRate = 0.02f;
    public float shearDotEps = 1e-4f;

    private enum OpType : byte
    {
        SetPosition,
        SetRotationEuler,
        SetScaleUniform,
        SetTRS,
        QueryWorld,
        TransformPoint,
        TransformVector,
        Reparent
    }

    private class Op
    {
        public OpType type;
        public TSVector v1;   // pos / euler(rad) / scale / point / vector
        public TSVector v2;   // extra (for TRS: euler; for scale maybe unused)
        public TSVector v3;   // extra (for TRS: scale)
        public int i1;        // child index (reparent or target node)
        public int i2;        // parent index for reparent (-1 root)
        public byte flags;    // bit0 keepWorld
    }

    private List<Op> _ops; // 预生成操作列表（每帧重建）
    private List<FixedNode3D> _nodes;
    private System.Random _prng;

    // 累计计数
    private long _totalAppliedOps;

    // 每类操作抽样累计 ticks 和次数
    private long[] _sampleTicks = new long[Enum.GetValues(typeof(OpType)).Length];
    private long[] _sampleCounts = new long[Enum.GetValues(typeof(OpType)).Length];

    // 总操作执行计时（不含准备）
    private Stopwatch _swApply = new Stopwatch();
    private long _accumApplyTicks;

#if UNITY_2020_2_OR_NEWER
    private static readonly ProfilerMarker PM_AllApply = new ProfilerMarker("FN_Rand.ApplyAll");
#endif

    private bool _running;
    private int _iter;

    void Start()
    {
        InitRandom();
        BuildHierarchy();
        _ops = new List<Op>(opsPerIteration);
        if (autoRun) StartRun();
    }

    void Update()
    {
        if (Input.GetKeyDown(triggerKey)) StartRun();
        if (!_running) return;
        if (_iter >= iterations)
        {
            _running = false;
            PrintSummary();
            return;
        }
        RunOneIteration();
        _iter++;
    }

    private void StartRun()
    {
        _iter = 0;
        _running = true;
        _accumApplyTicks = 0;
        _totalAppliedOps = 0;
        Array.Clear(_sampleTicks, 0, _sampleTicks.Length);
        Array.Clear(_sampleCounts, 0, _sampleCounts.Length);
        if (enableReparent) FixedNode3D.ResetCycleStats();
#if FIXEDNODE3D_PROFILE
        FixedNode3D.ResetStats();
#endif
    }

    private void InitRandom()
    {
        _prng = useSeed ? new System.Random(randomSeed) : new System.Random();
    }

    private float Rand01() => (float)_prng.NextDouble();
    private float RandRange(float a, float b) => a + (b - a) * Rand01();

    private TSVector RandPos()
    {
        return new TSVector((FP)RandRange(posMin.x, posMax.x), (FP)RandRange(posMin.y, posMax.y), (FP)RandRange(posMin.z, posMax.z));
    }
    private TSVector RandEulerRad()
    {
        Vector3 eDeg = new Vector3(RandRange(eulerMinDeg.x, eulerMaxDeg.x), RandRange(eulerMinDeg.y, eulerMaxDeg.y), RandRange(eulerMinDeg.z, eulerMaxDeg.z));
        Vector3 eRad = eDeg * Mathf.Deg2Rad;
        return new TSVector((FP)eRad.x, (FP)eRad.y, (FP)eRad.z);
    }
    private TSVector RandUniformScale()
    {
        float s;
        if (uniformScaleSet != null && uniformScaleSet.Length > 0 && randomPickScale)
            s = uniformScaleSet[_prng.Next(uniformScaleSet.Length)];
        else if (uniformScaleSet != null && uniformScaleSet.Length > 0)
            s = uniformScaleSet[0];
        else s = fallbackUniformScale;
        if (!allowNonUniformScale)
            return new TSVector((FP)s, (FP)s, (FP)s);
        else
            return new TSVector((FP)RandRange(s * 0.8f, s * 1.2f), (FP)RandRange(s * 0.8f, s * 1.2f), (FP)RandRange(s * 0.8f, s * 1.2f));
    }
    private TSVector RandPoint()
    {
        return new TSVector((FP)RandRange(testPointMin.x, testPointMax.x), (FP)RandRange(testPointMin.y, testPointMax.y), (FP)RandRange(testPointMin.z, testPointMax.z));
    }

    private void BuildHierarchy()
    {
        _nodes = new List<FixedNode3D>(nodeCount);
        var root = new FixedNode3D(null, true);
        _nodes.Add(root);
        for (int i = 1; i < nodeCount; i++)
        {
            FixedNode3D parent;
            switch (buildMode)
            {
                case BuildMode.Chain:
                    parent = _nodes[i - 1];
                    break;
                case BuildMode.Wide:
                {
                    int pIndex = (i - 1) / Mathf.Max(1, wideBranching);
                    parent = _nodes[pIndex];
                    break;
                }
                default:
                case BuildMode.RandomTree:
                {
                    int pIdx = (Rand01() < randomParentBias) ? _prng.Next(i) : i - 1;
                    parent = _nodes[pIdx];
                    break;
                }
            }
            _nodes.Add(new FixedNode3D(parent, keepWorld: true));
        }
    }

    private void GenerateOperations()
    {
        _ops.Clear();
        _ops.Capacity = Math.Max(_ops.Capacity, opsPerIteration);
        float total = weights.Sum();
        if (total <= 0f) total = 1f; // 避免除 0

        for (int k = 0; k < opsPerIteration; k++)
        {
            float r = Rand01() * total;
            float accum = 0f;
            OpType t;
            accum += weights.setPosition; if (r < accum) { t = OpType.SetPosition; goto SELECTED; }
            accum += weights.setRotationEuler; if (r < accum) { t = OpType.SetRotationEuler; goto SELECTED; }
            accum += weights.setScaleUniform; if (r < accum) { t = OpType.SetScaleUniform; goto SELECTED; }
            accum += weights.setTRS; if (r < accum) { t = OpType.SetTRS; goto SELECTED; }
            accum += weights.queryWorld; if (r < accum) { t = OpType.QueryWorld; goto SELECTED; }
            accum += weights.transformPoint; if (r < accum) { t = OpType.TransformPoint; goto SELECTED; }
            accum += weights.transformVector; if (r < accum) { t = OpType.TransformVector; goto SELECTED; }
            t = OpType.Reparent;
        SELECTED:
            Op op = new Op { type = t };
            switch (t)
            {
                case OpType.SetPosition:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandPos();
                    break;
                case OpType.SetRotationEuler:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandEulerRad();
                    break;
                case OpType.SetScaleUniform:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandUniformScale();
                    break;
                case OpType.SetTRS:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandPos();
                    op.v2 = RandEulerRad();
                    op.v3 = RandUniformScale();
                    break;
                case OpType.QueryWorld:
                    op.i1 = _prng.Next(nodeCount);
                    break;
                case OpType.TransformPoint:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandPoint();
                    break;
                case OpType.TransformVector:
                    op.i1 = _prng.Next(nodeCount);
                    op.v1 = RandPoint();
                    break;
                case OpType.Reparent:
                    if (!enableReparent || nodeCount < 3) { t = OpType.QueryWorld; op.type = t; op.i1 = _prng.Next(nodeCount); break; }
                    op.i1 = 1 + _prng.Next(nodeCount - 1); // 避免 root 自身
                    bool toNull = Rand01() < reparentToNullProbability;
                    op.i2 = toNull ? -1 : _prng.Next(nodeCount);
                    if (op.i2 == op.i1) op.i2 = -1; // 简化
                    bool keepW = Rand01() < reparentKeepWorldProbability;
                    op.flags = (byte)(keepW ? 1 : 0);
                    break;
            }
            _ops.Add(op);
        }
    }

    private void ApplyOperations()
    {
#if UNITY_2020_2_OR_NEWER
        PM_AllApply.Begin();
#endif
        _swApply.Restart();
        var opsArr = _ops;
        int count = opsArr.Count;
        bool sample = enablePerOpSampling && perOpSampleRate > 0f;
        for (int i = 0; i < count; i++)
        {
            Op op = opsArr[i];
            FixedNode3D n = _nodes[op.i1];
            bool doSample = sample && Rand01() < perOpSampleRate;
            long ts0 = 0;
            if (doSample) ts0 = Stopwatch.GetTimestamp();
            switch (op.type)
            {
                case OpType.SetPosition:
                    n.localPosition = op.v1; break;
                case OpType.SetRotationEuler:
                    n.localEuler = op.v1; break;
                case OpType.SetScaleUniform:
                    n.localScale = op.v1; break;
                case OpType.SetTRS:
                    n.localEuler = op.v2; n.localScale = op.v3; n.localPosition = op.v1; break;
                case OpType.QueryWorld:
                    if (weights.queryWorldMatrixOnly)
                    {
                        _ = n.globalMatrix; // baseline: 只触发 world 更新 + matrix 返回
                    }
                    else
                    {
                        _ = n.worldPosition; _ = n.worldRotation; _ = n.worldScale; _ = n.globalMatrix;
                    }
                    break;
                case OpType.TransformPoint:
                    {
                        var wp = n.TransformPointLocalToWorld(op.v1);
                        var lp = n.TransformPointWorldToLocal(wp);
                        if (lp.x == (FP)99999) Debug.Log("Impossible");
                    }
                    break;
                case OpType.TransformVector:
                    {
                        var wv = n.LocalVectorToWorld(op.v1);
                        var lv = n.WorldVectorToLocal(wv);
                        if (lv.x == (FP)99999) Debug.Log("Impossible");
                    }
                    break;
                case OpType.Reparent:
                    {
                        FixedNode3D newParent = op.i2 < 0 ? null : _nodes[op.i2];
                        bool keepWorld = (op.flags & 1) != 0;
                        n.SetParent(newParent, keepWorld);
                    }
                    break;
            }
            if (doSample)
            {
                long dt = Stopwatch.GetTimestamp() - ts0;
                _sampleTicks[(int)op.type] += dt;
                _sampleCounts[(int)op.type]++;
            }
        }
        _swApply.Stop();
        _accumApplyTicks += _swApply.ElapsedTicks;
        _totalAppliedOps += count;
#if UNITY_2020_2_OR_NEWER
        PM_AllApply.End();
#endif
    }

    private void ShearSampleIfNeeded()
    {
        if (!enableShearSample || shearSampleRate <= 0f) return;
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (Rand01() > shearSampleRate) continue;
            var gm = _nodes[i].globalMatrix;
            Vector3 c0 = new Vector3((float)gm.m00, (float)gm.m10, (float)gm.m20);
            Vector3 c1 = new Vector3((float)gm.m01, (float)gm.m11, (float)gm.m21);
            Vector3 c2 = new Vector3((float)gm.m02, (float)gm.m12, (float)gm.m22);
            float l0 = c0.magnitude; if (l0 < 1e-20f) l0 = 1e-20f;
            float l1 = c1.magnitude; if (l1 < 1e-20f) l1 = 1e-20f;
            float l2 = c2.magnitude; if (l2 < 1e-20f) l2 = 1e-20f;
            c0 /= l0; c1 /= l1; c2 /= l2;
            float d01 = Mathf.Abs(Vector3.Dot(c0, c1));
            float d02 = Mathf.Abs(Vector3.Dot(c0, c2));
            float d12 = Mathf.Abs(Vector3.Dot(c1, c2));
            if (d01 > shearDotEps || d02 > shearDotEps || d12 > shearDotEps)
            {
                Debug.LogError($"[ShearDetected] node={i} d01={d01:E6} d02={d02:E6} d12={d12:E6}");
            }
        }
    }

    private void RunOneIteration()
    {
        // 1) 准备随机操作（不计时）
        GenerateOperations();
        // 2) 应用并计时
        ApplyOperations();
        // 3) 可选 shear 抽样（不计入操作 Apply 时间）
        ShearSampleIfNeeded();
        if (logPerIteration)
        {
            double ms = _swApply.Elapsed.TotalMilliseconds;
            Debug.Log($"Iter={_iter} Ops={_ops.Count} Apply(ms)={ms:F3} Avg(ns/op)={(ms*1e6)/Math.Max(1,_ops.Count):F2}");
        }
    }

    private void PrintSummary()
    {
        double tick2ms = 1000.0 / Stopwatch.Frequency;
        double totalApplyMs = _accumApplyTicks * tick2ms;
        Debug.Log("==== FixedNode3D 随机操作性能结果 ====" +
            $"\nNodes={nodeCount} Iterations={iterations} Ops/Iter={opsPerIteration} TotalOps={_totalAppliedOps}" +
            $"\nApplyTime(ms)={totalApplyMs:F3} Avg(ns/op)={(totalApplyMs*1e6)/Math.Max(1,_totalAppliedOps):F2}" +
            (enableReparent ? $"\nCyclePrevented={FixedNode3D.cyclePreventedCount}" : "") +
            BuildSamplingReport() +
#if FIXEDNODE3D_PROFILE
            BuildFixedNodeStatsReport() +
#endif
            "\n================================");
    }

    private string BuildSamplingReport()
    {
        if (!enablePerOpSampling || perOpSampleRate <= 0f) return "";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("\n-- PerOp Sampling (ns/op) --\n");
        var names = Enum.GetNames(typeof(OpType));
        for (int i = 0; i < names.Length; i++)
        {
            long cnt = _sampleCounts[i];
            if (cnt == 0) continue;
            double ns = (_sampleTicks[i] * 1e9) / Stopwatch.Frequency / cnt;
            sb.Append(names[i]).Append(':').Append(ns.ToString("F2")).Append(" ns (samples=").Append(cnt).Append(")\n");
        }
        return sb.ToString();
    }

    private string BuildFixedNodeStatsReport()
    {
#if !FIXEDNODE3D_PROFILE
        return string.Empty;
#else
        long q = FixedNode3D.Stat_WorldQueryCount;
        long hit = FixedNode3D.Stat_WorldFastPathHit;
        long miss = FixedNode3D.Stat_WorldFastPathMiss;
        double hitRate = q > 0 ? (double)hit / q * 100.0 : 0.0;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("\n-- FixedNode3D Stats --\n");
        sb.Append("WorldQuery=").Append(q)
          .Append(" Recompute=").Append(FixedNode3D.Stat_WorldRecomputeCount)
          .Append(" LocalMatRebuild=").Append(FixedNode3D.Stat_LocalMatrixRecomputeCount)
          .Append(" EulerScaleExtract=").Append(FixedNode3D.Stat_EulerScaleExtractCount)
          .Append("\nFastPath Hit=").Append(hit).Append(" Miss=").Append(miss).Append(" HitRate=").Append(hitRate.ToString("F2")).Append("%")
          .Append(" MaxBatchDepth=").Append(FixedNode3D.Stat_MaxBatchRecomputeDepth)
          .Append("\nRotScaleCacheMiss=").Append(FixedNode3D.Stat_WorldRotScaleCacheMiss)
          .Append(" InverseCacheMiss=").Append(FixedNode3D.Stat_InverseCacheMiss)
          .Append("\nReparent Calls=").Append(FixedNode3D.Stat_ReparentCalls)
          .Append(" KeepWorld=").Append(FixedNode3D.Stat_ReparentKeepWorld)
          .Append(" SkipWorldPath=").Append(FixedNode3D.Stat_ReparentSkipWorld);
        return sb.ToString();
#endif
    }

    private void OnValidate()
    {
        nodeCount = Mathf.Max(1, nodeCount);
        iterations = Mathf.Max(1, iterations);
        opsPerIteration = Mathf.Max(1, opsPerIteration);
        wideBranching = Mathf.Max(1, wideBranching);
        perOpSampleRate = Mathf.Clamp01(perOpSampleRate);
        shearDotEps = Mathf.Max(1e-8f, shearDotEps);
    }
}

