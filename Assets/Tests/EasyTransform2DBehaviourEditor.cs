#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(EasyTransform2DBehaviour))]
public class EasyTransform2DBehaviourEditor : Editor
{
    private EasyTransform2DBehaviour comp;

    private void OnEnable()
    {
        comp = (EasyTransform2DBehaviour)target;
    }

    public override void OnInspectorGUI()
    {
        // 原始序列化区域（初始化相关）
        DrawInitializationSection();

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Runtime Local (Editable)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUI.BeginChangeCheck();
            Vector2 lp = EditorGUILayout.Vector2Field("Local Position", comp.localPosition);
            float lr = EditorGUILayout.FloatField("Local Rotation Z", comp.localRotationDegrees);
            Vector2 ls = EditorGUILayout.Vector2Field("Local Scale", comp.localScale);
            bool applyScaleZAsOne = EditorGUILayout.Toggle("Apply Scale Z=1", comp.applyScaleZAsOne);
            if (EditorGUI.EndChangeCheck() && Application.isPlaying)
            {
                Undo.RecordObject(comp, "Modify EasyTransform2DBehaviour Local");
                comp.localPosition = lp;
                comp.localRotationDegrees = lr;
                comp.localScale = ls;
                comp.applyScaleZAsOne = applyScaleZAsOne;
                comp.ForceSyncNow();
                EditorUtility.SetDirty(comp);
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Runtime World (ReadOnly)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector2Field("World Position", comp.worldPosition);
            EditorGUILayout.FloatField("World Rotation Z", comp.worldRotationDegrees);
            EditorGUILayout.Vector2Field("World Scale", comp.worldScale);
            EditorGUILayout.Vector2Field("Lossy Scale", comp.lossyScale);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Force Read From Unity"))
            {
                comp.ForceReadFromUnity();
                comp.ForceSyncNow();
            }
            if (GUILayout.Button("Force Write (Flush)"))
            {
                comp.ForceFlush(true);
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Mark Dirty (No Immediate Write)"))
                comp.ForceMarkDirty();
        }

        if (Application.isPlaying)
            Repaint(); // 实时刷新 world 值显示
    }

    private void DrawInitializationSection()
    {
        EditorGUILayout.LabelField("Initialization (Play Mode Only)", EditorStyles.boldLabel);
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            // 跳过脚本引用
            if (iterator.propertyPath == "m_Script") continue;
            // 只绘制定义的可序列化字段（排除 _node / runtime 内部标志）
            if (iterator.propertyPath == "_node") continue;
            if (iterator.propertyPath.StartsWith("_")) continue;
            if (iterator.propertyPath == "Parent") continue;

            EditorGUILayout.PropertyField(iterator, true);
        }
        serializedObject.ApplyModifiedProperties();
    }
}

#endif