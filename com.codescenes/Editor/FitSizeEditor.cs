using SceneBuilder.Authoring;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="FitSize"/>. Shows only the fields relevant to the selected
    /// <see cref="FitSize.Mode"/>: a single scalar `value` for Width/Height/Depth, the per-axis
    /// `size` for Explicit, and neither for None.
    /// </summary>
    [CustomEditor(typeof(FitSize))]
    public sealed class FitSizeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var modeProp = serializedObject.FindProperty("mode");
            EditorGUILayout.PropertyField(modeProp);

            var mode = (FitSize.Mode)modeProp.enumValueIndex;
            switch (mode)
            {
                case FitSize.Mode.Width:
                case FitSize.Mode.Height:
                case FitSize.Mode.Depth:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("value"), new UnityEngine.GUIContent(mode.ToString()));
                    break;
                case FitSize.Mode.Explicit:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
                    break;
                case FitSize.Mode.None:
                default:
                    break;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
