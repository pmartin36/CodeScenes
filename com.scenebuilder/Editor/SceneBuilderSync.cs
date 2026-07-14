using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Sync-back (scene-&gt;code): reads the live scene, reconciles it against the builder file keyed on
    /// GlobalObjectId, and patches the builder source in place (formatting-preserving) via Roslyn.
    /// </summary>
    public static class SceneBuilderSync
    {
        private const string BuilderPath = "Assets/SceneBuilder/DemoScene.cs";
        private const string SidecarPath = "Assets/SceneBuilder/DemoScene.sbmap.json";

        [MenuItem("SceneBuilder/Sync DemoScene (scene -> code)")]
        public static void SyncDemo()
        {
            try
            {
                if (!File.Exists(BuilderPath) || !File.Exists(SidecarPath))
                {
                    Debug.LogError($"[SceneBuilder] Build first — missing {BuilderPath} or {SidecarPath}.");
                    return;
                }

                var source = File.ReadAllText(BuilderPath);
                var map = IdentityMapJson.Deserialize(File.ReadAllText(SidecarPath));
                var parse = BuilderParser.Parse(source, map);

                var snapshot = SceneSnapshotReader.Read(SceneManager.GetActiveScene());
                var result = Reconciler.Reconcile(parse.Model, snapshot, map, parse.Anchors);

                foreach (var c in result.Conflicts)
                {
                    Debug.LogWarning($"[SceneBuilder] Conflict ({c.Kind}) on '{c.LogicalId}': {c.Reason}");
                }

                if (result.Patch.Edits.Length == 0)
                {
                    Debug.Log("[SceneBuilder] Scene already matches code — nothing to sync.");
                    return;
                }

                var newSource = SourcePatchApplier.Apply(source, result.Patch, parse.Anchors);
                if (newSource != source)
                {
                    File.WriteAllText(BuilderPath, newSource);
                    AssetDatabase.Refresh();
                    Debug.Log($"[SceneBuilder] Synced {result.Patch.Edits.Length} edit(s) back into {BuilderPath}.");
                }
                else
                {
                    Debug.Log("[SceneBuilder] Reconcile produced no source change.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Sync failed:\n" + e);
            }
        }
    }
}
