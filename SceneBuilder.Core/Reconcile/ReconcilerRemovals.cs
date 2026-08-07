using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;

namespace SceneBuilder.Core.Reconcile
{
    // Scene-deleted-object detection: the removal pass of Reconcile, sibling of
    // ReconcilerAppends.cs.
    public static partial class Reconciler
    {
        // Symmetric to DetectAppends: walks the IdentityMap (code-side objects) looking for
        // GameObject entries whose GlobalObjectId is absent from the live snapshot - i.e.
        // scene-deleted objects. Emits one RemoveStatement + one RemovedLogicalIds entry per
        // deleted object. Independent of Differ's ops (RemoveNode cannot be used here).
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
            // LogicalIds targeted by an ObjectRef field anywhere in the source model — see call
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
                .Where(e => (IdentityNodeIndex.IsNode(e) || e.Kind == IdentityNodeIndex.Component) && e.ParentLogicalId != null)
                .GroupBy(e => e.ParentLogicalId!)
                .ToDictionary(g => g.Key, g => g.ToArray());

            foreach (var entry in identityMap.Entries)
            {
                if (!IdentityNodeIndex.IsMappedNode(entry))
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
                    IdentityNodeIndex.IsMappedNode(d)
                    && snapshotByGoid.ContainsKey(d.GlobalObjectId));

                // A field elsewhere in the source still names this handle. The owner is gone
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
    }
}
