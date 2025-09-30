using System.Collections;
using Core.TrueSync;
using UnityEngine;

public class Easy2DTestRunner : MonoBehaviour
{
    [Header("开关单项测试")]
    public bool runAll = true;

    private IEnumerator Start()
    {
        if (runAll)
        {
            yield return Test_SimpleLocalEdits();
            yield return Test_Reparent_KeepWorld();
            yield return Test_Reparent_RecalcLocal();
            yield return Test_DeepHierarchy_NegScale();
            yield return Test_SequentialMixedWrites();
            yield return Test_WorldMatrix_And_PointRoundTrip();
            yield return Test_DirectionRoundTrip();
            yield return Test_SetWorldPositionRotation();
            yield return Test_LookAt_AngleTo();
            yield return Test_Rotate_Translate();
            yield return Test_Reparent_KeepWorld_NegScale();
            yield return Test_Random_Stability();
            yield return Test_ExtremeRotation_TinyScale();
            yield return Test_FloatVsFixed_BasicConsistency();
        }

        Debug.Log("<color=lime>All Easy2D runtime tests scheduled.</color>");
    }

    #region 工具创建
    private EasyTransform2DBehaviour CreateEasy(string name, Vector2 lp, float rotDeg, Vector2 scl, Transform parent = null)
    {
        var go = new GameObject("[E]" + name);
        if (parent) go.transform.SetParent(parent, false);
        var b = go.AddComponent<EasyTransform2DBehaviour>();
        b.localPosition = lp;
        b.localRotationDegrees = rotDeg;
        b.localScale = scl;
        b.ForceSyncNow();
        return b;
    }

    private EasyFixedTransform2DBehaviour CreateEasyFixed(string name, Vector2 lp, float rotDeg, Vector2 scl, Transform parent = null)
    {
        var go = new GameObject("[F]" + name);
        if (parent) go.transform.SetParent(parent, false);
        var b = go.AddComponent<EasyFixedTransform2DBehaviour>();
        b.localPosition = lp.ToTSVector2();
        b.localRotationDegrees = rotDeg;
        b.localScale = scl.ToTSVector2();
        b.ForceSyncNow();
        return b;
    }

    private void AssertWorldSync(EasyTransform2DBehaviour b, string tag)
    {
        b.ForceSyncNow();
        var tf = b.transform;
        Easy2DTestCommon.AssertNear(b.worldPosition, (Vector2)tf.position, Easy2DTestCommon.PosEps, tag + " world pos");
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(b.worldRotationDegrees),
            Easy2DTestCommon.N360(tf.eulerAngles.z),
            Easy2DTestCommon.RotEpsDeg, tag + " world rot");
        Vector2 lossy = new Vector2(tf.lossyScale.x, tf.lossyScale.y);
        Easy2DTestCommon.AssertNear(b.worldScale, lossy, Easy2DTestCommon.ScaleEps, tag + " world scale");
    }
    #endregion

    private IEnumerator Test_SimpleLocalEdits()
    {
        var b = CreateEasy("Simple", new Vector2(1,2), 10f, new Vector2(2,0.5f));
        AssertWorldSync(b, "init");
        b.localPosition = new Vector2(-3.2f, 7.7f);
        b.localRotationDegrees = 275.5f;
        b.localScale = new Vector2(0.25f, 3.5f);
        yield return null; // 若内部延迟写
        AssertWorldSync(b, "after edits");
        Easy2DTestCommon.LogPass(nameof(Test_SimpleLocalEdits));
    }

    private IEnumerator Test_Reparent_KeepWorld()
    {
        var a = CreateEasy("A", new Vector2(2,1), 30, new Vector2(2,1));
        var b = CreateEasy("B", new Vector2(-5,3), -45, new Vector2(0.5f,3));
        var child = CreateEasy("Child", new Vector2(1,-1), 15, new Vector2(1.2f,0.8f));

        child.transform.SetParent(a.transform, false);
        yield return null;
        AssertWorldSync(child, "under A");

        Vector2 wp = child.worldPosition;
        float wr = child.worldRotationDegrees;
        Vector2 ws = child.worldScale;

        child.transform.SetParent(b.transform, true);
        yield return null;

        Easy2DTestCommon.AssertNear(child.worldPosition, wp, 1e-5f, "keepWorld pos");
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(wr), Easy2DTestCommon.N360(child.worldRotationDegrees), 1e-4f, "keepWorld rot");
        Easy2DTestCommon.AssertNear(child.worldScale, ws, 1e-5f, "keepWorld scale");
        AssertWorldSync(child, "after reparent");
        Easy2DTestCommon.LogPass(nameof(Test_Reparent_KeepWorld));
    }

    private IEnumerator Test_Reparent_RecalcLocal()
    {
        var p1 = CreateEasy("P1", Vector2.zero, 0, Vector2.one);
        var p2 = CreateEasy("P2", new Vector2(10,0), 90, new Vector2(2,2));
        var ch = CreateEasy("Ch", new Vector2(2,3), 25, Vector2.one);
        ch.transform.SetParent(p1.transform, false);
        yield return null;
        AssertWorldSync(ch, "under p1");
        var old = ch.worldPosition;
        ch.transform.SetParent(p2.transform, false);
        yield return null;
        if (old == ch.worldPosition) Debug.LogError("World should change when not keeping world.");
        AssertWorldSync(ch, "under p2");
        Easy2DTestCommon.LogPass(nameof(Test_Reparent_RecalcLocal));
    }

    private IEnumerator Test_DeepHierarchy_NegScale()
    {
        var r = CreateEasy("R", new Vector2(1,1), 15, new Vector2(2,-1));
        var a = CreateEasy("A", new Vector2(-2,0.5f), -20, new Vector2(-0.5f,3));
        var b = CreateEasy("B", new Vector2(4,-1), 95, new Vector2(1.5f,-2));
        var leaf = CreateEasy("Leaf", new Vector2(0.3f,2.2f), 180, new Vector2(-1,-1));
        a.transform.SetParent(r.transform,false);
        b.transform.SetParent(a.transform,false);
        leaf.transform.SetParent(b.transform,false);
        yield return null;
        AssertWorldSync(r,"r");
        AssertWorldSync(a,"a");
        AssertWorldSync(b,"b");
        AssertWorldSync(leaf,"leaf");
        Easy2DTestCommon.LogPass(nameof(Test_DeepHierarchy_NegScale));
    }

    private IEnumerator Test_SequentialMixedWrites()
    {
        var b = CreateEasy("Seq", Vector2.zero, 0, Vector2.one);
        for (int i=0;i<20;i++)
        {
            b.localPosition = new Vector2(Mathf.Sin(i*0.37f)*5f, Mathf.Cos(i*0.29f)*3f);
            b.localRotationDegrees = i * 137.511f;
            b.localScale = new Vector2(1f + (i%5)*0.2f, 1f + ((i+2)%7)*0.15f);
            yield return null;
            AssertWorldSync(b,"iter "+i);
        }
        Easy2DTestCommon.LogPass(nameof(Test_SequentialMixedWrites));
    }

    private IEnumerator Test_WorldMatrix_And_PointRoundTrip()
    {
        var p = CreateEasy("P", new Vector2(2,-1), 35, new Vector2(2,-1));
        var c = CreateEasy("C", new Vector2(-3,4), -75, new Vector2(-0.5f,3));
        c.transform.SetParent(p.transform,false);
        yield return null;
        AssertWorldSync(p,"p");
        AssertWorldSync(c,"c");

        for (int i=0;i<15;i++)
        {
            Vector2 lp = new Vector2(Mathf.Sin(i*0.37f)*3f, Mathf.Cos(i*0.29f)*2f);
            Vector2 wp1 = c.Node.TransformPoint(lp);
            Vector3 wp2v3 = c.transform.TransformPoint(new Vector3(lp.x, lp.y,0));
            Vector2 wp2 = new Vector2(wp2v3.x, wp2v3.y);
            Easy2DTestCommon.AssertNear(wp1, wp2, 1e-5f, "TransformPoint "+i);

            Vector2 back = c.Node.InverseTransformPoint(wp1);
            Easy2DTestCommon.AssertNear(back, lp, 1e-5f, "InversePoint "+i);
        }
        Easy2DTestCommon.LogPass(nameof(Test_WorldMatrix_And_PointRoundTrip));
    }

    private IEnumerator Test_DirectionRoundTrip()
    {
        var b = CreateEasy("Dir", new Vector2(0.5f,1), 275, new Vector2(-2,0.5f));
        yield return null;
        for (int i=0;i<12;i++)
        {
            Vector2 d = new Vector2(Mathf.Cos(i*0.41f), Mathf.Sin(i*1.23f));
            Vector2 wd1 = b.Node.TransformDirection(d);
            Vector3 wd2v3 = b.transform.TransformDirection(new Vector3(d.x,d.y,0));
            Vector2 wd2 = new Vector2(wd2v3.x, wd2v3.y);
            Easy2DTestCommon.AssertNear(wd1.normalized, wd2.normalized, 1e-5f, "Dir "+i);

            Vector2 localBack = b.Node.InverseTransformDirection(wd1);
            Easy2DTestCommon.AssertNear(localBack.normalized, d.normalized, 1e-5f, "Dir back "+i);
        }
        Easy2DTestCommon.LogPass(nameof(Test_DirectionRoundTrip));
    }

    private IEnumerator Test_SetWorldPositionRotation()
    {
        var p = CreateEasy("P", new Vector2(3,-2), -33, new Vector2(-1.2f,2.5f));
        var c = CreateEasy("C", new Vector2(1,2), 10, new Vector2(0.5f,-0.8f));
        c.transform.SetParent(p.transform,false);
        yield return null;

        Vector2 targetPos = new Vector2(-7.3f, 4.6f);
        float targetRot = 512.75f;
        c.position = targetPos;
        c.rotationDegrees = targetRot;
        yield return null;

        Easy2DTestCommon.AssertNear(c.worldPosition, targetPos, 1e-5f, "SetWorld pos");
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(targetRot), Easy2DTestCommon.N360(c.worldRotationDegrees), 1e-4f, "SetWorld rot");
        AssertWorldSync(c,"after set");
        Easy2DTestCommon.LogPass(nameof(Test_SetWorldPositionRotation));
    }

    private IEnumerator Test_LookAt_AngleTo()
    {
        var b = CreateEasy("Looker", Vector2.zero, 0, Vector2.one);
        yield return null;
        Vector2[] targets = {
            new Vector2(5,0), new Vector2(0,4),
            new Vector2(-3,3), new Vector2(-2,-5),
            new Vector2(6,-1)
        };
        foreach (var t in targets)
        {
            b.Node.LookAt(t);
            yield return null;
            Vector2 dir = (t - b.worldPosition).normalized;
            float expect = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(expect), Easy2DTestCommon.N360(b.worldRotationDegrees), 1e-3f, "LookAt "+t);
            float delta = b.Node.GetAngleTo(t);
            if (Mathf.Abs(delta) > 1e-3f) Debug.LogError("GetAngleTo not ~0");
        }
        Easy2DTestCommon.LogPass(nameof(Test_LookAt_AngleTo));
    }

    private IEnumerator Test_Rotate_Translate()
    {
        var b = CreateEasy("Mover", new Vector2(2,-1), 45, new Vector2(2,-3));
        yield return null;
        Vector2 expect = b.worldPosition;

        b.Node.Translate(new Vector2(1,2), Space.Self);
        yield return null;
        Vector3 unityAfter = b.transform.position; // 已写入
        Easy2DTestCommon.AssertNear(b.worldPosition, (Vector2)unityAfter, 1e-4f, "Translate self");

        b.Node.Translate(new Vector2(-3,1), Space.World);
        expect = (Vector2)unityAfter + new Vector2(-3,1);
        yield return null;
        Easy2DTestCommon.AssertNear(b.worldPosition, expect, 1e-4f, "Translate world");

        float baseRot = b.worldRotationDegrees;
        b.Node.Rotate(90, Space.Self);
        yield return null;
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(baseRot+90), Easy2DTestCommon.N360(b.worldRotationDegrees), 1e-3f, "Rotate self");

        b.Node.Rotate(-45, Space.World);
        yield return null;
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(baseRot+45), Easy2DTestCommon.N360(b.worldRotationDegrees), 1e-3f, "Rotate world");

        Easy2DTestCommon.LogPass(nameof(Test_Rotate_Translate));
    }

    private IEnumerator Test_Reparent_KeepWorld_NegScale()
    {
        var a = CreateEasy("A", new Vector2(1,2), 25, new Vector2(-2,1));
        var b = CreateEasy("B", new Vector2(-3,-1), -70, new Vector2(0.5f,-3));
        var c = CreateEasy("C", new Vector2(2,-2), 10, new Vector2(-1,-1));
        c.transform.SetParent(a.transform,false);
        yield return null;
        Vector2 wp = c.worldPosition;
        float wr = c.worldRotationDegrees;
        Vector2 ws = c.worldScale;

        c.transform.SetParent(b.transform,true);
        yield return null;
        Easy2DTestCommon.AssertNear(c.worldPosition, wp, 1e-5f, "keep world pos");
        Easy2DTestCommon.AssertAngle(Easy2DTestCommon.N360(wr), Easy2DTestCommon.N360(c.worldRotationDegrees), 1e-4f, "keep world rot");
        Easy2DTestCommon.AssertNear(c.worldScale, ws, 1e-5f, "keep world scale");
        Easy2DTestCommon.LogPass(nameof(Test_Reparent_KeepWorld_NegScale));
    }

    private IEnumerator Test_Random_Stability()
    {
        var root = CreateEasy("Root", new Vector2(1,-2), 15, new Vector2(2,-1));
        var mid  = CreateEasy("Mid", new Vector2(-3,4), -120, new Vector2(-0.5f,3));
        var leaf = CreateEasy("Leaf", new Vector2(0.7f,-1.1f), 200, new Vector2(-1.2f,-0.8f));
        mid.transform.SetParent(root.transform,false);
        leaf.transform.SetParent(mid.transform,false);
        yield return null;

        Random.InitState(9876);
        for (int frame=0; frame<15; frame++)
        {
            root.localPosition = new Vector2(Random.Range(-5f,5f), Random.Range(-5f,5f));
            root.localRotationDegrees = Random.Range(-720f,720f);
            root.localScale = new Vector2(Random.Range(0.2f,3f)*(Random.value<0.5f?-1:1),
                                          Random.Range(0.2f,3f)*(Random.value<0.5f?-1:1));

            mid.localPosition = new Vector2(Random.Range(-3f,3f), Random.Range(-3f,3f));
            mid.localRotationDegrees = Random.Range(-1080f,1080f);
            mid.localScale = new Vector2(Random.Range(0.2f,4f)*(Random.value<0.5f?-1:1),
                                         Random.Range(0.2f,4f)*(Random.value<0.5f?-1:1));

            leaf.localPosition = new Vector2(Random.Range(-2f,2f), Random.Range(-2f,2f));
            leaf.localRotationDegrees = Random.Range(-1440f,1440f);
            leaf.localScale = new Vector2(Random.Range(0.1f,2f)*(Random.value<0.5f?-1:1),
                                          Random.Range(0.1f,2f)*(Random.value<0.5f?-1:1));

            yield return null;

            AssertWorldSync(root,"root "+frame);
            AssertWorldSync(mid,"mid "+frame);
            AssertWorldSync(leaf,"leaf "+frame);

            Vector2 lp = new Vector2(Random.Range(-2f,2f), Random.Range(-2f,2f));
            Vector2 wp = leaf.Node.TransformPoint(lp);
            Vector2 lpBack = leaf.Node.InverseTransformPoint(wp);
            Easy2DTestCommon.AssertNear(lpBack, lp, 1e-5f, "point round "+frame);

            Vector2 d = new Vector2(Random.Range(-1f,1f), Random.Range(-1f,1f));
            if (d.sqrMagnitude > 1e-6f)
            {
                Vector2 wd = leaf.Node.TransformDirection(d);
                Vector2 dBack = leaf.Node.InverseTransformDirection(wd);
                Easy2DTestCommon.AssertNear(dBack.normalized, d.normalized, 1e-5f, "dir round "+frame);
            }
        }
        Easy2DTestCommon.LogPass(nameof(Test_Random_Stability));
    }

    private IEnumerator Test_ExtremeRotation_TinyScale()
    {
        var b = CreateEasy("Extreme", Vector2.zero, 0, new Vector2(1e-7f,-1e-8f));
        yield return null;
        // 若内部做最小尺度夹紧，只验证非零并>=阈值(示例1e-6)
        if (Mathf.Abs(b.localScale.x) < 1e-6f || Mathf.Abs(b.localScale.y) < 1e-6f)
            Debug.LogError("Scale clamp failed (expected min).");
        b.localRotationDegrees = 1234567.89f;
        yield return null;
        Easy2DTestCommon.AssertAngle(
            Easy2DTestCommon.N360(1234567.89f),
            Easy2DTestCommon.N360(b.worldRotationDegrees),
            1e-3f,"extreme rot");
        Easy2DTestCommon.LogPass(nameof(Test_ExtremeRotation_TinyScale));
    }

    private IEnumerator Test_FloatVsFixed_BasicConsistency()
    {
        var e = CreateEasy("FloatA", new Vector2(1,2), 30, new Vector2(2,-1));
        var f = CreateEasyFixed("FixedA", new Vector2(1,2), 30, new Vector2(2,-1));

        yield return null;
        Easy2DTestCommon.CompareEasyVsFixed(e,f,"init");

        for (int i=0;i<10;i++)
        {
            Vector2 lp = new Vector2(Mathf.Sin(i*0.37f)*3f, Mathf.Cos(i*0.29f)*2f);
            float rot = i * 137.5f;
            Vector2 sc = new Vector2( (i%3+1)*0.5f*( (i&1)==0?1:-1),
                                      (i%4+1)*0.4f*( (i&2)==0?1:-1));

            e.SetLocalTRSDegrees(lp, rot, sc);
            f.SetLocalTRSDegrees(lp.ToTSVector2(), rot, sc.ToTSVector2());
            yield return null;

            Easy2DTestCommon.CompareEasyVsFixed(e,f,"iter "+i);

            // 世界操作
            Vector2 wAdd = new Vector2(0.25f*i, -0.1f*i);
            e.position = e.worldPosition + wAdd;
            f.position = f.worldPosition + wAdd.ToTSVector2();
            e.rotationDegrees = e.worldRotationDegrees + 33.333f;
            f.rotationDegrees = f.worldRotationDegrees + 33.333f;
            yield return null;
            Easy2DTestCommon.CompareEasyVsFixed(e,f,"world op "+i);

            // 平移/旋转自空间
            e.Node.Translate(new Vector2(0.3f,-0.2f), Space.Self);
            f.Node.Translate(new Core.TrueSync.TSVector2(0.3f,-0.2f), Space.Self);
            e.Node.Rotate(15f, Space.World);
            f.Node.Rotate(15f, Space.World);
            yield return null;
            Easy2DTestCommon.CompareEasyVsFixed(e,f,"post translate/rotate "+i);
        }

        Easy2DTestCommon.LogPass(nameof(Test_FloatVsFixed_BasicConsistency));
    }
}
