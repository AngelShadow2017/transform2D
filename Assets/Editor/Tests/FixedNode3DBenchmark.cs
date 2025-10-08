#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Core.TrueSync; // FP, TSVector, TSQuaternion

/*
 * FixedNode3D World Query Benchmark
 * 需求: “在修改完所有的 local 后统一逐一访问 world，测试速率”
 * 场景: 构建两类层级（线性链、平铺），批量修改所有节点 localPosition（触发 DIRTY_GLOBAL_TRANSFORM），
 *       然后顺序访问 worldPosition / worldRotation（首次、二次命中）测量平均纳秒/节点。
 * 指标:
 *   - PosFirst: 修改后首次访问 worldPosition 平均 ns
 *   - RotFirst: 首次访问 worldRotation (触发矩阵分解) 平均 ns
 *   - RotSecond: 第二次访问 worldRotation (缓存命中) 平均 ns
 * 可配置参数在 Config 结构。
 * 注意: Stopwatch 精度在 Editor 下足够；建议在空场景运行，关闭其他繁重刷新。
 */
public static class FixedNode3DBenchmark
{
    private struct Config
    {
        public int NodeCount;          // 节点数量
        public int WarmupIterations;   // 预热轮次（不计入平均）
        public int MeasureIterations;  // 计入平均的轮次
        public bool LinearChain;       // true=线性链，否则平铺
    }

    [MenuItem("Tools/FixedNode3D/Benchmark Batch World Query")] public static void RunAll()
    {
        // 可按需调整列表内不同规模
        var configs = new [] {
            new Config{ NodeCount = 1_000, WarmupIterations = 2, MeasureIterations = 5, LinearChain = true },
            new Config{ NodeCount = 10_000, WarmupIterations = 2, MeasureIterations = 5, LinearChain = true },
            new Config{ NodeCount = 10_000, WarmupIterations = 2, MeasureIterations = 5, LinearChain = false },
        };
        foreach (var c in configs) RunSingle(c);
    }

    private static void RunSingle(in Config cfg)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var freq = (double)Stopwatch.Frequency;

        double sumPos = 0, sumRotFirst = 0, sumRotSecond = 0;

        for (int iter = -cfg.WarmupIterations; iter < cfg.MeasureIterations; iter++)
        {
            // 重建层级，保证每轮首访都是冷缓存
            var nodes = BuildHierarchy(cfg.NodeCount, cfg.LinearChain);

            // 批量修改 local —— 这里只改 position，触发 DIRTY_GLOBAL_TRANSFORM；不改旋转可模拟“仅位移”场景
            BatchMutateLocals(nodes);

            // 1) 首次访问 worldPosition（触发全局矩阵批量重建）
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < nodes.Count; i++) { _ = nodes[i].worldPosition; }
            sw.Stop();
            double posNsPerNode = sw.ElapsedTicks * 1_000_000_000.0 / freq / nodes.Count;

            // 2) 首次访问 worldRotation （触发旋转+缩放分解）
            sw.Restart();
            for (int i = 0; i < nodes.Count; i++) { _ = nodes[i].worldRotation; }
            sw.Stop();
            double rotFirstNsPerNode = sw.ElapsedTicks * 1_000_000_000.0 / freq / nodes.Count;

            // 3) 第二次访问 worldRotation （缓存命中路径）
            sw.Restart();
            for (int i = 0; i < nodes.Count; i++) { _ = nodes[i].worldRotation; }
            sw.Stop();
            double rotSecondNsPerNode = sw.ElapsedTicks * 1_000_000_000.0 / freq / nodes.Count;

            bool isWarmup = iter < 0;
            UnityEngine.Debug.Log($"[FixedNode3D Benchmark] {(cfg.LinearChain?"Chain":"Flat")} Nodes={cfg.NodeCount} Iter={(isWarmup?"W":iter.ToString())} PosFirst={posNsPerNode:F1} ns RotFirst={rotFirstNsPerNode:F1} ns RotSecond={rotSecondNsPerNode:F1} ns");

            if (!isWarmup)
            {
                sumPos += posNsPerNode;
                sumRotFirst += rotFirstNsPerNode;
                sumRotSecond += rotSecondNsPerNode;
            }
        }

        double denom = cfg.MeasureIterations;
        UnityEngine.Debug.Log($"[FixedNode3D Benchmark AVERAGE] {(cfg.LinearChain?"Chain":"Flat")} Nodes={cfg.NodeCount} PosFirst={sumPos/denom:F1} ns RotFirst={sumRotFirst/denom:F1} ns RotSecond={sumRotSecond/denom:F1} ns");
    }

    private static List<FixedNode3D> BuildHierarchy(int count, bool linear)
    {
        var list = new List<FixedNode3D>(count);
        if (count <= 0) return list;
        var root = new FixedNode3D();
        root.SetAsTopLevel(true, keepGlobal:true);
        list.Add(root);
        if (linear)
        {
            var parent = root;
            for (int i = 1; i < count; i++) { var n = new FixedNode3D(parent); list.Add(n); parent = n; }
        }
        else
        {
            for (int i = 1; i < count; i++) { var n = new FixedNode3D(root); list.Add(n); }
        }
        return list;
    }

    private static void BatchMutateLocals(List<FixedNode3D> nodes)
    {
        // 位置微调，避免溢出；不修改旋转/缩放 -> 仅触发 DIRTY_GLOBAL_TRANSFORM
        // 如果希望同时测试旋转重新组合，把下面注释部分解开。
        FP dx = (FP)0.001; FP dy = (FP)0.002; FP dz = (FP)0.003;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var lp = n.localPosition; // 读取当前（会构造 TSVector）
            n.localPosition = new TSVector(lp.x + dx, lp.y + dy, lp.z + dz);
            // 若要更重路径，可启用：
            var lr = n.localRotation; // quaternion
            n.localEuler = new TSVector(lr.x + (FP)0.0001, lr.y, lr.z); // 迫使 DIRTY_LOCAL_TRANSFORM
        }
    }
}
#endif

