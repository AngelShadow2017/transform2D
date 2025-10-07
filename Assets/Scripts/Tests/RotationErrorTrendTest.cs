using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Core.TrueSync;

/*
RotationErrorTrendTest
---------------------------------
目标：检测 FixedNode3D 与 Unity Transform 在多步/多模式旋转下的误差属性：
- 累积 (cumulative drift)
- 跳变 (sporadic spikes)
- 稳定 (stable)

核心思路：
1. 生成一条可配置的旋转序列（模式：QuaternionIncrement / EulerIncrement / RandomEulerSet / Mixed）。
2. 每步同时对 Unity Transform 与 FixedNode3D 应用等价操作。
3. 采集每步世界旋转夹角 error[i] = Angle(Fixed.worldRotation, Unity.rotation)。
4. 统计：
   - 最终误差 finalError
   - 平均误差 avgError
   - 最大误差 maxError
   - 连续斜率（最小二乘拟合） slopeDegPerStep
   - 误差差分 deltaError[i] = error[i]-error[i-1]
   - 跳变事件：deltaError[i] > jumpIncreaseDeg 且 error[i-1] < lowBaselineDeg
   - 稳定区计数、单调增长比率、冻结帧检测（error 不变步数）
   - 四元数符号翻转事件（dot<0 与上一帧符号不同）
5. 分类规则（默认阈值，可调）：
   - If finalError <= stableFinalDeg && slopeDegPerStep <= stableSlopeDeg -> Stable
   - Else if jumpCount >= minJumpEvents && slopeDegPerStep <= jumpSlopeUpper -> Spiky
   - Else if slopeDegPerStep >= cumulativeSlopeMin && finalError - initialError > growthNetDeg -> Cumulative
   - 否则 Unclassified
6. 可选写 CSV 以及在 Editor 中实时打印中间值（logEverySteps > 0）。
7. 支持多层级（chainDepth）父子节点，每层都应用相同旋转增量（放大潜在层级放大效应）。

使用：
- 挂到空物体上
- 设置参数后 Play；或按 triggerKey 启动
- 结束后查看 Console Summary 与 CSV（若开启）
*/
public class RotationErrorTrendTest : MonoBehaviour
{
    public enum SequenceMode { QuaternionIncrement, EulerIncrement, RandomEulerSet, Mixed }

    [Header("Sequence Config")]
    public SequenceMode mode = SequenceMode.QuaternionIncrement;
    [Tooltip("总迭代步数")] public int steps = 20000;
    [Tooltip("每隔多少步打印一次中间日志(0=不打印)")] public int logEverySteps = 2000;
    [Tooltip("四元数增量角（度），用于 QuaternionIncrement / Mixed")]
    public float deltaAngleDeg = 0.1f;
    [Tooltip("旋转轴（单位化前），用于 QuaternionIncrement / Mixed")] public Vector3 deltaAxis = Vector3.up;
    [Tooltip("EulerIncrement 增量 (度)，或 Mixed 中对欧拉的增量")] public Vector3 eulerDeltaDeg = new Vector3(0,0.1f,0);
    [Tooltip("RandomEulerSet 时每分量随机范围 (度)")] public Vector3 randomEulerMin = Vector3.zero;
    public Vector3 randomEulerMax = new Vector3(360,360,360);

    [Header("Hierarchy / Scale")] public int chainDepth = 1; // 根 + (chainDepth-1) 子 = 总层数
    [Tooltip("是否统一使用 ZXY 顺序以匹配 Unity Quaternion.Euler 映射")] public bool forceUnityZXYOrder = true;
    [Tooltip("统一缩放（保持无 shear）")] public float uniformScale = 1f;

    [Header("Error Classification Thresholds")] 
    [Tooltip("最终误差稳定阈值 (度)")] public float stableFinalDeg = 0.2f;
    [Tooltip("稳定判定斜率阈值 (度/步)")] public float stableSlopeDeg = 1e-5f;
    [Tooltip("跳变事件所需的瞬时增量最小角度")] public float jumpIncreaseDeg = 0.5f;
    [Tooltip("跳变前的基线误差需低于此值")] public float lowBaselineDeg = 0.2f;
    [Tooltip("累计漂移最小斜率")] public float cumulativeSlopeMin = 1e-4f;
    [Tooltip("净增长 (final - initial) 达到此值可判定为漂移")] public float growthNetDeg = 2.0f;
    [Tooltip("跳变分类允许的最大斜率")]
    public float jumpSlopeUpper = 5e-5f;
    [Tooltip("跳变分类所需最小跳变事件数")] public int minJumpEvents = 3;
    [Tooltip("如果误差连续不变超过 freezeSteps 则报告")] public int freezeSteps = 5000;

    [Header("CSV Output")] public bool writeCsv = false; public string csvFileName = "RotationErrorTrend.csv";
    [Tooltip("是否写出全部步（否则仅写 summary）")] public bool writeFullSeries = false;

    [Header("Controls")] public bool autoRunOnStart = true; public KeyCode triggerKey = KeyCode.T; public bool runOnce = true;

    [Header("Noise / Options")] 
    [Tooltip("混合模式中欧拉与四元数增量交替步长(>=1)")] public int mixedSwitchInterval = 1;
    [Tooltip("是否在每步后立即访问 worldRotation 强制刷新缓存（正常应访问，但可以测试未访问路径）")] public bool forceWorldRotationAccess = true;
    [Tooltip("是否归一四元数符号到 w>=0，减少符号翻转角度跳动")]
    public bool canonicalizeSign = true;

    private struct NodePair { public Transform u; public FixedNode3D f; }
    private List<NodePair> _chain = new List<NodePair>();

    // Data series (可选截断)
    private List<float> _errors = new List<float>(8192);
    private List<float> _deltaErrors = new List<float>(8192);

    // Stats
    private int _jumpCount; private float _largestJump;
    private int _symbolFlipCount; private int _freezeRun; private bool _reportedFreeze;
    private float _maxError; private float _sumError; private float _initialError;

    // Regression accumulators for slope: y=error, x=step index (1..N)
    private double _sumX, _sumY, _sumXY, _sumXX;

    private int _executedSteps; private bool _running; private bool _completed;
    private System.Random _rand;

    private void Start(){ if (autoRunOnStart) Begin(); }
    private void Update(){ if (Input.GetKeyDown(triggerKey) && (!_running || (!_completed && !runOnce))) Begin(); if (_running) StepLoop(); }

    private void Begin(){ ResetAll(); BuildChain(); _running = true; }

    private void ResetAll(){
        _chain.Clear(); _errors.Clear(); _deltaErrors.Clear();
        _jumpCount = 0; _largestJump = 0; _symbolFlipCount=0; _freezeRun=0; _reportedFreeze=false;
        _maxError = 0; _sumError=0; _initialError=0; _executedSteps=0; _completed=false;
        _sumX=_sumY=_sumXY=_sumXX=0; _rand = new System.Random(12345);
    }

    private void BuildChain(){
        Transform prevU = this.transform; FixedNode3D prevF = new FixedNode3D(null, keepWorld:true);
        if (forceUnityZXYOrder) prevF.rotationOrder = FixedNode3D.RotationOrder.ZXY;
        _chain.Add(new NodePair{ u=prevU, f=prevF });
        for(int i=1;i<Mathf.Max(1,chainDepth);i++){
            var go = new GameObject($"RotNode_{i}"); go.transform.SetParent(prevU, worldPositionStays:true);
            var fn = new FixedNode3D(prevF, keepWorld:true); if(forceUnityZXYOrder) fn.rotationOrder=FixedNode3D.RotationOrder.ZXY;
            _chain.Add(new NodePair{ u=go.transform, f=fn });
            prevU = go.transform; prevF = fn;
        }
        // 初始化全部本地姿态与统一 scale
        foreach(var n in _chain){ n.u.localScale = Vector3.one * uniformScale; n.f.localScale = new TSVector((FP)uniformScale,(FP)uniformScale,(FP)uniformScale); }
    }

    private void StepLoop(){
        if (_executedSteps >= steps){ Finish(); return; }
        _executedSteps++;
        ApplyStep(_executedSteps);
        float err = MeasureError();
        if (_executedSteps == 1) _initialError = err; else _deltaErrors.Add(err - _errors[_errors.Count-1]);
        _errors.Add(err); _sumError += err; if (err>_maxError) _maxError = err;
        // Jump detection
        if (_executedSteps>1){ float de = _errors[^1]-_errors[^2]; if (de > jumpIncreaseDeg && _errors[^2] < lowBaselineDeg){ _jumpCount++; if(de>_largestJump)_largestJump=de; } }
        // Freeze detection
        if (_executedSteps>1){ if (Mathf.Approximately(_errors[^1], _errors[^2])) _freezeRun++; else _freezeRun=0; if (!_reportedFreeze && _freezeRun>=freezeSteps){ Debug.LogWarning($"[RotationErrorTrend] Freeze detected at step={_executedSteps} run={_freezeRun}"); _reportedFreeze=true; } }
        // Regression accumulators
        double x = _executedSteps; double y = err; _sumX += x; _sumY += y; _sumXY += x*y; _sumXX += x*x;
        if (logEverySteps>0 && (_executedSteps % logEverySteps)==0){ Debug.Log(BuildProgressLine()); }
    }

    private string BuildProgressLine(){
        double n=_executedSteps; double denom = (n*_sumXX - _sumX*_sumX); double slope = denom==0?0: (n*_sumXY - _sumX*_sumY)/denom; double mean = _sumError/_errors.Count;
        return $"[RotationErrorTrend] step={_executedSteps} err={_errors[^1]:F6} max={_maxError:F6} slope={slope:F6} jumps={_jumpCount} freezeRun={_freezeRun}";
    }

    private void Finish(){ _running=false; _completed=true; var summary = BuildSummary(); Debug.Log(summary); if (writeCsv) WriteCsv(); }

    private void ApplyStep(int step){
        bool useQuat = mode switch { SequenceMode.QuaternionIncrement => true, SequenceMode.EulerIncrement => false, SequenceMode.RandomEulerSet => false, SequenceMode.Mixed => ((step/mixedSwitchInterval)%2)==0, _ => true };
        if (mode == SequenceMode.RandomEulerSet){ ApplyRandomEuler(); return; }
        if (useQuat) ApplyQuaternionIncrement(); else ApplyEulerIncrement();
    }

    private void ApplyQuaternionIncrement(){
        Vector3 ax = deltaAxis.sqrMagnitude < 1e-12f ? Vector3.up : deltaAxis; ax.Normalize();
        var dqUnity = Quaternion.AngleAxis(deltaAngleDeg, ax);
        var dqTS = TSQuaternion.AngleAxis((FP)deltaAngleDeg, new TSVector((FP)ax.x,(FP)ax.y,(FP)ax.z));
        for(int i=0;i<_chain.Count;i++){
            var np=_chain[i];
            // Unity: 右乘 vs 左乘保持一致使用乘前 (delta * current)
            np.u.localRotation = dqUnity * np.u.localRotation;
            // FixedNode: 使用 quaternion 增量
            var q = np.f.localRotation; q = dqTS * q; if (canonicalizeSign && q.w < FP.Zero){ q.x=-q.x; q.y=-q.y; q.z=-q.z; q.w=-q.w; }
            np.f.localRotation = q;
        }
    }

    private void ApplyEulerIncrement(){
        for(int i=0;i<_chain.Count;i++){
            var np=_chain[i];
            Vector3 cur = np.u.localEulerAngles; cur += eulerDeltaDeg; np.u.localRotation = Quaternion.Euler(cur);
            // FixedNode: 弧度增量
            var le = np.f.localEuler; le.x += (FP)(eulerDeltaDeg.x * Mathf.Deg2Rad); le.y += (FP)(eulerDeltaDeg.y * Mathf.Deg2Rad); le.z += (FP)(eulerDeltaDeg.z * Mathf.Deg2Rad); np.f.localEuler = le;
        }
    }

    private void ApplyRandomEuler(){
        for(int i=0;i<_chain.Count;i++){
            var np=_chain[i];
            Vector3 rnd = new Vector3(
                UnityEngine.Random.Range(randomEulerMin.x, randomEulerMax.x),
                UnityEngine.Random.Range(randomEulerMin.y, randomEulerMax.y),
                UnityEngine.Random.Range(randomEulerMin.z, randomEulerMax.z));
            np.u.localRotation = Quaternion.Euler(rnd);
            var rad = rnd * Mathf.Deg2Rad; np.f.localEuler = new TSVector((FP)rad.x,(FP)rad.y,(FP)rad.z);
        }
    }

    private float MeasureError(){
        // 比对末端节点（最大层级）世界旋转（也可选所有节点平均，这里先用末端放大层级效应）
        var tail = _chain[_chain.Count-1];
        if (forceWorldRotationAccess){ _ = tail.f.worldRotation; }
        Quaternion ur = tail.u.rotation; TSQuaternion frTS = tail.f.worldRotation; Quaternion fr = new Quaternion((float)frTS.x,(float)frTS.y,(float)frTS.z,(float)frTS.w);
        if (canonicalizeSign && fr.w < 0){ fr = new Quaternion(-fr.x,-fr.y,-fr.z,-fr.w); }
        if (canonicalizeSign && ur.w < 0){ ur = new Quaternion(-ur.x,-ur.y,-ur.z,-ur.w); }
        // 符号翻转检测 (dot sign change)
        float dot = Quaternion.Dot(ur, fr); if (dot < 0) _symbolFlipCount++;
        float ang = Quaternion.Angle(ur, fr); return ang;
    }

    private string BuildSummary(){
        int n = _errors.Count; if (n==0) return "[RotationErrorTrend] No data";
        double denom = (n*_sumXX - _sumX*_sumX); double slope = denom==0?0: (n*_sumXY - _sumX*_sumY)/denom; double avg = _sumError/n;
        float finalError = _errors[^1]; float netGrowth = finalError - _initialError;
        string classification = Classify(finalError, slope, netGrowth);
        return "=== Rotation Error Trend Summary ===\n"+
               $"Mode={mode} Steps={n} ChainDepth={chainDepth}\n"+
               $"FinalErrorDeg={finalError:F6} MaxErrorDeg={_maxError:F6} AvgErrorDeg={avg:F6} InitialErrDeg={_initialError:F6}\n"+
               $"NetGrowthDeg={netGrowth:F6} SlopeDegPerStep={slope:F9}\n"+
               $"JumpCount={_jumpCount} LargestJumpDeg={_largestJump:F6} SymbolFlipCount={_symbolFlipCount} FreezeRun={_freezeRun}\n"+
               $"Classification={classification}\n"+
               $"Thresh(stableFinal={stableFinalDeg}, stableSlope={stableSlopeDeg}, jumpIncrease={jumpIncreaseDeg}, cumulativeSlopeMin={cumulativeSlopeMin})\n"+
               (writeCsv?"CSV="+GetCsvPath():"(CSV disabled)");
    }

    private string Classify(float finalErr, double slope, float netGrowth){
        if (finalErr <= stableFinalDeg && Math.Abs(slope) <= stableSlopeDeg) return "Stable";
        if (_jumpCount >= minJumpEvents && Math.Abs(slope) <= jumpSlopeUpper) return "Spiky";
        if (slope >= cumulativeSlopeMin && netGrowth > growthNetDeg) return "Cumulative";
        return "Unclassified";
    }

    private void WriteCsv(){
        try{
            string path = GetCsvPath(); using(var sw = new StreamWriter(path,false)){ sw.WriteLine("Step,ErrorDeg,DeltaErrorDeg"); if (writeFullSeries){ for(int i=0;i<_errors.Count;i++){ float d = i==0?0:_errors[i]-_errors[i-1]; sw.WriteLine($"{i+1},{_errors[i]:F6},{d:F6}"); } } else { var last=_errors[^1]; float d = _errors.Count>1? _errors[^1]-_errors[^2]:0; sw.WriteLine($"{_errors.Count},{last:F6},{d:F6}"); } sw.Flush(); }
            Debug.Log($"[RotationErrorTrend] CSV written: {path}");
        }catch(Exception ex){ Debug.LogError($"[RotationErrorTrend] CSV write failed: {ex.Message}"); }
    }

    private string GetCsvPath(){ return Path.Combine(Application.persistentDataPath, csvFileName); }
}

