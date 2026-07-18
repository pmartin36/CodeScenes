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
using SceneBuilder.Core.Validation;

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

        /// <summary>
        /// The builder/sidecar paths this session's LAST successful <see cref="Run"/> built — the
        /// auto-sync scene-&gt;code executor's fallback discovery for "which builder governs the
        /// active scene" (b5-t1), so a caller that builds against a non-default path (e.g. an
        /// isolated test fixture) is the pair auto-sync reconciles against, without a wider
        /// scene-&gt;builder registry this single-demo-builder milestone does not need. Null until the
        /// first successful <see cref="Run"/> this session; wiped on domain reload.
        /// </summary>
        internal static string? LastBuilderPath;
        internal static string? LastSidecarPath;

        /// <summary>Summary of a <see cref="Run"/> build for callers/tests.</summary>
        public sealed class BuildResult
        {
            /// <summary>The IdentityMap written to the sidecar (with post-save GlobalObjectIds).</summary>
            public IdentityMap Map { get; set; } = new();

            /// <summary>Number of GameObjects resolved or created by the execution.</summary>
            public int ObjectCount { get; set; }

            /// <summary>Number of plan ops executed against the scene.</summary>
            public int PlanOpCount { get; set; }

            /// <summary>
            /// Planning-phase diagnostics collected before any mutation. Non-empty means the Build
            /// REFUSED (scene untouched) — collect-all, not throw-on-first (b3-t2). Empty on a clean
            /// build.
            /// </summary>
            public System.Collections.Generic.IReadOnlyList<SceneBuilder.Core.Validation.Diagnostic> Diagnostics
                { get; set; } = System.Array.Empty<SceneBuilder.Core.Validation.Diagnostic>();

            /// <summary>
            /// Info-severity diagnostics from a SUCCESSFUL build's <c>plan.Diagnostics</c> (e.g. SB2301
            /// "prefab overrides preserved but not modelled"). Distinct from <see cref="Diagnostics"/>,
            /// which means "build refused" — <see cref="Flags"/> is populated only on the success path
            /// and never implies the build was refused.
            /// </summary>
            public System.Collections.Generic.IReadOnlyList<SceneBuilder.Core.Validation.Diagnostic> Flags
                { get; set; } = System.Array.Empty<SceneBuilder.Core.Validation.Diagnostic>();
        }

        [MenuItem("CodeScenes/Build DemoScene (code -> scene)")]
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

                var result = Run(BuilderPath, ScenePath, SidecarPath, EditorSceneManager.GetActiveScene());
                foreach (var diagnostic in result.Diagnostics)
                {
                    Debug.LogError(FormatDiagnostic(diagnostic));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Build failed:\n" + e);
            }
        }

        private static string FormatDiagnostic(SceneBuilder.Core.Validation.Diagnostic diagnostic) =>
            $"[SceneBuilder] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): {diagnostic.Message}";

        /// <summary>
        /// Build (code-&gt;scene) against a PASSED scene + paths: parse the builder file at
        /// <paramref name="builderPath"/>, materialize a Plan against <paramref name="scene"/> and the
        /// sidecar at <paramref name="sidecarPath"/>, execute it IN PLACE, save the scene to
        /// <paramref name="scenePath"/>, and rewrite the sidecar. The testable seam behind
        /// <see cref="BuildDemo"/>. Collect-all-refuse for planning-phase errors: never throws for a
        /// resolvable type/asset/identity problem — returns a <see cref="BuildResult"/> whose
        /// <see cref="BuildResult.Diagnostics"/> carries every error found, scene left untouched.
        /// </summary>
        public static BuildResult Run(string builderPath, string scenePath, string sidecarPath, Scene scene)
        {
            var source = File.ReadAllText(builderPath);

            // Carry over existing GlobalObjectIds so rebuilds reconcile in place (no id churn).
            IdentityMap? existingMap = File.Exists(sidecarPath)
                ? IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath))
                : null;

            // THE shared collect-all planning walk (b4's headless validator drives the exact same
            // one): raw parse (un-normalized short tokens — normalizing first would THROW on the
            // very first unresolved/ambiguous type, defeating collect-all), then every planning-phase
            // error class (structural ambiguities, component types, asset refs) in ONE pass via
            // PlanningValidator + the non-throwing UnityResolutionProvider. REFUSE, never guess
            // (§4/§7): on any error, return every diagnostic collected — scene untouched, nothing
            // created — instead of throwing on the first one found.
            var rawParse = BuilderParser.Parse(source, existingMap);
            var validation = PlanningValidator.Validate(
                rawParse, source, new UnityResolutionProvider(existingMap?.Assets), builderPath);

            // NOTE: no Debug.LogError here — `Run` is the testable seam and must stay silent on a
            // collected refusal so callers (including tests) observe it via the returned
            // `BuildResult.Diagnostics`, not an unhandled console error. The interactive entry point
            // (`BuildDemo`) logs each diagnostic for the user after calling `Run`.
            if (!validation.Ok)
            {
                return new BuildResult { Diagnostics = validation.Diagnostics };
            }

            // THE shared source->desired seam (parse -> resolve authored paths -> lower asset refs).
            // Sync goes through the exact same call, so neither direction can skip a stage. Guaranteed
            // not to throw here: the walk above already confirmed every type/asset/builtin resolves
            // via the SAME resolvers.
            var loaded = DesiredModelLoader.Load(source, existingMap);
            var parse = loaded.Parse;
            var desired = loaded.Desired;

            // Structurally remap the freshly-parsed model against the PRIOR sidecar so a renamed
            // or reordered handle-less object inherits its prior GlobalObjectId (no dup-create),
            // and a removed object survives as an orphan for the removal path to destroy. First
            // build (no sidecar) => empty prior => all-new, which is correct.
            var priorSidecar = existingMap ?? new IdentityMap();
            var remapped = IdentityRemapper.Remap(parse.Model, priorSidecar);

            // Read the PASSED scene as `actual` — never NewScene / wipe.
            var snapshot = SceneSnapshotReader.Read(scene);

            var plan = Materializer.Materialize(desired, snapshot, remapped);

            PlanExecutor.ExecutionResult execution;
            using (SuppressionScope.SuppressScene())
            {
                execution = PlanExecutor.Execute(plan, remapped, scene);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, scenePath);
            }

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

            LastBuilderPath = builderPath;
            LastSidecarPath = sidecarPath;

            return new BuildResult
            {
                Map = map,
                ObjectCount = execution.GameObjectsByLogicalId.Count,
                PlanOpCount = plan.Ops.Length,
                Flags = plan.Diagnostics,
            };
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

                // b5-t3: stamp the instance root's GlobalObjectId + the (TargetPrefabId, TargetObjectId)
                // pair-key + SourcePrefabGuid via the SAME probe the read side (b5-t2) uses, so build-side
                // and read-side identity are byte-identical (the Differ's pair-key match depends on it).
                // Runs AFTER SaveScene, so the GlobalObjectId pair is real.
                if (e.Kind == "PrefabInstance"
                    && execution.GameObjectsByLogicalId.TryGetValue(e.LogicalId, out var instanceGo) && instanceGo != null)
                {
                    var (sourceGuid, key, _) = PrefabInstanceProbe.ReadInstanceRoot(instanceGo);
                    return e with
                    {
                        GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(instanceGo).ToString(),
                        PrefabKey = key,
                        SourcePrefabGuid = sourceGuid ?? e.SourcePrefabGuid,
                    };
                }

                return e;
            }).ToArray();

            return map with { Entries = entries };
        }
    }
}
