using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // DetectAppends (incl. its recursion) lives in ReconcilerAppends.cs — a pure split to keep this
    // file under the project's file-size budget after b4-t3 threaded `pendingTargets` through it.
    public static partial class Reconciler
    {
        private sealed record SnapshotEntry(SnapshotNode Node, string? ParentGlobalObjectId, int SiblingIndex);

        public static ReconcileResult Reconcile(
            SceneModel expected,
            SceneSnapshot actual,
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? anchors = null,
            IReadOnlyCollection<string>? reservedIdentifiers = null,
            IReadOnlyDictionary<string, FlagPresence>? flagPresence = null,
            IReadOnlyDictionary<string, SourceSpan>? componentAnchors = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans = null,
            IReadOnlyDictionary<string, string>? handles = null)
        {
            var changeSet = Differ.Diff(expected, actual, identityMap);

            var logicalIdToGlobalObjectId = identityMap.Entries
                .Where(e => (e.Kind == "GameObject" || e.Kind == "PrefabInstance") && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

            var globalObjectIdToLogicalId = logicalIdToGlobalObjectId
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

            // m6-b4-t1: source-prefab GUID -> last-known asset path, re-deriving the
            // `.Instance(path)` argument for a snapshot-only prefab-instance root. First-wins on a
            // (shouldn't-happen) duplicate guid, skips entries with no guid.
            var prefabPathByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in identityMap.Assets)
            {
                if (!string.IsNullOrEmpty(asset.Guid) && !prefabPathByGuid.ContainsKey(asset.Guid))
                {
                    prefabPathByGuid[asset.Guid] = asset.LastKnownPath;
                }
            }

            var logicalIdToParentLogicalId = identityMap.Entries
                .Where(e => e.Kind == "GameObject" || e.Kind == "PrefabInstance")
                .ToDictionary(e => e.LogicalId, e => e.ParentLogicalId);

            var snapshotByGoid = new Dictionary<string, SnapshotEntry>();
            FlattenSnapshot(actual.Roots, null, snapshotByGoid);

            var modelByLogicalId = new Dictionary<string, GameObjectNode>();
            FlattenModel(expected.Roots, modelByLogicalId);

            var conflicts = new List<Conflict>();
            var reportedMissingAnchor = new HashSet<string>();
            ISet<string> suppressed = new HashSet<string>();

            if (anchors != null)
            {
                var (ambiguityConflicts, ambiguitySuppressed) =
                    ConflictDetector.DetectAmbiguousReorders(expected, changeSet, anchors, logicalIdToGlobalObjectId);
                conflicts.AddRange(ambiguityConflicts);
                suppressed = ambiguitySuppressed;
            }

            var edits = new List<SourceEdit>();

            var addedEntries = new List<IdentityMapEntry>();
            var removedLogicalIds = new List<string>();
            var skippedFields = new List<SceneBuilder.Core.Plan.SkippedField>();
            var addedAssets = new List<AssetEntry>();
            var nextIndexByParentKey = new Dictionary<string, int>();
            var introducedHandleByParent = new Dictionary<string, string>();

            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in identityMap.Entries)
            {
                reserved.Add(entry.LogicalId);
            }

            foreach (var logicalId in modelByLogicalId.Keys)
            {
                reserved.Add(logicalId);
            }

            if (reservedIdentifiers != null)
            {
                foreach (var identifier in reservedIdentifiers)
                {
                    reserved.Add(identifier);
                }
            }

            if (handles != null)
            {
                foreach (var handle in handles.Values)
                {
                    reserved.Add(handle);
                }
            }

            // THE single place a handle-less object acquires a handle. A reparent ONTO it, a child
            // appended UNDER it and a component attached TO it can all occur in one sync, and each
            // must NAME it in the emitted source. Resolving through here guarantees they agree on one
            // name and that exactly one of them carries the Introduce flag — two names, or two
            // introductions, and the applier cannot emit the file at all.
            //
            // Returns the object's EFFECTIVE LogicalId as well as its receiver: they are the same
            // string, because a statement's `var` name IS its LogicalId (LogicalIdResolver.Resolve
            // gives the handle name top priority). Callers that need the post-rewrite id use this.
            //
            // SIDE-EFFECTING: the first call for a handle-less object registers the introduction and
            // re-keys the sidecar. Call it only when about to emit an edit that depends on it.
            // THE single re-key path. A LogicalId is DERIVED FROM THE SOURCE — it is the statement's
            // `var` name, or the synthesized "{parent}/{name}/{index}" of a handle-less statement — so
            // any rewrite that gives an object a handle, or moves it under a different parent, changes
            // its id. Leaving the sidecar on the old id strands the GlobalObjectId, and the next sync
            // reads the scene object as unmapped and CREATES IT AGAIN. Components come along because
            // their ids embed their owner's.
            void Rekey(string oldId, string newId, string? newParentLogicalId)
            {
                if (oldId == newId)
                {
                    return;
                }

                // CHAIN-AWARE. Two rekeys can hit one object in a single sync: a reparent moves it
                // (re-keying its id to the new parent's), and then the duplicate-name pass injects an
                // `.Id(...)` on it. Emitting both as independent add/remove pairs leaves the
                // INTERMEDIATE id in the sidecar forever: UpdateSidecar subtracts RemovedLogicalIds
                // from the ON-DISK entries only and then concats AddedEntries wholesale, so removing
                // an id that this same batch added is a silent no-op. Rewrite the pending entry
                // instead — the sidecar only ever sees the final id.
                var pending = addedEntries.FindIndex(e => e.LogicalId == oldId);
                if (pending >= 0)
                {
                    for (var i = 0; i < addedEntries.Count; i++)
                    {
                        var entry = addedEntries[i];
                        if (entry.LogicalId == oldId)
                        {
                            addedEntries[i] = entry with { LogicalId = newId, ParentLogicalId = newParentLogicalId };
                        }
                        else if (entry.Kind == "Component"
                            && entry.ParentLogicalId == oldId
                            && entry.LogicalId.StartsWith(oldId + "/", StringComparison.Ordinal))
                        {
                            addedEntries[i] = entry with
                            {
                                LogicalId = newId + entry.LogicalId.Substring(oldId.Length),
                                ParentLogicalId = newId,
                            };
                        }
                    }

                    return;
                }

                removedLogicalIds.Add(oldId);
                var source = identityMap.Entries.FirstOrDefault(e => e.LogicalId == oldId
                    && (e.Kind == "GameObject" || e.Kind == "PrefabInstance"));
                addedEntries.Add((source ?? new IdentityMapEntry { LogicalId = oldId, Kind = "GameObject" })
                    with
                    {
                        LogicalId = newId,
                        GlobalObjectId = logicalIdToGlobalObjectId.GetValueOrDefault(oldId, string.Empty),
                        ParentLogicalId = newParentLogicalId,
                    });

                foreach (var component in identityMap.Entries)
                {
                    if (component.Kind != "Component"
                        || component.ParentLogicalId != oldId
                        || !component.LogicalId.StartsWith(oldId + "/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    removedLogicalIds.Add(component.LogicalId);
                    addedEntries.Add(component with
                    {
                        LogicalId = newId + component.LogicalId.Substring(oldId.Length),
                        ParentLogicalId = newId,
                    });
                }
            }

            // Whether an object's statement authors an explicit `var` (so its LogicalId IS that name
            // and no rewrite of its parent can change it).
            bool HasAuthoredHandle(string logicalId)
            {
                if (handles != null)
                {
                    return handles.ContainsKey(logicalId);
                }

                var grandparentId = logicalIdToParentLogicalId.GetValueOrDefault(logicalId);
                return !LogicalIdResolver.TryParseSynthesized(logicalId, grandparentId, out _, out _);
            }

            (string? Handle, bool Introduce) ResolveOwnerHandle(string? logicalId)
            {
                if (logicalId == null)
                {
                    return (null, false); // the scene root: the receiver is the Build parameter
                }

                if (HasAuthoredHandle(logicalId))
                {
                    // Its `var` name IS its LogicalId, so for a `handles` table the two agree, and for
                    // the no-table fallback the id itself is the receiver.
                    return (handles != null ? handles[logicalId] : logicalId, false);
                }

                if (introducedHandleByParent.TryGetValue(logicalId, out var already))
                {
                    return (already, false);
                }

                var objectName = modelByLogicalId.TryGetValue(logicalId, out var model) ? model.Name : logicalId;
                var derived = HandleNaming.Derive(objectName, reserved);
                reserved.Add(derived);
                introducedHandleByParent[logicalId] = derived;

                // The rewrite gives the object a `var`, which BuilderParser will read as its LogicalId.
                Rekey(logicalId, derived, logicalIdToParentLogicalId.GetValueOrDefault(logicalId));

                return (derived, true);
            }

            foreach (var op in changeSet.Ops)
            {
                if (!logicalIdToGlobalObjectId.TryGetValue(op.LogicalId, out var goid)
                    || !snapshotByGoid.TryGetValue(goid, out var entry))
                {
                    // Unmapped or missing-from-snapshot targets are create/delete-in-scene
                    // artifacts (b2-t4 handles the conflict); no sync-back edit here.
                    continue;
                }

                if (suppressed.Contains(op.LogicalId))
                {
                    // Already recorded as an AmbiguousAnchor conflict; suppress every edit for it.
                    continue;
                }

                if (anchors != null && !anchors.ContainsKey(op.LogicalId))
                {
                    if (reportedMissingAnchor.Add(op.LogicalId))
                    {
                        conflicts.Add(ConflictDetector.MissingAnchor(op.LogicalId, goid));
                    }

                    continue;
                }

                switch (op)
                {
                    case SetName:
                        edits.Add(new PatchArgument
                        {
                            Anchor = op.LogicalId,
                            ArgName = "name",
                            NewExpr = Quote(entry.Node.Name),
                        });
                        break;

                    case SetTransform:
                        if (modelByLogicalId.TryGetValue(op.LogicalId, out var modelNode))
                        {
                            var masked = MaskDriven(modelNode.Transform, entry.Node.Transform);
                            edits.AddRange(TransformEdits(op.LogicalId, modelNode.Transform, masked));
                        }

                        break;

                    case Reparent:
                        {
                            string? newParentAnchor = entry.ParentGlobalObjectId != null
                                && globalObjectIdToLogicalId.TryGetValue(entry.ParentGlobalObjectId, out var parentLogicalId)
                                ? parentLogicalId
                                : null;

                            // A reparent must NAME the new parent, so it needs a handle on it exactly
                            // as a child append does — a handle-less new parent used to be a hard
                            // "reparent is not expressible" failure.
                            var (newParentHandle, introduceNewParentHandle) = ResolveOwnerHandle(newParentAnchor);

                            edits.Add(new MoveStatement
                            {
                                Anchor = op.LogicalId,
                                NewParentAnchor = newParentAnchor,
                                NewParentHandle = newParentHandle,
                                IntroduceNewParentHandle = introduceNewParentHandle,
                                NewSiblingIndex = entry.SiblingIndex,
                            });

                            // A handle-less node's id embeds its PARENT's, so the move changes it.
                            // Without the re-key the sidecar keeps the OLD id, its GlobalObjectId is
                            // stranded, and the very next sync reads the moved object as unmapped and
                            // appends it a SECOND time.
                            if (!HasAuthoredHandle(op.LogicalId))
                            {
                                Rekey(
                                    op.LogicalId,
                                    LogicalIdResolver.Synthesize(newParentHandle, entry.Node.Name, entry.SiblingIndex),
                                    newParentHandle);
                            }
                        }

                        break;

                    case Reorder:
                        edits.Add(new ReorderStatement { Anchor = op.LogicalId, NewSiblingIndex = entry.SiblingIndex });
                        break;

                    case SetTag:
                        if (flagPresence != null)
                        {
                            var presence = flagPresence.TryGetValue(op.LogicalId, out var p) ? p : default;
                            var flagEdit = ArgumentFlagEdit(
                                op.LogicalId,
                                FlagKind.Tag,
                                presence.HasTag,
                                entry.Node.Tag == "Untagged",
                                SourceExpr.StringLiteral(entry.Node.Tag));
                            if (flagEdit != null)
                            {
                                edits.Add(flagEdit);
                            }
                        }

                        break;

                    case SetLayer:
                        if (flagPresence != null)
                        {
                            var presence = flagPresence.TryGetValue(op.LogicalId, out var p) ? p : default;
                            var flagEdit = ArgumentFlagEdit(
                                op.LogicalId,
                                FlagKind.Layer,
                                presence.HasLayer,
                                entry.Node.Layer == 0,
                                SourceExpr.IntLiteral(entry.Node.Layer));
                            if (flagEdit != null)
                            {
                                edits.Add(flagEdit);
                            }
                        }

                        break;

                    case SetActive:
                        if (flagPresence != null)
                        {
                            var presence = flagPresence.TryGetValue(op.LogicalId, out var p) ? p : default;
                            var flagEdit = ArgumentFlagEdit(
                                op.LogicalId,
                                FlagKind.Active,
                                presence.HasActive,
                                entry.Node.Active,
                                entry.Node.Active ? "true" : "false");
                            if (flagEdit != null)
                            {
                                edits.Add(flagEdit);
                            }
                        }

                        break;

                    case SetStatic:
                        if (flagPresence != null)
                        {
                            var presence = flagPresence.TryGetValue(op.LogicalId, out var p) ? p : default;
                            if (entry.Node.IsStatic && !presence.HasStatic)
                            {
                                edits.Add(new IntroduceFlagCall { Anchor = op.LogicalId, Flag = FlagKind.Static, ArgExpr = null });
                            }
                            else if (!entry.Node.IsStatic && presence.HasStatic)
                            {
                                edits.Add(new RemoveFlagCall { Anchor = op.LogicalId, Flag = FlagKind.Static });
                            }
                        }

                        break;

                    default:
                        // AddNode/RemoveNode are out of M2 sync-back scope; no SourceEdit is
                        // emitted for them.
                        break;
                }
            }

            // b4-t2: two scene-derived membership sets for ObjectRef dangling-reference detection,
            // built ONCE and threaded into every ReconcileComponents call below.
            //   sceneLiveTargets: "object exists in the current scene" — the SAME liveness predicate
            //     DetectRemovals uses (a mapped entry whose GlobalObjectId is still in the snapshot).
            //     A deleted target is still authored in the model AND in `handles`, so membership in
            //     either of those cannot signal liveness.
            //   resolvableTargets: "reference can be honored to a real object" — sceneLiveTargets PLUS
            //     the parsed source model and the handle table, so a rewire to an object this sync
            //     hasn't mapped yet (but is genuinely authored) is not mistaken for dangling.
            var sceneLiveTargets = new HashSet<string>(
                identityMap.Entries
                    .Where(e => e.Kind == "GameObject"
                        && !string.IsNullOrEmpty(e.GlobalObjectId)
                        && snapshotByGoid.ContainsKey(e.GlobalObjectId))
                    .Select(e => e.LogicalId),
                StringComparer.Ordinal);

            var resolvableTargets = new HashSet<string>(sceneLiveTargets, StringComparer.Ordinal);
            resolvableTargets.UnionWith(modelByLogicalId.Keys);
            if (handles != null)
            {
                resolvableTargets.UnionWith(handles.Keys);
            }

            // b4-t3: same-batch create-candidate identities — snapshot nodes present in the scene
            // but with no IdentityMap entry yet (about to be mapped by DetectAppends THIS pass). A
            // ref to one of these is PENDING, not dangling: it resolves on the guaranteed second
            // Sync once DetectAppends maps it. DISTINCT address space from resolvableTargets
            // (LogicalIds): an ObjectRef to an unmapped target carries the target's
            // GlobalObjectId — its only identity before DetectAppends runs (b4-t3/research.md B5
            // CONTRACT) — never a LogicalId.
            var pendingTargets = new HashSet<string>(
                snapshotByGoid.Keys.Where(goid => !globalObjectIdToLogicalId.ContainsKey(goid)),
                StringComparer.Ordinal);

            // Mapped-owner component pass (add/remove/reorder on GameObjects already present in
            // the IdentityMap). Snapshot+map-driven; independent of DetectAppends, which only
            // visits UNMAPPED nodes and `continue`s past mapped owners.
            foreach (var (ownerLogicalId, goid) in logicalIdToGlobalObjectId)
            {
                if (!snapshotByGoid.TryGetValue(goid, out var snapshotEntry))
                {
                    continue;
                }

                var sourceComponents = modelByLogicalId.TryGetValue(ownerLogicalId, out var ownerModel)
                    ? ownerModel.Components
                    : System.Array.Empty<ComponentData>();

                ComponentReconciler.ReconcileComponents(
                    ownerLogicalId,
                    sourceComponents,
                    snapshotEntry.Node.Components,
                    identityMap,
                    componentAnchors,
                    fieldArgumentSpans,
                    sceneLiveTargets,
                    resolvableTargets,
                    pendingTargets,
                    ResolveOwnerHandle,
                    edits,
                    addedEntries,
                    removedLogicalIds,
                    conflicts,
                    skippedFields,
                    addedAssets);
            }

            DetectAppends(
                actual.Roots,
                null,
                true,
                expected,
                globalObjectIdToLogicalId,
                modelByLogicalId,
                logicalIdToGlobalObjectId,
                logicalIdToParentLogicalId,
                reserved,
                resolvableTargets,
                pendingTargets,
                ResolveOwnerHandle,
                nextIndexByParentKey,
                edits,
                addedEntries,
                removedLogicalIds,
                conflicts,
                addedAssets,
                prefabPathByGuid);

            // THE write-path chokepoint for duplicate sibling names.
            //
            // Sync is the ONLY path that writes builder source, so this is the one place that can
            // guarantee the file never CONTAINS a pair of statements distinguishable only by their
            // position. It runs over the SNAPSHOT — the post-sync truth — which is why it covers every
            // way a duplicate arises with ONE rule instead of three call-site patches:
            //   * an APPEND of an object whose name already exists under that parent,
            //   * a RENAME of an existing object ONTO a sibling's name (no append involved at all),
            //   * a group the user/LLM hand-authored ambiguously (healed while the positional mapping
            //     is still trustworthy, i.e. before a reorder can scramble it).
            // A fix at any one of those call sites would leave the other two silently destroying data.
            EnsureNoAmbiguousDuplicateNames(actual.Roots);

            // b6-t1: every LogicalId targeted by an ObjectRef field ANYWHERE in the source model —
            // cross-object references live outside the structural parent/child + owner/component
            // dependency graph DetectRemovals otherwise walks, so a handle can be "still needed" by a
            // sibling's field without being a structural dependent of it. Consulted below so a
            // structural delete-cascade never strips a `var door = scene.Add(...)` declaration (or its
            // own component statements) out from under a surviving `.Set(x => x.target, door)`
            // argument — that produces non-compiling source (CS0103), never a style issue.
            var referencedByFieldTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in modelByLogicalId.Values)
            {
                foreach (var component in node.Components)
                {
                    foreach (var (_, value) in component.Fields)
                    {
                        if (value is ValueNode.ObjectRef(var targetLogicalId) && targetLogicalId != null)
                        {
                            referencedByFieldTargets.Add(targetLogicalId);
                        }
                    }
                }
            }

            DetectRemovals(identityMap, anchors, componentAnchors, snapshotByGoid, referencedByFieldTargets, edits, removedLogicalIds, conflicts);

            // Walks the scene tree; for every sibling group of >= 2 same-named objects whose statements
            // would ALL be positional, injects `.Id(...)` into every member but the first. One
            // positional member per group is enough: the claim queue LogicalIdResolver builds is keyed
            // (parent, name), so a group with a single positional statement has a single claimable id
            // and that statement claims it wherever it sits in the file. That is the whole point —
            // `.Id(...)` lives IN the statement, a sibling index is only IMPLIED BY its position.
            void EnsureNoAmbiguousDuplicateNames(SnapshotNode[] siblings)
            {
                foreach (var group in siblings.GroupBy(n => n.Name))
                {
                    var positional = group.Where(IsPositionalInSource).ToList();
                    if (positional.Count < 2)
                    {
                        continue;
                    }

                    // A group whose members are ALREADY scrambled by a pending reorder is the one case
                    // an id cannot rescue: the mapping from statement to object is exactly what the
                    // reorder destroyed, so injecting would PIN A GUESS — permanently, and silently,
                    // in the file. DetectAmbiguousReorders has already surfaced that as a conflict for
                    // the user to resolve with an explicit `.Id(...)`; healing is only sound while the
                    // positional mapping is still trustworthy.
                    if (positional.Any(m => globalObjectIdToLogicalId.TryGetValue(m.GlobalObjectId, out var id)
                        && suppressed.Contains(id)))
                    {
                        continue;
                    }

                    foreach (var member in positional.Skip(1))
                    {
                        InjectHandle(member);
                    }
                }

                foreach (var node in siblings)
                {
                    EnsureNoAmbiguousDuplicateNames(node.Children);
                }
            }

            // Whether the statement this scene object will be described by has neither a handle nor an
            // explicit `.Id(...)`. Mapped objects are judged on their PARSED statement (positional-ness
            // is a property of the statement, which a rename/reparent does not change); objects being
            // appended this batch are judged on the AppendStatement about to be emitted.
            bool IsPositionalInSource(SnapshotNode node)
            {
                if (string.IsNullOrEmpty(node.GlobalObjectId))
                {
                    return false;
                }

                if (globalObjectIdToLogicalId.TryGetValue(node.GlobalObjectId, out var originalId))
                {
                    return modelByLogicalId.TryGetValue(originalId, out var modelNode)
                        && ConflictDetector.IsPositional(modelNode, logicalIdToParentLogicalId.GetValueOrDefault(originalId));
                }

                var append = FindAppend(node.GlobalObjectId);
                return append != null && append.Handle == null;
            }

            AppendStatement? FindAppend(string globalObjectId)
            {
                var entry = addedEntries.LastOrDefault(e => e.Kind == "GameObject" && e.GlobalObjectId == globalObjectId);
                return entry == null
                    ? null
                    : edits.OfType<AppendStatement>().FirstOrDefault(a => a.NewLogicalId == entry.LogicalId);
            }

            // The mapped arm of the retired `InjectExplicitId`: the unmapped (same-batch append) arm
            // is DEAD — a duplicate append already heads its own handle via DetectAppends' third
            // `headsHandle` clause (b2-t2), so a positional append never reaches this pass.
            void InjectHandle(SnapshotNode node)
            {
                var goid = node.GlobalObjectId;
                if (!globalObjectIdToLogicalId.TryGetValue(goid, out var originalId))
                {
                    return;
                }

                // Already in the file: the anchor is the id the source was PARSED under (what
                // `anchors` is keyed by).
                if (anchors != null && !anchors.ContainsKey(originalId))
                {
                    return;
                }

                // THE one handle-introduction path — derives the name, reserves it, and Rekeys the
                // sidecar so the GlobalObjectId follows the id. Do NOT re-Rekey here.
                var (handle, introduce) = ResolveOwnerHandle(originalId);
                if (introduce && handle != null)
                {
                    edits.Add(new IntroduceHandle { Anchor = originalId, Handle = handle });
                }
            }

            return new ReconcileResult
            {
                Patch = new SourcePatch { Edits = edits.ToArray() },
                Conflicts = conflicts.ToArray(),
                AddedEntries = addedEntries.ToArray(),
                RemovedLogicalIds = removedLogicalIds.ToArray(),
                Skipped = skippedFields.ToArray(),
                AddedAssets = DedupAssetsByGuid(addedAssets),
            };
        }

        // netstandard2.1 has no DistinctBy; dedup manually, keeping first occurrence per Guid.
        private static AssetEntry[] DedupAssetsByGuid(List<AssetEntry> addedAssets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<AssetEntry>(addedAssets.Count);
            foreach (var entry in addedAssets)
            {
                if (seen.Add(entry.Guid))
                {
                    result.Add(entry);
                }
            }

            return result.ToArray();
        }

        // Symmetric to DetectAppends: walks the IdentityMap (code-side objects) looking for
        // GameObject entries whose GlobalObjectId is absent from the live snapshot - i.e.
        // scene-deleted objects. Emits one RemoveStatement + one RemovedLogicalIds entry per
        // deleted object. Independent of Differ's ops (see research.md for why RemoveNode
        // cannot be used here).
        // A deleted entry that still heads a handle referenced by a surviving child (a
        // GameObject child whose own GlobalObjectId is still present in the snapshot) cannot
        // be removed - doing so would break the surviving child's statement. Such entries
        // surface a ReferencedHandle conflict instead and are skipped entirely.
        //
        // Dependents of a removed owner are handle-bound statements: a child's `scene.Add(..., parent)`
        // and a component's `owner.Component<T>(...)` both name the owner's handle. The two kinds are
        // NOT symmetric:
        //   - a child GameObject CAN outlive its owner (reparented in the scene, so its own
        //     GlobalObjectId survives) -> the owner's handle is still referenced -> ReferencedHandle.
        //   - a component CANNOT outlive its owner: destroying a GameObject destroys its components,
        //     and components are not snapshot nodes, so a surviving component does not exist.
        //     Component statements therefore CASCADE with the owner. Leaving them behind emits
        //     `box.Component<T>(...)` with no `box` in scope - CS0103, source that will not compile.
        // Components of a SURVIVING owner are not this function's business: ReconcileComponents
        // handles those (it runs per snapshot-present owner), so the two paths never overlap.
        private static void DetectRemovals(
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? anchors,
            IReadOnlyDictionary<string, SourceSpan>? componentAnchors,
            IReadOnlyDictionary<string, SnapshotEntry> snapshotByGoid,
            // b6-t1: LogicalIds targeted by an ObjectRef field anywhere in the source model — see call
            // site. A deleted owner still named by a surviving field argument cannot be removed either;
            // ComponentReconciler's field-diff pass (Detection 1, restored for the same
            // default-filtered-out-of-snapshot case this removal would otherwise race) already reports
            // the located DanglingReference for every such field, so this only needs to leave the
            // declaration (and the removed owner's own component statements) alone — never a second
            // conflict for the same field.
            ISet<string> referencedByFieldTargets,
            List<SourceEdit> edits,
            List<string> removedLogicalIds,
            List<Conflict> conflicts)
        {
            var dependentsByOwner = identityMap.Entries
                .Where(e => (e.Kind == "GameObject" || e.Kind == "Component" || e.Kind == "PrefabInstance") && e.ParentLogicalId != null)
                .GroupBy(e => e.ParentLogicalId!)
                .ToDictionary(g => g.Key, g => g.ToArray());

            foreach (var entry in identityMap.Entries)
            {
                if ((entry.Kind != "GameObject" && entry.Kind != "PrefabInstance") || string.IsNullOrEmpty(entry.GlobalObjectId))
                {
                    continue;
                }

                if (snapshotByGoid.ContainsKey(entry.GlobalObjectId))
                {
                    continue;
                }

                var dependents = dependentsByOwner.TryGetValue(entry.LogicalId, out var found)
                    ? found
                    : Array.Empty<IdentityMapEntry>();

                // Only a GameObject or PrefabInstance dependent can survive its owner (components
                // are never snapshot nodes).
                var hasSurvivingChild = dependents.Any(d =>
                    (d.Kind == "GameObject" || d.Kind == "PrefabInstance")
                    && !string.IsNullOrEmpty(d.GlobalObjectId)
                    && snapshotByGoid.ContainsKey(d.GlobalObjectId));

                // b6-t1: a field elsewhere in the source still names this handle. The owner is gone
                // from the scene, so ComponentReconciler's field-diff pass already reports a located
                // DanglingReference for that field (Detection 1) — this only has to leave the
                // declaration standing so the still-authored argument keeps resolving, never CS0103.
                if (referencedByFieldTargets.Contains(entry.LogicalId))
                {
                    continue;
                }

                if (hasSurvivingChild)
                {
                    conflicts.Add(new Conflict
                    {
                        Kind = ConflictKind.ReferencedHandle,
                        LogicalId = entry.LogicalId,
                        GlobalObjectId = entry.GlobalObjectId,
                        Reason = $"Cannot remove '{entry.LogicalId}': it heads a handle referenced by a surviving child statement.",
                    });

                    continue;
                }

                if (anchors != null && !anchors.ContainsKey(entry.LogicalId))
                {
                    conflicts.Add(ConflictDetector.UnanchorableDelete(entry.LogicalId, entry.GlobalObjectId));
                    continue;
                }

                edits.Add(new RemoveStatement { Anchor = entry.LogicalId });
                removedLogicalIds.Add(entry.LogicalId);

                // Cascade: the owner's handle is gone, so every component statement bound to it goes too.
                foreach (var component in dependents)
                {
                    if (component.Kind != "Component")
                    {
                        continue;
                    }

                    if (componentAnchors != null && !componentAnchors.ContainsKey(component.LogicalId))
                    {
                        conflicts.Add(ConflictDetector.UnanchorableComponentEdit(component.LogicalId, "remove"));
                        continue;
                    }

                    edits.Add(new RemoveStatement { Anchor = component.LogicalId });
                    removedLogicalIds.Add(component.LogicalId);
                }
            }
        }

        // A transform argument is patched IFF its CANONICAL EMITTED FORM would actually change.
        //
        // Comparing the raw values instead is a convergence bug. A rotation is authored, and can only
        // ever be authored, as an Euler triple: the source's `rot:` literal is parsed via EulerToQuat
        // (Core's double math, cast to float) while the scene's quaternion comes from Unity's own float
        // math, so the two quats differ in their last bits for the SAME rotation. Exact `!=` therefore
        // fired on every single sync, forever, emitting a `rot:` patch whose text — Float() rounds to
        // 4dp — was byte-identical to what was already there. It stayed invisible only because
        // EditsApplied counts applied text changes, so a patch that rewrites a line to itself scores
        // zero. That is a latent perpetual rewrite, and with the file watcher driving code->scene it is
        // a feedback loop; it is also exactly the defect the asset-ref path had.
        //
        // The canonical literal IS the authored representation here: if it does not change, the source
        // cannot change, so there is nothing to patch. Comparing it keeps the project's stated rule —
        // equality is exact on CANONICAL FORM, no float tolerance — while making the reconcile unable
        // to emit an edit that provably rewrites text to itself.
        private static IEnumerable<SourceEdit> TransformEdits(string logicalId, TransformData model, TransformData snapshot)
        {
            var modelPos = SourceExpr.Vec3Literal(model.Position);
            var snapshotPos = SourceExpr.Vec3Literal(snapshot.Position);
            if (!string.Equals(modelPos, snapshotPos, StringComparison.Ordinal))
            {
                yield return new PatchArgument { Anchor = logicalId, ArgName = "pos", NewExpr = snapshotPos };
            }

            var modelRot = SourceExpr.Vec3Literal(Rotation.QuatToEuler(model.Rotation));
            var snapshotRot = SourceExpr.Vec3Literal(Rotation.QuatToEuler(snapshot.Rotation));
            if (!string.Equals(modelRot, snapshotRot, StringComparison.Ordinal))
            {
                yield return new PatchArgument { Anchor = logicalId, ArgName = "rot", NewExpr = snapshotRot };
            }

            var modelScale = SourceExpr.Vec3Literal(model.Scale);
            var snapshotScale = SourceExpr.Vec3Literal(snapshot.Scale);
            if (!string.Equals(modelScale, snapshotScale, StringComparison.Ordinal))
            {
                yield return new PatchArgument { Anchor = logicalId, ArgName = "scale", NewExpr = snapshotScale };
            }
        }

        // b4-t2: driven axes never sync scene->source: hold the source model's value on each driven
        // axis so it cannot differ, while free axes still reflect the scene. snapshot.DrivenChannels
        // is enabled-coupled (b6); default None returns the snapshot unchanged for every non-spatial
        // node.
        private static TransformData MaskDriven(TransformData model, TransformData snapshot)
        {
            var d = snapshot.DrivenChannels;
            if (d == ChannelMask.None)
            {
                return snapshot;
            }

            var pos = new Vec3(
                (d & ChannelMask.PositionX) != 0 ? model.Position.X : snapshot.Position.X,
                (d & ChannelMask.PositionY) != 0 ? model.Position.Y : snapshot.Position.Y,
                (d & ChannelMask.PositionZ) != 0 ? model.Position.Z : snapshot.Position.Z);
            var scale = new Vec3(
                (d & ChannelMask.ScaleX) != 0 ? model.Scale.X : snapshot.Scale.X,
                (d & ChannelMask.ScaleY) != 0 ? model.Scale.Y : snapshot.Scale.Y,
                (d & ChannelMask.ScaleZ) != 0 ? model.Scale.Z : snapshot.Scale.Z);
            return snapshot with { Position = pos, Scale = scale };
        }

        private static void FlattenSnapshot(SnapshotNode[] nodes, string? parentGoid, Dictionary<string, SnapshotEntry> snapshotByGoid)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (!string.IsNullOrEmpty(node.GlobalObjectId))
                {
                    snapshotByGoid[node.GlobalObjectId] = new SnapshotEntry(node, parentGoid, i);
                }

                FlattenSnapshot(node.Children, node.GlobalObjectId, snapshotByGoid);
            }
        }

        private static void FlattenModel(GameObjectNode[] nodes, Dictionary<string, GameObjectNode> modelByLogicalId)
        {
            foreach (var node in nodes)
            {
                modelByLogicalId[node.LogicalId] = node;
                FlattenModel(node.Children, modelByLogicalId);
            }
        }

        private static string Quote(string value) => "\"" + value + "\"";

        // Shared decision table for the three argument-carrying flags (Tag/Layer/Active).
        // `present` = the flag call physically appears in the anchored statement (from
        // FlagPresence); `isDefault` = the SNAPSHOT (scene) value equals the flag's type
        // default. Static has no argument, so it is handled separately in the switch above.
        private static SourceEdit? ArgumentFlagEdit(string anchor, FlagKind flag, bool present, bool isDefault, string literal)
        {
            if (present)
            {
                return isDefault
                    ? new RemoveFlagCall { Anchor = anchor, Flag = flag }
                    : new PatchFlagArgument { Anchor = anchor, Flag = flag, NewExpr = literal };
            }

            return isDefault ? null : new IntroduceFlagCall { Anchor = anchor, Flag = flag, ArgExpr = literal };
        }
    }
}
