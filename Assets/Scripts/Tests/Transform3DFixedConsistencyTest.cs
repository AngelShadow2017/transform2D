#if false
using System;
using System.Collections.Generic;
using UnityEngine;
using Core.TrueSync;

public class Transform3DFixedConsistencyTest : MonoBehaviour
{
    [Header("Hierarchy")]
    public int depth = 3;
    public int childrenPerNode = 3;

    [Header("Random Ranges")]
    public Vector3 posRange = new Vector3(5, 5, 5);
    public Vector2 uniformScaleRange = new Vector2(0.2f, 3f);
    public bool useUniformScale = false;

    [Header("Test")]
    public int iterations = 200;
    public int operationsPerIteration = 10;
    public int seed = 1234;
    public float posTolerance = 1e-4f;
    public float rotAngleToleranceDeg = 0.01f;
    public float matrixElemTolerance = 2e-4f;
    public bool logFirstMismatchDetail = true;

    private System.Random _rng;
    private readonly List<NodePair> _pairs = new();
    private Transform3DFixed _fixedRoot;

    // 上一次世界矩阵
    private readonly List<Matrix4x4> _prevUnityWorld = new();
    private readonly List<Matrix4x4> _prevFixedWorld = new();

    // 上一次世界位置与旋转
    private readonly List<Vector3> _prevUnityPos = new();
    private readonly List<Vector3> _prevFixedPos = new();
    private readonly List<Quaternion> _prevUnityRot = new();
    private readonly List<Quaternion> _prevFixedRot = new();

    private struct NodePair
    {
        public Transform unity;
        public Transform3DFixedSkew fixedT;
    }

    void Start()
    {
        RunTest();
    }

    [ContextMenu("Run Consistency Test")]
    public void RunTest()
    {
        Cleanup();
        _rng = new System.Random(seed);
        BuildHierarchy();
        _fixedRoot.RecalculateWorldRecursive();
        InitPreviousCaches();
        ExecuteIterations();
    }

    private void Cleanup()
    {
        foreach (var p in _pairs)
        {
            if (p.unity != null) DestroyImmediate(p.unity.gameObject);
        }
        _pairs.Clear();
        _fixedRoot = null;
        _prevUnityWorld.Clear();
        _prevFixedWorld.Clear();
        _prevUnityPos.Clear();
        _prevFixedPos.Clear();
        _prevUnityRot.Clear();
        _prevFixedRot.Clear();
    }

    private void BuildHierarchy()
    {
        _fixedRoot = new Transform3DFixed();
        var rootGO = new GameObject("Root_Unity");
        _pairs.Add(new NodePair { unity = rootGO.transform, fixedT = _fixedRoot });

        void Recurse(Transform3DFixedSkew parentFixed, Transform parentUnity, int currentDepth)
        {
            if (currentDepth >= depth) return;
            for (int i = 0; i < childrenPerNode; i++)
            {
                var go = new GameObject($"N_{currentDepth}_{i}");
                go.transform.SetParent(parentUnity, false);

                var f = new Transform3DFixedSkew(parentFixed, keepWorld: false);
                _pairs.Add(new NodePair { unity = go.transform, fixedT = f });

                Recurse(f, go.transform, currentDepth + 1);
            }
        }

        Recurse(_fixedRoot, rootGO.transform, 1);
    }

    private void InitPreviousCaches()
    {
        _prevUnityWorld.Clear();
        _prevFixedWorld.Clear();
        _prevUnityPos.Clear();
        _prevFixedPos.Clear();
        _prevUnityRot.Clear();
        _prevFixedRot.Clear();

        for (int i = 0; i < _pairs.Count; i++)
        {
            var u = _pairs[i].unity;
            var f = _pairs[i].fixedT;

            _prevUnityWorld.Add(u.localToWorldMatrix);
            _prevFixedWorld.Add(f.WorldMatrix4x4);

            _prevUnityPos.Add(u.position);
            var fp = f.worldPosition;
            _prevFixedPos.Add(new Vector3((float)fp.x, (float)fp.y, (float)fp.z));

            _prevUnityRot.Add(u.rotation);
            var fr = f.worldRotation;
            _prevFixedRot.Add(new Quaternion((float)fr.x, (float)fr.y, (float)fr.z, (float)fr.w));
        }
    }

    private void ExecuteIterations()
    {
        int totalNodes = _pairs.Count;
        int posMismatch = 0;
        int rotMismatch = 0;
        int matrixMismatch = 0;
        bool loggedDetail = false;

        for (int it = 0; it < iterations; it++)
        {
            // 随机赋本地 TRS
            for (int i = 0; i < totalNodes; i++)
            {
                var pair = _pairs[i];
                Vector3 lp = new Vector3(
                    RandRange(-posRange.x, posRange.x),
                    RandRange(-posRange.y, posRange.y),
                    RandRange(-posRange.z, posRange.z));
                Quaternion lr = UnityEngine.Random.rotation;
                Vector3 ls;
                if (useUniformScale)
                {
                    float s = RandRange(uniformScaleRange.x, uniformScaleRange.y);
                    ls = new Vector3(s, s, s);
                }
                else
                {
                    ls = new Vector3(
                        RandRange(uniformScaleRange.x, uniformScaleRange.y),
                        RandRange(uniformScaleRange.x, uniformScaleRange.y),
                        RandRange(uniformScaleRange.x, uniformScaleRange.y));
                }

                pair.unity.localPosition = lp;
                pair.unity.localRotation = lr;
                pair.unity.localScale = ls;

                var fpPos = new TSVector((FP)lp.x, (FP)lp.y, (FP)lp.z);
                var fpRot = new TSQuaternion((FP)lr.x, (FP)lr.y, (FP)lr.z, (FP)lr.w);
                var fpScale = new TSVector((FP)ls.x, (FP)ls.y, (FP)ls.z);
                pair.fixedT.SetLocalTRS(fpPos, fpRot, fpScale);
            }

            _fixedRoot.RecalculateWorldRecursive();

            // 随机操作
            for (int op = 0; op < operationsPerIteration; op++)
            {
                int idx = _rng.Next(0, totalNodes);
                var pair = _pairs[idx];
                int choice = 2;//_rng.Next(0, 3);
                switch (choice)
                {
                    case 0:
                    {
                        Vector3 d = new Vector3(RandRange(-1, 1), RandRange(-1, 1), RandRange(-1, 1));
                        pair.unity.Translate(d, Space.Self);
                        pair.fixedT.Translate(new TSVector((FP)d.x, (FP)d.y, (FP)d.z), Space.Self);
                        break;
                    }
                    case 1:
                    {
                        Quaternion dq = Quaternion.Euler(RandRange(-20, 20), RandRange(-20, 20), RandRange(-20, 20));
                        pair.unity.rotation = dq * pair.unity.rotation;
                        var fpDQ = new TSQuaternion((FP)dq.x, (FP)dq.y, (FP)dq.z, (FP)dq.w);
                        pair.fixedT.SetWorldRotation(fpDQ * pair.fixedT.worldRotation);
                        break;
                    }
                    case 2:
                    {
                        if (idx == 0) break;
                        bool toNull = false;//_rng.NextDouble() < 0.3;
                        Transform newParentUnity = toNull ? null : _pairs[0].unity;
                        Transform3DFixedSkew newParentFixed = toNull ? null : _fixedRoot;
                        bool keepWorld = _rng.NextDouble() < 0.5;
                        pair.unity.SetParent(newParentUnity, worldPositionStays: keepWorld);
                        pair.fixedT.SetParent(newParentFixed, keepWorld);
                        break;
                    }
                }
                _fixedRoot.RecalculateWorldRecursive();
            }

            // 校验
            for (int i = 0; i < totalNodes; i++)
            {
                var pair = _pairs[i];

                // 当前世界位置/旋转
                Vector3 wpU = pair.unity.position;
                TSVector wpf = pair.fixedT.worldPosition;
                Vector3 wpF = new Vector3((float)wpf.x, (float)wpf.y, (float)wpf.z);

                Quaternion wrU = pair.unity.rotation;
                TSQuaternion wrFq = pair.fixedT.worldRotation;
                Quaternion wrF = new Quaternion((float)wrFq.x, (float)wrFq.y, (float)wrFq.z, (float)wrFq.w);

                // 矩阵
                Matrix4x4 mU = pair.unity.localToWorldMatrix;
                Matrix4x4 mF = pair.fixedT.WorldMatrix4x4;

                // Position 误差
                float posErr = (wpU - wpF).magnitude;
                if (posErr > posTolerance)
                {
                    posMismatch++;
                    if (logFirstMismatchDetail && !loggedDetail)
                    {
                        Debug.Log(
                            $"[Mismatch:Position] Node={pair.unity.name} PosErr={posErr}\n" +
                            $"Prev Unity Pos: {FormatV3(_prevUnityPos[i])}  Prev Fixed Pos: {FormatV3(_prevFixedPos[i])}\n" +
                            $"Curr Unity Pos: {FormatV3(wpU)}  Curr Fixed Pos: {FormatV3(wpF)}");
                        loggedDetail = true;
                    }
                }

                // Rotation 误差
                float ang = Quaternion.Angle(wrU, wrF);
                if (ang > rotAngleToleranceDeg)
                {
                    rotMismatch++;
                    if (logFirstMismatchDetail && !loggedDetail)
                    {
                        Debug.Log(
                            $"[Mismatch:Rotation] Node={pair.unity.name} AngleErr={ang} deg\n" +
                            $"Prev Unity Rot: {FormatQ(_prevUnityRot[i])}  Prev Fixed Rot: {FormatQ(_prevFixedRot[i])}\n" +
                            $"Curr Unity Rot: {FormatQ(wrU)}  Curr Fixed Rot: {FormatQ(wrF)}");
                        loggedDetail = true;
                    }
                }

                // Matrix 误差
                float maxElemErr = MaxMatrixDiff(mU, mF);
                if (maxElemErr > matrixElemTolerance)
                {
                    matrixMismatch++;
                    if (logFirstMismatchDetail && !loggedDetail)
                    {
                        Debug.Log(
                            $"[Mismatch:Matrix] Node={pair.unity.name} MaxElemErr={maxElemErr}\n" +
                            $"Prev Unity:\n{MatrixToString(_prevUnityWorld[i])}\nPrev Fixed:\n{MatrixToString(_prevFixedWorld[i])}\n" +
                            $"Curr Unity:\n{MatrixToString(mU)}\nCurr Fixed:\n{MatrixToString(mF)}");
                        Debug.Log(pair.unity.lossyScale+" "+pair.fixedT.lossyScale);
                        loggedDetail = true;
                    }
                }

                // 更新缓存
                _prevUnityWorld[i] = mU;
                _prevFixedWorld[i] = mF;
                _prevUnityPos[i] = wpU;
                _prevFixedPos[i] = wpF;
                _prevUnityRot[i] = wrU;
                _prevFixedRot[i] = wrF;
            }
        }

        Debug.Log(
            $"[Transform3DFixedSkew Consistency Test] Nodes={_pairs.Count} Iterations={iterations}\n" +
            $"PosMismatch={posMismatch} RotMismatch={rotMismatch} MatrixMismatch={matrixMismatch}\n" +
            $"Tol(Pos={posTolerance}, RotDeg={rotAngleToleranceDeg}, MatElem={matrixElemTolerance})");
    }

    private float RandRange(float a, float b) => (float)(_rng.NextDouble() * (b - a) + a);

    private static float MaxMatrixDiff(Matrix4x4 a, Matrix4x4 b)
    {
        float max = 0f;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                float d = Mathf.Abs(a[r, c] - b[r, c]);
                if (d > max) max = d;
            }
        return max;
    }

    private static string MatrixToString(Matrix4x4 m)
    {
        return $"{m.m00,8:F6} {m.m01,8:F6} {m.m02,8:F6} {m.m03,8:F6}\n" +
               $"{m.m10,8:F6} {m.m11,8:F6} {m.m12,8:F6} {m.m13,8:F6}\n" +
               $"{m.m20,8:F6} {m.m21,8:F6} {m.m22,8:F6} {m.m23,8:F6}\n" +
               $"{m.m30,8:F6} {m.m31,8:F6} {m.m32,8:F6} {m.m33,8:F6}";
    }

    private static string FormatV3(Vector3 v) => $"({v.x:F6},{v.y:F6},{v.z:F6})";
    private static string FormatQ(Quaternion q) => $"({q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6})";
}
#endif