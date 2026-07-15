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
        private const string BuilderPath = "Assets/SceneBuilder/DemoScene.cs";
        private const string ScenePath = "Assets/SceneBuilder/DemoScene.unity";
        private const string SidecarPath = "Assets/SceneBuilder/DemoScene.sbmap.json";

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

            var parse = BuilderParser.Parse(source, existingMap);

            // §M3: rewrite transient member:<name> field keys to real serialized paths BEFORE diff.
            var desired = AuthoredPathResolver.Resolve(parse.Model);

            // §M4: lower authored Asset("path") refs to their AssetDatabase (guid, fileId, typeHint)
            // BEFORE diff/materialize, so Core stores the authoritative GUID and the write side can
            // resolve the object. GUID-authoritative: a path stale from a move/rename recovers its GUID
            // from the sidecar Assets[] cache (ref survives); only a GUID that maps to NOTHING (asset
            // truly deleted) fails loud. The resolver harvests every referenced GUID at its current
            // path so Build can refresh Assets[] below.
            var assetResolver = new AssetReferenceResolver.LoweringResolver(existingMap?.Assets);
            desired = SceneBuilder.Core.Lowering.AssetRefLowering.Lower(desired, assetResolver.Resolve);

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
                Assets = MergeAssets(parse.IdentityMap.Assets, assetResolver.Harvested),
                Entries = remapped.Entries.Where(e => currentLogicalIds.Contains(e.LogicalId)).ToArray(),
            };
            var map = WithGlobalObjectIds(currentStructure, execution);
            File.WriteAllText(sidecarPath, IdentityMapJson.Serialize(map));
            AssetDatabase.Refresh();

            Debug.Log($"[SceneBuilder] Built in place: {execution.GameObjectsByLogicalId.Count} object(s), " +
                      $"{plan.Ops.Length} plan op(s) into {scenePath}. Sidecar: {sidecarPath}");

            return new BuildResult
            {
                Map = map,
                ObjectCount = execution.GameObjectsByLogicalId.Count,
                PlanOpCount = plan.Ops.Length,
            };
        }

        // Fold every harvested asset entry (GUID at its current path) into the existing Assets[] cache,
        // keyed by GUID; a harvested entry wins (its LastKnownPath reflects the current project layout)
        // over a stale cached one, so a moved/renamed asset's LastKnownPath is refreshed.
        private static AssetEntry[] MergeAssets(AssetEntry[] existing, IReadOnlyList<AssetEntry> harvested)
        {
            if (harvested.Count == 0)
            {
                return existing;
            }

            var byGuid = new Dictionary<string, AssetEntry>();
            foreach (var entry in existing)
            {
                byGuid[entry.Guid] = entry;
            }

            foreach (var entry in harvested)
            {
                byGuid[entry.Guid] = entry;
            }

            return byGuid.Values.ToArray();
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
