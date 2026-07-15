using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    public static class Reconciler
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
                .Where(e => e.Kind == "GameObject" && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

            var globalObjectIdToLogicalId = logicalIdToGlobalObjectId
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

            var logicalIdToParentLogicalId = identityMap.Entries
                .Where(e => e.Kind == "GameObject")
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
                addedEntries.Add(new IdentityMapEntry
                {
                    LogicalId = newId,
                    GlobalObjectId = logicalIdToGlobalObjectId.GetValueOrDefault(oldId, string.Empty),
                    Kind = "GameObject",
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
                            edits.AddRange(TransformEdits(op.LogicalId, modelNode.Transform, entry.Node.Transform));
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
                ResolveOwnerHandle,
                nextIndexByParentKey,
                edits,
                addedEntries,
                removedLogicalIds,
                conflicts,
                addedAssets);

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

            DetectRemovals(identityMap, anchors, componentAnchors, snapshotByGoid, edits, removedLogicalIds, conflicts);

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
                        InjectExplicitId(member);
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
                return append != null && append.Handle == null && append.ExplicitId == null;
            }

            AppendStatement? FindAppend(string globalObjectId)
            {
                var entry = addedEntries.LastOrDefault(e => e.Kind == "GameObject" && e.GlobalObjectId == globalObjectId);
                return entry == null
                    ? null
                    : edits.OfType<AppendStatement>().FirstOrDefault(a => a.NewLogicalId == entry.LogicalId);
            }

            void InjectExplicitId(SnapshotNode node)
            {
                var goid = node.GlobalObjectId;

                // The object's id RIGHT NOW in this batch: an append's predicted id, or a mapped id
                // possibly already re-keyed by a reparent earlier in this same reconcile.
                var pending = addedEntries.LastOrDefault(e => e.Kind == "GameObject" && e.GlobalObjectId == goid);
                var mapped = globalObjectIdToLogicalId.TryGetValue(goid, out var originalId);
                var currentId = pending?.LogicalId ?? originalId;
                if (currentId == null)
                {
                    return;
                }

                var newId = MintId(node.Name);

                if (!mapped)
                {
                    // Appended this batch: rewrite the pending AppendStatement so the statement is
                    // emitted WITH its `.Id(...)` — the file never exists in an ambiguous state, not
                    // even for one sync. A positional append heads no handle, which (see DetectAppends'
                    // `headsHandle`) means it has no representable components and no create-candidate
                    // children, so nothing else embeds its id.
                    var append = edits.OfType<AppendStatement>().FirstOrDefault(a => a.NewLogicalId == currentId);
                    if (append == null)
                    {
                        return;
                    }

                    edits[edits.IndexOf(append)] = append with { NewLogicalId = newId, ExplicitId = newId };
                    Rekey(currentId, newId, append.ParentHandle);
                    return;
                }

                // Already in the file: patch its statement in place. The anchor is the id the source
                // was PARSED under (what `anchors` is keyed by), which is not necessarily `currentId`
                // once a reparent has re-keyed it this batch.
                if (anchors != null && !anchors.ContainsKey(originalId!))
                {
                    return;
                }

                edits.Add(new IntroduceIdCall { Anchor = originalId!, NewId = newId });
                Rekey(currentId, newId, logicalIdToParentLogicalId.GetValueOrDefault(currentId));
            }

            // Deterministic and SEMANTIC: `Enemy-2`, `Enemy-3`, ... — derived from the object's own
            // name, never a random GUID. The builder file gets rewritten by an LLM (CLAUDE.md); an
            // opaque GUID does not survive that, whereas a name-derived id reads as meaningful and is
            // reproduced verbatim. `reserved` already holds every known LogicalId and handle, so the
            // first free suffix is collision-checked against the whole file.
            string MintId(string name)
            {
                for (var n = 2; ; n++)
                {
                    var candidate = name + "-" + n.ToString(CultureInfo.InvariantCulture);
                    if (reserved.Add(candidate))
                    {
                        return candidate;
                    }
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

        // Snapshot-driven create-detection: walks the ACTUAL (scene) tree depth-first looking
        // for GameObjectIds absent from the IdentityMap. Emits an AppendStatement only for a
        // create candidate whose parent is a root or an already-MAPPED parent. A create candidate
        // with >=1 create-candidate child heads its own handle (b2-t3) so its descendants can be
        // appended referencing it; a create candidate with no create-candidate children is a leaf
        // (Handle stays null, recursion into its children is a no-op guard).
        private static void DetectAppends(
            SnapshotNode[] nodes,
            string? parentLogicalId,
            bool parentIsMapped,
            SceneModel expected,
            IReadOnlyDictionary<string, string> goidToLogicalId,
            IReadOnlyDictionary<string, GameObjectNode> modelByLogicalId,
            IReadOnlyDictionary<string, string> logicalIdToGlobalObjectId,
            IReadOnlyDictionary<string, string?> logicalIdToParentLogicalId,
            HashSet<string> reserved,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            Dictionary<string, int> nextIndexByParentKey,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<string> removedLogicalIds,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets)
        {
            // The array position IS the scene sibling index (FlattenSnapshot keys SnapshotEntry off the
            // same index), and it is what a created node's statement must be placed at.
            for (var siblingIndex = 0; siblingIndex < nodes.Length; siblingIndex++)
            {
                var node = nodes[siblingIndex];
                string? mappedLogicalId = null;
                var isMapped = !string.IsNullOrEmpty(node.GlobalObjectId)
                    && goidToLogicalId.TryGetValue(node.GlobalObjectId, out mappedLogicalId);

                if (isMapped)
                {
                    DetectAppends(
                        node.Children,
                        mappedLogicalId,
                        true,
                        expected,
                        goidToLogicalId,
                        modelByLogicalId,
                        logicalIdToGlobalObjectId,
                        logicalIdToParentLogicalId,
                        reserved,
                        resolveOwnerHandle,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts,
                        addedAssets);

                    continue;
                }

                if (parentIsMapped)
                {
                    var parentKey = parentLogicalId ?? string.Empty;
                    if (!nextIndexByParentKey.TryGetValue(parentKey, out var index))
                    {
                        index = parentLogicalId == null
                            ? expected.Roots.Length
                            : modelByLogicalId.TryGetValue(parentLogicalId, out var parentModel)
                                ? parentModel.Children.Length
                                : 0;
                    }

                    nextIndexByParentKey[parentKey] = index + 1;

                    // The parent's receiver, via the ONE handle-introduction path (shared with
                    // reparent and component-attach emission, so one parent never gets two handles).
                    // `parentHandle` doubles as the parent's EFFECTIVE LogicalId: a statement's `var`
                    // name IS its LogicalId, so for a handled parent the two are already the same
                    // string, and for a handle-less one the introduction makes them so.
                    var (parentHandle, introduceParentHandle) = resolveOwnerHandle(parentLogicalId);

                    // b4-t1 §13: representable (non-Transform) components force the owner to head
                    // a handle too, so the same-batch component append (ComponentPatchApplier) has
                    // an `OwnerHandle` to attach onto.
                    var representableComponents = ComponentReconciler.ExcludeTransform(node.Components);

                    // b2-t3: a node with >=1 create-candidate (unmapped, non-empty-goid) child
                    // heads its own handle so its descendants can reference it - otherwise they
                    // would be stranded (the old dead-end recursion below).
                    var headsHandle = node.Children.Any(c =>
                        !string.IsNullOrEmpty(c.GlobalObjectId) && !goidToLogicalId.ContainsKey(c.GlobalObjectId))
                        || representableComponents.Length > 0;

                    string newLogicalId;
                    string? ownHandle = null;

                    if (headsHandle)
                    {
                        ownHandle = HandleNaming.Derive(node.Name, reserved);
                        reserved.Add(ownHandle);
                        newLogicalId = ownHandle;
                    }
                    else
                    {
                        newLogicalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
                    }

                    edits.Add(new AppendStatement
                    {
                        NewLogicalId = newLogicalId,
                        ParentAnchor = parentLogicalId,
                        NewSiblingIndex = siblingIndex,
                        Name = node.Name,
                        Transform = node.Transform != new TransformData() ? node.Transform : null,
                        Active = node.Active != true ? node.Active : null,
                        Tag = node.Tag != "Untagged" ? node.Tag : null,
                        Layer = node.Layer != 0 ? node.Layer : null,
                        IsStatic = node.IsStatic != false ? node.IsStatic : null,
                        Handle = ownHandle,
                        ParentHandle = parentHandle,
                        IntroduceParentHandle = introduceParentHandle,
                    });

                    addedEntries.Add(new IdentityMapEntry
                    {
                        LogicalId = newLogicalId,
                        GlobalObjectId = node.GlobalObjectId,
                        Kind = "GameObject",
                        ParentLogicalId = parentHandle,
                    });

                    // §13 one-pass attach: the owner is mapped IN-MEMORY by the AddedEntry just
                    // above, so its components attach in this same pass instead of waiting for a
                    // 2nd Sync. Reuses ComponentReconciler's ADD-emission shape (same
                    // `{owner}/{Type}#{ordinal}` id, same AppendComponentStatement + Component
                    // AddedEntry) rather than reinventing it here.
                    if (representableComponents.Length > 0)
                    {
                        var keys = ComponentReconciler.ComputeComponentKeys(representableComponents);
                        for (var i = 0; i < representableComponents.Length; i++)
                        {
                            ComponentReconciler.EmitComponentAppend(
                                newLogicalId,
                                newLogicalId,
                                keys[i].TypeFullName,
                                keys[i].Ordinal,
                                i,
                                representableComponents[i].Fields,
                                ownHandle,
                                false,
                                edits,
                                addedEntries,
                                addedAssets);
                        }
                    }

                    DetectAppends(
                        node.Children,
                        headsHandle ? ownHandle : null,
                        headsHandle,
                        expected,
                        goidToLogicalId,
                        modelByLogicalId,
                        logicalIdToGlobalObjectId,
                        logicalIdToParentLogicalId,
                        reserved,
                        resolveOwnerHandle,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts,
                        addedAssets);

                    continue;
                }

                DetectAppends(
                    node.Children,
                    null,
                    false,
                    expected,
                    goidToLogicalId,
                    modelByLogicalId,
                    logicalIdToGlobalObjectId,
                    logicalIdToParentLogicalId,
                    reserved,
                    resolveOwnerHandle,
                    nextIndexByParentKey,
                    edits,
                    addedEntries,
                    removedLogicalIds,
                    conflicts,
                    addedAssets);
            }
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
            List<SourceEdit> edits,
            List<string> removedLogicalIds,
            List<Conflict> conflicts)
        {
            var dependentsByOwner = identityMap.Entries
                .Where(e => (e.Kind == "GameObject" || e.Kind == "Component") && e.ParentLogicalId != null)
                .GroupBy(e => e.ParentLogicalId!)
                .ToDictionary(g => g.Key, g => g.ToArray());

            foreach (var entry in identityMap.Entries)
            {
                if (entry.Kind != "GameObject" || string.IsNullOrEmpty(entry.GlobalObjectId))
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

                // Only a GameObject dependent can survive its owner (components are never snapshot nodes).
                var hasSurvivingChild = dependents.Any(d =>
                    d.Kind == "GameObject"
                    && !string.IsNullOrEmpty(d.GlobalObjectId)
                    && snapshotByGoid.ContainsKey(d.GlobalObjectId));

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
