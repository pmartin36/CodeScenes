#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Non-modal surfacing for the b6-t1 both-sides-changed conflict resolution (spec checklist #10,
    /// §7 fail-loud): a located <see cref="Debug.LogError"/>, the `// CONFLICT:` marker-line text, and
    /// a best-effort scene-view overlay registry. NEVER opens <c>EditorUtility.DisplayDialog</c> — the
    /// scene-wins tie-break already resolved the value; this only makes the resolution visible.
    /// </summary>
    public sealed class ConflictSurfacing
    {
        private static readonly HashSet<string> _registered = new();

        /// <summary>
        /// Test-observable seam: keys (component/GameObject LogicalId) registered for the next
        /// scene-view overlay draw. Cleared by <see cref="Clear"/> on the next converged cycle.
        /// </summary>
        public static IReadOnlyCollection<string> RegisteredObjects => _registered;

        static ConflictSurfacing()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        /// <summary>
        /// Logs a located Console error naming the object/field a conflict resolved on. This is the
        /// ONLY surfacing that can fail a test's <c>LogAssert.Expect</c> — never a dialog, never silent.
        /// </summary>
        public void LogConflict(string logicalId, string fieldKey, string sceneValueExpr, string codeValueExpr)
        {
            Debug.LogError(
                $"[CodeScenes] CONFLICT on '{logicalId}' field '{fieldKey}': scene value {sceneValueExpr} " +
                $"kept (scene wins); code value {codeValueExpr} preserved in a // CONFLICT: marker.");
        }

        /// <summary>
        /// The `// CONFLICT:` marker text inserted at the resolved statement (no leading indent, no
        /// trailing newline — the caller owns placement). Preserves the prior CODE value so it is
        /// recoverable, never silently discarded.
        /// </summary>
        public static string BuildMarkerLine(string fieldKey, string priorCodeExpr, string sceneValueExpr) =>
            $"// CONFLICT: {fieldKey} code value was {priorCodeExpr}; scene value {sceneValueExpr} applied (scene wins).";

        /// <summary>Registers a key (component/GameObject LogicalId) for the next scene-view overlay draw.</summary>
        public void RegisterOverlay(string key) => _registered.Add(key);

        /// <summary>Clears the overlay registry — called at the start of the next converged cycle.</summary>
        public static void Clear() => _registered.Clear();

        // Best-effort draw only; the registry (the test-observable seam) is populated regardless of
        // whether this ever paints anything (e.g. headless batchmode has no SceneView).
        private static void OnSceneGui(SceneView view)
        {
            foreach (var key in _registered)
            {
                Handles.BeginGUI();
                GUILayout.Label($"CodeScenes conflict: {key}");
                Handles.EndGUI();
            }
        }
    }
}
