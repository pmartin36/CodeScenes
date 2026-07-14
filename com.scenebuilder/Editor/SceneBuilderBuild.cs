using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// First milestone Build: parse a designated builder file, materialize the Plan, and execute it
    /// into a fresh scene next to it, writing the identity sidecar. (Sync-back comes next.)
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
                var parse = BuilderParser.Parse(source);
                var plan = Materializer.Materialize(parse.Model, new SceneSnapshot(), parse.IdentityMap);

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var byLogicalId = PlanExecutor.Execute(plan);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);

                // GlobalObjectId only exists after save; capture it now and write the sidecar.
                var map = WithGlobalObjectIds(parse.IdentityMap, byLogicalId) with { Scene = ScenePath };
                File.WriteAllText(SidecarPath, IdentityMapJson.Serialize(map));
                AssetDatabase.Refresh();

                Debug.Log($"[SceneBuilder] Built {byLogicalId.Count} object(s) into {ScenePath} " +
                          $"({plan.Ops.Length} plan ops). Sidecar: {SidecarPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Build failed:\n" + e);
            }
        }

        private static IdentityMap WithGlobalObjectIds(IdentityMap map, Dictionary<string, GameObject> byLogicalId)
        {
            var entries = map.Entries.Select(e =>
            {
                if (e.Kind == "GameObject" && byLogicalId.TryGetValue(e.LogicalId, out var go) && go != null)
                {
                    return e with { GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString() };
                }
                return e;
            }).ToArray();

            return map with { Entries = entries };
        }
    }
}
