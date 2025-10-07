#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Core.TrueSync;

[CustomEditor(typeof(EasyFixedTransform3DBehaviour))]
[CanEditMultipleObjects]
public class EasyFixedTransform3DBehaviourEditor : Editor
{
    private EasyFixedTransform3DBehaviour comp;

    private SerializedProperty setLocalPosition;
    private SerializedProperty initialLocalPosition;
    private SerializedProperty errorIfUnsetLocalPosition;

    private SerializedProperty setLocalEulerDegrees;
    private SerializedProperty initialLocalEulerDegrees;
    private SerializedProperty errorIfUnsetLocalEuler;

    private SerializedProperty setLocalScale;
    private SerializedProperty initialLocalScale;
    private SerializedProperty errorIfUnsetLocalScale;

    private SerializedProperty syncPosition;
    private SerializedProperty syncLinear;

    private SerializedProperty keepUnityScaleWOnWrite;

    private void OnEnable()
    {
        comp = (EasyFixedTransform3DBehaviour)target;

        setLocalPosition = serializedObject.FindProperty("setLocalPosition");
        initialLocalPosition = serializedObject.FindProperty("initialLocalPosition");
        errorIfUnsetLocalPosition = serializedObject.FindProperty("errorIfUnsetLocalPosition");

        setLocalEulerDegrees = serializedObject.FindProperty("setLocalEulerDegrees");
        initialLocalEulerDegrees = serializedObject.FindProperty("initialLocalEulerDegrees");
        errorIfUnsetLocalEuler = serializedObject.FindProperty("errorIfUnsetLocalEuler");

        setLocalScale = serializedObject.FindProperty("setLocalScale");
        initialLocalScale = serializedObject.FindProperty("initialLocalScale");
        errorIfUnsetLocalScale = serializedObject.FindProperty("errorIfUnsetLocalScale");

        syncPosition = serializedObject.FindProperty("syncPosition");
        syncLinear = serializedObject.FindProperty("syncLinear");

        keepUnityScaleWOnWrite = serializedObject.FindProperty("keepUnityScaleWOnWrite");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawLocalInitSection();
        EditorGUILayout.Space(6f);
        DrawSyncToggles();
        EditorGUILayout.Space(6f);
        DrawRuntimeLocalSection();
        EditorGUILayout.Space(6f);
        DrawWorldReadonlySection();
        EditorGUILayout.Space(6f);
        DrawForceAPIs();
        EditorGUILayout.Space(6f);
        DrawParentSection();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawLocalInitSection()
    {
        EditorGUILayout.LabelField("本地初始配置", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(setLocalPosition, new GUIContent("设置初始 LocalPosition"));
        if (setLocalPosition.boolValue)
            EditorGUILayout.PropertyField(initialLocalPosition, new GUIContent("初始 LocalPosition"));
        else
            EditorGUILayout.PropertyField(errorIfUnsetLocalPosition, new GUIContent("未设置时报错"));

        EditorGUILayout.PropertyField(setLocalEulerDegrees, new GUIContent("设置初始 LocalEuler (Deg)"));
        if (setLocalEulerDegrees.boolValue)
            EditorGUILayout.PropertyField(initialLocalEulerDegrees, new GUIContent("初始欧拉(度)"));
        else
            EditorGUILayout.PropertyField(errorIfUnsetLocalEuler, new GUIContent("未设置时报错"));

        EditorGUILayout.PropertyField(setLocalScale, new GUIContent("设置初始 LocalScale"));
        if (setLocalScale.boolValue)
            EditorGUILayout.PropertyField(initialLocalScale, new GUIContent("初始 LocalScale"));
        else
            EditorGUILayout.PropertyField(errorIfUnsetLocalScale, new GUIContent("未设置时报错"));

        EditorGUILayout.PropertyField(keepUnityScaleWOnWrite, new GUIContent("保留无关轴 Scale (占位)"));
    }

    private void DrawSyncToggles()
    {
        EditorGUILayout.LabelField("同步选项", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.PropertyField(syncPosition, new GUIContent("同步 Position"));
            EditorGUILayout.PropertyField(syncLinear, new GUIContent("同步 Rotation+Scale"));
            if (!syncPosition.boolValue || !syncLinear.boolValue)
            {
                EditorGUILayout.HelpBox("关闭的部分不会写入 Unity Transform。", MessageType.Info);
            }
        }
    }

    private void DrawRuntimeLocalSection()
    {
        EditorGUILayout.LabelField("运行期本地属性(可写)", EditorStyles.boldLabel);

        if (Application.isPlaying)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Local Position
                TSVector lp = comp.localPosition;
                Vector3 newLp = EditorGUILayout.Vector3Field("Local Position", ToUnity(lp));
                if (!Approximately(newLp, lp))
                {
                    Undo.RecordObject(comp, "Change Local Position");
                    comp.localPosition = new TSVector((FP)newLp.x, (FP)newLp.y, (FP)newLp.z);
                }

                // Local Euler Degrees
                TSVector leDeg = comp.localEulerDegrees;
                Vector3 newLe = EditorGUILayout.Vector3Field("Local Euler (Deg)", ToUnity(leDeg));
                if (!Approximately(newLe, leDeg))
                {
                    Undo.RecordObject(comp, "Change Local Euler");
                    comp.localEulerDegrees = new TSVector((FP)newLe.x, (FP)newLe.y, (FP)newLe.z);
                }

                // Local Scale
                TSVector ls = comp.localScale;
                Vector3 newLs = EditorGUILayout.Vector3Field("Local Scale", ToUnity(ls));
                if (!Approximately(newLs, ls))
                {
                    Undo.RecordObject(comp, "Change Local Scale");
                    comp.localScale = new TSVector((FP)newLs.x, (FP)newLs.y, (FP)newLs.z);
                }
            }
        }
        else
        {
            EditorGUILayout.HelpBox("进入 Play 模式后可直接编辑本地 TRS。", MessageType.Info);
        }
    }

    private void DrawWorldReadonlySection()
    {
        EditorGUILayout.LabelField("世界属性 (只读)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            var wp = comp.worldPosition;
            var wr = comp.worldEulerDegrees; // world euler (deg)
            var ws = comp.worldScale;

            EditorGUILayout.Vector3Field("World Position", ToUnity(wp));
            EditorGUILayout.Vector3Field("World Euler (Deg)", ToUnity(wr));
            EditorGUILayout.Vector3Field("World Scale", ToUnity(ws));
        }
    }

    private void DrawForceAPIs()
    {
        EditorGUILayout.LabelField("同步控制", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Is Dirty", comp.IsDirty.ToString());
            EditorGUILayout.LabelField("Wrote This Frame", comp.WriteAppliedThisFrame.ToString());

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Force Flush")) comp.ForceFlush();
                if (GUILayout.Button("Force Flush (Write Even If Clean)")) comp.ForceFlush(true);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Force Read From Unity")) comp.ForceReadFromUnity();
                if (GUILayout.Button("Force Mark Dirty")) comp.ForceMarkDirty();
            }
            if (GUILayout.Button("Force Sync Now")) comp.ForceSyncNow();
        }
    }

    private void DrawParentSection()
    {
        EditorGUILayout.LabelField("层级", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.ObjectField("当前父级(EasyFixed3D)", comp.Parent, typeof(EasyFixedTransform3DBehaviour), true);
            if (Application.isPlaying)
            {
                if (GUILayout.Button("刷新父级 (从 Transform)"))
                {
                    var p = comp.transform.parent ? comp.transform.parent.GetComponent<EasyFixedTransform3DBehaviour>() : null;
                    if (p != comp.Parent)
                    {
                        Undo.RecordObject(comp, "Set Parent");
                        comp.SetParent(p, true);
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("在 Play 模式下可动态同步层级。", MessageType.None);
            }
        }
    }

    private static Vector3 ToUnity(TSVector v) => new Vector3(v.x.AsFloat(), v.y.AsFloat(), v.z.AsFloat());

    private static bool Approximately(Vector3 a, TSVector b)
    {
        return Mathf.Approximately(a.x, b.x.AsFloat()) && Mathf.Approximately(a.y, b.y.AsFloat()) && Mathf.Approximately(a.z, b.z.AsFloat());
    }
}
#endif