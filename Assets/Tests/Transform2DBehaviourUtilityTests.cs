using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;

public class Transform2DBehaviourUtilityTests
{
    private Transform2DBehaviour Create(string name, Vector2 lp, float rotDeg, Vector2 scl, Transform parent = null)
    {
        var go = new GameObject(name);
        if (parent) go.transform.SetParent(parent, false);
        var b = go.AddComponent<Transform2DBehaviour>();
        b.syncMode = Transform2DBehaviour.SyncMode.WriteToUnity;
        b.localPosition = lp;
        b.localRotationDegrees = rotDeg;
        b.localScale = scl;
        return b;
    }

    private static float N360(float z) => ((z % 360f) + 360f) % 360f;

    private static void AssertWorldSync(Transform2DBehaviour b, string msg = "")
    {
        b.ForceSyncNow(true);
        var tf = b.transform;
        Debug.Log(b.worldPosition + " " + tf.position + " " + tf.eulerAngles.z + " " + b.worldRotationDegrees + " " + tf.lossyScale + " " + b.worldScale + " " + msg);
        Assert.That(Vector2.SqrMagnitude(b.worldPosition - (Vector2)tf.position), Is.LessThan(1e-5f), "Pos " + msg);
        Assert.AreEqual(N360(b.worldRotationDegrees), N360(tf.eulerAngles.z), 1e-3f, "Rot " + msg);
        Vector2 lossy = new Vector2(tf.lossyScale.x, tf.lossyScale.y);
        Assert.That(Vector2.SqrMagnitude(b.worldScale - lossy), Is.LessThan(1e-4f), "Scale " + msg);
    }

    private static void AssertMatrixApproxEqual(Matrix4x4 a, Matrix4x4 b, float eps = 1e-4f)
    {
        // 比较 2D 相关部分
        Assert.That(Mathf.Abs(a.m00 - b.m00), Is.LessThan(eps));
        Assert.That(Mathf.Abs(a.m01 - b.m01), Is.LessThan(eps));
        Assert.That(Mathf.Abs(a.m10 - b.m10), Is.LessThan(eps));
        Assert.That(Mathf.Abs(a.m11 - b.m11), Is.LessThan(eps));
        Assert.That(Mathf.Abs(a.m03 - b.m03), Is.LessThan(eps));
        Assert.That(Mathf.Abs(a.m13 - b.m13), Is.LessThan(eps));
    }

    [Test]
    public void Utility_WorldMatrix_Matches_Unity()
    {
        var p = Create("P", new Vector2(2, -1), 35, new Vector2(2, -1));
        var c = Create("C", new Vector2(-3, 4), -75, new Vector2(-0.5f, 3));
        c.SetParent(p, false);
        AssertWorldSync(p, "parent");
        AssertWorldSync(c, "child");
        AssertMatrixApproxEqual(p.Node.WorldMatrix4x4, p.transform.localToWorldMatrix);
        AssertMatrixApproxEqual(c.Node.WorldMatrix4x4, c.transform.localToWorldMatrix);
    }

    [Test]
    public void Utility_TransformPoint_Inverse_RoundTrip()
    {
        var p = Create("Root", new Vector2(1.2f, -3.4f), 123.4f, new Vector2(2, -1.5f));
        var c = Create("Leaf", new Vector2(-2, 5), -210f, new Vector2(-0.75f, 0.33f));
        c.SetParent(p, false);

        for (int i = 0; i < 25; i++)
        {
            Vector2 lp = new Vector2(Mathf.Sin(i * 0.37f) * 3f, Mathf.Cos(i * 0.29f) * 2f);
            Vector2 wp1 = c.Node.TransformPoint(lp);
            Vector3 wp2v3 = c.transform.TransformPoint(new Vector3(lp.x, lp.y, 0));
            Vector2 wp2 = new Vector2(wp2v3.x, wp2v3.y);
            Assert.That(Vector2.SqrMagnitude(wp1 - wp2), Is.LessThan(1e-5f), "TransformPoint iter " + i);

            Vector2 lpBack = c.Node.InverseTransformPoint(wp1);
            Assert.That(Vector2.SqrMagnitude(lpBack - lp), Is.LessThan(1e-5f), "Inverse roundtrip " + i);
        }
    }

    [Test]
    public void Utility_TransformDirection_RoundTrip()
    {
        var b = Create("B", new Vector2(0.5f, 1), 275f, new Vector2(-2, 0.5f));
        for (int i = 0; i < 20; i++)
        {
            Vector2 d = new Vector2(Mathf.Cos(i * 0.41f), Mathf.Sin(i * 1.23f));
            Vector2 wd1 = b.Node.TransformDirection(d);
            Vector3 wd2v3 = b.transform.TransformDirection(new Vector3(d.x, d.y, 0));
            Vector2 wd2 = new Vector2(wd2v3.x, wd2v3.y);
            // 方向可能带缩放；我们比较归一化方向
            Assert.That(Vector2.SqrMagnitude(wd1.normalized - wd2.normalized), Is.LessThan(1e-5f), "Dir iter " + i);

            Vector2 localBack = b.Node.InverseTransformDirection(wd1);
            Assert.That(Vector2.SqrMagnitude((localBack.normalized - d.normalized)), Is.LessThan(1e-5f), "Dir inverse " + i);
        }
    }

    [Test]
    public void Utility_SetWorldPositionRotation_Precise()
    {
        var p = Create("P", new Vector2(3, -2), -33f, new Vector2(-1.2f, 2.5f));
        var c = Create("C", new Vector2(1, 2), 10f, new Vector2(0.5f, -0.8f));
        c.SetParent(p, false);
        AssertWorldSync(c, "before");

        Vector2 targetPos = new Vector2(-7.3f, 4.6f);
        float targetRot = 512.75f;
        c.position=(targetPos);
        c.rotationDegrees=(targetRot);
        Debug.Log(c.Node);
        Assert.That(Vector2.SqrMagnitude(c.worldPosition - targetPos), Is.LessThan(1e-5f));
        Assert.AreEqual(N360(targetRot), N360(c.worldRotationDegrees), 1e-4f);
        AssertWorldSync(c, "after set world");
    }

    [Test]
    public void Utility_LookAt_And_GetAngleTo()
    {
        var b = Create("Looker", Vector2.zero, 0, new Vector2(1, 1));
        Vector2[] targets =
        {
            new Vector2(5,0),
            new Vector2(0,4),
            new Vector2(-3,3),
            new Vector2(-2,-5),
            new Vector2(6,-1)
        };
        foreach (var t in targets)
        {
            b.Node.LookAt(t);
            Vector2 dir = (t - b.worldPosition).normalized;
            float expect = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Assert.AreEqual(N360(expect), N360(b.worldRotationDegrees), 1e-3f, "LookAt " + t);
            float delta = b.Node.GetAngleTo(t);
            Assert.That(Mathf.Abs(delta) < 1e-3f, "GetAngleTo ~0 after LookAt");
        }
    }

    [Test]
    public void Utility_Rotate_And_Translate_Self_World()
    {
        var b = Create("Mover", new Vector2(2, -1), 45f, new Vector2(2, -3));
        AssertWorldSync(b, "before");
        Vector2 original = b.worldPosition;
        float originalRot = b.worldRotationDegrees;
        b.Node.Translate(new Vector2(1, 2), Space.Self);
        b.transform.Translate(new Vector2(1, 2),Space.Self);
        Vector2 expect = b.transform.position;
        Vector3 v3 = b.transform.position;
        b.ForceSyncNow();
        Debug.Log(b.Node.worldPosition+" "+v3);
        Assert.That(Vector2.SqrMagnitude(b.worldPosition - expect), Is.LessThan(1e-4f), "Translate self");
        AssertWorldSync(b, "after self translate");

        b.Node.Translate(new Vector2(-3, 1), Space.World);
        expect += new Vector2(-3, 1);
        Assert.That(Vector2.SqrMagnitude(b.worldPosition - expect), Is.LessThan(1e-4f), "Translate world");

        b.Node.Rotate(90f, Space.Self);
        Assert.AreEqual(N360(originalRot + 90f), N360(b.worldRotationDegrees), 1e-3f, "Rotate self");

        b.Node.Rotate(-45f, Space.World);
        Assert.AreEqual(N360(originalRot + 45f), N360(b.worldRotationDegrees), 1e-3f, "Rotate world");

        AssertWorldSync(b, "final");
    }

    [Test]
    public void Utility_Reparent_KeepWorld_With_NegativeScale()
    {
        var a = Create("A", new Vector2(1, 2), 25f, new Vector2(-2, 1));
        var b = Create("B", new Vector2(-3, -1), -70f, new Vector2(0.5f, -3));
        var c = Create("C", new Vector2(2, -2), 10f, new Vector2(-1, -1));
        c.SetParent(a, false);
        Debug.Log(c.Node.localPosition+" "+c.Node.localRotationDegrees+" "+c.Node.localScale+" "+c.transform.parent);
        AssertWorldSync(c, "under A");
        Vector2 wp = c.worldPosition;
        float wr = c.worldRotationDegrees;
        Vector2 ws = c.worldScale;

        c.SetParent(b, true);
        AssertWorldSync(c, "after b");
        Assert.That(Vector2.SqrMagnitude(c.worldPosition - wp), Is.LessThan(1e-5f));
        Assert.AreEqual(N360(wr), N360(c.worldRotationDegrees), 1e-4f);
        Assert.That(Vector2.SqrMagnitude(c.worldScale - ws), Is.LessThan(1e-5f));
        AssertWorldSync(c, "after reparent keepWorld");
    }

    [UnityTest]
    public IEnumerator Utility_Random_RoundTrip_Stability()
    {
        var root = Create("Root", new Vector2(1, -2), 15f, new Vector2(2, -1));
        var mid = Create("Mid", new Vector2(-3, 4), -120f, new Vector2(-0.5f, 3));
        var leaf = Create("Leaf", new Vector2(0.7f, -1.1f), 200f, new Vector2(-1.2f, -0.8f));
        mid.SetParent(root, false);
        leaf.SetParent(mid, false);

        Random.InitState(9876);
        for (int frame = 0; frame < 20; frame++)
        {
            // 随机编辑
            root.localPosition = new Vector2(Random.Range(-5f, 5f), Random.Range(-5f, 5f));
            root.localRotationDegrees = Random.Range(-720f, 720f);
            root.localScale = new Vector2(Random.Range(0.2f, 3f) * (Random.value < 0.5f ? -1 : 1),
                                          Random.Range(0.2f, 3f) * (Random.value < 0.5f ? -1 : 1));

            mid.localPosition = new Vector2(Random.Range(-3f, 3f), Random.Range(-3f, 3f));
            mid.localRotationDegrees = Random.Range(-1080f, 1080f);
            mid.localScale = new Vector2(Random.Range(0.2f, 4f) * (Random.value < 0.5f ? -1 : 1),
                                         Random.Range(0.2f, 4f) * (Random.value < 0.5f ? -1 : 1));

            leaf.localPosition = new Vector2(Random.Range(-2f, 2f), Random.Range(-2f, 2f));
            leaf.localRotationDegrees = Random.Range(-1440f, 1440f);
            leaf.localScale = new Vector2(Random.Range(0.1f, 2f) * (Random.value < 0.5f ? -1 : 1),
                                          Random.Range(0.1f, 2f) * (Random.value < 0.5f ? -1 : 1));

            yield return null;

            AssertWorldSync(root, "root frame " + frame);
            AssertWorldSync(mid, "mid frame " + frame);
            AssertWorldSync(leaf, "leaf frame " + frame);

            // 点/方向往返随机
            Vector2 lp = new Vector2(Random.Range(-2f, 2f), Random.Range(-2f, 2f));
            Vector2 wp = leaf.Node.TransformPoint(lp);
            Vector2 lpBack = leaf.Node.InverseTransformPoint(wp);
            Assert.That(Vector2.SqrMagnitude(lp - lpBack), Is.LessThan(1e-5f), "roundtrip point " + frame);

            Vector2 d = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f));
            if (d.sqrMagnitude > 1e-6f)
            {
                Vector2 wd = leaf.Node.TransformDirection(d);
                Vector2 dBack = leaf.Node.InverseTransformDirection(wd);
                Assert.That(Vector2.SqrMagnitude(d.normalized - dBack.normalized), Is.LessThan(1e-5f), "roundtrip dir " + frame);
            }
        }
    }

    [Test]
    public void Utility_ExtremeRotationAndTinyScale()
    {
        var b = Create("Extreme", new Vector2(0,0), 0, new Vector2(1e-7f, -1e-8f));
        // 触发最小缩放夹紧
        Assert.That(Mathf.Abs(b.localScale.x) >= 1e-6f);
        Assert.That(Mathf.Abs(b.localScale.y) >= 1e-6f);

        b.localRotationDegrees = 1234567.89f;
        Assert.AreEqual(N360(1234567.89f), N360(b.worldRotationDegrees), 1e-3f);
        AssertWorldSync(b, "extreme");
    }
}
