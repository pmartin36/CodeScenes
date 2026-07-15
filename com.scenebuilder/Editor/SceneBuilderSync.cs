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
        private const string BuilderName = "DemoScene";

        // <ProjectRoot>/SceneBuilders/ — outside Assets/, so writing the builder never triggers a
        // domain reload. Resolved per call (not a const) since the project root is only known at
        // runtime; see SceneBuilderPaths.
        private static string BuilderPath => SceneBuilderPaths.Builder(BuilderName);
        private static string SidecarPath => SceneBuilderPaths.Sidecar(BuilderName);

        /// <summary>Summary of a <see cref="Run"/> sync for callers/tests.</summary>
        public sealed class SyncResult
        {
            /// <summary>Number of source edits applied to the builder file.</summary>
            public int EditsApplied { get; set; }

            /// <summary>Reconcile conflicts surfaced (transform/name/parent/flags/components).</summary>
            public Conflict[] Conflicts { get; set; } = System.Array.Empty<Conflict>();

            /// <summary>Sidecar entries added by this sync (structural creates).</summary>
            public int AddedEntries { get; set; }

            /// <summary>Sidecar entries removed by this sync (structural deletes).</summary>
            public int RemovedEntries { get; set; }

            /// <summary>True when the builder source or the sidecar changed.</summary>
            public bool Changed { get; set; }

            /// <summary>
            /// Compile errors in the builder source this sync wrote (empty when it compiles, or when
            /// no source was written). Already reported to the Console by <see cref="Run"/>.
            /// </summary>
            public BuilderDiagnostic[] CompileErrors { get; set; } = System.Array.Empty<BuilderDiagnostic>();
        }

        [MenuItem("SceneBuilder/Sync DemoScene (scene -> code)")]
        public static void SyncDemo()
        {
            try
            {
                SceneBuilderPaths.EnsureBuildersDirectory();

                if (!File.Exists(BuilderPath) || !File.Exists(SidecarPath))
                {
                    Debug.LogError($"[SceneBuilder] Build first — missing {BuilderPath} or {SidecarPath}.");
                    return;
                }

                Run(BuilderPath, SidecarPath, SceneManager.GetActiveScene());
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SceneBuilder] Sync failed:\n" + e);
            }
        }

        /// <summary>
        /// Sync-back (scene-&gt;code) against a PASSED scene + paths: read <paramref name="scene"/>,
        /// reconcile it against the builder file at <paramref name="builderPath"/> keyed on
        /// GlobalObjectId, patch THAT builder source in place, and update THAT sidecar at
        /// <paramref name="sidecarPath"/>. The testable seam behind <see cref="SyncDemo"/>. Throws on
        /// failure (no swallowing) so callers/tests observe errors.
        /// </summary>
        public static SyncResult Run(string builderPath, string sidecarPath, Scene scene)
        {
            var source = File.ReadAllText(builderPath);
            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
            var parse = BuilderParser.Parse(source, map);

            // §M3: resolve transient member:<name> field keys to serialized paths BEFORE reconcile,
            // remapping the field-argument spans in lockstep so span-local field patches still match.
            var (desired, fieldArgumentSpans) = AuthoredPathResolver.Resolve(parse.Model, parse.FieldArgumentSpans);

            var snapshot = SceneSnapshotReader.Read(scene);

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
            // §M4: a scene edit that introduces a new asset GUID must persist it into the sidecar
            // Assets[] cache even when nothing structural changed.
            var hasAssetDelta = result.AddedAssets.Length > 0;

            if (!hasSourceEdits && !hasMapDelta && !hasAssetDelta)
            {
                Debug.Log("[SceneBuilder] Scene already matches code — nothing to sync.");
                return new SyncResult { Conflicts = result.Conflicts, Changed = false };
            }

            var editsApplied = 0;
            var currentSource = source;
            var compileErrors = System.Array.Empty<BuilderDiagnostic>();
            if (hasSourceEdits)
            {
                // Component edits anchor on component LogicalIds — merge those anchors in.
                var anchors = MergeAnchors(parse.Anchors, parse.ComponentAnchors);
                var newSource = SourcePatchApplier.Apply(source, result.Patch, anchors);
                if (newSource != source)
                {
                    currentSource = newSource;
                    File.WriteAllText(builderPath, newSource);
                    editsApplied = result.Patch.Edits.Length;
                    Debug.Log($"[SceneBuilder] Synced {result.Patch.Edits.Length} edit(s) back into {builderPath}.");

                    // The builder lives outside Assets/, so Unity's compiler no longer vets what we
                    // just wrote. Check it ourselves, immediately, so a bad emission surfaces in the
                    // Console at the moment it is written instead of silently breaking the next build.
                    compileErrors = BuilderCompileCheck.CheckAndReport(
                        newSource, $"Sync wrote {Path.GetFileName(builderPath)}");
                }
            }

            if (hasMapDelta || hasAssetDelta)
            {
                UpdateSidecar(map, result, currentSource, sidecarPath);
            }

            // No AssetDatabase.Refresh(): the only things this method writes are the builder .cs and the
            // sidecar .json, both under <ProjectRoot>/SceneBuilders/ — outside the roots Unity scans, so
            // there is nothing to import. Refreshing here would trigger a domain reload on every sync.

            return new SyncResult
            {
                EditsApplied = editsApplied,
                Conflicts = result.Conflicts,
                AddedEntries = result.AddedEntries.Length,
                RemovedEntries = result.RemovedLogicalIds.Length,
                Changed = editsApplied > 0 || hasMapDelta || hasAssetDelta,
                CompileErrors = compileErrors,
            };
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
        private static void UpdateSidecar(IdentityMap map, ReconcileResult result, string currentSource, string sidecarPath)
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

            // §M4: fold every asset GUID the reconcile harvested (AddedAssets) into the Assets[] cache
            // so a newly-referenced asset's { Guid, LastKnownPath, TypeHint } persists; a re-referenced
            // GUID refreshes its LastKnownPath.
            var updated = map with
            {
                Entries = reparsed.IdentityMap.Entries,
                Assets = MergeAssets(map.Assets, result.AddedAssets),
            };
            File.WriteAllText(sidecarPath, IdentityMapJson.Serialize(updated));
            Debug.Log($"[SceneBuilder] Sidecar updated: +{result.AddedEntries.Length} / -{result.RemovedLogicalIds.Length} entr(ies), " +
                      $"+{result.AddedAssets.Length} asset(s).");
        }

        // Merge harvested asset entries into the existing Assets[] cache, keyed by GUID; a harvested
        // entry wins (its LastKnownPath reflects the current scene) over a stale cached one.
        private static AssetEntry[] MergeAssets(AssetEntry[] existing, AssetEntry[] added)
        {
            if (added.Length == 0)
            {
                return existing;
            }

            var byGuid = new Dictionary<string, AssetEntry>();
            foreach (var entry in existing)
            {
                byGuid[entry.Guid] = entry;
            }

            foreach (var entry in added)
            {
                byGuid[entry.Guid] = entry;
            }

            return byGuid.Values.ToArray();
        }
    }
}
