#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Transform2DBehaviour))]
public class Transform2DBehaviourEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var t = (Transform2DBehaviour)target;

        EditorGUILayout.LabelField("Sync", EditorStyles.boldLabel);
        t.syncMode = (Transform2DBehaviour.SyncMode)EditorGUILayout.EnumPopup("Sync Mode", t.syncMode);
        t.applyScaleZAsOne = EditorGUILayout.Toggle("Apply Scale Z=1", t.applyScaleZAsOne);
        t.readScaleFromLocal = EditorGUILayout.Toggle("Read Local Scale", t.readScaleFromLocal);
        t.autoAttachByUnityHierarchy = EditorGUILayout.Toggle("Auto Attach By Unity Hierarchy", t.autoAttachByUnityHierarchy);
        t.adoptUnityOnPlayInWriteMode = EditorGUILayout.Toggle("Adopt Unity On Play (Write Mode)", t.adoptUnityOnPlayInWriteMode);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editable (Local)", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        var lp = EditorGUILayout.Vector2Field("Local Position", t.localPosition);
        var lr = EditorGUILayout.FloatField("Local Rotation (deg)", t.localRotationDegrees);
        var ls = EditorGUILayout.Vector2Field("Local Scale", t.localScale);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(t, "Edit Transform2DBehaviour");
            t.SetLocalTRSDegrees(lp, lr, ls);
            t.EditorForceApply();
            EditorUtility.SetDirty(t);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("World (只读)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector2Field("World Position", t.worldPosition);
            EditorGUILayout.FloatField("World Rotation (deg)", t.worldRotationDegrees);
            EditorGUILayout.Vector2Field("World Scale", t.worldScale);
        }

        if (GUILayout.Button("Force Read From Unity") && t.syncMode == Transform2DBehaviour.SyncMode.ReadFromUnity)
        {
            t.ReadFromUnityTransform();
            EditorUtility.SetDirty(t);
        }

        if (GUILayout.Button("Force Write To Unity") && t.syncMode == Transform2DBehaviour.SyncMode.WriteToUnity)
        {
            t.WriteToUnityTransform();
            EditorUtility.SetDirty(t);
        }
    }
}
#endif