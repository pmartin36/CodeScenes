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

                var currentSource = source;
                if (hasSourceEdits)
                {
                    // Component edits anchor on component LogicalIds — merge those anchors in.
                    var anchors = MergeAnchors(parse.Anchors, parse.ComponentAnchors);
                    var newSource = SourcePatchApplier.Apply(source, result.Patch, anchors);
                    if (newSource != source)
                    {
                        currentSource = newSource;
                        File.WriteAllText(BuilderPath, newSource);
                        Debug.Log($"[SceneBuilder] Synced {result.Patch.Edits.Length} edit(s) back into {BuilderPath}.");
                    }
                }

                if (hasMapDelta)
                {
                    UpdateSidecar(map, result, currentSource);
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
        // GlobalObjectIds already came from the snapshot). The persisted sidecar must ALSO carry the
        // structural fingerprint (Name+SiblingIndex) for GameObject entries so the NEXT Build's
        // IdentityRemapper can match by name/sibling — so we RE-PARSE the patched source (whose
        // ParseResult.IdentityMap already populates Name+SiblingIndex) and carry the GlobalObjectIds
        // over via the merged (survivor + reconcile-added) map.
        private static void UpdateSidecar(IdentityMap map, ReconcileResult result, string currentSource)
        {
            var removed = new HashSet<string>(result.RemovedLogicalIds);
            var mergedEntries = map.Entries
                .Where(e => !removed.Contains(e.LogicalId))
                .Concat(result.AddedEntries)
                .ToArray();
            var mergedMap = map with { Entries = mergedEntries };

            // BuilderParser.Parse carries each node's GlobalObjectId over from `mergedMap` by
            // LogicalId while populating Name+SiblingIndex from the parsed structure.
            var reparsed = BuilderParser.Parse(currentSource, mergedMap);

            var updated = map with { Entries = reparsed.IdentityMap.Entries };
            File.WriteAllText(SidecarPath, IdentityMapJson.Serialize(updated));
            Debug.Log($"[SceneBuilder] Sidecar updated: +{result.AddedEntries.Length} / -{result.RemovedLogicalIds.Length} entr(ies).");
        }
    }
}
