#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Core.TrueSync; // FP, TSVector2, etc.

/*
 * Transform2DFixed 随机操作基准测试
 * 目标: 模拟大量节点在各种常见写/读操作混合下的平均耗时（纳秒/次），用于评估增量更新与缓存策略表现。
 *
 * 思路:
 *   1. 根据配置构建层级 (线性链 / 平铺)，节点数量 N。
 *   2. 预生成 OperationCount 条随机操作 (不计时) —— 包含操作类型、目标节点索引、需要的参数值。
 *   3. 逐条执行并用 Stopwatch.GetTimestamp() 细粒度统计：每条操作单独计时并累加到各自类别。
 *      注意: 单次操作极快，计时噪声较大，通过大量样本取平均；不要用 sw.Restart() 以免额外开销。
 *   4. 多轮 (Warmup + Measure)，丢弃预热数据后给出平均纳秒/操作。
 *
 * 已实现操作类别(可按需扩展):
 *   - SetLocalPos / SetLocalRot / SetLocalScale
 *   - SetLocalTRS (一次性设置全部)
 *   - SetWorldPos / SetWorldRot
 *   - TranslateSelf / RotateSelf (自空间)
 *   - Query: worldPosition / WorldRotationRad / worldScale / worldMatrix / worldMatrixInverse
 *   - TransformPoint / InverseTransformPoint / TransformDirection / InverseTransformDirection
 *   - TransformVector / InverseTransformVector
 *
 * 可配置权重，控制随机分布。权重=0 表示禁用该操作。
 *
 * 结果输出：
 *   [Transform2DFixed RandomOps] (Phase) Nodes=... Ops=... Type=... Count=... AvgNs=...
 *   [Transform2DFixed RandomOps AVERAGE] ... 按操作类型列出最终平均。只显示执行次数>0 的类型。
 *
 * 使用方法：菜单 Tools/Transform2DFixed/Benchmark Random Ops
 */
public static class Transform2DFixedRandomBenchmark
{
    #region 配置与操作定义
    private enum OpType : int
    {
        SetLocalPos,
        SetLocalRot,
        SetLocalScale,
        SetLocalTRS,
        SetWorldPos,
        SetWorldRot,
        TranslateSelf,
        RotateSelf,
        QueryWorldPos,
        QueryWorldRot,
        QueryWorldScale,
        QueryWorldMatrix,
        QueryWorldMatrixInv,
        TransformPoint,
        InverseTransformPoint,
        TransformDirection,
        InverseTransformDirection,
        TransformVector,
        InverseTransformVector,
        COUNT
    }

    [Serializable]
    private struct OpWeights
    {
        public int SetLocalPos;
        public int SetLocalRot;
        public int SetLocalScale;
        public int SetLocalTRS;
        public int SetWorldPos;
        public int SetWorldRot;
        public int TranslateSelf;
        public int RotateSelf;
        public int QueryWorldPos;
        public int QueryWorldRot;
        public int QueryWorldScale;
        public int QueryWorldMatrix;
        public int QueryWorldMatrixInv;
        public int TransformPoint;
        public int InverseTransformPoint;
        public int TransformDirection;
        public int InverseTransformDirection;
        public int TransformVector;
        public int InverseTransformVector;

        public int Total => SetLocalPos + SetLocalRot + SetLocalScale + SetLocalTRS + SetWorldPos + SetWorldRot + TranslateSelf + RotateSelf +
                             QueryWorldPos + QueryWorldRot + QueryWorldScale + QueryWorldMatrix + QueryWorldMatrixInv + TransformPoint + InverseTransformPoint + TransformDirection + InverseTransformDirection + TransformVector + InverseTransformVector;
    }

    private struct Config
    {
        public int NodeCount;
        public int OperationCount;
        public int WarmupIterations;
        public int MeasureIterations;
        public bool LinearChain;
        public int Seed;
        public OpWeights Weights;
        public string Tag; // 用于区分输出
    }

    // Clustered 模式配置：Cycles 次 ( QueryPass -> BatchModify -> QueryPass )
    private struct ClusterConfig
    {
        public int NodeCount;
        public int Cycles;            // 每次包含 2 次查询 + 1 次集中修改
        public int WarmupIterations;
        public int MeasureIterations;
        public bool LinearChain;      // 是否线性链
        public int Seed;              // 随机参数种子（用于生成修改的 TRS）
        public string Tag;            // 输出标签
        public bool UseSetLocalTRS;   // 是否用 SetLocalTRS (否则单独改 Pos/Rot/Scale)
    }

    private struct Op
    {
        public OpType Type;
        public int NodeIndex;     // 作用节点
        public TSVector2 V2a;     // 向量或点
        public TSVector2 V2b;     // 备用向量 (SetLocalTRS 使用 scale)
        public FP Fp0;            // 角度等参数 (弧度或度数按约定)
    }
    #endregion

    #region Sink (防止 JIT/IL 优化消除)
    private static TSVector2 _sinkV2;
    private static FP _sinkFP;
    private static TMatrix2x3 _sinkM;
    private static void Consume(in TSVector2 v) { _sinkV2 = v; }
    private static void Consume(in FP f) { _sinkFP = f; }
    private static void Consume(in TMatrix2x3 m) { _sinkM = m; }
    #endregion

    #region 菜单入口
    [MenuItem("Tools/Transform2DFixed/Benchmark Random Ops")] public static void RunAll()
    {
        var baseWeights = new OpWeights
        {
            SetLocalPos = 5,
            SetLocalRot = 3,
            SetLocalScale = 3,
            SetLocalTRS = 2,
            SetWorldPos = 4,
            SetWorldRot = 3,
            TranslateSelf = 5,
            RotateSelf = 4,
            QueryWorldPos = 6,
            QueryWorldRot = 4,
            QueryWorldScale = 3,
            QueryWorldMatrix = 3,
            QueryWorldMatrixInv = 3,
            TransformPoint = 4,
            InverseTransformPoint = 4,
            TransformDirection = 3,
            InverseTransformDirection = 3,
            TransformVector = 3,
            InverseTransformVector = 3,
        };

        var configs = new[]
        {
            new Config{ Tag="SmallFlat", NodeCount=1_000, OperationCount=200_000, WarmupIterations=1, MeasureIterations=3, LinearChain=false, Seed=123, Weights=baseWeights },
            new Config{ Tag="SmallChain", NodeCount=1_000, OperationCount=200_000, WarmupIterations=1, MeasureIterations=3, LinearChain=true, Seed=124, Weights=baseWeights },
            new Config{ Tag="LargeFlat", NodeCount=10_000, OperationCount=400_000, WarmupIterations=1, MeasureIterations=3, LinearChain=false, Seed=125, Weights=baseWeights },
            new Config{ Tag="LargeChain", NodeCount=10_000, OperationCount=400_000, WarmupIterations=1, MeasureIterations=3, LinearChain=true, Seed=126, Weights=baseWeights },
        };

        foreach (var cfg in configs)
            RunSingle(cfg);
    }

    [MenuItem("Tools/Transform2DFixed/Benchmark Clustered Ops")] public static void RunClusteredAll()
    {
        var clusteredConfigs = new[]
        {
            new ClusterConfig{ Tag="ClusterFlat1k", NodeCount=1_000, Cycles=50, WarmupIterations=1, MeasureIterations=3, LinearChain=false, Seed=321, UseSetLocalTRS=true },
            new ClusterConfig{ Tag="ClusterChain1k", NodeCount=1_000, Cycles=50, WarmupIterations=1, MeasureIterations=3, LinearChain=true, Seed=322, UseSetLocalTRS=true },
            new ClusterConfig{ Tag="ClusterFlat10k", NodeCount=10_000, Cycles=20, WarmupIterations=1, MeasureIterations=3, LinearChain=false, Seed=323, UseSetLocalTRS=true },
            new ClusterConfig{ Tag="ClusterChain10k", NodeCount=10_000, Cycles=20, WarmupIterations=1, MeasureIterations=3, LinearChain=true, Seed=324, UseSetLocalTRS=true },
        };
        foreach (var c in clusteredConfigs)
            RunClusteredSingle(c);
    }
    #endregion

    #region 主流程 (随机混合)
    private static void RunSingle(in Config cfg)
    {
        var freq = (double)Stopwatch.Frequency;
        int opTypeCount = (int)OpType.COUNT;
        double[] sumTicksPerType = new double[opTypeCount];
        int[] totalCountPerType = new int[opTypeCount];

        for (int iter = -cfg.WarmupIterations; iter < cfg.MeasureIterations; iter++)
        {
            var nodes = BuildHierarchy(cfg.NodeCount, cfg.LinearChain);
            var ops = GenerateOps(cfg, nodes.Count);
            long[] ticksPerType = new long[opTypeCount];
            int[] countPerType = new int[opTypeCount];

            foreach (var op in ops)
            {
                var node = nodes[op.NodeIndex];
                long t0 = Stopwatch.GetTimestamp();
                switch (op.Type)
                {
                    case OpType.SetLocalPos: node.localPosition = op.V2a; break;
                    case OpType.SetLocalRot: node.LocalRotationRad = op.Fp0; break;
                    case OpType.SetLocalScale: node.localScale = op.V2a; break;
                    case OpType.SetLocalTRS: node.SetLocalTRSRad(op.V2a, op.Fp0, op.V2b); break;
                    case OpType.SetWorldPos: node.SetWorldPosition(op.V2a); break;
                    case OpType.SetWorldRot: node.SetWorldRotationRad(op.Fp0); break;
                    case OpType.TranslateSelf: node.Translate(op.V2a, Space.Self); break;
                    case OpType.RotateSelf: node.Rotate(op.Fp0 * FP.Rad2Deg); break;
                    case OpType.QueryWorldPos: Consume(node.worldPosition); break;
                    case OpType.QueryWorldRot: Consume(node.WorldRotationRad); break;
                    case OpType.QueryWorldScale: Consume(node.worldScale); break;
                    case OpType.QueryWorldMatrix: Consume(node.worldMatrix); break;
                    case OpType.QueryWorldMatrixInv: Consume(node.worldMatrixInverse); break;
                    case OpType.TransformPoint: Consume(node.TransformPoint(op.V2a)); break;
                    case OpType.InverseTransformPoint: Consume(node.InverseTransformPoint(op.V2a)); break;
                    case OpType.TransformDirection: Consume(node.TransformDirection(op.V2a)); break;
                    case OpType.InverseTransformDirection: Consume(node.InverseTransformDirection(op.V2a)); break;
                    case OpType.TransformVector: Consume(node.TransformVector(op.V2a)); break;
                    case OpType.InverseTransformVector: Consume(node.InverseTransformVector(op.V2a)); break;
                }
                long t1 = Stopwatch.GetTimestamp();
                long dt = t1 - t0;
                ticksPerType[(int)op.Type] += dt;
                countPerType[(int)op.Type]++;
            }

            bool warmup = iter < 0;
            UnityEngine.Debug.Log($"[Transform2DFixed RandomOps] {(warmup?"WARMUP":"MEASURE")} Tag={cfg.Tag} Iter={(warmup?"W":iter.ToString())} Nodes={cfg.NodeCount} Ops={cfg.OperationCount}");

            if (!warmup)
            {
                for (int i = 0; i < opTypeCount; i++)
                {
                    if (countPerType[i] == 0) continue;
                    sumTicksPerType[i] += ticksPerType[i];
                    totalCountPerType[i] += countPerType[i];
                }
            }
        }

        UnityEngine.Debug.Log($"[Transform2DFixed RandomOps AVERAGE] Tag={cfg.Tag} Nodes={cfg.NodeCount} OpsPerIter={cfg.OperationCount} iters={cfg.MeasureIterations}");
        for (int i = 0; i < (int)OpType.COUNT; i++)
        {
            int cnt = totalCountPerType[i];
            if (cnt == 0) continue;
            double nsPerOp = sumTicksPerType[i] * 1_000_000_000.0 / Stopwatch.Frequency / cnt;
            UnityEngine.Debug.Log($"  Type={(OpType)i,-22} Count={cnt,8} Avg={nsPerOp:F2} ns");
        }
    }
    #endregion

    #region 主流程 (Clustered 固定顺序)
    private static void RunClusteredSingle(in ClusterConfig cfg)
    {
        int opTypeCount = (int)OpType.COUNT;
        double[] sumTicksPerType = new double[opTypeCount];
        int[] totalCountPerType = new int[opTypeCount];

        for (int iter = -cfg.WarmupIterations; iter < cfg.MeasureIterations; iter++)
        {
            var nodes = BuildHierarchy(cfg.NodeCount, cfg.LinearChain);
            long[] ticksPerType = new long[opTypeCount];
            int[] countPerType = new int[opTypeCount];
            var rnd = new System.Random(cfg.Seed + iter + 9999); // 每次迭代扰动

            for (int cycle = 0; cycle < cfg.Cycles; cycle++)
            {
                // Pass 1: Queries BEFORE modifications
                foreach (var node in nodes)
                {
                    QueryAll(node, ticksPerType, countPerType);
                }
                // Pass 2:集中修改 (一次 SetLocalTRS 或拆分)
                foreach (var node in nodes)
                {
                    // 生成随机 TRS
                    FP rot = (FP)((rnd.NextDouble() * 2.0 - 1.0) * Math.PI);
                    TSVector2 pos = new TSVector2((FP)((rnd.NextDouble()*2-1)*5.0), (FP)((rnd.NextDouble()*2-1)*5.0));
                    TSVector2 scale = new TSVector2((FP)(1 + (rnd.NextDouble()*2-1)*0.5), (FP)(1 + (rnd.NextDouble()*2-1)*0.5));
                    long t0;
                    if (cfg.UseSetLocalTRS)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        node.SetLocalTRSRad(pos, rot, scale);
                        long dt = Stopwatch.GetTimestamp() - t0;
                        ticksPerType[(int)OpType.SetLocalTRS] += dt;
                        countPerType[(int)OpType.SetLocalTRS]++;
                    }
                    else
                    {
                        t0 = Stopwatch.GetTimestamp(); node.localPosition = pos; long dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.SetLocalPos]+=dt; countPerType[(int)OpType.SetLocalPos]++;
                        t0 = Stopwatch.GetTimestamp(); node.LocalRotationRad = rot; dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.SetLocalRot]+=dt; countPerType[(int)OpType.SetLocalRot]++;
                        t0 = Stopwatch.GetTimestamp(); node.localScale = scale; dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.SetLocalScale]+=dt; countPerType[(int)OpType.SetLocalScale]++;
                    }
                }
                // Pass 3: Queries AFTER modifications (缓存复用明显：同节点第一次查询会触发更新，其后的属性访问应该更快)
                foreach (var node in nodes)
                {
                    QueryAll(node, ticksPerType, countPerType);
                }
            }

            bool warmup = iter < 0;
            UnityEngine.Debug.Log($"[Transform2DFixed ClusteredOps] {(warmup?"WARMUP":"MEASURE")} Tag={cfg.Tag} Iter={(warmup?"W":iter.ToString())} Nodes={cfg.NodeCount} Cycles={cfg.Cycles}");
            if (!warmup)
            {
                for (int i = 0; i < opTypeCount; i++)
                {
                    if (countPerType[i] == 0) continue;
                    sumTicksPerType[i] += ticksPerType[i];
                    totalCountPerType[i] += countPerType[i];
                }
            }
        }

        UnityEngine.Debug.Log($"[Transform2DFixed ClusteredOps AVERAGE] Tag={cfg.Tag} Nodes={cfg.NodeCount} Cycles={cfg.Cycles} iters={cfg.MeasureIterations}");
        for (int i = 0; i < (int)OpType.COUNT; i++)
        {
            int cnt = totalCountPerType[i];
            if (cnt == 0) continue;
            double nsPerOp = sumTicksPerType[i] * 1_000_000_000.0 / Stopwatch.Frequency / cnt;
            UnityEngine.Debug.Log($"  Type={(OpType)i,-22} Count={cnt,8} Avg={nsPerOp:F2} ns");
        }
    }

    private static void QueryAll(Transform2DFixed node, long[] ticksPerType, int[] countPerType)
    {
        // 单独计时每个查询，体现“第一项触发 UpdateWorld 其他复用缓存”开销对比
        long t0; long dt;
        t0 = Stopwatch.GetTimestamp(); Consume(node.worldPosition); dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.QueryWorldPos]+=dt; countPerType[(int)OpType.QueryWorldPos]++;
        t0 = Stopwatch.GetTimestamp(); Consume(node.WorldRotationRad); dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.QueryWorldRot]+=dt; countPerType[(int)OpType.QueryWorldRot]++;
        t0 = Stopwatch.GetTimestamp(); Consume(node.worldScale); dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.QueryWorldScale]+=dt; countPerType[(int)OpType.QueryWorldScale]++;
        t0 = Stopwatch.GetTimestamp(); Consume(node.worldMatrix); dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.QueryWorldMatrix]+=dt; countPerType[(int)OpType.QueryWorldMatrix]++;
        t0 = Stopwatch.GetTimestamp(); Consume(node.worldMatrixInverse); dt = Stopwatch.GetTimestamp()-t0; ticksPerType[(int)OpType.QueryWorldMatrixInv]+=dt; countPerType[(int)OpType.QueryWorldMatrixInv]++;
    }
    #endregion

    #region 构建层级 & 生成操作 (随机模式)
    private static List<Transform2DFixed> BuildHierarchy(int count, bool linear)
    {
        var list = new List<Transform2DFixed>(count);
        if (count <= 0) return list;
        var root = new Transform2DFixed();
        list.Add(root);
        if (linear)
        {
            var p = root;
            for (int i = 1; i < count; i++) { var n = new Transform2DFixed(p); list.Add(n); p = n; }
        }
        else
        {
            for (int i = 1; i < count; i++) { var n = new Transform2DFixed(root); list.Add(n); }
        }
        return list;
    }

    private static Op[] GenerateOps(in Config cfg, int nodeCount)
    {
        var ops = new Op[cfg.OperationCount];
        var weights = cfg.Weights;
        int totalW = weights.Total;
        if (totalW <= 0) throw new Exception("Total operation weight must be > 0");
        var rnd = new System.Random(cfg.Seed);

        var cumulative = new List<(int cut, OpType type)>(32);
        int acc = 0;
        void AddW(int w, OpType t) { if (w <= 0) return; acc += w; cumulative.Add((acc, t)); }
        AddW(weights.SetLocalPos, OpType.SetLocalPos);
        AddW(weights.SetLocalRot, OpType.SetLocalRot);
        AddW(weights.SetLocalScale, OpType.SetLocalScale);
        AddW(weights.SetLocalTRS, OpType.SetLocalTRS);
        AddW(weights.SetWorldPos, OpType.SetWorldPos);
        AddW(weights.SetWorldRot, OpType.SetWorldRot);
        AddW(weights.TranslateSelf, OpType.TranslateSelf);
        AddW(weights.RotateSelf, OpType.RotateSelf);
        AddW(weights.QueryWorldPos, OpType.QueryWorldPos);
        AddW(weights.QueryWorldRot, OpType.QueryWorldRot);
        AddW(weights.QueryWorldScale, OpType.QueryWorldScale);
        AddW(weights.QueryWorldMatrix, OpType.QueryWorldMatrix);
        AddW(weights.QueryWorldMatrixInv, OpType.QueryWorldMatrixInv);
        AddW(weights.TransformPoint, OpType.TransformPoint);
        AddW(weights.InverseTransformPoint, OpType.InverseTransformPoint);
        AddW(weights.TransformDirection, OpType.TransformDirection);
        AddW(weights.InverseTransformDirection, OpType.InverseTransformDirection);
        AddW(weights.TransformVector, OpType.TransformVector);
        AddW(weights.InverseTransformVector, OpType.InverseTransformVector);
        int finalTotal = acc;

        for (int i = 0; i < ops.Length; i++)
        {
            int r = rnd.Next(finalTotal);
            OpType chosen = OpType.QueryWorldPos;
            for (int j = 0; j < cumulative.Count; j++)
            { if (r < cumulative[j].cut) { chosen = cumulative[j].type; break; } }
            int nodeIdx = rnd.Next(nodeCount);
            Op op = new Op { Type = chosen, NodeIndex = nodeIdx };
            double RandRange(double span) => (rnd.NextDouble() * 2.0 - 1.0) * span;
            switch (chosen)
            {
                case OpType.SetLocalPos:
                case OpType.SetWorldPos:
                case OpType.TranslateSelf:
                    op.V2a = new TSVector2((FP)RandRange(5.0), (FP)RandRange(5.0)); break;
                case OpType.SetLocalRot:
                case OpType.SetWorldRot:
                case OpType.RotateSelf:
                    op.Fp0 = (FP)RandRange(Math.PI); break;
                case OpType.SetLocalScale:
                    op.V2a = new TSVector2((FP)RandRange(2.0) + (FP)1, (FP)RandRange(2.0) + (FP)1); break;
                case OpType.SetLocalTRS:
                    op.V2a = new TSVector2((FP)RandRange(5.0), (FP)RandRange(5.0));
                    op.Fp0 = (FP)RandRange(Math.PI);
                    op.V2b = new TSVector2((FP)RandRange(2.0) + (FP)1, (FP)RandRange(2.0) + (FP)1); break;
                case OpType.TransformPoint:
                case OpType.InverseTransformPoint:
                case OpType.TransformDirection:
                case OpType.InverseTransformDirection:
                case OpType.TransformVector:
                case OpType.InverseTransformVector:
                    op.V2a = new TSVector2((FP)RandRange(10.0), (FP)RandRange(10.0)); break;
            }
            ops[i] = op;
        }
        return ops;
    }
    #endregion
}
#endif
