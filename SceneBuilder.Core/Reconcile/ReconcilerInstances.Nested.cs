using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // m-nested-props b4-t2: below-root ("nested", Target.ChildPath != "") membership diff for a
    // MAPPED PrefabInstance — the scoped-`.On` twin of ReconcilerInstances.cs's root emit, plus the
    // (previously missing, see research.md) child-GameObject add/remove machinery. Split into its
    // own partial-class file per the M10 partial-file precedent.
    public static partial class Reconciler
    {
        // Builds the ONE AppendScopedOn edit for a single nested op at `childPath`. Multiple ops
        // for the same (Anchor, SelectorMatchKey) fold into one `.On(...)` block at apply time
        // (SourcePatchApplier.ScopedOn.cs's ResolveScopedOnAppends) — no grouping needed here.
        private static AppendScopedOn BuildAppendScopedOn(
            string instanceLogicalId,
            string childPath,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            ScopedOp op)
        {
            TryRenderScopedSelector(facadeCatalog, prefabGuid, childPath, out var selectorExpr);

            return new AppendScopedOn
            {
                Anchor = instanceLogicalId,
                SelectorMatchKey = childPath,
                SelectorExpr = selectorExpr,
                Ops = new[] { op },
            };
        }

        // Reverse-resolves a resolved child path ("Turret/Barrel", RealName-joined) to a typed
        // member-chain selector ("sel => sel.Turret.Barrel", PropertyName-joined) by walking the
        // FacadeCatalog entry's FacadeNode.Children by RealName (mirrors
        // FacadeCatalog.TryResolveSelector's forward walk, reversed). A miss at any segment, an
        // absent catalog, or no entry for prefabGuid falls back to the string selector form.
        private static bool TryRenderScopedSelector(FacadeCatalog? facadeCatalog, string? prefabGuid, string childPath, out string selectorExpr)
        {
            if (facadeCatalog != null && !string.IsNullOrEmpty(prefabGuid) && facadeCatalog.TryGetEntryByGuid(prefabGuid, out var entry))
            {
                var node = entry.Root;
                var propertyNames = new List<string>();
                var matchedAll = true;

                foreach (var segment in childPath.Split('/'))
                {
                    FacadeChild? matched = null;
                    foreach (var child in node.Children)
                    {
                        if (child.RealName == segment)
                        {
                            matched = child;
                            break;
                        }
                    }

                    if (matched == null)
                    {
                        matchedAll = false;
                        break;
                    }

                    propertyNames.Add(matched.PropertyName);
                    node = matched.Node;
                }

                if (matchedAll)
                {
                    selectorExpr = "sel => sel." + string.Join(".", propertyNames);
                    return true;
                }
            }

            selectorExpr = SourceExpr.StringLiteral(childPath);
            return false;
        }

        // Scene->code diff for AddedGameObjects: key is (Parent, Node.Name), mirroring
        // InstanceOverrideDiff.EmitAddedGameObjects (materialize direction) with roles reversed —
        // here the SNAPSHOT is truth. A snapshot-only entry appends a top-level `.AddChild(...)`
        // (never inside `.On` — a newly-added child has no resolved facade identity yet); a
        // model-only entry (reverted in Unity) drops the authored call.
        private static void ReconcileAddedGameObjects(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            var modelKeys = new HashSet<(OverrideTarget Parent, string Name)>();
            foreach (var modelAdded in model.AddedGameObjects)
            {
                modelKeys.Add((modelAdded.Parent, modelAdded.Node.Name));
            }

            var snapshotKeys = new HashSet<(OverrideTarget Parent, string Name)>();
            foreach (var snapshotAdded in snapshot.AddedGameObjects)
            {
                var key = (snapshotAdded.Parent, snapshotAdded.Node.Name);
                snapshotKeys.Add(key);

                if (modelKeys.Contains(key))
                {
                    continue;
                }

                // The PARENT is a resolved source sub-object, so it carries a façade identity — emit
                // the typed selector when the catalog resolves it (root parent "" falls back to the
                // empty string literal). The new child NAME stays a string (specs/27).
                TryRenderScopedSelector(facadeCatalog, prefabGuid, snapshotAdded.Parent.ChildPath, out var parentSelectorExpr);

                // m-ui-recttransform b3-t1 (iteration 2): the SAME create-with-payload split the
                // plain-append path uses (Reconciler.SplitCreatedPayload) — an added UI child's
                // layout must ride the SAME AddChild closure a component payload already uses,
                // never be silently dropped. `canHostRectTransformCall: true` — `.AddChild(...)`
                // returns a NodeHandle, which declares `.RectTransform(...)`. `AddedGameObject.Node`
                // is a GameObjectNode, which carries no GlobalObjectId.
                // `.AddChild(...)` is NodeHandle-hosted
                // (canHostRectTransformCall: true makes the baseline unreachable) — state
                // `prefabBaseline: null` rather than omit it.
                var (_, rectTransformPayload) = SplitCreatedPayload(
                    snapshotAdded.Node.Transform,
                    canHostRectTransformCall: true,
                    prefabBaseline: null,
                    instanceLogicalId,
                    globalObjectId: null,
                    conflicts);

                edits.Add(new AppendInstanceAddChild
                {
                    Anchor = instanceLogicalId,
                    ParentPath = snapshotAdded.Parent.ChildPath,
                    ParentSelectorExpr = parentSelectorExpr,
                    Name = snapshotAdded.Node.Name,
                    Node = snapshotAdded.Node,
                    RectTransform = rectTransformPayload,
                });
            }

            foreach (var modelAdded in model.AddedGameObjects)
            {
                var key = (modelAdded.Parent, modelAdded.Node.Name);
                if (snapshotKeys.Contains(key))
                {
                    continue;
                }

                edits.Add(new DropInstanceCall
                {
                    Anchor = instanceLogicalId,
                    Kind = InstanceCallKind.AddChild,
                    PropertyPath = modelAdded.Node.Name,
                });
            }
        }

        // Scene->code diff for RemovedGameObjects: mirrors ReconcileRemovedComponents exactly
        // (HashSet<OverrideTarget>, equality ignores ChildPath).
        private static void ReconcileRemovedGameObjects(
            PrefabInstanceNode model,
            SnapshotNode snapshot,
            string instanceLogicalId,
            FacadeCatalog? facadeCatalog,
            string? prefabGuid,
            List<SourceEdit> edits)
        {
            var modelTargets = new HashSet<OverrideTarget>(model.RemovedGameObjects);
            var snapshotTargets = new HashSet<OverrideTarget>(snapshot.RemovedGameObjects);

            foreach (var target in snapshot.RemovedGameObjects)
            {
                if (modelTargets.Contains(target))
                {
                    continue;
                }

                // The removed child is a resolved source sub-object — emit the typed selector when
                // the catalog resolves it, else the string path (specs/27).
                TryRenderScopedSelector(facadeCatalog, prefabGuid, target.ChildPath, out var selectorExpr);
                edits.Add(new AppendInstanceRemoveChild { Anchor = instanceLogicalId, ChildPath = target.ChildPath, SelectorExpr = selectorExpr });
            }

            foreach (var target in model.RemovedGameObjects)
            {
                if (snapshotTargets.Contains(target))
                {
                    continue;
                }

                edits.Add(new DropInstanceCall
                {
                    Anchor = instanceLogicalId,
                    Kind = InstanceCallKind.RemoveChild,
                    PropertyPath = target.ChildPath,
                });
            }
        }
    }
}
