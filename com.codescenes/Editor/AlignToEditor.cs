using SceneBuilder.Authoring;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="AlignTo"/>. Shows the six per-axis mode/offset fields
    /// (<see cref="AlignTo.xMode"/>/<see cref="AlignTo.xOffset"/>/... for y/z), the
    /// <see cref="AlignTo.target"/>/<see cref="AlignTo.frame"/> overrides, the
    /// <see cref="AlignTo.space"/> reference frame, and the <see cref="AlignTo.captureThreshold"/>
    /// detach field. Unlike <see cref="FitSizeEditor"/> there is no conditional show/hide — every
    /// field is always shown.
    /// </summary>
    [CustomEditor(typeof(AlignTo))]
    public sealed class AlignToEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("xMode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("xOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("yMode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("yOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("zMode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("zOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("target"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("frame"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("space"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("captureThreshold"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
