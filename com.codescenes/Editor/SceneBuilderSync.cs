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

            /// <summary>
            /// Number of edits the reconcile PRODUCED, before applying them — i.e. how many changes it
            /// believed the source needed. Distinct from <see cref="EditsApplied"/>, which counts only
            /// edits whose applied text actually DIFFERED from the source: a patch that re-emits
            /// byte-identical text scores zero there while still meaning the reconcile wrongly decided
            /// the source was out of date. On a no-op re-sync this must be 0; a non-zero value is a
            /// convergence defect even when the text happens to match.
            /// </summary>
            public int PatchEdits { get; set; }

            /// <summary>Reconcile conflicts surfaced (transform/name/parent/flags/components).</summary>
            public Conflict[] Conflicts { get; set; } = System.Array.Empty<Conflict>();

            /// <summary>Sidecar entries added by this sync (structural creates).</summary>
            public int AddedEntries { get; set; }

            /// <summary>Sidecar entries removed by this sync (structural deletes).</summary>
            public int RemovedEntries { get; set; }

            /// <summary>
            /// True when this sync actually WROTE — i.e. the builder source or the sidecar differs on
            /// disk from what was there before. A sync that reconciled to the same bytes is not a
            /// change and reports false, so a watcher can trust this bit to decide whether to react.
            /// </summary>
            public bool Changed { get; set; }

            /// <summary>
            /// Compile errors in the builder source this sync wrote (empty when it compiles, or when
            /// no source was written). Already reported to the Console by <see cref="Run"/>.
            /// </summary>
            public BuilderDiagnostic[] CompileErrors { get; set; } = System.Array.Empty<BuilderDiagnostic>();
        }

        [MenuItem("CodeScenes/Sync DemoScene (scene -> code)")]
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

            // THE shared source->desired seam (parse -> resolve authored paths -> lower asset refs).
            // Build goes through the exact same call. Sync used to open-code the first two stages and
            // silently omit the third, which is precisely why it never converged: an unlowered source
            // ref carries Guid="", AssetRef.Equals keys on (Guid, FileId) ONLY, so it could never equal
            // the snapshot's populated ref and every sync re-patched every asset ref forever.
            var loaded = DesiredModelLoader.Load(source, map);

            // A colliding LogicalId is a property of the SOURCE alone; FlattenModel drops one node
            // (last-write-wins), so heal BEFORE reconcile. Gate on DuplicateLogicalId only — an
            // AmbiguousAnchor group is healed by the Reconciler's own duplicate-name pass.
            if (loaded.Parse.Ambiguities.Any(c => c.Kind == ConflictKind.DuplicateLogicalId))
            {
                var healed = IdCollisionHealer.Heal(source, loaded.Parse);
                if (SceneBuilderPaths.WriteIfChanged(builderPath, healed))
                {
                    source = healed;
                    loaded = DesiredModelLoader.Load(healed, map);
                }
            }

            var parse = loaded.Parse;
            var desired = loaded.Desired;
            var fieldArgumentSpans = loaded.FieldArgumentSpans;

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
            // Assets[] cache even when nothing structural changed. The gate is the REAL delta — entries
            // actually added, or whose LastKnownPath/TypeHint actually moved — NOT the count of refs
            // the reconcile harvested. AddedAssets is a harvest: a scene that merely CONTAINS a
            // material yields one every single pass, so gating on its length made "nothing to sync"
            // unreachable and rewrote the sidecar forever.
            var assetMerge = AssetCacheMerge.Merge(map.Assets, result.AddedAssets);
            var hasAssetDelta = assetMerge.ChangedCount > 0;

            if (!hasSourceEdits && !hasMapDelta && !hasAssetDelta)
            {
                Debug.Log("[SceneBuilder] Scene already matches code — nothing to sync.");
                return new SyncResult
                {
                    Conflicts = result.Conflicts,
                    Changed = false,
                    PatchEdits = result.Patch.Edits.Length,
                };
            }

            var editsApplied = 0;
            var currentSource = source;
            var compileErrors = System.Array.Empty<BuilderDiagnostic>();
            if (hasSourceEdits)
            {
                // Component edits anchor on component LogicalIds — merge those anchors in.
                var anchors = MergeAnchors(parse.Anchors, parse.ComponentAnchors);
                var newSource = SourcePatchApplier.Apply(source, result.Patch, anchors);
                if (SceneBuilderPaths.WriteIfChanged(builderPath, newSource))
                {
                    currentSource = newSource;
                    editsApplied = result.Patch.Edits.Length;
                    Debug.Log($"[SceneBuilder] Synced {result.Patch.Edits.Length} edit(s) back into {builderPath}.");

                    // The builder lives outside Assets/, so Unity's compiler no longer vets what we
                    // just wrote. Check it ourselves, immediately, so a bad emission surfaces in the
                    // Console at the moment it is written instead of silently breaking the next build.
                    compileErrors = BuilderCompileCheck.CheckAndReport(
                        newSource, $"Sync wrote {Path.GetFileName(builderPath)}");
                }
            }

            // The sidecar is keyed by LogicalId, and a LogicalId is DERIVED from the source (name +
            // sibling index + parent path). So ANY source rewrite can re-key it — a rename, reparent
            // or reorder changes the LogicalId of the node, its components and its whole subtree.
            // Skipping the sidecar write whenever there was no map delta therefore left it pointing
            // at LogicalIds the source no longer contains, and the NEXT sync mis-reconciled: a
            // renamed object surfaced as a MissingSourceAnchor conflict, and a re-synced object was
            // re-created as a DUPLICATE statement. Persist whenever the source actually changed.
            var sidecarWritten = false;
            if (editsApplied > 0 || hasMapDelta || hasAssetDelta)
            {
                sidecarWritten = UpdateSidecar(map, result, currentSource, sidecarPath, assetMerge);
            }

            // No AssetDatabase.Refresh(): the only things this method writes are the builder .cs and the
            // sidecar .json, both under <ProjectRoot>/SceneBuilders/ — outside the roots Unity scans, so
            // there is nothing to import. Refreshing here would trigger a domain reload on every sync.

            return new SyncResult
            {
                EditsApplied = editsApplied,
                PatchEdits = result.Patch.Edits.Length,
                Conflicts = result.Conflicts,
                AddedEntries = result.AddedEntries.Length,
                RemovedEntries = result.RemovedLogicalIds.Length,
                // Changed means BYTES CHANGED ON DISK, and nothing else. Deriving it from intent
                // (`hasAssetDelta` etc.) is what let it report true on a sync that wrote nothing; both
                // writes below route through WriteIfChanged, so this is now an observation, not a claim.
                Changed = editsApplied > 0 || sidecarWritten,
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
        /// <summary>Returns true when the sidecar on disk actually changed.</summary>
        private static bool UpdateSidecar(
            IdentityMap map,
            ReconcileResult result,
            string currentSource,
            string sidecarPath,
            AssetCacheMerge.Result assetMerge)
        {
            var removed = new HashSet<string>(result.RemovedLogicalIds);
            var mergedEntries = map.Entries
                .Where(e => !removed.Contains(e.LogicalId))
                .Concat(result.AddedEntries)
                .ToArray();
            var mergedMap = map with { Entries = mergedEntries };

            // BuilderParser.Parse populates Name+SiblingIndex from the parsed structure, but it only
            // carries a GlobalObjectId over when the LogicalId is UNCHANGED. That loses the id for
            // exactly the edits sync exists to make: a rename/reparent/reorder rewrites the source,
            // which re-derives the LogicalId, which orphans the entry — and the next Build/Sync then
            // sees a mapped object with no id and emits a spurious create.
            //
            // IdentityRemapper is the project's existing answer to that (LogicalId, then Name, then
            // SiblingIndex, parent-by-parent) — the same structural matching the Build path relies on.
            // Run it over the re-parsed model so ids survive the rewrite.
            var reparsed = BuilderParser.Parse(currentSource, mergedMap);
            var remapped = IdentityRemapper.Remap(reparsed.Model, mergedMap);

            // Split of authority, and it matters: the patched SOURCE decides WHICH entries exist and
            // in what order; the remap only supplies the GlobalObjectId each one carries. Taking
            // Remap's entry list wholesale would be wrong on both counts — it appends prior entries it
            // could not pair (correct when Build merges, but here a stale leftover), and a reparent
            // makes it emit the moved node TWICE (once unmatched with no id, once as the unconsumed
            // prior), which is a duplicate LogicalId that throws the very next parse.
            var carriedGlobalObjectIds = new Dictionary<string, string>();
            foreach (var entry in remapped.Entries)
            {
                if (string.IsNullOrEmpty(entry.GlobalObjectId) || carriedGlobalObjectIds.ContainsKey(entry.LogicalId))
                {
                    continue;
                }

                carriedGlobalObjectIds[entry.LogicalId] = entry.GlobalObjectId;
            }

            // §M4: fold every asset GUID the reconcile harvested (AddedAssets) into the Assets[] cache
            // so a newly-referenced asset's { Guid, LastKnownPath, TypeHint } persists; a re-referenced
            // GUID refreshes its LastKnownPath.
            var updated = map with
            {
                // BuilderParser already carried ids over for the LogicalIds the rewrite left alone;
                // the remap fills in ONLY the ones it re-keyed (a rename/reparent/reorder), so a
                // confident exact-LogicalId match is never overwritten by a structural guess.
                Entries = reparsed.IdentityMap.Entries
                    .Select(e => string.IsNullOrEmpty(e.GlobalObjectId)
                        && carriedGlobalObjectIds.TryGetValue(e.LogicalId, out var carried)
                            ? e with { GlobalObjectId = carried }
                            : e)
                    .ToArray(),
                Assets = assetMerge.Merged,
            };

            var wrote = SceneBuilderPaths.WriteIfChanged(sidecarPath, IdentityMapJson.Serialize(updated));
            if (wrote)
            {
                // The asset count is the REAL delta (added / path-or-type changed), not the number of
                // refs harvested — reporting the harvest meant every sync of a scene holding one
                // material claimed "+1 asset(s)" while adding nothing.
                Debug.Log($"[SceneBuilder] Sidecar updated: +{result.AddedEntries.Length} / -{result.RemovedLogicalIds.Length} entr(ies), " +
                          $"+{assetMerge.ChangedCount} asset(s).");
            }

            return wrote;
        }
    }
}
