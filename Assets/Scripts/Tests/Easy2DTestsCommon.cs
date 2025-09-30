using Core.TrueSync;
using UnityEngine;

public static class Easy2DTestCommon
{
    public static float PosEps = 1e-5f;
    public static float RotEpsDeg = 1e-3f;
    public static float ScaleEps = 1e-3f;
    public static float DirEps = 1e-5f;

    public static float N360(float z) => (z % 360f + 360f) % 360f;

    public static void AssertNear(Vector2 a, Vector2 b, float eps, string msg)
    {
        if ((a - b).sqrMagnitude > eps * eps)
            Debug.LogError($"[FAIL] {msg} Vec diff {a} vs {b} eps={eps}");
    }
    public static void AssertNear(float a, float b, float eps, string msg)
    {
        if (Mathf.Abs(a - b) > eps)
            Debug.LogError($"[FAIL] {msg} Float diff {a} vs {b} eps={eps}");
    }

    public static void AssertAngle(float aDeg, float bDeg, float eps, string msg)
    {
        float da = Mathf.DeltaAngle(aDeg, bDeg);
        if (Mathf.Abs(da) > eps)
            Debug.LogError($"[FAIL] {msg} Angle diff {aDeg} vs {bDeg} delta={da} eps={eps}");
    }

    public static void LogPass(string msg) => Debug.Log($"[PASS] {msg}");

    public static void CompareEasyVsFixed(
        EasyTransform2DBehaviour e,
        EasyFixedTransform2DBehaviour f,
        string tag)
    {
        e.ForceSyncNow();
        f.ForceSyncNow();

        Vector2 wpE = e.worldPosition;
        Vector2 wpF = f.worldPosition.ToVector();  // 假设 fixed 版可隐式转
        AssertNear(wpE, wpF, PosEps * 5f, tag + " worldPosition");

        float wrE = N360(e.worldRotationDegrees);
        float wrF = N360((float)f.worldRotationDegrees);
        AssertAngle(wrE, wrF, RotEpsDeg * 5f, tag + " worldRotation");

        Vector2 wsE = e.worldScale;
        Vector2 wsF = f.worldScale.ToVector();
        AssertNear(wsE, wsF, ScaleEps * 5f, tag + " worldScale");
    }
}