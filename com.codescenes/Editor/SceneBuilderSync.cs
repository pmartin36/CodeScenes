using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
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

            /// <summary>
            /// b6-t1: canonical `"{logicalId}.{fieldKey}"` keys resolved as a TRUE both-sides conflict by
            /// <see cref="RunConflictAware"/> (empty for the plain <see cref="Run"/> overloads, which never
            /// compute a conflict set). Scene-wins already decided the written value; this is telemetry —
            /// the prior code value lives in the source's `// CONFLICT:` marker, not here.
            /// </summary>
            public string[] ConflictFields { get; set; } = System.Array.Empty<string>();
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
            => Run(builderPath, sidecarPath, scene, preAssembledSnapshot: null);

        /// <summary>
        /// Sync-back (scene-&gt;code) identical to <see cref="Run(string, string, Scene)"/>, except the
        /// scene snapshot is either the passed <paramref name="preAssembledSnapshot"/> (e.g. the
        /// auto-sync executor's O(changed) <c>ChangeScopedSnapshot</c> result) or, when null, a cold
        /// <see cref="SceneSnapshotReader.Read"/> — so the two overloads are byte-identical in effect
        /// given an equivalent snapshot. Also logs a loud convergence-defect error when the reconcile
        /// emits a patch that applies byte-identically (PatchEdits &gt; 0, EditsApplied == 0), so both
        /// the manual and auto-sync paths inherit the guard by default.
        /// </summary>
        public static SyncResult Run(
            string builderPath,
            string sidecarPath,
            Scene scene,
            SceneBuilder.Core.Model.SceneSnapshot? preAssembledSnapshot)
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

            // M5: reverse-map a live scene-object reference to its LogicalId (or raw GlobalObjectId
            // when not yet mapped) — built once per sync from the IdentityMap, used only for the cold
            // read (a preAssembledSnapshot, e.g. auto-sync's ChangeScopedSnapshot, carries its own
            // resolver set by the caller).
            var sceneRef = ObjectReferenceResolver.BuildSceneRefResolver(map);
            var snapshot = preAssembledSnapshot ?? SceneSnapshotReader.Read(scene, sceneRef);

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

            // Convergence-defect guard (inherit-by-default: the manual SyncDemo path gets it too):
            // the reconcile PRODUCED edits (PatchEdits > 0) but the applied text was byte-identical to
            // what was already on disk (EditsApplied == 0, i.e. WriteIfChanged found no real diff).
            // That means the reconcile believed the source was stale when it was not — a bug in the
            // reconcile, not a real change. Already treated as converged (no rewrite); this just makes
            // the defect loud instead of silent.
            if (result.Patch.Edits.Length > 0 && editsApplied == 0)
            {
                Debug.LogError(
                    $"[SceneBuilder] Convergence defect: reconcile produced {result.Patch.Edits.Length} " +
                    "patch edit(s) that applied byte-identically — treating as converged (not re-applying).");
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

        // ---- b6-t1: conflict-aware combined (scene+code) sync -------------------------------------

        /// <summary>
        /// Canonical field-key attribution: `(logicalId-or-componentLogicalId, fieldKey)`. A plain
        /// struct (not a positional record) — com.codescenes/Editor targets netstandard2.1 without an
        /// <c>IsExternalInit</c> polyfill in scope, unlike SceneBuilder.Core.
        /// </summary>
        private readonly struct FieldKey : IEquatable<FieldKey>
        {
            public readonly string Group;
            public readonly string Field;

            public FieldKey(string group, string field)
            {
                Group = group;
                Field = field;
            }

            public bool Equals(FieldKey other) => Group == other.Group && Field == other.Field;
            public override bool Equals(object obj) => obj is FieldKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Group, Field);
        }

        private sealed class ConflictInfo
        {
            public readonly FieldKey Key;
            public readonly string DisplayName;
            public readonly string SceneExpr;
            public readonly string CodeExpr;

            public ConflictInfo(FieldKey key, string displayName, string sceneExpr, string codeExpr)
            {
                Key = key;
                DisplayName = displayName;
                SceneExpr = sceneExpr;
                CodeExpr = codeExpr;
            }
        }

        /// <summary>Owning-GameObject LogicalId -&gt; authored Name, for located-error messages.</summary>
        private static void FlattenNames(
            IReadOnlyList<SceneBuilder.Core.Model.GameObjectNode> nodes,
            Dictionary<string, string> map)
        {
            foreach (var node in nodes)
            {
                map[node.LogicalId] = node.Name;
                FlattenNames(node.Children, map);
            }
        }

        /// <summary>
        /// Combined scene+code sync for a both-sides-changed cycle (b6-t1, spec checklist #9, #10):
        /// 3-way field-level merge of <paramref name="baselineSource"/>/<paramref name="baselineSnapshot"/>
        /// (last-converged), <paramref name="builderPath"/>'s CURRENT on-disk source (the code edits) and
        /// <paramref name="liveSnapshot"/> (the scene edits). Every non-overlapping field applies in its
        /// own direction; a true same-field-same-object overlap resolves SCENE-WINS with the prior code
        /// value preserved in an inline `// CONFLICT:` marker, a located Console error, and a scene-view
        /// overlay registration — never a modal. Reuses the exact same write tail as <see cref="Run"/>
        /// (<see cref="SourcePatchApplier"/>, <see cref="SceneBuilderPaths.WriteIfChanged"/>,
        /// <see cref="UpdateSidecar"/>, <see cref="BuilderCompileCheck"/>). Does NOT push code-only fields
        /// into the scene — the caller runs <see cref="SceneBuilderBuild.Run"/> against the source this
        /// writes for that (research.md step 3), so a single write tail keeps writing the authority for
        /// EACH side simple: this method owns the source, Build owns the scene.
        /// </summary>
        public static SyncResult RunConflictAware(
            string builderPath,
            string sidecarPath,
            Scene scene,
            SceneBuilder.Core.Model.SceneSnapshot liveSnapshot,
            string baselineSource,
            SceneBuilder.Core.Model.SceneSnapshot baselineSnapshot,
            ConflictSurfacing surfacing)
        {
            var newSource = File.ReadAllText(builderPath);
            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));

            var baselineLoaded = DesiredModelLoader.Load(baselineSource, map);
            var newLoaded = DesiredModelLoader.Load(newSource, map);

            // SCENE-changed keys: baseline (last-converged) desired vs the LIVE scene — exactly the
            // fields the user moved in the scene since convergence.
            var sceneReconcile = Reconciler.Reconcile(
                baselineLoaded.Desired,
                liveSnapshot,
                map,
                baselineLoaded.Parse.Anchors,
                reservedIdentifiers: null,
                flagPresence: baselineLoaded.Parse.FlagPresence,
                componentAnchors: baselineLoaded.Parse.ComponentAnchors,
                fieldArgumentSpans: baselineLoaded.FieldArgumentSpans,
                handles: baselineLoaded.Parse.Handles);

            var sceneKeys = new HashSet<FieldKey>(
                sceneReconcile.Patch.Edits
                    .Select(e => KeyOfSourceEdit(e, baselineLoaded.FieldArgumentSpans))
                    .Where(k => k.HasValue)
                    .Select(k => k!.Value));

            // CODE-changed keys+ops: the NEW (on-disk) desired vs the BASELINE DESIRED (both parsed
            // from source, never live-scene) — exactly the fields the user edited in code since
            // convergence. Deliberately NOT `Differ.Diff(newDesired, baselineSnapshot, ...)` (what
            // research.md's blueprint proposed): `SerializedFieldBridge.ReadComponent` omits any live
            // field that equals its type's freshly-constructed DEFAULT (e.g. a Rigidbody's default
            // mass IS 1) from a snapshot's `Fields` map entirely, and `Differ`'s component walk treats
            // "absent from actual" as "changed" — so a field a builder explicitly authors AT the
            // type's default value (a common, ordinary case, not an edge case) reads as code-changed
            // on EVERY cycle even when the source line never moved. `baselineLoaded.Desired` never
            // drops a field this way (both sides are parsed source, never default-filtered), so
            // comparing desired-vs-desired is exact where desired-vs-snapshot is not.
            var codeOps = DiffDesiredFields(baselineLoaded.Desired, newLoaded.Desired);
            var codeOpsByKey = new Dictionary<FieldKey, ChangeOp>();
            foreach (var op in codeOps)
            {
                foreach (var key in KeysOfChangeOp(op))
                {
                    codeOpsByKey[key] = op;
                }
            }

            // The APPLICABLE patch: new (on-disk) source vs the live scene — correct spans for the file
            // we are about to write. Partitioned below by attribution against the two key sets above.
            var applicable = Reconciler.Reconcile(
                newLoaded.Desired,
                liveSnapshot,
                map,
                newLoaded.Parse.Anchors,
                reservedIdentifiers: null,
                flagPresence: newLoaded.Parse.FlagPresence,
                componentAnchors: newLoaded.Parse.ComponentAnchors,
                fieldArgumentSpans: newLoaded.FieldArgumentSpans,
                handles: newLoaded.Parse.Handles);

            // Resolves the AUTHORED GameObject name (e.g. "Box") for the located-error message — the
            // LogicalId itself is the resolved HANDLE when the statement declares one (e.g. `var box =
            // scene.Add("Box")` resolves to "box"), which is NOT the same string a reader (or a test
            // regex on the Console message) expects to find.
            var namesByLogicalId = new Dictionary<string, string>();
            FlattenNames(newLoaded.Desired.Roots, namesByLogicalId);

            var keptEdits = new List<SourceEdit>();
            var conflicts = new List<ConflictInfo>();
            foreach (var edit in applicable.Patch.Edits)
            {
                var key = KeyOfSourceEdit(edit, newLoaded.FieldArgumentSpans);
                if (key == null)
                {
                    // Unattributable (structural: append/move/reorder/remove/introduce-*) — always
                    // kept, same as the single-direction Run: this task's merge is field-level only.
                    keptEdits.Add(edit);
                    continue;
                }

                var k = key.Value;
                var sceneChanged = sceneKeys.Contains(k);
                var codeChanged = codeOpsByKey.TryGetValue(k, out var codeOp);

                if (!sceneChanged)
                {
                    // CODE-only: the scene never touched this field — never emit the revert-to-old-
                    // scene-value edit that would silently clobber the user's code edit.
                    continue;
                }

                keptEdits.Add(edit);
                if (codeChanged)
                {
                    var ownerLogicalId = k.Group.Split('/')[0];
                    var displayName = namesByLogicalId.TryGetValue(ownerLogicalId, out var name) ? name : k.Group;
                    conflicts.Add(new ConflictInfo(k, displayName, SceneExprOfEdit(edit), RenderPriorCodeExpr(codeOp!, k.Field)));
                }
            }

            foreach (var c in applicable.Conflicts)
            {
                Debug.LogWarning($"[SceneBuilder] Conflict ({c.Kind}) on '{c.LogicalId}': {c.Reason}");
            }

            var assetMerge = AssetCacheMerge.Merge(map.Assets, applicable.AddedAssets);
            var hasMapDelta = applicable.AddedEntries.Length > 0 || applicable.RemovedLogicalIds.Length > 0;
            var hasAssetDelta = assetMerge.ChangedCount > 0;

            var result = new SyncResult
            {
                PatchEdits = applicable.Patch.Edits.Length,
                Conflicts = applicable.Conflicts,
                ConflictFields = conflicts.Select(c => $"{c.Key.Group}.{c.Key.Field}").ToArray(),
            };

            if (keptEdits.Count == 0 && !hasMapDelta && !hasAssetDelta)
            {
                return result;
            }

            var currentSource = newSource;
            var editsApplied = 0;
            var compileErrors = System.Array.Empty<BuilderDiagnostic>();
            if (keptEdits.Count > 0)
            {
                var anchors = MergeAnchors(newLoaded.Parse.Anchors, newLoaded.Parse.ComponentAnchors);
                var filteredPatch = new SourcePatch { FilePath = builderPath, Edits = keptEdits.ToArray() };
                var patchedSource = SourcePatchApplier.Apply(newSource, filteredPatch, anchors);

                if (conflicts.Count > 0)
                {
                    // Re-resolve spans against the JUST-PATCHED text (a scene-value literal can differ
                    // in length from the code literal it replaced) before inserting marker lines.
                    var patchedLoaded = DesiredModelLoader.Load(patchedSource, map);
                    patchedSource = InsertConflictMarkers(
                        patchedSource, patchedLoaded.FieldArgumentSpans, patchedLoaded.Parse.Anchors, conflicts);

                    foreach (var c in conflicts)
                    {
                        surfacing.LogConflict(c.DisplayName, c.Key.Field, c.SceneExpr, c.CodeExpr);
                        surfacing.RegisterOverlay(c.Key.Group);
                    }
                }

                if (SceneBuilderPaths.WriteIfChanged(builderPath, patchedSource))
                {
                    currentSource = patchedSource;
                    editsApplied = keptEdits.Count;
                    Debug.Log($"[SceneBuilder] Conflict-aware sync applied {keptEdits.Count} edit(s) " +
                              $"({conflicts.Count} resolved conflict(s)) into {builderPath}.");

                    compileErrors = BuilderCompileCheck.CheckAndReport(
                        patchedSource, $"Conflict-aware sync wrote {Path.GetFileName(builderPath)}");
                }
            }

            var sidecarWritten = false;
            if (editsApplied > 0 || hasMapDelta || hasAssetDelta)
            {
                sidecarWritten = UpdateSidecar(map, applicable, currentSource, sidecarPath, assetMerge);
            }

            result.EditsApplied = editsApplied;
            result.Changed = editsApplied > 0 || sidecarWritten;
            result.CompileErrors = compileErrors;
            return result;
        }

        /// <summary>
        /// Field-level `oldModel -&gt; newModel` diff for two DESIRED models (both parsed source, never
        /// live-scene) — the code-side half of the b6-t1 merge (see the caller's comment for why this
        /// is NOT <see cref="Differ.Diff"/> against a snapshot). Structural changes (a LogicalId present
        /// on only one side) are skipped: this task's merge is field-level only, matching
        /// <see cref="KeyOfSourceEdit"/>'s same structural pass-through on the scene side.
        /// </summary>
        private static List<ChangeOp> DiffDesiredFields(SceneModel oldModel, SceneModel newModel)
        {
            var oldByLogicalId = new Dictionary<string, GameObjectNode>();
            FlattenGameObjects(oldModel.Roots, oldByLogicalId);
            var newByLogicalId = new Dictionary<string, GameObjectNode>();
            FlattenGameObjects(newModel.Roots, newByLogicalId);

            var ops = new List<ChangeOp>();
            foreach (var (logicalId, newNode) in newByLogicalId)
            {
                if (!oldByLogicalId.TryGetValue(logicalId, out var oldNode))
                {
                    continue; // structurally new this batch — not a field-level edit.
                }

                if (!string.Equals(oldNode.Name, newNode.Name, StringComparison.Ordinal))
                {
                    ops.Add(new SetName { LogicalId = logicalId, Name = newNode.Name });
                }

                if (!string.Equals(oldNode.Tag, newNode.Tag, StringComparison.Ordinal))
                {
                    ops.Add(new SetTag { LogicalId = logicalId, Tag = newNode.Tag });
                }

                if (oldNode.Layer != newNode.Layer)
                {
                    ops.Add(new SetLayer { LogicalId = logicalId, Layer = newNode.Layer });
                }

                if (oldNode.Active != newNode.Active)
                {
                    ops.Add(new SetActive { LogicalId = logicalId, Active = newNode.Active });
                }

                if (oldNode.IsStatic != newNode.IsStatic)
                {
                    ops.Add(new SetStatic { LogicalId = logicalId, IsStatic = newNode.IsStatic });
                }

                if (!Equals(oldNode.Transform, newNode.Transform))
                {
                    ops.Add(new SetTransform { LogicalId = logicalId, Transform = newNode.Transform });
                }

                var oldComponentsByLogicalId = oldNode.Components.ToDictionary(c => c.LogicalId);
                foreach (var newComponent in newNode.Components)
                {
                    if (!oldComponentsByLogicalId.TryGetValue(newComponent.LogicalId, out var oldComponent))
                    {
                        continue; // component attached this batch — structural, not field-level.
                    }

                    foreach (var (fieldKey, newValue) in newComponent.Fields)
                    {
                        if (!oldComponent.Fields.TryGetValue(fieldKey, out var oldValue) || !Equals(oldValue, newValue))
                        {
                            ops.Add(new SetField
                            {
                                LogicalId = logicalId,
                                ComponentLogicalId = newComponent.LogicalId,
                                Path = fieldKey,
                                Value = newValue,
                            });
                        }
                    }
                }
            }

            return ops;
        }

        private static void FlattenGameObjects(IReadOnlyList<GameObjectNode> nodes, Dictionary<string, GameObjectNode> map)
        {
            foreach (var node in nodes)
            {
                map[node.LogicalId] = node;
                FlattenGameObjects(node.Children, map);
            }
        }

        /// <summary>Canonical key for a scene-side <see cref="SourceEdit"/> — null when unattributable (structural).</summary>
        private static FieldKey? KeyOfSourceEdit(
            SourceEdit edit,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans)
        {
            switch (edit)
            {
                case PatchArgument { ArgName: "name" } pa:
                    return new FieldKey(pa.Anchor, "name");
                case PatchArgument pa when pa.ArgName is "pos" or "rot" or "scale":
                    return new FieldKey(pa.Anchor, "transform." + pa.ArgName);
                case PatchFlagArgument pf:
                    return new FieldKey(pf.Anchor, FlagFieldName(pf.Flag));
                case PatchComponentField pcf:
                    if (fieldArgumentSpans.TryGetValue(pcf.Anchor, out var compSpans))
                    {
                        foreach (var (fieldKey, span) in compSpans)
                        {
                            if (span.Equals(pcf.ValueSpan))
                            {
                                return new FieldKey(pcf.Anchor, fieldKey);
                            }
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Canonical key(s) for a code-side <see cref="ChangeOp"/> — a transform op yields all three sub-keys.</summary>
        private static IEnumerable<FieldKey> KeysOfChangeOp(ChangeOp op)
        {
            switch (op)
            {
                case SetName n:
                    yield return new FieldKey(n.LogicalId, "name");
                    break;
                case SetTag t:
                    yield return new FieldKey(t.LogicalId, "tag");
                    break;
                case SetLayer l:
                    yield return new FieldKey(l.LogicalId, "layer");
                    break;
                case SetActive a:
                    yield return new FieldKey(a.LogicalId, "active");
                    break;
                case SetStatic s:
                    yield return new FieldKey(s.LogicalId, "static");
                    break;
                case SetTransform tr:
                    yield return new FieldKey(tr.LogicalId, "transform.pos");
                    yield return new FieldKey(tr.LogicalId, "transform.rot");
                    yield return new FieldKey(tr.LogicalId, "transform.scale");
                    break;
                case SetField f:
                    yield return new FieldKey(f.ComponentLogicalId, f.Path);
                    break;
            }
        }

        private static string FlagFieldName(FlagKind flag) => flag switch
        {
            FlagKind.Tag => "tag",
            FlagKind.Layer => "layer",
            FlagKind.Active => "active",
            FlagKind.Static => "static",
            _ => "flag",
        };

        /// <summary>The SCENE-side rendered literal already computed by the reconcile for this edit.</summary>
        private static string SceneExprOfEdit(SourceEdit edit) => edit switch
        {
            PatchArgument pa => pa.NewExpr,
            PatchFlagArgument pf => pf.NewExpr,
            PatchComponentField pcf => pcf.NewExpr,
            _ => "",
        };

        /// <summary>Renders the CODE-side (prior) value a conflicting <see cref="ChangeOp"/> carries, for the marker.</summary>
        private static string RenderPriorCodeExpr(ChangeOp op, string subKey) => op switch
        {
            SetName n => SourceExpr.StringLiteral(n.Name),
            SetTag t => SourceExpr.StringLiteral(t.Tag),
            SetLayer l => SourceExpr.IntLiteral(l.Layer),
            SetActive a => a.Active ? "true" : "false",
            SetStatic s => s.IsStatic ? "true" : "false",
            SetTransform tr => subKey switch
            {
                "transform.pos" => SourceExpr.Vec3Literal(tr.Transform.Position),
                "transform.scale" => SourceExpr.Vec3Literal(tr.Transform.Scale),
                _ => $"new UnityEngine.Quaternion({tr.Transform.Rotation.X}f, {tr.Transform.Rotation.Y}f, " +
                     $"{tr.Transform.Rotation.Z}f, {tr.Transform.Rotation.W}f)",
            },
            SetField f => SourceExpr.ValueNodeLiteral(f.Value),
            _ => "?",
        };

        /// <summary>
        /// Inserts a `// CONFLICT:` comment line immediately above each conflicting statement/field, at
        /// its CURRENT (post-patch) position — never replacing the applied scene value inline. Processed
        /// bottom-to-top so each insertion leaves earlier offsets valid.
        /// </summary>
        private static string InsertConflictMarkers(
            string source,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyList<ConflictInfo> conflicts)
        {
            var insertions = new List<(int Position, string Text)>();
            foreach (var c in conflicts)
            {
                int anchorStart;
                if (fieldArgumentSpans.TryGetValue(c.Key.Group, out var compSpans)
                    && compSpans.TryGetValue(c.Key.Field, out var valueSpan))
                {
                    anchorStart = valueSpan.Start;
                }
                else if (anchors.TryGetValue(c.Key.Group, out var goSpan))
                {
                    anchorStart = goSpan.Start;
                }
                else
                {
                    continue; // cannot relocate post-patch — should not happen, never lose the value though.
                }

                var clamped = Math.Min(anchorStart, source.Length);
                var lineStart = source.LastIndexOf('\n', Math.Max(clamped - 1, 0)) + 1;
                var indentEnd = lineStart;
                while (indentEnd < source.Length && (source[indentEnd] == ' ' || source[indentEnd] == '\t'))
                {
                    indentEnd++;
                }

                var indent = source.Substring(lineStart, indentEnd - lineStart);
                var markerLine = indent + ConflictSurfacing.BuildMarkerLine(c.Key.Field, c.CodeExpr, c.SceneExpr) + "\n";
                insertions.Add((lineStart, markerLine));
            }

            foreach (var (position, text) in insertions.OrderByDescending(i => i.Position))
            {
                source = source.Insert(position, text);
            }

            return source;
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
            var reparsed = ComponentTypeNormalizer.ParseAndNormalize(currentSource, mergedMap);
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
