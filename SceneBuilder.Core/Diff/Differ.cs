using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Diff
{
    public static class Differ
    {
        private sealed record SnapshotEntry(SnapshotNode Node, string? ParentGlobalObjectId, int SiblingIndex);

        public static ChangeSet Diff(SceneModel desired, SceneSnapshot actual, IdentityMap identityMap)
        {
            var logicalIdToGlobalObjectId = identityMap.Entries
                .Where(e => e.Kind == "GameObject" && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

            var globalObjectIdToLogicalId = logicalIdToGlobalObjectId
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

            var snapshotByGoid = new Dictionary<string, SnapshotEntry>();
            FlattenSnapshot(actual.Roots, null, snapshotByGoid);

            var visitedGoids = new HashSet<string>();
            var ops = new List<ChangeOp>();

            WalkDesired(desired.Roots, null, logicalIdToGlobalObjectId, snapshotByGoid, visitedGoids, ops);

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

            return new ChangeSet { Ops = ops.ToArray() };
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
            List<ChangeOp> ops)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                if (logicalIdToGlobalObjectId.TryGetValue(node.LogicalId, out var goid)
                    && snapshotByGoid.TryGetValue(goid, out var entry))
                {
                    visitedGoids.Add(goid);
                    EmitEdits(node, parentLogicalId, i, logicalIdToGlobalObjectId, entry, ops);
                }
                else
                {
                    EmitCreate(node, parentLogicalId, ops);
                }

                WalkDesired(node.Children, node.LogicalId, logicalIdToGlobalObjectId, snapshotByGoid, visitedGoids, ops);
            }
        }

        private static void EmitEdits(
            GameObjectNode node,
            string? parentLogicalId,
            int siblingIndex,
            Dictionary<string, string> logicalIdToGlobalObjectId,
            SnapshotEntry entry,
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

            if (node.Transform != snapshot.Transform)
            {
                ops.Add(new SetTransform { LogicalId = node.LogicalId, Transform = node.Transform });
            }

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
        }

        private static void EmitCreate(GameObjectNode node, string? parentLogicalId, List<ChangeOp> ops)
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
        }
    }
}
