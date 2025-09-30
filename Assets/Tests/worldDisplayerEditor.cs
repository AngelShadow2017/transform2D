#if UNITY_EDITOR
// 文件名：Assets/Editor/Transform2DBehaviourEditor.cs
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(worldDisplayer))]
public class worldDisplayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t2d = (worldDisplayer)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("World Info", EditorStyles.boldLabel);
        EditorGUILayout.Vector2Field("World Position", t2d.transform.position);
        EditorGUILayout.FloatField("World Rotation", t2d.transform.eulerAngles.z);
        EditorGUILayout.Vector2Field("World Scale", t2d.transform.lossyScale);

        // 实时刷新
        if (Application.isPlaying)
            Repaint();
    }
}
#endif