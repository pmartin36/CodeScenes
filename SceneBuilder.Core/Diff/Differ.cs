using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;

namespace SceneBuilder.Core.Diff
{
    public static class Differ
    {
        private sealed record SnapshotEntry(SnapshotNode Node, string? ParentGlobalObjectId, int SiblingIndex);

        public static ChangeSet Diff(SceneModel desired, SceneSnapshot actual, IdentityMap identityMap)
        {
            var logicalIdToGlobalObjectId = identityMap.Entries
                .Where(e => (e.Kind == "GameObject" || e.Kind == "PrefabInstance") && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

            var globalObjectIdToLogicalId = logicalIdToGlobalObjectId
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

            var snapshotByGoid = new Dictionary<string, SnapshotEntry>();
            FlattenSnapshot(actual.Roots, null, snapshotByGoid);

            var visitedGoids = new HashSet<string>();
            var ops = new List<ChangeOp>();
            var diagnostics = new List<Diagnostic>();

            WalkDesired(desired.Roots, null, logicalIdToGlobalObjectId, snapshotByGoid, visitedGoids, identityMap, ops, diagnostics);

            foreach (var kv in snapshotByGoid)
            {
                if (visitedGoids.Contains(kv.Key))
                {
                    continue;
                }

                if (!identityMap.IsManaged(kv.Key))
                {
                    continue;
                }

                var logicalId = globalObjectIdToLogicalId.TryGetValue(kv.Key, out var lid) ? lid : "";
                ops.Add(new RemoveNode { LogicalId = logicalId });
            }

            return new ChangeSet { Ops = ops.ToArray(), Diagnostics = diagnostics };
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

        private static void WalkDesired(
            GameObjectNode[] nodes,
            string? parentLogicalId,
            Dictionary<string, string> logicalIdToGlobalObjectId,
            Dictionary<string, SnapshotEntry> snapshotByGoid,
            HashSet<string> visitedGoids,
            IdentityMap identityMap,
            List<ChangeOp> ops,
            List<Diagnostic> diagnostics)
        {
            // b6-t1: siblingIndex counts only nodes MATCHED to a live snapshot entry, not raw source
            // position. A desired node absent from the live snapshot (EmitCreate below — either
            // genuinely new, or scene-deleted but kept in source because a cross-object field still
            // references its handle, see Reconciler.DetectRemovals) occupies no slot in the live
            // scene's sibling ordering, so it must not shift a MATCHED sibling's expected index — that
            // produced a spurious Reorder on every matched sibling following a kept dangling entry.
            var matchedIndex = 0;
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (logicalIdToGlobalObjectId.TryGetValue(node.LogicalId, out var goid)
                    && snapshotByGoid.TryGetValue(goid, out var entry))
                {
                    visitedGoids.Add(goid);
                    EmitEdits(node, parentLogicalId, matchedIndex, logicalIdToGlobalObjectId, entry, identityMap, ops);

                    // b3-t2: OpaqueOverrides is READ-ONLY (M10) — never diffed into an op, only
                    // flagged. Diffing it would emit a spurious SetField and corrupt the preserved
                    // token. The live snapshot is the authoritative source of overrides (desired
                    // parsed from code carries none).
                    if (entry.Node.OpaqueOverrides is { RawToken: var rawToken } && !string.IsNullOrEmpty(rawToken))
                    {
                        diagnostics.Add(new Diagnostic
                        {
                            Code = DiagnosticCodes.PrefabOverridesNotModelled,
                            Severity = DiagnosticSeverity.Info,
                            Message = $"Prefab instance '{node.LogicalId}' has property overrides preserved but not modelled in code (M10).",
                        });
                    }

                    matchedIndex++;
                }
                else if (node is PrefabInstanceNode instanceNode)
                {
                    EmitCreateInstance(instanceNode, parentLogicalId, i, ops);
                }
                else
                {
                    EmitCreate(node, parentLogicalId, identityMap, ops);
                }

                WalkDesired(node.Children, node.LogicalId, logicalIdToGlobalObjectId, snapshotByGoid, visitedGoids, identityMap, ops, diagnostics);
            }
        }

        private static void EmitEdits(
            GameObjectNode node,
            string? parentLogicalId,
            int siblingIndex,
            Dictionary<string, string> logicalIdToGlobalObjectId,
            SnapshotEntry entry,
            IdentityMap identityMap,
            List<ChangeOp> ops)
        {
            var snapshot = entry.Node;

            if (node.Name != snapshot.Name)
            {
                ops.Add(new SetName { LogicalId = node.LogicalId, Name = node.Name });
            }

            if (node.Tag != snapshot.Tag)
            {
                ops.Add(new SetTag { LogicalId = node.LogicalId, Tag = node.Tag });
            }

            if (node.Layer != snapshot.Layer)
            {
                ops.Add(new SetLayer { LogicalId = node.LogicalId, Layer = node.Layer });
            }

            if (node.Active != snapshot.Active)
            {
                ops.Add(new SetActive { LogicalId = node.LogicalId, Active = node.Active });
            }

            if (node.IsStatic != snapshot.IsStatic)
            {
                ops.Add(new SetStatic { LogicalId = node.LogicalId, IsStatic = node.IsStatic });
            }

            EmitTransformEdit(node, snapshot, ops);

            string? desiredParentGoid = parentLogicalId != null && logicalIdToGlobalObjectId.TryGetValue(parentLogicalId, out var pGoid)
                ? pGoid
                : null;

            if (desiredParentGoid != entry.ParentGlobalObjectId)
            {
                ops.Add(new Reparent { LogicalId = node.LogicalId, NewParentLogicalId = parentLogicalId });
            }

            if (siblingIndex != entry.SiblingIndex)
            {
                ops.Add(new Reorder { LogicalId = node.LogicalId, SiblingIndex = siblingIndex });
            }

            EmitComponentEdits(node, snapshot, identityMap, ops);
        }

        // b3-t1: channel-masked transform diff. A channel present in the SNAPSHOT's DrivenChannels
        // (the live, enabled FitSize/SurfaceSnap mask) holds the snapshot's own value in the effective
        // transform, contributing no diff on that channel; free channels (and rotation, never driven)
        // still diff normally against the desired value. Emits a SetTransform iff the effective
        // transform actually differs from the snapshot; a driven-only drift emits nothing. Mask None
        // (the common case) makes effective == desired field-wise, preserving today's whole-transform
        // behavior byte-for-byte.
        private static void EmitTransformEdit(GameObjectNode node, SnapshotNode snapshot, List<ChangeOp> ops)
        {
            var driven = snapshot.Transform.DrivenChannels;
            var desired = node.Transform;
            var actual = snapshot.Transform;

            var position = new Vec3(
                (driven & ChannelMask.PositionX) != 0 ? actual.Position.X : desired.Position.X,
                (driven & ChannelMask.PositionY) != 0 ? actual.Position.Y : desired.Position.Y,
                (driven & ChannelMask.PositionZ) != 0 ? actual.Position.Z : desired.Position.Z);

            var scale = new Vec3(
                (driven & ChannelMask.ScaleX) != 0 ? actual.Scale.X : desired.Scale.X,
                (driven & ChannelMask.ScaleY) != 0 ? actual.Scale.Y : desired.Scale.Y,
                (driven & ChannelMask.ScaleZ) != 0 ? actual.Scale.Z : desired.Scale.Z);

            var effective = desired with { Position = position, Scale = scale, DrivenChannels = driven };

            if (effective.Position == actual.Position && effective.Rotation == actual.Rotation && effective.Scale == actual.Scale)
            {
                return;
            }

            ops.Add(new SetTransform { LogicalId = node.LogicalId, Transform = effective });
        }

        // Diffs components on a matched GameObject (b4-t1). Component correspondence between
        // desired and actual is by (Type.FullName, ordinal-within-that-type) — this exactly
        // reconstructs the LogicalId scheme the parser assigns
        // (BuilderParser.AssignComponentLogicalIds), so desired-side identity is just the
        // component's own LogicalId. Transform is excluded from the actual side (never authored,
        // never removed/reordered). Removed-component identity is resolved from the IdentityMap's
        // Component entries (managed gate), never synthesized.
        private static void EmitComponentEdits(GameObjectNode node, SnapshotNode snapshot, IdentityMap identityMap, List<ChangeOp> ops)
        {
            var ownerLogicalId = node.LogicalId;
            var desiredComps = node.Components;
            var actualComps = snapshot.Components
                .Where(c => c.Type.FullName != "UnityEngine.Transform")
                .ToArray();

            var desiredKeys = ComputeComponentKeys(desiredComps);
            var actualKeys = ComputeComponentKeys(actualComps);

            var desiredIndexByKey = new Dictionary<(string TypeFullName, int Ordinal), int>();
            for (var i = 0; i < desiredComps.Length; i++)
            {
                desiredIndexByKey[desiredKeys[i]] = i;
            }

            var actualIndexByKey = new Dictionary<(string TypeFullName, int Ordinal), int>();
            for (var i = 0; i < actualComps.Length; i++)
            {
                actualIndexByKey[actualKeys[i]] = i;
            }

            var managedEntriesByType = identityMap.Entries
                .Where(e => e.Kind == "Component" && e.ParentLogicalId == ownerLogicalId)
                .GroupBy(e => e.ComponentType ?? "")
                .ToDictionary(g => g.Key, g => g.ToList());

            var addOps = new List<ChangeOp>();
            var setFieldOps = new List<ChangeOp>();
            var removeOps = new List<ChangeOp>();
            var reorderOps = new List<ChangeOp>();

            for (var i = 0; i < desiredComps.Length; i++)
            {
                var desiredComponent = desiredComps[i];
                if (actualIndexByKey.TryGetValue(desiredKeys[i], out var actualIndex))
                {
                    var actualComponent = actualComps[actualIndex];
                    foreach (var field in desiredComponent.Fields)
                    {
                        if (!actualComponent.Fields.TryGetValue(field.Key, out var actualValue) || actualValue != field.Value)
                        {
                            setFieldOps.Add(new SetField
                            {
                                LogicalId = ownerLogicalId,
                                ComponentLogicalId = desiredComponent.LogicalId,
                                Path = field.Key,
                                Value = field.Value,
                            });
                        }
                    }
                }
                else
                {
                    addOps.Add(new AddComponent { LogicalId = ownerLogicalId, Component = desiredComponent });
                }
            }

            for (var i = 0; i < actualComps.Length; i++)
            {
                var key = actualKeys[i];
                if (desiredIndexByKey.ContainsKey(key))
                {
                    continue;
                }

                var actualComponent = actualComps[i];
                if (managedEntriesByType.TryGetValue(actualComponent.Type.FullName, out var entries) && key.Ordinal < entries.Count)
                {
                    removeOps.Add(new RemoveComponent
                    {
                        LogicalId = ownerLogicalId,
                        ComponentLogicalId = entries[key.Ordinal].LogicalId,
                        ComponentType = actualComponent.Type,
                    });
                }
            }

            if (new HashSet<(string TypeFullName, int Ordinal)>(desiredKeys).SetEquals(actualKeys))
            {
                for (var i = 0; i < desiredComps.Length; i++)
                {
                    var actualIndex = actualIndexByKey[desiredKeys[i]];
                    if (i != actualIndex)
                    {
                        reorderOps.Add(new ReorderComponent
                        {
                            LogicalId = ownerLogicalId,
                            ComponentLogicalId = desiredComps[i].LogicalId,
                            ToIndex = i,
                        });
                    }
                }
            }

            ops.AddRange(addOps);
            ops.AddRange(setFieldOps);
            ops.AddRange(removeOps);
            ops.AddRange(reorderOps);
        }

        private static (string TypeFullName, int Ordinal)[] ComputeComponentKeys(ComponentData[] components)
        {
            var keys = new (string TypeFullName, int Ordinal)[components.Length];
            var ordinalByType = new Dictionary<string, int>();
            for (var i = 0; i < components.Length; i++)
            {
                var typeFullName = components[i].Type.FullName;
                var ordinal = ordinalByType.TryGetValue(typeFullName, out var count) ? count : 0;
                ordinalByType[typeFullName] = ordinal + 1;
                keys[i] = (typeFullName, ordinal);
            }

            return keys;
        }

        // b3-t1: unmatched PrefabInstanceNode ⇒ instantiate-not-create. No SetName/SetTag/SetLayer/
        // SetActive/SetStatic and no EmitComponentEdits — v1 instances carry no authored Components,
        // so the only emitted edit beyond the instantiate itself is the root transform.
        private static void EmitCreateInstance(PrefabInstanceNode node, string? parentLogicalId, int siblingIndex, List<ChangeOp> ops)
        {
            ops.Add(new AddInstance
            {
                LogicalId = node.LogicalId,
                Guid = node.SourcePrefab.Guid,
                ParentLogicalId = parentLogicalId,
                SiblingIndex = siblingIndex,
            });
            ops.Add(new SetTransform { LogicalId = node.LogicalId, Transform = node.Transform });
        }

        private static void EmitCreate(GameObjectNode node, string? parentLogicalId, IdentityMap identityMap, List<ChangeOp> ops)
        {
            ops.Add(new AddNode { LogicalId = node.LogicalId, Name = node.Name, ParentLogicalId = parentLogicalId });
            ops.Add(new SetTransform { LogicalId = node.LogicalId, Transform = node.Transform });

            if (node.Tag != "Untagged")
            {
                ops.Add(new SetTag { LogicalId = node.LogicalId, Tag = node.Tag });
            }

            if (node.Layer != 0)
            {
                ops.Add(new SetLayer { LogicalId = node.LogicalId, Layer = node.Layer });
            }

            if (node.Active != true)
            {
                ops.Add(new SetActive { LogicalId = node.LogicalId, Active = node.Active });
            }

            if (node.IsStatic != false)
            {
                ops.Add(new SetStatic { LogicalId = node.LogicalId, IsStatic = node.IsStatic });
            }

            // A newly-created node has no snapshot; diffing its components against an empty
            // snapshot emits an AddComponent (carrying each field) for every authored component,
            // matching the mapped path's emission. Children are handled by WalkDesired's recursion.
            EmitComponentEdits(node, new SnapshotNode(), identityMap, ops);
        }
    }
}
