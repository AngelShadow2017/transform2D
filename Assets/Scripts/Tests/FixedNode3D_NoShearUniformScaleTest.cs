using System;
using System.Collections.Generic;
using Core.TrueSync;
using UnityEngine;

// 假设 FixedNode3D / TSVector / TSQuaternion / FP / TMatrix3x4 已存在
// using Core.TrueSync;

/*
新增内容概要（相对上一个无 shear 测试脚本）：
1. 新增“重设父节点”测试（SetParent）：
   - 在每次迭代结束追加若干次随机重父操作（可配置 reparentOpsPerIteration）。
   - 同步对 Unity Transform 和 FixedNode3D 执行：
       A) keepWorld = true：期望重设后世界 TRS 完全保持（仅父层级改变）。
       B) keepWorld = false：期望子节点本地 TRS 完全保持（世界 TRS 按新父变化）。
   - 同时测试设置为 null（成为根）与在不同分支之间移动。
2. 由于使用均匀缩放 + 无 shear，理论上浮点误差仅来自 Unity 的 float 与内部 FP 转换；
   因此允许极小 epsilon（可调 reparentPosEps / reparentRotEpsDeg / reparentScaleEps）。
3. 增加统计：reparentKeepWorldMismatchCount / reparentKeepLocalMismatchCount / reparentShearDetectedCount。
4. 为防止形成环，挑选新父时避免使用当前节点及其子孙。
5. 提供可选开关：enableReparentTests。
*/

public class FixedNode3D_NoShearUniformScale_ReparentTest : MonoBehaviour
{
    [Header("Hierarchy")]
    public int nodeCount = 32;
    public bool randomTreeInitial = true;
    [Range(0,1f)]
    public float initialReparentBias = 0.6f;

    [Header("Local Position Range")]
    public Vector3 posMin = new Vector3(-8,-4,-8);
    public Vector3 posMax = new Vector3( 8, 4, 8);

    [Header("Local Euler (Degrees)")]
    public Vector3 eulerMin = Vector3.zero;
    public Vector3 eulerMax = new Vector3(360,360,360);

    [Header("Uniform Scale")]
    public bool useDiscreteScaleSet = true;
    public float[] scaleCandidates = new float[] { 0.5f, 1f, 2f, 4f };
    public float uniformScaleMin = 0.25f;
    public float uniformScaleMax = 4f;

    [Header("Main Iteration")]
    public int iterations = 400;
    public int framesPerBatch = 1;
    public bool runOnStart = true;
    public bool runContinuously = false;
    public KeyCode triggerKey = KeyCode.U;

    [Header("Tolerance (World Consistency)")]
    public float posEps = 1e-4f;
    public float rotEpsDeg = 0.02f;
    public float scaleEps = 1e-4f;
    public float shearDotEps = 1e-4f;

    [Header("Reparent Tests")]
    public bool enableReparentTests = true;
    [Tooltip("每次迭代后进行多少个重父操作（多次可覆盖 keepWorld = true / false）。")]
    public int reparentOpsPerIteration = 6;

    [Tooltip("Reparent keepWorld=true 时验证世界 TRS 的容差（可与主测试一致或更严格）。")]
    public float reparentPosEps = 2e-5f;
    public float reparentRotEpsDeg = 0.01f;
    public float reparentScaleEps = 2e-5f;

    [Tooltip("Reparent keepWorld=false 时验证本地 TRS 的容差。")]
    public float reparentLocalPosEps = 1e-6f;
    public float reparentLocalRotEpsDeg = 0.005f;
    public float reparentLocalScaleEps = 1e-6f;

    [Header("Logging")]
    public bool logFirstMismatch = true;
    public bool logShearDetect = true;
    public bool stopOnShear = true;
    public bool verbosePerIteration = false;
    public bool summaryAtEnd = true;
    public bool logFirstReparentMismatch = true;

    private struct NodePair
    {
        public Transform unity;
        public FixedNode3D fixedNode;
        public string name;
    }

    private readonly List<NodePair> nodes = new List<NodePair>();

    // 主循环统计
    private int iterIndex;
    private bool running;

    private int mismatchPos;
    private int mismatchRot;
    private int mismatchScale;
    private int shearCount;

    private float maxPosErr;
    private float maxRotErr;
    private float maxScaleErr;

    // Reparent 统计
    private int reparentKeepWorldMismatchCount;
    private int reparentKeepLocalMismatchCount;
    private int reparentShearDetectedCount;

    void Start()
    {
        BuildInitialHierarchy();
        if (runOnStart)
            running = true;
    }

    void Update()
    {
        if (Input.GetKeyDown(triggerKey))
        {
            ResetStats();
            iterIndex = 0;
            running = true;
        }

        if (runContinuously)
            running = true;

        if (!running) return;

        for (int f = 0; f < framesPerBatch; f++)
        {
            if (iterIndex >= iterations)
            {
                running = false;
                if (summaryAtEnd) PrintSummary();
                break;
            }

            RunOneIteration(iterIndex);

            if (enableReparentTests)
                RunReparentPhase(iterIndex);

            iterIndex++;
        }
    }

    private void ResetStats()
    {
        mismatchPos = mismatchRot = mismatchScale = 0;
        shearCount = 0;
        maxPosErr = maxRotErr = maxScaleErr = 0;

        reparentKeepWorldMismatchCount = 0;
        reparentKeepLocalMismatchCount = 0;
        reparentShearDetectedCount = 0;
        logFirstMismatch = true;
        logFirstReparentMismatch = true;
    }

    private void BuildInitialHierarchy()
    {
        nodes.Clear();
        var rootT = transform;
        var rootF = new FixedNode3D(null, keepWorld: true);

        nodes.Add(new NodePair
        {
            unity = rootT,
            fixedNode = rootF,
            name = rootT.name
        });

        for (int i = 1; i < nodeCount; i++)
        {
            int parentIdx;
            if (randomTreeInitial)
            {
                parentIdx = (UnityEngine.Random.value < initialReparentBias)
                    ? UnityEngine.Random.Range(0, i) : (i - 1);
            }
            else parentIdx = i - 1;

            var parentUnity = nodes[parentIdx].unity;
            var parentFixed = nodes[parentIdx].fixedNode;

            var go = new GameObject("Node_" + i);
            go.transform.SetParent(parentUnity, worldPositionStays: true);

            var fn = new FixedNode3D(parentFixed, keepWorld: true);

            nodes.Add(new NodePair
            {
                unity = go.transform,
                fixedNode = fn,
                name = go.name
            });
        }
    }

    private float RandomUniformScale()
    {
        if (useDiscreteScaleSet)
        {
            if (scaleCandidates == null || scaleCandidates.Length == 0)
                return 1f;
            return scaleCandidates[UnityEngine.Random.Range(0, scaleCandidates.Length)];
        }
        else
        {
            return UnityEngine.Random.Range(uniformScaleMin, uniformScaleMax);
        }
    }

    private void RunOneIteration(int idx)
    {
        // 为所有节点设置随机本地 TRS (uniform scale)
        for (int i = 0; i < nodes.Count; i++)
        {
            var np = nodes[i];

            Vector3 lp = new Vector3(
                UnityEngine.Random.Range(posMin.x, posMax.x),
                UnityEngine.Random.Range(posMin.y, posMax.y),
                UnityEngine.Random.Range(posMin.z, posMax.z));

            Vector3 leDeg = new Vector3(
                UnityEngine.Random.Range(eulerMin.x, eulerMax.x),
                UnityEngine.Random.Range(eulerMin.y, eulerMax.y),
                UnityEngine.Random.Range(eulerMin.z, eulerMax.z));

            float s = RandomUniformScale();
            Vector3 ls = new Vector3(s, s, s);

            // Unity
            np.unity.localPosition = lp;
            np.unity.localRotation = Quaternion.Euler(leDeg);
            np.unity.localScale = ls;

            // FixedNode
            ApplyUniformLocalTRS(np.fixedNode, lp, leDeg, s);
        }

        // 校验世界 TRS 与 shear
        for (int i = 0; i < nodes.Count; i++)
        {
            var np = nodes[i];
            var unity = np.unity;
            var fn = np.fixedNode;

            Vector3 uwp = unity.position;
            Vector3 fwp = ToVector3(fn.worldPosition);
            float posErr = (uwp - fwp).magnitude;
            maxPosErr = Mathf.Max(maxPosErr, posErr);
            bool posBad = posErr > posEps;
            if (posBad) mismatchPos++;

            Quaternion ur = unity.rotation;
            Quaternion fr = ToQuaternion(fn.worldRotation);
            float rotErr = Quaternion.Angle(ur, fr);
            maxRotErr = Mathf.Max(maxRotErr, rotErr);
            bool rotBad = rotErr > rotEpsDeg;
            if (rotBad) mismatchRot++;

            Vector3 us = unity.lossyScale;
            Vector3 fs = ToVector3(fn.worldScale);
            float scaleErr = (us - fs).magnitude;
            maxScaleErr = Mathf.Max(maxScaleErr, scaleErr);
            bool scaleBad = scaleErr > scaleEps;
            if (scaleBad) mismatchScale++;

            // Shear 检测 (FixedNode globalMatrix)
            var gm = fn.globalMatrix;
            Vector3 c0 = new Vector3((float)gm.m00, (float)gm.m10, (float)gm.m20);
            Vector3 c1 = new Vector3((float)gm.m01, (float)gm.m11, (float)gm.m21);
            Vector3 c2 = new Vector3((float)gm.m02, (float)gm.m12, (float)gm.m22);

            float l0 = c0.magnitude; if (l0 < 1e-12f) l0 = 1e-12f;
            float l1 = c1.magnitude; if (l1 < 1e-12f) l1 = 1e-12f;
            float l2 = c2.magnitude; if (l2 < 1e-12f) l2 = 1e-12f;

            Vector3 n0 = c0 / l0;
            Vector3 n1 = c1 / l1;
            Vector3 n2 = c2 / l2;

            float d01 = Mathf.Abs(Vector3.Dot(n0, n1));
            float d02 = Mathf.Abs(Vector3.Dot(n0, n2));
            float d12 = Mathf.Abs(Vector3.Dot(n1, n2));

            bool shear = (d01 > shearDotEps) || (d02 > shearDotEps) || (d12 > shearDotEps);
            if (shear)
            {
                shearCount++;
                if (logShearDetect)
                {
                    Debug.LogError($"[SHEAR] iter={idx} node={i} {np.name} dot01={d01:E6} dot02={d02:E6} dot12={d12:E6}");
                }
                if (stopOnShear)
                {
                    running = false;
                    PrintSummary();
                    return;
                }
            }

            if ((posBad || rotBad || scaleBad) && logFirstMismatch)
            {
                Debug.LogError(
                    $"[MISMATCH] iter={idx} node={i} {np.name}\n" +
                    $"PosErr={posErr:E6} RotDegErr={rotErr:E6} ScaleErr={scaleErr:E6}\n" +
                    $"UnityPos={uwp} FixedPos={fwp}\n" +
                    $"UnityRot={ur.eulerAngles} FixedRot={fr.eulerAngles}\n" +
                    $"UnityScale={us} FixedScale={fs}");
                logFirstMismatch = false;
            }
        }

        if (verbosePerIteration)
        {
            Debug.Log($"Iter {idx} base pass: MaxPosErr={maxPosErr:E6} MaxRotErr={maxRotErr:E6} MaxScaleErr={maxScaleErr:E6}");
        }
    }

    // Reparent 测试阶段
    private void RunReparentPhase(int idx)
    {
        if (nodes.Count <= 1) return;

        for (int op = 0; op < reparentOpsPerIteration; op++)
        {
            // 随机选择一个非根节点
            int childIndex = UnityEngine.Random.Range(1, nodes.Count);
            var child = nodes[childIndex];

            bool useKeepWorld = (op % 2 == 0); // 交替测试 keepWorld
            bool toNull = UnityEngine.Random.value < 0.2f; // 20% 机会设为根

            // 记录旧状态
            Vector3 oldWorldPosUnity = child.unity.position;
            Quaternion oldWorldRotUnity = child.unity.rotation;
            Vector3 oldWorldScaleUnity = child.unity.lossyScale;

            Vector3 oldWorldPosFixed = ToVector3(child.fixedNode.worldPosition);
            Quaternion oldWorldRotFixed = ToQuaternion(child.fixedNode.worldRotation);
            Vector3 oldWorldScaleFixed = ToVector3(child.fixedNode.worldScale);

            Vector3 oldLocalPosUnity = child.unity.localPosition;
            Quaternion oldLocalRotUnity = child.unity.localRotation;
            Vector3 oldLocalScaleUnity = child.unity.localScale;

            TSVector oldLocalPosFixedTS = child.fixedNode.localPosition;
            TSQuaternion oldLocalRotFixedTS = child.fixedNode.localRotation;
            TSVector oldLocalScaleFixedTS = child.fixedNode.localScale;

            // 选新父
            Transform newParentUnity = null;
            FixedNode3D newParentFixed = null;

            if (!toNull)
            {
                // 需要选择一个不是自身也不是自身后代的节点
                // 构建后代集合
                var descendants = CollectDescendantsIndices(childIndex);
                int safety = 0;
                while (true)
                {
                    int candidate = UnityEngine.Random.Range(0, nodes.Count);
                    if (candidate != childIndex && !descendants.Contains(candidate))
                    {
                        newParentUnity = nodes[candidate].unity;
                        newParentFixed = nodes[candidate].fixedNode;
                        break;
                    }
                    if (++safety > 200) break;
                }
                if (newParentUnity == null)
                {
                    // 退化：直接改为根
                    toNull = true;
                }
            }

            // 执行 Unity SetParent
            child.unity.SetParent(toNull ? null : newParentUnity, worldPositionStays: useKeepWorld);

            // 执行 FixedNode SetParent
            child.fixedNode.SetParent(toNull ? null : newParentFixed, keepWorld: useKeepWorld);

            // 验证
            if (useKeepWorld)
            {
                // 世界 TRS 应保持
                Vector3 newWorldPosU = child.unity.position;
                Quaternion newWorldRotU = child.unity.rotation;
                Vector3 newWorldScaleU = child.unity.lossyScale;

                Vector3 newWorldPosF = ToVector3(child.fixedNode.worldPosition);
                Quaternion newWorldRotF = ToQuaternion(child.fixedNode.worldRotation);
                Vector3 newWorldScaleF = ToVector3(child.fixedNode.worldScale);

                float diffPosUnity = (newWorldPosU - oldWorldPosUnity).magnitude;
                float diffPosFixed = (newWorldPosF - oldWorldPosFixed).magnitude;

                float diffRotUnity = Quaternion.Angle(newWorldRotU, oldWorldRotUnity);
                float diffRotFixed = Quaternion.Angle(newWorldRotF, oldWorldRotFixed);

                float diffScaleUnity = (newWorldScaleU - oldWorldScaleUnity).magnitude;
                float diffScaleFixed = (newWorldScaleF - oldWorldScaleFixed).magnitude;

                bool bad = diffPosUnity > reparentPosEps || diffPosFixed > reparentPosEps
                           || diffRotUnity > reparentRotEpsDeg || diffRotFixed > reparentRotEpsDeg
                           || diffScaleUnity > reparentScaleEps || diffScaleFixed > reparentScaleEps;

                if (bad)
                {
                    reparentKeepWorldMismatchCount++;
                    if (logFirstReparentMismatch)
                    {
                        Debug.LogError(
                            $"[REPARENT keepWorld=TRUE MISMATCH] iter={idx} op={op} node={childIndex} {child.name}\n" +
                            $"ΔPosUnity={diffPosUnity:E6} ΔPosFixed={diffPosFixed:E6} eps={reparentPosEps}\n" +
                            $"ΔRotUnityDeg={diffRotUnity:E6} ΔRotFixedDeg={diffRotFixed:E6} epsDeg={reparentRotEpsDeg}\n" +
                            $"ΔScaleUnity={diffScaleUnity:E6} ΔScaleFixed={diffScaleFixed:E6} eps={reparentScaleEps}\n" +
                            $"OldWorldPos(Fixed/Unity)={oldWorldPosFixed}/{oldWorldPosUnity} NewWorldPos(Fixed/Unity)={newWorldPosF}/{newWorldPosU}");
                        logFirstReparentMismatch = false;
                    }
                }
            }
            else
            {
                // 本地 TRS 应保持
                Vector3 newLocalPosU = child.unity.localPosition;
                Quaternion newLocalRotU = child.unity.localRotation;
                Vector3 newLocalScaleU = child.unity.localScale;

                TSVector newLocalPosFTS = child.fixedNode.localPosition;
                TSQuaternion newLocalRotFTS = child.fixedNode.localRotation;
                TSVector newLocalScaleFTS = child.fixedNode.localScale;

                float dLocalPosUnity = (newLocalPosU - oldLocalPosUnity).magnitude;
                float dLocalRotUnity = Quaternion.Angle(newLocalRotU, oldLocalRotUnity);
                float dLocalScaleUnity = (newLocalScaleU - oldLocalScaleUnity).magnitude;

                float dLocalPosFixed = ToVector3(newLocalPosFTS - oldLocalPosFixedTS).magnitude;
                float dLocalRotFixed = Quaternion.Angle(ToQuaternion(newLocalRotFTS), ToQuaternion(oldLocalRotFixedTS));
                float dLocalScaleFixed = ToVector3(newLocalScaleFTS - oldLocalScaleFixedTS).magnitude;

                bool bad = dLocalPosUnity > reparentLocalPosEps || dLocalPosFixed > reparentLocalPosEps
                           || dLocalRotUnity > reparentLocalRotEpsDeg || dLocalRotFixed > reparentLocalRotEpsDeg
                           || dLocalScaleUnity > reparentLocalScaleEps || dLocalScaleFixed > reparentLocalScaleEps;

                if (bad)
                {
                    reparentKeepLocalMismatchCount++;
                    if (logFirstReparentMismatch)
                    {
                        Debug.LogError(
                            $"[REPARENT keepWorld=FALSE MISMATCH] iter={idx} op={op} node={childIndex} {child.name}\n" +
                            $"ΔLocalPos Unity/Fixed = {dLocalPosUnity:E6}/{dLocalPosFixed:E6} eps={reparentLocalPosEps}\n" +
                            $"ΔLocalRotDeg Unity/Fixed = {dLocalRotUnity:E6}/{dLocalRotFixed:E6} epsDeg={reparentLocalRotEpsDeg}\n" +
                            $"ΔLocalScale Unity/Fixed = {dLocalScaleUnity:E6}/{dLocalScaleFixed:E6} eps={reparentLocalScaleEps}\n" +
                            $"OldLocalPos(Unity/Fixed)={oldLocalPosUnity}/{ToVector3(oldLocalPosFixedTS)} -> NewLocalPos(Unity/Fixed)={newLocalPosU}/{ToVector3(newLocalPosFTS)}");
                        logFirstReparentMismatch = false;
                    }
                }
            }

            // 额外：reparent 后再次检测 shear (理论上仍无)
            var gm = child.fixedNode.globalMatrix;
            Vector3 cc0 = new Vector3((float)gm.m00, (float)gm.m10, (float)gm.m20);
            Vector3 cc1 = new Vector3((float)gm.m01, (float)gm.m11, (float)gm.m21);
            Vector3 cc2 = new Vector3((float)gm.m02, (float)gm.m12, (float)gm.m22);

            float L0 = cc0.magnitude; if (L0 < 1e-12f) L0 = 1e-12f;
            float L1 = cc1.magnitude; if (L1 < 1e-12f) L1 = 1e-12f;
            float L2 = cc2.magnitude; if (L2 < 1e-12f) L2 = 1e-12f;

            cc0 /= L0; cc1 /= L1; cc2 /= L2;
            float dd01 = Mathf.Abs(Vector3.Dot(cc0, cc1));
            float dd02 = Mathf.Abs(Vector3.Dot(cc0, cc2));
            float dd12 = Mathf.Abs(Vector3.Dot(cc1, cc2));
            bool shearReparent = (dd01 > shearDotEps) || (dd02 > shearDotEps) || (dd12 > shearDotEps);
            if (shearReparent)
            {
                reparentShearDetectedCount++;
                if (logShearDetect)
                {
                    Debug.LogError($"[SHEAR AFTER REPARENT] iter={idx} op={op} node={childIndex} {child.name} d01={dd01:E6} d02={dd02:E6} d12={dd12:E6}");
                }
                if (stopOnShear)
                {
                    running = false;
                    PrintSummary();
                    return;
                }
            }
        }
    }

    private HashSet<int> CollectDescendantsIndices(int index)
    {
        var set = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(index);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            for (int i = 0; i < nodes.Count; i++)
            {
                // 如果 i 的父是 cur
                var parent = nodes[i].unity.parent;
                if (parent == nodes[cur].unity)
                {
                    if (set.Add(i))
                        queue.Enqueue(i);
                }
            }
        }
        return set;
    }

    private void PrintSummary()
    {
        Debug.Log(
            "==== FixedNode3D Uniform No-Shear + Reparent Test Summary ====\n" +
            $"IterationsRun: {iterIndex}\n" +
            $"Nodes: {nodes.Count}\n" +
            $"Base PosMismatch: {mismatchPos} (eps={posEps}) MaxPosErr={maxPosErr:E6}\n" +
            $"Base RotMismatch: {mismatchRot} (deg eps={rotEpsDeg}) MaxRotErrDeg={maxRotErr:E6}\n" +
            $"Base ScaleMismatch: {mismatchScale} (eps={scaleEps}) MaxScaleErr={maxScaleErr:E6}\n" +
            $"Base ShearDetected: {shearCount} (dot eps={shearDotEps})\n" +
            $"Reparent keepWorld mismatches: {reparentKeepWorldMismatchCount} (posEps={reparentPosEps}, rotEpsDeg={reparentRotEpsDeg}, scaleEps={reparentScaleEps})\n" +
            $"Reparent keepLocal mismatches: {reparentKeepLocalMismatchCount} (localPosEps={reparentLocalPosEps}, localRotEpsDeg={reparentLocalRotEpsDeg}, localScaleEps={reparentLocalScaleEps})\n" +
            $"Reparent shear detections: {reparentShearDetectedCount}\n" +
            $"ALL OK (no mismatches & no shear) = " +
            (mismatchPos==0 && mismatchRot==0 && mismatchScale==0 && shearCount==0 &&
              reparentKeepWorldMismatchCount==0 && reparentKeepLocalMismatchCount==0 &&
              reparentShearDetectedCount==0) );
    }

    #region Helpers
    private void ApplyUniformLocalTRS(FixedNode3D node, Vector3 lp, Vector3 leDeg, float uniformScale)
    {
        var scale = new TSVector((FP)uniformScale, (FP)uniformScale, (FP)uniformScale);
        var pos   = new TSVector((FP)lp.x, (FP)lp.y, (FP)lp.z);
        Vector3 leRad = leDeg * Mathf.Deg2Rad;
        var eulerRad = new TSVector((FP)leRad.x, (FP)leRad.y, (FP)leRad.z);
        node.localEuler = eulerRad;
        node.localScale = scale;
        node.localPosition = pos;
    }

    private static Vector3 ToVector3(TSVector v) =>
        new Vector3((float)v.x, (float)v.y, (float)v.z);

    private static Quaternion ToQuaternion(TSQuaternion q) =>
        new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);
    #endregion

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (nodes.Count > 0)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(nodes[0].unity.position, 0.25f);
        }
    }
#endif
}

/*
使用说明：
1. 将该脚本挂在一个空物体作为根。
2. 保证 FixedNode3D 及 TrueSync 相关类型已导入。
3. 根据需要配置：
   - uniform scale 选择：useDiscreteScaleSet 或改为连续随机。
   - Reparent 测试的操作次数、误差阈值。
4. 运行：
   - runOnStart=true 自动开始；否则按 triggerKey（默认 U）。
5. 观察 Console 总结：
   - 基础迭代（随机 TRS）与 reparent 两阶段分别统计 mismatch。
   - 由于全部使用均匀缩放 + 不引入 shear（retainShearOnEdit=false），只要算法无误就应全部 0。
6. 可调小 epsilon 以更严格验证（受浮点与 FP -> float 转换影响，别设置比 1e-7 小太多）。
*/