#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Non-modal surfacing for the both-sides-changed conflict resolution (spec checklist #10,
    /// §7 fail-loud): a located <see cref="Debug.LogError"/>, the `// CONFLICT:` marker-line text, and
    /// a best-effort scene-view overlay registry. NEVER opens <c>EditorUtility.DisplayDialog</c> — the
    /// scene-wins tie-break already resolved the value; this only makes the resolution visible. Also
    /// owns the STANDING-note channel (<see cref="SurfaceNotes"/>): a <see cref="Conflict"/> with a
    /// non-null <c>RecurrenceKey</c> is a fact of the scene+source that recurs on every reconcile, so
    /// it is logged at most once per editor session per key rather than on every single sync.
    /// </summary>
    public sealed class ConflictSurfacing
    {
        private static readonly HashSet<string> _registered = new();

        // Session-scoped: keys are `builderPath + '\0' + RecurrenceKey`. Cleared on domain reload
        // (a static dies with it, so "once per session" needs no persistence) or explicitly via
        // ResetNotes, the test seam mirroring Clear() for the overlay registry below.
        private static readonly HashSet<string> _surfacedNoteKeys = new();

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

        /// <summary>A located, non-fatal note: the plugin changed something structural the user did not
        /// explicitly ask for, and must not do it silently.</summary>
        public static void LogNote(string logicalId, string message)
            => Debug.LogWarning($"[CodeScenes] NOTE on '{logicalId}': {message}");

        /// <summary>A located failure (§7 fail-loud): an authored value could NOT be applied.</summary>
        public static void LogLocatedError(string logicalId, string message)
            => Debug.LogError($"[CodeScenes] ERROR on '{logicalId}': {message}");

        /// <summary>
        /// The `// CONFLICT:` marker text inserted at the resolved statement (no leading indent, no
        /// trailing newline — the caller owns placement). Preserves the prior CODE value so it is
        /// recoverable, never silently discarded.
        /// </summary>
        public static string BuildMarkerLine(string fieldKey, string priorCodeExpr, string sceneValueExpr) =>
            $"// CONFLICT: {fieldKey} code value was {priorCodeExpr}; scene value {sceneValueExpr} applied (scene wins).";

        /// <summary>The marker's SCENE-side text for a field whose setter was removed (it has no
        /// literal to render — spec 32 C2's scene->code reset). ASCII only: this string is written
        /// verbatim into the user's builder .cs via <see cref="BuildMarkerLine"/>.
        /// The value is declared here as the ONE owner
        /// of this fixed copy; <c>SceneBuilderSync.Conflicts.cs</c>'s <c>SceneExprOfEdit</c> must
        /// return it instead of its own private literal.</summary>
        public const string RemovedFieldMarkerValue = "(removed: reset to type default)";

        /// <summary>Registers a key (component/GameObject LogicalId) for the next scene-view overlay draw.</summary>
        public void RegisterOverlay(string key) => _registered.Add(key);

        /// <summary>Clears the overlay registry — called at the start of the next converged cycle.</summary>
        public static void Clear() => _registered.Clear();

        /// <summary>
        /// Surfaces each standing <paramref name="notes"/> entry at most once per editor session per
        /// (<paramref name="builderPath"/>, <c>RecurrenceKey</c>) pair — first occurrence logged via
        /// <see cref="LogNote"/> and returned, a repeat (including a second entry in THIS SAME call
        /// sharing a key, e.g. two components of one type) suppressed and logged nothing. Returns the
        /// subset actually surfaced, so a caller can report exactly what a sync newly announced.
        /// </summary>
        public static IReadOnlyList<Conflict> SurfaceNotes(IReadOnlyList<Conflict> notes, string builderPath)
        {
            var surfaced = new List<Conflict>();
            foreach (var note in notes)
            {
                if (note.RecurrenceKey is null)
                {
                    continue;
                }

                if (!_surfacedNoteKeys.Add(builderPath + '\0' + note.RecurrenceKey))
                {
                    continue;
                }

                LogNote(note.LogicalId ?? "", note.Reason);
                surfaced.Add(note);
            }

            return surfaced;
        }

        /// <summary>Test seam: clears the standing-note session registry so a test can assert the
        /// "surfaced once" behavior from a clean slate.</summary>
        public static void ResetNotes() => _surfacedNoteKeys.Clear();

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
