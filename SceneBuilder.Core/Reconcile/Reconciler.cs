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
            IReadOnlyDictionary<string, FlagPresence>? flagPresence = null)
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

                            edits.Add(new MoveStatement { Anchor = op.LogicalId, NewParentAnchor = newParentAnchor });
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

            var addedEntries = new List<IdentityMapEntry>();
            var removedLogicalIds = new List<string>();
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
                introducedHandleByParent,
                nextIndexByParentKey,
                edits,
                addedEntries,
                removedLogicalIds,
                conflicts);

            DetectRemovals(identityMap, anchors, snapshotByGoid, edits, removedLogicalIds, conflicts);

            return new ReconcileResult
            {
                Patch = new SourcePatch { Edits = edits.ToArray() },
                Conflicts = conflicts.ToArray(),
                AddedEntries = addedEntries.ToArray(),
                RemovedLogicalIds = removedLogicalIds.ToArray(),
            };
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
            Dictionary<string, string> introducedHandleByParent,
            Dictionary<string, int> nextIndexByParentKey,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<string> removedLogicalIds,
            List<Conflict> conflicts)
        {
            foreach (var node in nodes)
            {
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
                        introducedHandleByParent,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts);

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

                    var grandparentLogicalId = parentLogicalId != null
                        ? logicalIdToParentLogicalId.GetValueOrDefault(parentLogicalId)
                        : null;
                    var handleless = parentLogicalId != null
                        && LogicalIdResolver.TryParseSynthesized(parentLogicalId, grandparentLogicalId, out _, out _);

                    string? parentHandle;
                    var introduceParentHandle = false;

                    if (handleless)
                    {
                        if (!introducedHandleByParent.TryGetValue(parentLogicalId!, out var handle))
                        {
                            var parentName = modelByLogicalId.TryGetValue(parentLogicalId!, out var parentModelNode)
                                ? parentModelNode.Name
                                : parentLogicalId!;
                            handle = HandleNaming.Derive(parentName, reserved);
                            reserved.Add(handle);
                            introducedHandleByParent[parentLogicalId!] = handle;
                            removedLogicalIds.Add(parentLogicalId!);

                            addedEntries.Add(new IdentityMapEntry
                            {
                                LogicalId = handle,
                                GlobalObjectId = logicalIdToGlobalObjectId.GetValueOrDefault(parentLogicalId!, string.Empty),
                                Kind = "GameObject",
                                ParentLogicalId = grandparentLogicalId,
                            });
                        }

                        parentHandle = handle;
                        introduceParentHandle = true;
                    }
                    else
                    {
                        parentHandle = parentLogicalId;
                    }

                    // b2-t3: a node with >=1 create-candidate (unmapped, non-empty-goid) child
                    // heads its own handle so its descendants can reference it - otherwise they
                    // would be stranded (the old dead-end recursion below).
                    var headsHandle = node.Children.Any(c =>
                        !string.IsNullOrEmpty(c.GlobalObjectId) && !goidToLogicalId.ContainsKey(c.GlobalObjectId));

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
                        newLogicalId = LogicalIdResolver.Synthesize(handleless ? parentHandle : parentLogicalId, node.Name, index);
                    }

                    edits.Add(new AppendStatement
                    {
                        NewLogicalId = newLogicalId,
                        ParentAnchor = parentLogicalId,
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

                    if (node.Components.Length > 0)
                    {
                        conflicts.Add(new Conflict
                        {
                            Kind = ConflictKind.UnrepresentedComponents,
                            LogicalId = newLogicalId,
                            GlobalObjectId = node.GlobalObjectId,
                            Reason = $"Created object '{newLogicalId}' carries {node.Components.Length} component(s) not represented in sync-back (components are M3, out of scope); appended as GameObject + transform + flags only.",
                        });
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
                        introducedHandleByParent,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts);

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
                    introducedHandleByParent,
                    nextIndexByParentKey,
                    edits,
                    addedEntries,
                    removedLogicalIds,
                    conflicts);
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
        private static void DetectRemovals(
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? anchors,
            IReadOnlyDictionary<string, SnapshotEntry> snapshotByGoid,
            List<SourceEdit> edits,
            List<string> removedLogicalIds,
            List<Conflict> conflicts)
        {
            var childrenByParent = identityMap.Entries
                .Where(e => e.Kind == "GameObject" && e.ParentLogicalId != null)
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

                var hasSurvivingChild = childrenByParent.TryGetValue(entry.LogicalId, out var children)
                    && children.Any(c => !string.IsNullOrEmpty(c.GlobalObjectId) && snapshotByGoid.ContainsKey(c.GlobalObjectId));

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
            }
        }

        private static IEnumerable<SourceEdit> TransformEdits(string logicalId, TransformData model, TransformData snapshot)
        {
            if (model.Position != snapshot.Position)
            {
                yield return new PatchArgument { Anchor = logicalId, ArgName = "pos", NewExpr = SourceExpr.Vec3Literal(snapshot.Position) };
            }

            if (model.Rotation != snapshot.Rotation)
            {
                yield return new PatchArgument
                {
                    Anchor = logicalId,
                    ArgName = "rot",
                    NewExpr = SourceExpr.Vec3Literal(Rotation.QuatToEuler(snapshot.Rotation)),
                };
            }

            if (model.Scale != snapshot.Scale)
            {
                yield return new PatchArgument { Anchor = logicalId, ArgName = "scale", NewExpr = SourceExpr.Vec3Literal(snapshot.Scale) };
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
