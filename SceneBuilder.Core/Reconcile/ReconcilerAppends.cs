using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // DetectAppends (incl. its recursion), moved verbatim out of Reconciler.cs (b4-t3) so the
    // `pendingTargets` threading it needed fits the project's file-size budget. Second partial-class
    // file — no visibility change, every callee is private static on the same class.
    public static partial class Reconciler
    {
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
            // b4-t3: threaded through only to reach the EmitComponentAppend call below (§13
            // one-pass attach) — same set ReconcileComponents' ADD path already receives.
            ISet<string> resolvableTargets,
            // b4-t3: same-batch create-candidate identities — threaded through only to reach the
            // EmitComponentAppend call below, mirroring resolvableTargets.
            ISet<string> pendingTargets,
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
                        resolvableTargets,
                        pendingTargets,
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
                    // b4-t1: Sizer-before-Snapper canonical order, regardless of live
                    // GetComponents order, so a created node's Sizer always attaches before its
                    // Snapper.
                    var representableComponents = SpatialComponentSource.OrderForEmit(
                        ComponentReconciler.ExcludeTransform(node.Components));

                    // b2-t3: a node with >=1 create-candidate (unmapped, non-empty-goid) child
                    // heads its own handle so its descendants can reference it - otherwise they
                    // would be stranded (the old dead-end recursion below).
                    // A create candidate whose Name duplicates another sibling in the same snapshot
                    // array also heads its own handle - this covers a duplicate against an
                    // already-MAPPED sibling as well as against another append, and keeps
                    // EnsureNoAmbiguousDuplicateNames from injecting a positional `.Id(...)`.
                    var headsHandle = node.Children.Any(c =>
                        !string.IsNullOrEmpty(c.GlobalObjectId) && !goidToLogicalId.ContainsKey(c.GlobalObjectId))
                        || representableComponents.Length > 0
                        || nodes.Count(n => n.Name == node.Name) > 1;

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
                                resolveOwnerHandle,
                                resolvableTargets,
                                pendingTargets,
                                conflicts,
                                // A genuinely-new object never overlaps a FIELD-VALUE DIFF pass (that
                                // pass only runs for mapped owners) — this append is the ONLY place an
                                // unresolvable target on it can ever be reported, so it must.
                                reportUnresolvable: true,
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
                        resolvableTargets,
                        pendingTargets,
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
                    resolvableTargets,
                    pendingTargets,
                    resolveOwnerHandle,
                    nextIndexByParentKey,
                    edits,
                    addedEntries,
                    removedLogicalIds,
                    conflicts,
                    addedAssets);
            }
        }
    }
}
