using SceneBuilder.Authoring;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="SurfaceSnap"/>. Shows the three per-axis enum dropdowns
    /// (<see cref="SurfaceSnap.vertical"/>/<see cref="SurfaceSnap.horizontal"/>/
    /// <see cref="SurfaceSnap.depth"/>), the optional <see cref="SurfaceSnap.target"/> surface
    /// override, and the <see cref="SurfaceSnap.captureThreshold"/> detach field. Unlike
    /// <see cref="FitSizeEditor"/> there is no conditional show/hide — every field is always shown.
    /// </summary>
    [CustomEditor(typeof(SurfaceSnap))]
    public sealed class SurfaceSnapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("vertical"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("horizontal"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("depth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("target"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("captureThreshold"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
