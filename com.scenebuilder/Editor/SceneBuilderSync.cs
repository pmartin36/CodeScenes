using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Sync-back (scene-&gt;code): reads the live scene, reconciles it against the builder file keyed on
    /// GlobalObjectId, patches the builder source in place (formatting-preserving) via Roslyn, and
    /// updates the identity sidecar with the reconcile's map deltas. Covers transform/name/parent (M2),
    /// structural create/delete (M2b), flags (M2c), and components + fields (M3).
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

                // §M3: resolve transient member:<name> field keys to serialized paths BEFORE reconcile,
                // remapping the field-argument spans in lockstep so span-local field patches still match.
                var (desired, fieldArgumentSpans) = AuthoredPathResolver.Resolve(parse.Model, parse.FieldArgumentSpans);

                var snapshot = SceneSnapshotReader.Read(SceneManager.GetActiveScene());

                var result = Reconciler.Reconcile(
                    desired,
                    snapshot,
                    map,
                    parse.Anchors,
                    reservedIdentifiers: null,
                    flagPresence: parse.FlagPresence,
                    componentAnchors: parse.ComponentAnchors,
                    fieldArgumentSpans: fieldArgumentSpans,
                    handles: parse.Handles);

                foreach (var c in result.Conflicts)
                {
                    Debug.LogWarning($"[SceneBuilder] Conflict ({c.Kind}) on '{c.LogicalId}': {c.Reason}");
                }

                foreach (var s in result.Skipped)
                {
                    Debug.LogWarning($"[SceneBuilder] Unsupported field on '{s.LogicalId}' path '{s.Path}' — left untouched.");
                }

                var hasSourceEdits = result.Patch.Edits.Length > 0;
                var hasMapDelta = result.AddedEntries.Length > 0 || result.RemovedLogicalIds.Length > 0;

                if (!hasSourceEdits && !hasMapDelta)
                {
                    Debug.Log("[SceneBuilder] Scene already matches code — nothing to sync.");
                    return;
                }

                if (hasSourceEdits)
                {
                    // Component edits anchor on component LogicalIds — merge those anchors in.
                    var anchors = MergeAnchors(parse.Anchors, parse.ComponentAnchors);
                    var newSource = SourcePatchApplier.Apply(source, result.Patch, anchors);
                    if (newSource != source)
                    {
                        File.WriteAllText(BuilderPath, newSource);
                        Debug.Log($"[SceneBuilder] Synced {result.Patch.Edits.Length} edit(s) back into {BuilderPath}.");
                    }
                }

                if (hasMapDelta)
                {
                    UpdateSidecar(map, result);
                }

                AssetDatabase.Refresh();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Sync failed:\n" + e);
            }
        }

        private static IReadOnlyDictionary<string, SourceSpan> MergeAnchors(
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyDictionary<string, SourceSpan> componentAnchors)
        {
            var merged = new Dictionary<string, SourceSpan>();
            foreach (var (key, span) in anchors)
            {
                merged[key] = span;
            }

            foreach (var (key, span) in componentAnchors)
            {
                merged[key] = span;
            }

            return merged;
        }

        // §M2b: add AddedEntries, drop RemovedLogicalIds. No scene re-save (created GameObjects'
        // GlobalObjectIds already came from the snapshot).
        private static void UpdateSidecar(IdentityMap map, ReconcileResult result)
        {
            var removed = new HashSet<string>(result.RemovedLogicalIds);
            var entries = map.Entries
                .Where(e => !removed.Contains(e.LogicalId))
                .Concat(result.AddedEntries)
                .ToArray();

            var updated = map with { Entries = entries };
            File.WriteAllText(SidecarPath, IdentityMapJson.Serialize(updated));
            Debug.Log($"[SceneBuilder] Sidecar updated: +{result.AddedEntries.Length} / -{result.RemovedLogicalIds.Length} entr(ies).");
        }
    }
}
