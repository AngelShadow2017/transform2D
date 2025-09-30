using UnityEngine;
using UnityEditor;
using Core.TrueSync;

[CustomEditor(typeof(EasyFixedTransform2DBehaviour))]
[CanEditMultipleObjects]
public class EasyFixedTransform2DBehaviourEditor : Editor
{
    private EasyFixedTransform2DBehaviour comp;

    private SerializedProperty setLocalPosition;
    private SerializedProperty initialLocalPosition;
    private SerializedProperty errorIfUnsetLocalPosition;

    private SerializedProperty setLocalRotation;
    private SerializedProperty initialLocalRotationDegrees;
    private SerializedProperty errorIfUnsetLocalRotation;

    private SerializedProperty setLocalScale;
    private SerializedProperty initialLocalScale;
    private SerializedProperty errorIfUnsetLocalScale;

    private SerializedProperty applyScaleZAsOne;

    private void OnEnable()
    {
        comp = (EasyFixedTransform2DBehaviour)target;

        setLocalPosition = serializedObject.FindProperty("setLocalPosition");
        initialLocalPosition = serializedObject.FindProperty("initialLocalPosition");
        errorIfUnsetLocalPosition = serializedObject.FindProperty("errorIfUnsetLocalPosition");

        setLocalRotation = serializedObject.FindProperty("setLocalRotation");
        initialLocalRotationDegrees = serializedObject.FindProperty("initialLocalRotationDegrees");
        errorIfUnsetLocalRotation = serializedObject.FindProperty("errorIfUnsetLocalRotation");

        setLocalScale = serializedObject.FindProperty("setLocalScale");
        initialLocalScale = serializedObject.FindProperty("initialLocalScale");
        errorIfUnsetLocalScale = serializedObject.FindProperty("errorIfUnsetLocalScale");

        applyScaleZAsOne = serializedObject.FindProperty("applyScaleZAsOne");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawLocalInitSection();
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

        EditorGUILayout.PropertyField(setLocalRotation, new GUIContent("设置初始 LocalRotation(Z Deg)"));
        if (setLocalRotation.boolValue)
            EditorGUILayout.PropertyField(initialLocalRotationDegrees, new GUIContent("初始旋转(度)"));
        else
            EditorGUILayout.PropertyField(errorIfUnsetLocalRotation, new GUIContent("未设置时报错"));

        EditorGUILayout.PropertyField(setLocalScale, new GUIContent("设置初始 LocalScale"));
        if (setLocalScale.boolValue)
            EditorGUILayout.PropertyField(initialLocalScale, new GUIContent("初始 LocalScale"));
        else
            EditorGUILayout.PropertyField(errorIfUnsetLocalScale, new GUIContent("未设置时报错"));

        EditorGUILayout.PropertyField(applyScaleZAsOne, new GUIContent("Z 缩放固定为 1"));
    }

    private void DrawRuntimeLocalSection()
    {
        EditorGUILayout.LabelField("运行期本地属性(可写)", EditorStyles.boldLabel);

        if (Application.isPlaying)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // localPosition
                TSVector2 lp = comp.localPosition;
                Vector2 newLp = EditorGUILayout.Vector2Field("Local Position", ToUnity(lp));
                if (!Mathf.Approximately(newLp.x, lp.x.AsFloat()) || !Mathf.Approximately(newLp.y, lp.y.AsFloat()))
                {
                    Undo.RecordObject(comp, "Change Local Position");
                    comp.localPosition = new TSVector2((FP)newLp.x, (FP)newLp.y);
                }

                // localRotationDegrees
                FP lrd = comp.localRotationDegrees;
                float newDeg = EditorGUILayout.FloatField("Local Rotation (Deg)", lrd.AsFloat());
                if (!Mathf.Approximately(newDeg, lrd.AsFloat()))
                {
                    Undo.RecordObject(comp, "Change Local Rotation");
                    comp.localRotationDegrees = (FP)newDeg;
                }

                // localScale
                TSVector2 ls = comp.localScale;
                Vector2 newLs = EditorGUILayout.Vector2Field("Local Scale", ToUnity(ls));
                if (!Mathf.Approximately(newLs.x, ls.x.AsFloat()) || !Mathf.Approximately(newLs.y, ls.y.AsFloat()))
                {
                    Undo.RecordObject(comp, "Change Local Scale");
                    comp.localScale = new TSVector2((FP)newLs.x, (FP)newLs.y);
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
            var ws = comp.worldScale;
            var ls = comp.lossyScale;

            EditorGUILayout.Vector2Field("World Position", ToUnity(wp));
            EditorGUILayout.FloatField("World Rotation Z", comp.worldRotationDegrees.AsFloat());
            EditorGUILayout.Vector2Field("World Scale", ToUnity(ws));
            EditorGUILayout.Vector2Field("Lossy Scale", ToUnity(ls));
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
                if (GUILayout.Button("Force Flush"))
                    comp.ForceFlush();
                if (GUILayout.Button("Force Flush (Write Even If Clean)"))
                    comp.ForceFlush(true);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Force Read From Unity"))
                    comp.ForceReadFromUnity();
                if (GUILayout.Button("Force Mark Dirty"))
                    comp.ForceMarkDirty();
            }
            if (GUILayout.Button("Force Sync Now"))
                comp.ForceSyncNow();
        }
    }

    private void DrawParentSection()
    {
        EditorGUILayout.LabelField("层级", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.ObjectField("当前父级(EasyFixed)", comp.Parent, typeof(EasyFixedTransform2DBehaviour), true);

            if (Application.isPlaying)
            {
                if (GUILayout.Button("刷新父级 (从 Transform)"))
                {
                    var p = comp.transform.parent
                        ? comp.transform.parent.GetComponent<EasyFixedTransform2DBehaviour>()
                        : null;
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

    private static Vector2 ToUnity(TSVector2 v)
    {
        return new Vector2(v.x.AsFloat(), v.y.AsFloat());
    }
}
