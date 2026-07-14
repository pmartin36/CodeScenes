#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

                var source = File.ReadAllText(BuilderPath);

                // Carry over existing GlobalObjectIds so rebuilds reconcile in place (no id churn).
                IdentityMap? existingMap = File.Exists(SidecarPath)
                    ? IdentityMapJson.Deserialize(File.ReadAllText(SidecarPath))
                    : null;

                var parse = BuilderParser.Parse(source, existingMap);

                // §M3: rewrite transient member:<name> field keys to real serialized paths BEFORE diff.
                var desired = AuthoredPathResolver.Resolve(parse.Model);

                // Read the CURRENT open scene as `actual` — never NewScene / wipe.
                var scene = EditorSceneManager.GetActiveScene();
                var snapshot = SceneSnapshotReader.Read(scene);

                var plan = Materializer.Materialize(desired, snapshot, parse.IdentityMap);

                var execution = PlanExecutor.Execute(plan, parse.IdentityMap, scene);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);

                // GlobalObjectIds are stable only after save; capture (survivors keep theirs) + persist.
                var map = WithGlobalObjectIds(parse.IdentityMap, execution) with { Scene = ScenePath };
                File.WriteAllText(SidecarPath, IdentityMapJson.Serialize(map));
                AssetDatabase.Refresh();

                Debug.Log($"[SceneBuilder] Built in place: {execution.GameObjectsByLogicalId.Count} object(s), " +
                          $"{plan.Ops.Length} plan op(s) into {ScenePath}. Sidecar: {SidecarPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Build failed:\n" + e);
            }
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
