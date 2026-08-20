using SceneBuilder.Authoring;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="Between"/>. Shows <see cref="Between.from"/>,
    /// <see cref="Between.to"/>, <see cref="Between.fraction"/>, <see cref="Between.axis"/>, and the
    /// optional <see cref="Between.orientation"/> override. Every field is always shown, mirroring
    /// <see cref="SurfaceSnapEditor"/>.
    /// </summary>
    [CustomEditor(typeof(Between))]
    public sealed class BetweenEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("from"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("to"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("fraction"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("axis"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("orientation"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
