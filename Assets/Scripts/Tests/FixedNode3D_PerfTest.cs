#if false
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
 * FixedNode3D 性能测试（无 shear / 隐形 shear）
 * 用法：
 * 1. 挂到一个空物体。
 * 2. 运行时按 play；或勾选 autoRun。
 * 3. 观察 Console 输出的统计结果。
 */
public class FixedNode3D_PerfTest : MonoBehaviour
{
    [Header("Hierarchy")]
    public int nodeCount = 10000;
    public enum BuildMode { RandomTree, Chain, Wide }
    public BuildMode buildMode = BuildMode.RandomTree;
    [Range(0f,1f)] public float randomParentBias = 0.7f; // RandomTree 父选择倾向较靠前
    public int wideBranching = 32; // Wide 模式每层最大子数

    [Header("Iterations")]
    public int iterations = 200;
    public int opsPerNode_SetLocalTRS = 1;
    public int opsPerNode_QueryWorld = 2;
    public int opsPerNode_SpaceConvert = 1;
    public int reparentOpsPerIteration = 50;
    public bool enableReparent = true;
    public bool randomRotationEveryChange = true;

    [Header("Uniform Scale")]
    public float[] scaleSet = new float[] { 0.5f, 1f, 2f, 4f };
    public bool randomScalePerOp = true;
    public float fallbackUniformScale = 1f;

    [Header("Shear Detection (Sampling)")]
    [Tooltip("0 关闭; 1 每帧全量; 建议 0.01~0.1")]
    [Range(0f,1f)] public float shearSampleRate = 0.02f;
    public float shearDotEps = 1e-4f;
    public bool stopOnShear = true;

    [Header("Run Control")]
    public bool autoRun = true;
    public KeyCode triggerKey = KeyCode.P;
    public bool logPerIteration = false;

    [Header("Random Ranges (Local TRS)")]
    public Vector3 posMin = new Vector3(-5,-3,-5);
    public Vector3 posMax = new Vector3( 5, 3, 5);
    public Vector3 eulerMinDeg = Vector3.zero;
    public Vector3 eulerMaxDeg = new Vector3(360,360,360);

    [Header("Seed")]
    public int randomSeed = 12345;
    public bool useSeed = true;

    [Header("Reparent Safety / Validation")]
    [Tooltip("禁用以隔离 Reparent 是否导致崩溃")] public bool reparentSafeMode = true;
    [Tooltip("打印阻止成环次数")] public bool logCyclePrevention = true;
    [Tooltip("周期性进行层级无环验证 (DFS) 有一定开销")] public bool validateCycles = false;
    [Tooltip("多少个 iteration 做一次 ValidateNoCycles")] public int validateEveryNIterations = 20;

    private struct Node
    {
        public FixedNode3D fn;
    }

    private List<Node> nodes = new List<Node>(1024);
    private System.Random prng;

    // Timers
    private Stopwatch swTotal = new Stopwatch();
    private long timeSetLocal, timeQueryWorld, timeSpaceConvert, timeReparent, timeShearCheck;

#if UNITY_2020_2_OR_NEWER
    private static readonly ProfilerMarker PM_SetLocal = new ProfilerMarker("FN.SetLocalTRS");
    private static readonly ProfilerMarker PM_QueryWorld = new ProfilerMarker("FN.QueryWorld");
    private static readonly ProfilerMarker PM_Space = new ProfilerMarker("FN.SpaceConv");
    private static readonly ProfilerMarker PM_Reparent = new ProfilerMarker("FN.Reparent");
    private static readonly ProfilerMarker PM_Shear = new ProfilerMarker("FN.ShearCheck");
#endif

    private bool running;
    private int iter;

    void Start()
    {
        InitRandom();
        BuildHierarchy();
        if (autoRun) { StartRun(); }
    }

    void Update()
    {
        if (Input.GetKeyDown(triggerKey))
        {
            StartRun();
        }

        if (!running) return;

        if (iter >= iterations)
        {
            running = false;
            PrintSummary();
            return;
        }

        RunOneIteration(iter);
        iter++;
    }

    private void StartRun()
    {
        ResetStats();
        iter = 0;
        running = true;
        swTotal.Start();
        if (enableReparent) FixedNode3D.ResetCycleStats();
    }

    private void InitRandom()
    {
        if (useSeed) prng = new System.Random(randomSeed);
        else prng = new System.Random();
    }

    private float Rand01() => (float)prng.NextDouble();
    private float RandRange(float a, float b) => a + (b - a) * Rand01();

    private float PickUniformScale()
    {
        if (!randomScalePerOp)
        {
            if (scaleSet != null && scaleSet.Length > 0) return scaleSet[0];
            return fallbackUniformScale;
        }
        if (scaleSet == null || scaleSet.Length == 0) return fallbackUniformScale;
        int idx = prng.Next(scaleSet.Length);
        return scaleSet[idx];
    }

    private void BuildHierarchy()
    {
        nodes.Clear();
        nodes.Capacity = nodeCount;

        // root
        var root = new FixedNode3D(null, true);
        nodes.Add(new Node { fn = root });

        for (int i = 1; i < nodeCount; i++)
        {
            FixedNode3D parent;
            switch (buildMode)
            {
                case BuildMode.Chain:
                    parent = nodes[i - 1].fn;
                    break;
                case BuildMode.Wide:
                {
                    // 近似层状: 父索引 ~ floor((i-1)/wideBranching)
                    int pIndex = (i - 1) / Mathf.Max(1, wideBranching);
                    parent = nodes[pIndex].fn;
                    break;
                }
                default:
                case BuildMode.RandomTree:
                {
                    int pIdx;
                    if (Rand01() < randomParentBias)
                        pIdx = prng.Next(i); // 任意已有节点
                    else
                        pIdx = i - 1;
                    parent = nodes[pIdx].fn;
                    break;
                }
            }
            var n = new FixedNode3D(parent, keepWorld: true);
            nodes.Add(new Node { fn = n });
        }
    }

    private void ResetStats()
    {
        swTotal.Reset();
        timeSetLocal = timeQueryWorld = timeSpaceConvert = timeReparent = timeShearCheck = 0;
    }

    private void RunOneIteration(int iterationIndex)
    {
        var sw = Stopwatch.StartNew();

        // 1) 批量本地 TRS 更新
        if (opsPerNode_SetLocalTRS > 0)
        {
#if UNITY_2020_2_OR_NEWER
            PM_SetLocal.Begin();
#endif
            var subSw = Stopwatch.StartNew();
            for (int pass = 0; pass < opsPerNode_SetLocalTRS; pass++)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var fn = nodes[i].fn;
                    // 生成均匀 scale
                    float s = PickUniformScale();
                    TSVector scale = new TSVector((FP)s, (FP)s, (FP)s);

                    // local position
                    TSVector pos = new TSVector(
                        (FP)RandRange(posMin.x, posMax.x),
                        (FP)RandRange(posMin.y, posMax.y),
                        (FP)RandRange(posMin.z, posMax.z));

                    // rotation
                    TSQuaternion rot;
                    if (randomRotationEveryChange)
                    {
                        // 随机欧拉再转四元
                        Vector3 eDeg = new Vector3(
                            RandRange(eulerMinDeg.x, eulerMaxDeg.x),
                            RandRange(eulerMinDeg.y, eulerMaxDeg.y),
                            RandRange(eulerMinDeg.z, eulerMaxDeg.z));
                        Vector3 eRad = eDeg * Mathf.Deg2Rad;
                        TSVector eTS = new TSVector((FP)eRad.x, (FP)eRad.y, (FP)eRad.z);
                        // 通过 localEuler 设置，内部保证无 shear（均匀缩放）
                        fn.localEuler = eTS;
                    }
                    rot = fn.localRotation; // 取刚才的

                    // 设置 scale/pos
                    fn.localScale = scale;
                    fn.localPosition = pos;
                }
            }
            subSw.Stop();
            timeSetLocal += subSw.ElapsedTicks;
#if UNITY_2020_2_OR_NEWER
            PM_SetLocal.End();
#endif
        }

        // 2) 查询世界数据
        if (opsPerNode_QueryWorld > 0)
        {
#if UNITY_2020_2_OR_NEWER
            PM_QueryWorld.Begin();
#endif
            var subSw = Stopwatch.StartNew();
            for (int pass = 0; pass < opsPerNode_QueryWorld; pass++)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var fn = nodes[i].fn;
                    _ = fn.worldPosition;
                    _ = fn.worldRotation;
                    _ = fn.worldScale;
                    _ = fn.globalMatrix;
                }
            }
            subSw.Stop();
            timeQueryWorld += subSw.ElapsedTicks;
#if UNITY_2020_2_OR_NEWER
            PM_QueryWorld.End();
#endif
        }

        // 3) 空间点/向量转换
        if (opsPerNode_SpaceConvert > 0)
        {
#if UNITY_2020_2_OR_NEWER
            PM_Space.Begin();
#endif
            var subSw = Stopwatch.StartNew();
            for (int pass = 0; pass < opsPerNode_SpaceConvert; pass++)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var fn = nodes[i].fn;
                    TSVector p = new TSVector((FP)0.1f, (FP)0.2f, -(FP)0.3f);
                    var wp = fn.TransformPointLocalToWorld(p);
                    var lp = fn.TransformPointWorldToLocal(wp);
                    var v = fn.LocalVectorToWorld(p);
                    var lv = fn.WorldVectorToLocal(v);
                    // 防止被编译器过度优化
                    if (lp.x == (FP)99999 || lv.y == (FP)99999) Debug.Log("Impossible");
                }
            }
            subSw.Stop();
            timeSpaceConvert += subSw.ElapsedTicks;
#if UNITY_2020_2_OR_NEWER
            PM_Space.End();
#endif
        }

        // 4) Reparent（可选）
        if (enableReparent && !reparentSafeMode && reparentOpsPerIteration > 0 && nodeCount > 2)
        {
#if UNITY_2020_2_OR_NEWER
            PM_Reparent.Begin();
#endif
            var subSw = Stopwatch.StartNew();
            try
            {
                for (int r = 0; r < reparentOpsPerIteration; r++)
                {
                    int childIdx = 1 + prng.Next(nodeCount - 1); // 避免 root
                    var child = nodes[childIdx].fn;

                    bool keepWorld = (prng.Next() & 1) == 0; // 50%
                    bool toNull = Rand01() < 0.15f; // 适当降低变 root 频率
                    FixedNode3D newParent = null;

                    if (!toNull)
                    {
                        // 增加尝试：避免直接挑选当前父，稍微减少重复操作
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            int cand = prng.Next(nodeCount);
                            if (cand == childIdx) continue;
                            newParent = nodes[cand].fn;
                            if (newParent == child.parent) continue; // 换个父
                            // 成环会在 SetParent 内部被阻止
                            break;
                        }
                    }

                    child.SetParent(toNull ? null : newParent, keepWorld);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Reparent exception caught: {ex.Message}\n{ex.StackTrace}\n自动禁用后续 reparent 以继续测试");
                enableReparent = false;
            }
            subSw.Stop();
            timeReparent += subSw.ElapsedTicks;
#if UNITY_2020_2_OR_NEWER
            PM_Reparent.End();
#endif
        }

        // 5) 采样 Shear 检测
        if (shearSampleRate > 0f)
        {
#if UNITY_2020_2_OR_NEWER
            PM_Shear.Begin();
#endif
            var subSw = Stopwatch.StartNew();
            int sampleCount = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Rand01() > shearSampleRate) continue;
                sampleCount++;
                var gm = nodes[i].fn.globalMatrix;

                // 列向量
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
                    Debug.LogError($"[ShearDetected] iter={iterationIndex} node={i} d01={d01:E6} d02={d02:E6} d12={d12:E6}");
                    if (stopOnShear)
                    {
                        running = false;
                        PrintSummary();
                        return;
                    }
                }
            }
            subSw.Stop();
            timeShearCheck += subSw.ElapsedTicks;
#if UNITY_2020_2_OR_NEWER
            PM_Shear.End();
#endif
        }

        // 6) 周期性无环验证 & cycle 防护统计输出
        if (logCyclePrevention && enableReparent && iter % 50 == 0)
        {
            int prevented = FixedNode3D.cyclePreventedCount;
            if (prevented > 0)
                Debug.Log($"[CycleStats] prevented={prevented} at iter={iter}");
        }
        if (validateCycles && (iterationIndex % Math.Max(1, validateEveryNIterations) == 0))
        {
            if (!FixedNode3D.ValidateNoCycles(nodes[0].fn, out string err))
            {
                Debug.LogError($"[ValidateNoCycles] FAILED: {err} at iter={iterationIndex}. 停止运行。");
                running = false;
                PrintSummary();
                return;
            }
        }

        sw.Stop();

        if (logPerIteration)
        {
            Debug.Log($"Iter {iterationIndex} elapsed(ms)={sw.Elapsed.TotalMilliseconds:F3}");
        }
    }

    private void PrintSummary()
    {
        swTotal.Stop();
        double tick2ms = 1000.0 / Stopwatch.Frequency;
        long totalTicks = timeSetLocal + timeQueryWorld + timeSpaceConvert + timeReparent + timeShearCheck;
        int prevented = FixedNode3D.cyclePreventedCount;

        int totalSetOps = nodeCount * opsPerNode_SetLocalTRS * iterations;
        int totalQueryOps = nodeCount * opsPerNode_QueryWorld * iterations;
        int totalSpaceOps = nodeCount * opsPerNode_SpaceConvert * iterations;
        int totalReparents = (enableReparent ? reparentOpsPerIteration * iterations : 0);

        Debug.Log(
            "==== FixedNode3D 性能测试结果 ====\n" +
            $"Nodes={nodeCount} Iterations={iterations}\n" +
            $"BuildMode={buildMode} RandomSeedUsed={(useSeed ? randomSeed.ToString() : "Random")}\n" +
            $"SetLocalTRS: {totalSetOps} ops, time={timeSetLocal * tick2ms:F3} ms, avg/ops={(timeSetLocal*tick2ms/Math.Max(1,totalSetOps))*1e6:F2} ns\n" +
            $"QueryWorld: {totalQueryOps} ops, time={timeQueryWorld * tick2ms:F3} ms, avg/ops={(timeQueryWorld*tick2ms/Math.Max(1,totalQueryOps))*1e6:F2} ns\n" +
            $"SpaceConvert: {totalSpaceOps} ops, time={timeSpaceConvert * tick2ms:F3} ms, avg/ops={(timeSpaceConvert*tick2ms/Math.Max(1,totalSpaceOps))*1e6:F2} ns\n" +
            $"Reparent: {totalReparents} ops, time={timeReparent * tick2ms:F3} ms, avg/ops={(timeReparent*tick2ms/Math.Max(1,totalReparents))*1e6:F2} ns\n" +
            $"ShearCheck(time sampled)={timeShearCheck * tick2ms:F3} ms (sampleRate={shearSampleRate})\n" +
            (enableReparent ? $"CyclePreventedCount={prevented}\n" : "") +
            $"Total Accounted = {totalTicks * tick2ms:F3} ms  WallClock = {swTotal.Elapsed.TotalMilliseconds:F3} ms\n" +
            "================================");
    }

    private void OnValidate()
    {
        nodeCount = Mathf.Max(1, nodeCount);
        iterations = Mathf.Max(1, iterations);
        opsPerNode_SetLocalTRS = Mathf.Max(0, opsPerNode_SetLocalTRS);
        opsPerNode_QueryWorld = Mathf.Max(0, opsPerNode_QueryWorld);
        opsPerNode_SpaceConvert = Mathf.Max(0, opsPerNode_SpaceConvert);
        reparentOpsPerIteration = Mathf.Max(0, reparentOpsPerIteration);
        wideBranching = Mathf.Max(1, wideBranching);
        shearDotEps = Mathf.Max(1e-8f, shearDotEps);
        validateEveryNIterations = Mathf.Max(1, validateEveryNIterations);
    }
}
#endif