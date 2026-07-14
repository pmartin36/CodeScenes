using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    public static class Reconciler
    {
        private sealed record SnapshotEntry(SnapshotNode Node, string? ParentGlobalObjectId, int SiblingIndex);

        public static ReconcileResult Reconcile(
            SceneModel expected,
            SceneSnapshot actual,
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? anchors = null)
        {
            var changeSet = Differ.Diff(expected, actual, identityMap);

            var logicalIdToGlobalObjectId = identityMap.Entries
                .Where(e => e.Kind == "GameObject" && !string.IsNullOrEmpty(e.GlobalObjectId))
                .ToDictionary(e => e.LogicalId, e => e.GlobalObjectId);

            var globalObjectIdToLogicalId = logicalIdToGlobalObjectId
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

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

                    default:
                        // SetTag/SetLayer/SetActive/SetStatic and AddNode/RemoveNode are out of
                        // M2 sync-back scope; no SourceEdit is emitted for them.
                        break;
                }
            }

            return new ReconcileResult { Patch = new SourcePatch { Edits = edits.ToArray() }, Conflicts = conflicts.ToArray() };
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
    }
}
