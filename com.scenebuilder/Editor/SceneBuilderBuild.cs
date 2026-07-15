#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Build (code-&gt;scene): parse the builder file, materialize a Plan against the CURRENT open
    /// scene + identity sidecar, and execute it IN PLACE — reconcile-into-existing, never wipe (§5).
    /// User-hand-added objects survive; coded objects keep their <see cref="GlobalObjectId"/>s across
    /// rebuilds. New objects' ids are captured on save.
    /// </summary>
    public static class SceneBuilderBuild
    {
        private const string BuilderName = "DemoScene";

        // The SCENE stays under Assets/ — it is a real Unity asset and EditorSceneManager.SaveScene
        // takes a project-relative path. Only the builder .cs and its sidecar move out to
        // <ProjectRoot>/SceneBuilders/, where writing them cannot trigger a domain reload.
        private const string ScenePath = "Assets/SceneBuilder/DemoScene.unity";

        private static string BuilderPath => SceneBuilderPaths.Builder(BuilderName);
        private static string SidecarPath => SceneBuilderPaths.Sidecar(BuilderName);

        /// <summary>Summary of a <see cref="Run"/> build for callers/tests.</summary>
        public sealed class BuildResult
        {
            /// <summary>The IdentityMap written to the sidecar (with post-save GlobalObjectIds).</summary>
            public IdentityMap Map { get; set; } = new();

            /// <summary>Number of GameObjects resolved or created by the execution.</summary>
            public int ObjectCount { get; set; }

            /// <summary>Number of plan ops executed against the scene.</summary>
            public int PlanOpCount { get; set; }
        }

        [MenuItem("SceneBuilder/Build DemoScene (code -> scene)")]
        public static void BuildDemo()
        {
            try
            {
                SceneBuilderPaths.EnsureBuildersDirectory();

                if (!File.Exists(BuilderPath))
                {
                    Debug.LogError($"[SceneBuilder] Builder file not found: {BuilderPath}");
                    return;
                }

                Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Build failed:\n" + e);
            }
        }

        /// <summary>
        /// Build (code-&gt;scene) against a PASSED scene + paths: parse the builder file at
        /// <paramref name="builderPath"/>, materialize a Plan against <paramref name="scene"/> and the
        /// sidecar at <paramref name="sidecarPath"/>, execute it IN PLACE, save the scene to
        /// <paramref name="scenePath"/>, and rewrite the sidecar. The testable seam behind
        /// <see cref="BuildDemo"/>. Throws on failure (no swallowing) so callers/tests observe errors.
        /// </summary>
        public static BuildResult Run(string builderPath, string scenePath, string sidecarPath, Scene scene)
        {
            var source = File.ReadAllText(builderPath);

            // Carry over existing GlobalObjectIds so rebuilds reconcile in place (no id churn).
            IdentityMap? existingMap = File.Exists(sidecarPath)
                ? IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath))
                : null;

            // THE shared source->desired seam (parse -> resolve authored paths -> lower asset refs).
            // Sync goes through the exact same call, so neither direction can skip a stage.
            var loaded = DesiredModelLoader.Load(source, existingMap);
            var parse = loaded.Parse;
            var desired = loaded.Desired;

            // REFUSE, never guess (§4/§7). Sibling statements that only their POSITION tells apart
            // cannot be matched to scene objects: Build would silently pick one, and picking wrong
            // destroys a real object and repurposes another — with a self-consistent end state, so
            // nothing surfaces. Sync injects `.Id(...)` to prevent the pair ever forming; a pair a
            // human/LLM hand-authored has no correct answer available, so it is an error the user
            // resolves. Thrown BEFORE Materialize/Execute: the scene is not touched.
            if (parse.Ambiguities.Count > 0)
            {
                throw new ParseException(FormatAmbiguities(parse.Ambiguities, source, builderPath), 0, 0);
            }

            // Structurally remap the freshly-parsed model against the PRIOR sidecar so a renamed
            // or reordered handle-less object inherits its prior GlobalObjectId (no dup-create),
            // and a removed object survives as an orphan for the removal path to destroy. First
            // build (no sidecar) => empty prior => all-new, which is correct.
            var priorSidecar = existingMap ?? new IdentityMap();
            var remapped = IdentityRemapper.Remap(parse.Model, priorSidecar);

            // Read the PASSED scene as `actual` — never NewScene / wipe.
            var snapshot = SceneSnapshotReader.Read(scene);

            var plan = Materializer.Materialize(desired, snapshot, remapped);

            var execution = PlanExecutor.Execute(plan, remapped, scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);

            // Persist the CURRENT code structure only (drop destroyed orphans), carrying the
            // remapped fingerprint (Name+SiblingIndex) and inherited GlobalObjectIds, then stamp
            // the post-save GlobalObjectIds for objects the execution created/touched.
            var currentLogicalIds = new HashSet<string>(parse.IdentityMap.Entries.Select(e => e.LogicalId));
            var currentStructure = new IdentityMap
            {
                SchemaVersion = parse.IdentityMap.SchemaVersion,
                Scene = scenePath,
                // §M4: ensure every referenced GUID has an Assets[] entry with its CURRENT path so the
                // cache stays a valid move-recovery source and future syncs re-derive correctly. A
                // re-referenced GUID refreshes its LastKnownPath (e.g. after a move).
                Assets = AssetCacheMerge.Merge(parse.IdentityMap.Assets, loaded.HarvestedAssets).Merged,
                Entries = remapped.Entries.Where(e => currentLogicalIds.Contains(e.LogicalId)).ToArray(),
            };
            var map = WithGlobalObjectIds(currentStructure, execution);

            // Write-if-changed: a rebuild that produces an identical sidecar must not bump its mtime —
            // the file watcher driving code->scene would fire on it for nothing.
            SceneBuilderPaths.WriteIfChanged(sidecarPath, IdentityMapJson.Serialize(map));

            // No AssetDatabase.Refresh(): the sidecar lives outside Assets/ (nothing to import), and the
            // scene was already registered with the AssetDatabase by EditorSceneManager.SaveScene above.
            // A Refresh here would cost a domain reload for no gain.

            Debug.Log($"[SceneBuilder] Built in place: {execution.GameObjectsByLogicalId.Count} object(s), " +
                      $"{plan.Ops.Length} plan op(s) into {scenePath}. Sidecar: {sidecarPath}");

            return new BuildResult
            {
                Map = map,
                ObjectCount = execution.GameObjectsByLogicalId.Count,
                PlanOpCount = plan.Ops.Length,
            };
        }

        /// <summary>
        /// Renders parse ambiguities as a located, actionable error (§7: fail loud, located — name the
        /// object and the source location, never a silent drop). Each conflict's SourceSpan is resolved
        /// to a file:line:column against the builder source so the user can click straight to it.
        /// </summary>
        private static string FormatAmbiguities(
            IReadOnlyList<SceneBuilder.Core.Reconcile.Conflict> ambiguities, string source, string builderPath)
        {
            var message = new System.Text.StringBuilder();
            message.AppendLine(
                $"[SceneBuilder] Build REFUSED: {ambiguities.Count} ambiguous duplicate sibling name(s) in {builderPath}.");
            message.AppendLine(
                "Building would have to GUESS which statement is which scene object, and guessing wrong " +
                "silently destroys a real object and repurposes another. Add `.Id(\"...\")` to disambiguate:");

            foreach (var conflict in ambiguities)
            {
                var location = conflict.Location == null
                    ? builderPath
                    : $"{builderPath}({LineOf(source, conflict.Location.Value.Start)},{ColumnOf(source, conflict.Location.Value.Start)})";
                message.AppendLine($"  {location}: {conflict.Reason}");
            }

            return message.ToString();
        }

        private static int LineOf(string source, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static int ColumnOf(string source, int offset)
        {
            var lineStart = source.LastIndexOf('\n', System.Math.Min(offset, source.Length - 1));
            return offset - lineStart;
        }

        private static IdentityMap WithGlobalObjectIds(IdentityMap map, PlanExecutor.ExecutionResult execution)
        {
            var entries = map.Entries.Select(e =>
            {
                if (e.Kind == "GameObject"
                    && execution.GameObjectsByLogicalId.TryGetValue(e.LogicalId, out var go) && go != null)
                {
                    return e with { GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString() };
                }

                if (e.Kind == "Component"
                    && execution.ComponentsByLogicalId.TryGetValue(e.LogicalId, out var comp) && comp != null)
                {
                    return e with { GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString() };
                }

                return e;
            }).ToArray();

            return map with { Entries = entries };
        }
    }
}
