using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Reconcile
{
    // DetectAppends (incl. its recursion), split out of Reconciler.cs so the
    // `pendingTargets` threading it needs fits the project's file-size budget. Second partial-class
    // file — no visibility change, every callee is private static on the same class.
    public static partial class Reconciler
    {
        // Snapshot-driven create-detection: walks the ACTUAL (scene) tree depth-first looking
        // for GameObjectIds absent from the IdentityMap. Emits an AppendStatement only for a
        // create candidate whose parent is a root or an already-MAPPED parent. A create candidate
        // with >=1 create-candidate child heads its own handle so its descendants can be
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
            // Threaded through only to reach the EmitComponentAppend call below (§13
            // one-pass attach) — same set ReconcileComponents' ADD path already receives.
            // A same-batch node whose goid is a forward/backward reference target (see
            // sameBatchRefTargets below) is registered into this set the moment it heads a
            // handle, so a same-batch referencing component classifies it Resolvable instead
            // of Pending.
            ISet<string> resolvableTargets,
            // Same-batch create-candidate identities — threaded through only to reach the
            // EmitComponentAppend call below, mirroring resolvableTargets. A goid promoted into
            // resolvableTargets (above) is removed from here in the same step.
            ISet<string> pendingTargets,
            // Every create-candidate goid that some OTHER same-batch create-candidate's
            // component references (Model/ObjectRefValues.Targets over each candidate's
            // representable-component fields, intersected with the candidate goid set) —
            // computed ONCE by CollectSameBatchRefTargets before the walk starts. A member of
            // this set heads its own handle (headsHandle's 4th reason) even with no
            // create-candidate children of its own, so its `Add` has somewhere to be referenced
            // from.
            ISet<string> sameBatchRefTargets,
            // THE batch handle registry: every create-candidate that heads a handle this sync
            // registers itself here under BOTH its GlobalObjectId and its own handle name, the
            // moment the handle is derived. Threaded into resolveOwnerHandle (Reconciler.cs) so
            // (a) a same-batch ObjectRef target (keyed by goid) resolves to its real handle
            // instead of rendering a dangling/Pending field, and (b) a same-batch PARENT's own
            // children resolve its receiver (keyed by the handle name itself, since a child's
            // ParentHandle is looked up by that string) without re-deriving a numeric-suffixed
            // duplicate.
            IDictionary<string, string> batchHandleByGoidOrId,
            // Every create-candidate's own representable-component append, buffered here instead
            // of appended to `edits` inline. Flushed by FlushPendingComponentAppends AFTER this
            // whole walk returns, so (1) by flush time every same-batch handle in
            // batchHandleByGoidOrId/resolvableTargets is registered regardless of visit order
            // (a forward OR backward same-batch reference both resolve), and (2) every
            // AppendStatement this walk emits precedes every AppendComponentStatement in `edits`,
            // so SourcePatchApplier always inserts a referenced target's `var` before the
            // referencing component (see StatementPlacement's declare-before-use floor).
            List<PendingComponentAppend> pendingComponentAppends,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            Dictionary<string, int> nextIndexByParentKey,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<string> removedLogicalIds,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets,
            // Source-prefab GUID -> last-known asset path (identityMap.Assets), used to
            // re-derive the `.Instance(path)` argument for a snapshot-only prefab-instance root.
            IReadOnlyDictionary<string, string> prefabPathByGuid,
            // Optional Guid->PropertyName reverse lookup — non-null iff the caller
            // threaded a manifest-backed FacadeCatalog; used by HandleInstanceNode to prefer the
            // typed `Instance(Prefabs.X)` emission over the string fallback.
            FacadeCatalog? facadeCatalog,
            // Catalogued AssetRef fields on a newly-created object pre-render into their
            // typed `Assets.<...>` member chain — threaded through only to reach the
            // EmitComponentAppend call below, mirroring facadeCatalog.
            AssetCatalog? assetCatalog = null,
            // The ONE per-type default-field index, built ONCE by Reconciler.Reconcile —
            // threaded through only to reach the EmitComponentAppend call below, mirroring
            // assetCatalog.
            ComponentDefaultOmission.Index? defaults = null)
        {
            // The array position IS the scene sibling index (FlattenSnapshot keys SnapshotEntry off the
            // same index), and it is what a created node's statement must be placed at.
            for (var siblingIndex = 0; siblingIndex < nodes.Length; siblingIndex++)
            {
                var node = nodes[siblingIndex];

                // A prefab-instance root is handled entirely by HandleInstanceNode and
                // NEVER recursed into — its children are the prefab's internal hierarchy, out of
                // scope until M10 — regardless of whether it is already mapped.
                if (node.SourcePrefabGuid != null)
                {
                    HandleInstanceNode(
                        node,
                        siblingIndex,
                        parentLogicalId,
                        parentIsMapped,
                        expected,
                        goidToLogicalId,
                        modelByLogicalId,
                        reserved,
                        prefabPathByGuid,
                        facadeCatalog,
                        resolveOwnerHandle,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        conflicts,
                        nodes);

                    continue;
                }

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
                        sameBatchRefTargets,
                        batchHandleByGoidOrId,
                        pendingComponentAppends,
                        resolveOwnerHandle,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts,
                        addedAssets,
                        prefabPathByGuid,
                        facadeCatalog,
                        assetCatalog,
                        defaults);

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

                    // §13: representable (non-Transform) components force the owner to head
                    // a handle too, so the same-batch component append (ComponentPatchApplier) has
                    // an `OwnerHandle` to attach onto.
                    // FitSize-before-AlignTo canonical order, regardless of live
                    // GetComponents order, so a created node's FitSize always attaches before its
                    // AlignTo.
                    var representableComponents = SpatialComponentSource.OrderForEmit(
                        ComponentReconciler.ExcludeTransform(node.Components));

                    // A node with >=1 create-candidate (unmapped, non-empty-goid) child
                    // heads its own handle so its descendants can reference it - otherwise they
                    // would be stranded (the old dead-end recursion below).
                    // A create candidate whose Name duplicates another sibling in the same snapshot
                    // array also heads its own handle - this covers a duplicate against an
                    // already-MAPPED sibling as well as against another append, and keeps
                    // EnsureNoAmbiguousDuplicateNames from injecting a positional `.Id(...)`.
                    // A create candidate that some OTHER same-batch create-candidate's component
                    // references also heads its own handle, even with no create-candidate children
                    // and no components of its own — otherwise the referencing field has no name
                    // to render and drops as Pending.
                    var headsHandle = node.Children.Any(c =>
                        !string.IsNullOrEmpty(c.GlobalObjectId) && !goidToLogicalId.ContainsKey(c.GlobalObjectId))
                        || representableComponents.Length > 0
                        || nodes.Count(n => n.Name == node.Name) > 1
                        || (!string.IsNullOrEmpty(node.GlobalObjectId) && sameBatchRefTargets.Contains(node.GlobalObjectId));

                    string newLogicalId;
                    string? ownHandle = null;

                    if (headsHandle)
                    {
                        ownHandle = HandleNaming.Derive(node.Name, reserved);
                        reserved.Add(ownHandle);
                        newLogicalId = ownHandle;

                        // THE batch handle registry write: registers this create-candidate under
                        // both its goid (so a same-batch ObjectRef target, keyed by goid, resolves)
                        // and its own handle name (so a same-batch CHILD's ParentHandle lookup,
                        // keyed by this node's LogicalId == its own handle, resolves too — see
                        // Reconciler.cs's resolveOwnerHandle branch). Every headsHandle node
                        // registers here, not just sameBatchRefTargets members, because the CS0103
                        // receiver defect (a headed parent's children re-deriving a numeric-suffixed
                        // duplicate) is independent of whether the parent is itself a ref target.
                        if (!string.IsNullOrEmpty(node.GlobalObjectId))
                        {
                            batchHandleByGoidOrId[node.GlobalObjectId] = ownHandle;
                        }

                        batchHandleByGoidOrId[ownHandle] = ownHandle;

                        // A same-batch ObjectRef target carries the target's goid (it has no
                        // LogicalId yet) — promote it out of "pending" now that it has a handle, so
                        // ClassifySnapshotRef (consulted later, once every create-candidate's handle
                        // is registered) sees it as Resolvable rather than dropping the field.
                        if (!string.IsNullOrEmpty(node.GlobalObjectId) && sameBatchRefTargets.Contains(node.GlobalObjectId))
                        {
                            resolvableTargets.Add(node.GlobalObjectId);
                            pendingTargets.Remove(node.GlobalObjectId);
                        }
                    }
                    else
                    {
                        newLogicalId = LogicalIdResolver.Synthesize(parentHandle, node.Name, index);
                    }

                    // §13: a created UI
                    // node's payload is driven-masked (D2) and X/Y-held (D1) BEFORE it is split
                    // between Transform (base GameObject data) and RectTransform (the chained
                    // `.RectTransform(...)` call) — the ONE such split (SplitCreatedPayload), also
                    // used by the instance-AddChild create path (ReconcilerInstances.Nested.cs).
                    var authoringDefaults = new TransformData();
                    // A plain `.Add(...)` is NodeHandle-hosted
                    // (canHostRectTransformCall: true makes the baseline unreachable) — state
                    // `prefabBaseline: null` rather than omit it.
                    var (transformPayload, rectTransformPayload) = SplitCreatedPayload(
                        node.Transform,
                        canHostRectTransformCall: true,
                        prefabBaseline: null,
                        newLogicalId,
                        node.GlobalObjectId,
                        conflicts);

                    edits.Add(new AppendStatement
                    {
                        NewLogicalId = newLogicalId,
                        ParentAnchor = parentLogicalId,
                        NewSiblingIndex = siblingIndex,
                        Name = node.Name,
                        Transform = transformPayload != authoringDefaults ? transformPayload : null,
                        RectTransform = rectTransformPayload,
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
                    // AddedEntry) rather than reinventing it here. The actual EmitComponentAppend
                    // call is DEFERRED (buffered, not invoked here) — see pendingComponentAppends'
                    // doc comment on the DetectAppends parameter above.
                    if (representableComponents.Length > 0)
                    {
                        var keys = ComponentReconciler.ComputeComponentKeys(representableComponents);
                        pendingComponentAppends.Add(new PendingComponentAppend(
                            newLogicalId, newLogicalId, representableComponents, keys, ownHandle));
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
                        sameBatchRefTargets,
                        batchHandleByGoidOrId,
                        pendingComponentAppends,
                        resolveOwnerHandle,
                        nextIndexByParentKey,
                        edits,
                        addedEntries,
                        removedLogicalIds,
                        conflicts,
                        addedAssets,
                        prefabPathByGuid,
                        facadeCatalog,
                        assetCatalog,
                        defaults);

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
                    sameBatchRefTargets,
                    batchHandleByGoidOrId,
                    pendingComponentAppends,
                    resolveOwnerHandle,
                    nextIndexByParentKey,
                    edits,
                    addedEntries,
                    removedLogicalIds,
                    conflicts,
                    addedAssets,
                    prefabPathByGuid,
                    facadeCatalog,
                    assetCatalog,
                    defaults);
            }
        }

        // One create-candidate's buffered representable-component append, resolved and appended
        // to the real `edits` list only after DetectAppends' whole walk returns (FlushPendingComponentAppends) —
        // see pendingComponentAppends' doc comment on the DetectAppends parameter.
        private readonly record struct PendingComponentAppend(
            string OwnerAnchor,
            string OwnerEffectiveId,
            ComponentData[] RepresentableComponents,
            (string TypeFullName, int Ordinal)[] Keys,
            string? OwnHandle);

        // The deferred half of the §13 one-pass attach: calls EmitComponentAppend for every
        // buffered create-candidate component, in the SAME per-owner and per-node order the walk
        // buffered them in (preserves FitSize-before-AlignTo and any other canonical
        // multi-component order). Called once, after DetectAppends' top-level call returns, so
        // every create-candidate's handle (batchHandleByGoidOrId) and same-batch-target
        // resolvability (resolvableTargets/pendingTargets) is already settled for the whole
        // snapshot regardless of DFS visit order.
        private static void FlushPendingComponentAppends(
            List<PendingComponentAppend> pendingComponentAppends,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            ISet<string> resolvableTargets,
            ISet<string> pendingTargets,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets,
            AssetCatalog? assetCatalog,
            ComponentDefaultOmission.Index? defaults)
        {
            foreach (var pending in pendingComponentAppends)
            {
                for (var i = 0; i < pending.RepresentableComponents.Length; i++)
                {
                    ComponentReconciler.EmitComponentAppend(
                        pending.OwnerAnchor,
                        pending.OwnerEffectiveId,
                        pending.Keys[i].TypeFullName,
                        pending.Keys[i].Ordinal,
                        i,
                        pending.RepresentableComponents[i].Fields,
                        resolveOwnerHandle,
                        resolvableTargets,
                        pendingTargets,
                        conflicts,
                        // A genuinely-new object never overlaps a FIELD-VALUE DIFF pass (that
                        // pass only runs for mapped owners) — this append is the ONLY place an
                        // unresolvable target on it can ever be reported, so it must.
                        reportUnresolvable: true,
                        pending.OwnHandle,
                        false,
                        edits,
                        addedEntries,
                        addedAssets,
                        assetCatalog,
                        defaults);
                }
            }
        }

        // Every create-candidate goid that some OTHER same-batch create-candidate's representable
        // component references, bounded by the append batch (not the whole scene): the
        // intersection of "every create-candidate goid" and "every ObjectRef target reachable in a
        // create-candidate's own component fields". A referrer that is a MAPPED owner is out of
        // scope here — that is the introduce path, a different mechanism entirely.
        private static HashSet<string> CollectSameBatchRefTargets(
            SnapshotNode[] roots, IReadOnlyDictionary<string, string> goidToLogicalId)
        {
            var candidateGoids = new HashSet<string>(StringComparer.Ordinal);
            var referencedByCandidates = new HashSet<string>(StringComparer.Ordinal);
            CollectCreateCandidateRefs(roots, parentIsMapped: true, goidToLogicalId, candidateGoids, referencedByCandidates);

            referencedByCandidates.IntersectWith(candidateGoids);
            return referencedByCandidates;
        }

        // Mirrors DetectAppends' own mapped/create-candidate/orphaned dispatch structurally (a node
        // is a create candidate iff unmapped AND its parent is a root, a MAPPED owner, or itself a
        // create candidate) without needing headsHandle — a node's headsHandle-driven eligibility
        // to recurse (clause 1) is already exactly "has >=1 create-candidate child", so this
        // simpler recursion visits precisely the same set DetectAppends will.
        private static void CollectCreateCandidateRefs(
            SnapshotNode[] nodes,
            bool parentIsMapped,
            IReadOnlyDictionary<string, string> goidToLogicalId,
            HashSet<string> candidateGoids,
            HashSet<string> referencedByCandidates)
        {
            foreach (var node in nodes)
            {
                // Prefab-instance roots are handled entirely by HandleInstanceNode and never
                // recursed into by DetectAppends — out of scope here too.
                if (node.SourcePrefabGuid != null)
                {
                    continue;
                }

                var isMapped = !string.IsNullOrEmpty(node.GlobalObjectId) && goidToLogicalId.ContainsKey(node.GlobalObjectId);
                if (isMapped)
                {
                    CollectCreateCandidateRefs(node.Children, true, goidToLogicalId, candidateGoids, referencedByCandidates);
                    continue;
                }

                if (parentIsMapped)
                {
                    if (!string.IsNullOrEmpty(node.GlobalObjectId))
                    {
                        candidateGoids.Add(node.GlobalObjectId);
                    }

                    foreach (var component in ComponentReconciler.ExcludeTransform(node.Components))
                    {
                        foreach (var (_, value) in component.Fields)
                        {
                            foreach (var target in ObjectRefValues.Targets(value))
                            {
                                referencedByCandidates.Add(target);
                            }
                        }
                    }

                    CollectCreateCandidateRefs(node.Children, true, goidToLogicalId, candidateGoids, referencedByCandidates);
                    continue;
                }

                CollectCreateCandidateRefs(node.Children, false, goidToLogicalId, candidateGoids, referencedByCandidates);
            }
        }
    }
}
