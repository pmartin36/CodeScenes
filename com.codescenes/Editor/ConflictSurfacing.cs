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

        /// <summary>A located, non-fatal note about a LIVE object the adapter is holding — the
        /// message argument is logged verbatim, with the object's live scene hierarchy path
        /// appended after the LogicalId. There is no untyped (string, string) overload: a Core
        /// <see cref="Conflict"/> has no <see cref="GameObject"/> and cannot reach the console this
        /// way.</summary>
        public static void LogNote(GameObject subject, string logicalId, string message)
            => Debug.LogWarning($"[CodeScenes] NOTE on '{logicalId}' ({SceneHierarchyPath.Of(subject)}): {message}");

        /// <summary>A located failure (§7 fail-loud) about a LIVE object the adapter is holding.</summary>
        public static void LogLocatedError(GameObject subject, string logicalId, string message)
            => Debug.LogError($"[CodeScenes] ERROR on '{logicalId}' ({SceneHierarchyPath.Of(subject)}): {message}");

        /// <summary>
        /// Surfaces the keyless per-pass <paramref name="conflicts"/> (RecurrenceKey null) to the
        /// console, attaching <paramref name="scenePath"/>'s resolved live hierarchy path to each
        /// report's <see cref="Conflict.Located"/> before logging. Returns the path-attached copies.
        /// </summary>
        public static Conflict[] SurfaceConflicts(IReadOnlyList<Conflict> conflicts, SceneHierarchyPath scenePath)
        {
            var located = new Conflict[conflicts.Count];
            for (var i = 0; i < conflicts.Count; i++)
            {
                var c = Locate(conflicts[i], scenePath);
                located[i] = c;
                Debug.LogWarning($"[SceneBuilder] Conflict ({c.Kind}) on {On(c)}");
            }

            return located;
        }

        /// <summary>The ONE rendering of a report's location — a located report composes its own
        /// text via <see cref="LocatedReport.Compose"/>; an unlocated one falls back to the legacy
        /// LogicalId+Reason shape.</summary>
        private static string On(Conflict c) =>
            c.Located is { } r ? r.Compose() : $"'{c.LogicalId}': {c.Reason}";

        /// <summary>Attaches <paramref name="scenePath"/>'s resolved live hierarchy path to
        /// <paramref name="c"/>'s <see cref="Conflict.Located"/>, or returns it unchanged when it
        /// carries none.</summary>
        private static Conflict Locate(Conflict c, SceneHierarchyPath scenePath) =>
            c.Located is { } r && scenePath.Of(r.LogicalId) is { } p
                ? c with { Located = r with { ScenePath = p } }
                : c;

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

        /// <summary>Test seam: clears the standing-note session registry so a test can assert the
        /// "surfaced once" behavior from a clean slate.</summary>
        public static void ResetNotes() => _surfacedNoteKeys.Clear();

        /// <summary>
        /// Surfaces each standing <paramref name="notes"/> entry at most once per editor session per
        /// (<paramref name="builderPath"/>, <c>RecurrenceKey</c>) pair — first occurrence attaches
        /// <paramref name="scenePath"/>'s resolved live hierarchy path to the report's
        /// <see cref="Conflict.Located"/>, logs it and is returned; a repeat (including a second
        /// entry in THIS SAME call sharing a key, e.g. two components of one type) is suppressed and
        /// logs nothing. Returns the subset actually surfaced, so a caller can report exactly what a
        /// sync newly announced.
        /// </summary>
        public static IReadOnlyList<Conflict> SurfaceNotes(
            IReadOnlyList<Conflict> notes, string builderPath, SceneHierarchyPath scenePath)
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

                var located = Locate(note, scenePath);
                Debug.LogWarning($"[CodeScenes] NOTE on {On(located)}");
                surfaced.Add(located);
            }

            return surfaced;
        }

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
