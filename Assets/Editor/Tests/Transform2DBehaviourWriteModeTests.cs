using NUnit.Framework;
using UnityEngine;
using System.Collections;
using UnityEngine.TestTools;
using Random = UnityEngine.Random;


public class Transform2DBehaviourWriteModeTests
{
    private Transform2DBehaviour CreateWriteBehaviour(string name, Vector2 pos, float rotDeg, Vector2 scl, Transform parent = null)
    {
        var go = new GameObject(name);
        if (parent) go.transform.SetParent(parent, false);
        var b = go.AddComponent<Transform2DBehaviour>();
        b.syncMode = Transform2DBehaviour.SyncMode.WriteToUnity; // 写入 Unity Transform
        b.localPosition = pos;
        b.localRotationDegrees = rotDeg;
        b.localScale = scl;
        return b;
    }

    private static float Normalize360(float z) => Mathf.Repeat(z, 360f);

    private static void AssertWorldSync(Transform2DBehaviour b, string msg = "")
    {
        var tf = b.transform;
        Debug.Log(b.worldPosition+" "+tf.position+" "+tf.eulerAngles.z+" "+b.rotationDegrees+" "+tf.lossyScale+" "+b.worldScale);
        // 位置 (忽略 z)
        Assert.That(Vector2.SqrMagnitude(b.worldPosition - (Vector2)tf.position), Is.LessThan(1e-5f), $"Pos mismatch {msg}");
        // 旋转
        float tr = Normalize360(tf.eulerAngles.z);
        Assert.AreEqual(Normalize360(b.worldRotationDegrees), tr, 1e-3f, $"Rot mismatch {msg}");
        // 缩放：嵌套时使用 lossyScale
        Vector2 lossy = new Vector2(tf.lossyScale.x, tf.lossyScale.y);
        Assert.That(Vector2.SqrMagnitude(b.worldScale - lossy), Is.LessThan(1e-1f), $"Scale mismatch {msg}");
    }

    [Test]
    public void WriteMode_SimpleLocalEdits()
    {
        var b = CreateWriteBehaviour("WriteNode", new Vector2(1, 2), 10f, new Vector2(2, 0.5f));
        AssertWorldSync(b, "after init");

        b.localPosition = new Vector2(-3.2f, 7.7f);
        b.localRotationDegrees = 275.5f;
        b.localScale = new Vector2(0.25f, 3.5f);
        AssertWorldSync(b, "after local edits");
    }

    
    [UnityTest]
    public IEnumerator WriteMode_FrameDelaySafety()
    {
        var b = CreateWriteBehaviour("WriteDelay", Vector2.zero, 0f, Vector2.one);
        b.localPosition = new Vector2(5, -4);
        b.localRotationDegrees = 123f;
        b.localScale = new Vector2(2, 2.5f);
        yield return null; // 等一帧（若内部在 LateUpdate 执行同步）
        AssertWorldSync(b, "after 1 frame");
    }

    [Test]
    public void WriteMode_Reparent_KeepWorld()
    {
        var parentA = CreateWriteBehaviour("WA", new Vector2(2, 1), 30f, new Vector2(2, 1));
        var parentB = CreateWriteBehaviour("WB", new Vector2(-5, 3), -45f, new Vector2(0.5f, 3));
        var child = CreateWriteBehaviour("Child", new Vector2(1, -1), 15f, new Vector2(1.2f, 0.8f));
        child.SetParent(parentA, keepWorldPosition: false);
        AssertWorldSync(child, "init under A");

        Vector2 wPos = child.worldPosition;
        float wRot = child.worldRotationDegrees;
        Vector2 wScl = child.worldScale;

        child.SetParent(parentB, keepWorldPosition: true);
        Assert.That(Vector2.SqrMagnitude(child.worldPosition - wPos), Is.LessThan(1e-5f));
        Assert.AreEqual(Normalize360(wRot), Normalize360(child.worldRotationDegrees), 1e-4f);
        Assert.That(Vector2.SqrMagnitude(child.worldScale - wScl), Is.LessThan(1e-5f));
        AssertWorldSync(child, "after keepWorld reparent");
    }

    [Test]
    public void WriteMode_Reparent_RecalcLocal()
    {
        var p1 = CreateWriteBehaviour("P1", new Vector2(0, 0), 0, new Vector2(1, 1));
        var p2 = CreateWriteBehaviour("P2", new Vector2(10, 0), 90, new Vector2(2, 2));
        var ch = CreateWriteBehaviour("Ch", new Vector2(2, 3), 25, new Vector2(1, 1));
        ch.SetParent(p1, false);
        AssertWorldSync(ch, "under p1");

        var oldWorld = ch.worldPosition;
        ch.SetParent(p2, false); // 不保持世界 -> 世界应变化
        Assert.AreNotEqual(oldWorld, ch.worldPosition);
        AssertWorldSync(ch, "under p2");
    }

    [Test]
    public void WriteMode_DeepHierarchy_NegativeScale()
    {
        var r = CreateWriteBehaviour("R", new Vector2(1, 1), 15f, new Vector2(2, -1)); // 含负缩放
        var a = CreateWriteBehaviour("A", new Vector2(-2, 0.5f), -20f, new Vector2(-0.5f, 3));
        var b = CreateWriteBehaviour("B", new Vector2(4, -1), 95f, new Vector2(1.5f, -2));
        var leaf = CreateWriteBehaviour("Leaf", new Vector2(0.3f, 2.2f), 180f, new Vector2(-1, -1));

        a.SetParent(r, false);
        b.SetParent(a, false);
        leaf.SetParent(b, false);
        AssertWorldSync(r, "r");
        AssertWorldSync(a, "a");
        AssertWorldSync(b, "b");
        AssertWorldSync(leaf, "leaf");
    }

    [Test]
    public void WriteMode_SequentialMixedWrites()
    {
        var b = CreateWriteBehaviour("Seq", Vector2.zero, 0, Vector2.one);
        for (int i = 0; i < 25; i++)
        {
            float px = Mathf.Sin(i * 0.37f) * 5f;
            float py = Mathf.Cos(i * 0.29f) * 3f;
            b.localPosition = new Vector2(px, py);

            b.localRotationDegrees = i * 137.511f; // 利用不整除 360 的角度
            b.localScale = new Vector2(1f + (i % 5) * 0.2f, 1f + ((i + 2) % 7) * 0.15f);

            AssertWorldSync(b, $"iter {i}");
        }
    }

    [UnityTest]
    public IEnumerator WriteMode_RandomizedStability()
    {
        var parent = CreateWriteBehaviour("RndParent", new Vector2(3, -2), 45f, new Vector2(2, 1));
        var child = CreateWriteBehaviour("RndChild", new Vector2(1, 0), 0, new Vector2(1, 1));
        child.SetParent(parent, false);
        AssertWorldSync(child, "initial");

        Random.InitState(12345);
        for (int i = 0; i < 30; i++)
        {
            parent.localPosition = new Vector2(Random.Range(-5f, 5f), Random.Range(-5f, 5f));
            parent.localRotationDegrees = Random.Range(0f, 360f);
            parent.localScale = new Vector2(Random.Range(0.2f, 3f), Random.Range(0.2f, 3f));

            child.localPosition = new Vector2(Random.Range(-2f, 2f), Random.Range(-2f, 2f));
            child.localRotationDegrees = Random.Range(-720f, 720f);
            child.localScale = new Vector2(Random.Range(0.1f, 4f), Random.Range(0.1f, 4f));

            yield return null; // 给同步一帧
            AssertWorldSync(parent, $"parent iter {i}");
            AssertWorldSync(child, $"child iter {i}");
        }
    }

    [Test]
    public void WriteMode_BatchSetLocalTRS_API()
    {
        var b = CreateWriteBehaviour("Batch", new Vector2(0,0), 0, Vector2.one);
        b.SetLocalTRSDegrees(new Vector2(7.7f, -3.3f), 512.25f, new Vector2(2.25f, 0.75f));
        AssertWorldSync(b, "after batch api");
        Assert.AreEqual(Normalize360(512.25f), Normalize360(b.transform.eulerAngles.z), 1e-3f);
    }
}
