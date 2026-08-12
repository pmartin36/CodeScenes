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
    // file under the project's file-size budget after threading `pendingTargets` through it.
    // DetectRemovals, its symmetric sibling, lives in ReconcilerRemovals.cs.
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
            IReadOnlyDictionary<string, string>? handles = null,
            FacadeCatalog? facadeCatalog = null,
            // Catalogued AssetRef fields render as their typed `Assets.<...>` member chain
            // instead of `Asset("path")`. Reconcile-time-only, mirroring facadeCatalog — never
            // reaches SourcePatchApplier/SourceExpr-at-apply.
            AssetCatalog? assetCatalog = null,
            // LogicalIds of components whose source construct is a chained call
            // (ParseResult.ChainedComponents) — feeds ComponentReconciler's REORDER-pass guard so
            // an owner with a chained component never emits an unrepresentable ReorderStatement.
            // `null` (the default, and every hand-built model/snapshot/map test call) means "no
            // source information" and keeps the existing behavior unchanged.
            IReadOnlyCollection<string>? chainedComponents = null,
            // m8: componentLogicalId -> the authored `.Ref<T>()` var name (feeds a listener
            // target's rendered token) and, per component+field, the ORDERED per-listener call
            // spans (feeds in-place patch/remove). `null` = "no source information", same contract
            // as componentAnchors/fieldArgumentSpans above; every existing caller/test stays green
            // unchanged.
            IReadOnlyDictionary<string, string>? componentHandles = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>? listenerCallSpans = null)
        {
            var changeSet = Differ.Diff(expected, actual, identityMap);

            // Converted ONCE, not per-owner: ComponentReconciler.ReconcileComponents runs once per
            // mapped owner below and every call shares this same set.
            ISet<string>? chainedComponentSet = chainedComponents == null
                ? null
                : new HashSet<string>(chainedComponents, StringComparer.Ordinal);

            // The ONE per-type default-field index, built ONCE per Reconcile (an O(1) lookup
            // constraint) and threaded to every emit site that decides what to
            // omit from a newly-authored field set.
            var defaultsIndex = ComponentDefaultOmission.Index.Build(actual.ComponentDefaults);

            // m8: the ONE per-type UnityEvent member-spelling index, built ONCE per Reconcile
            // (mirrors defaultsIndex above) and threaded to every ReconcileComponents call — the
            // UnityEventListeners intercept's `.OnEvent(...)`-form lookup.
            var memberSpellingsIndex = MemberSpellingIndex.Build(actual.MemberSpellings);

            var logicalIdToGlobalObjectId = IdentityNodeIndex.LogicalIdToGlobalObjectId(identityMap);

            var globalObjectIdToLogicalId = IdentityNodeIndex.GlobalObjectIdToLogicalId(identityMap);

            // Source-prefab GUID -> last-known asset path, re-deriving the
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
                .Where(IdentityNodeIndex.IsNode)
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
                    && IdentityNodeIndex.IsNode(e));
                addedEntries.Add((source ?? new IdentityMapEntry { LogicalId = oldId, Kind = "GameObject" })
                    with
                    {
                        LogicalId = newId,
                        GlobalObjectId = logicalIdToGlobalObjectId.GetValueOrDefault(oldId, string.Empty),
                        ParentLogicalId = newParentLogicalId,
                    });

                foreach (var component in identityMap.Entries)
                {
                    if (component.Kind != IdentityNodeIndex.Component
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
                    // artifacts (handled as a conflict); no sync-back edit here.
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

                    // op.Transform/op.Changed are NOT read — the op carries the MODEL
                    // transform and a raw-float Changed mask (ChannelMask.None for a promotion), so
                    // re-deriving from modelByLogicalId + the live entry and comparing CANONICAL
                    // literals here is authoritative, exactly as it is for SetTransform above.
                    case SetRectTransform:
                        if (modelByLogicalId.TryGetValue(op.LogicalId, out var rectModelNode))
                        {
                            EmitRectTransformEdits(op.LogicalId, goid, rectModelNode, entry.Node, anchors, edits, conflicts);
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

            // Two scene-derived membership sets for ObjectRef dangling-reference detection,
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
                    .Where(e => IdentityNodeIndex.IsMappedNode(e)
                        && snapshotByGoid.ContainsKey(e.GlobalObjectId))
                    .Select(e => e.LogicalId),
                StringComparer.Ordinal);

            var resolvableTargets = new HashSet<string>(sceneLiveTargets, StringComparer.Ordinal);
            resolvableTargets.UnionWith(modelByLogicalId.Keys);
            if (handles != null)
            {
                resolvableTargets.UnionWith(handles.Keys);
            }

            // A UnityEvent listener's Target can name a component LogicalId, never a GameObject
            // one — pre-M8 no ObjectRef ever targeted a component, so this union only widens
            // membership for listener targets (ComponentReconciler's UnityEventListeners
            // intercept), never for any other field. Not goid-gated (mirrors modelByLogicalId.Keys
            // above): the shape reconcile itself writes a Component identity-map entry with an
            // empty GlobalObjectId, so IsMappedComponent's non-empty-goid gate would silently drop
            // it here.
            resolvableTargets.UnionWith(modelByLogicalId.Values.SelectMany(n => n.Components).Select(c => c.LogicalId));
            resolvableTargets.UnionWith(
                identityMap.Entries.Where(e => e.Kind == IdentityNodeIndex.Component).Select(e => e.LogicalId));

            // Same-batch create-candidate identities — snapshot nodes present in the scene
            // but with no IdentityMap entry yet (about to be mapped by DetectAppends THIS pass). A
            // ref to one of these is PENDING, not dangling: it resolves on the guaranteed second
            // Sync once DetectAppends maps it. DISTINCT address space from resolvableTargets
            // (LogicalIds): an ObjectRef to an unmapped target carries the target's
            // GlobalObjectId — its only identity before DetectAppends runs — never a LogicalId.
            var pendingTargets = new HashSet<string>(
                snapshotByGoid.Keys.Where(goid => !globalObjectIdToLogicalId.ContainsKey(goid)),
                StringComparer.Ordinal);

            // A UnityEvent listener can also target a same-batch NEW component (not just a new
            // GameObject) — its owner's snapshot node carries the component's own stamped goid
            // before DetectAppends maps it. Union those in too, so that target classifies Pending
            // (defers to the guaranteed second Sync) instead of Dangling. Excludes an already-
            // mapped component's goid (globalObjectIdToComponentLogicalId) and an unstamped one
            // (empty GlobalObjectId — the prefab-instance channel boundary).
            var globalObjectIdToComponentLogicalId = IdentityNodeIndex.GlobalObjectIdToComponentLogicalId(identityMap);
            foreach (var snapshotEntry in snapshotByGoid.Values)
            {
                foreach (var component in snapshotEntry.Node.Components)
                {
                    if (component.GlobalObjectId.Length > 0
                        && !globalObjectIdToLogicalId.ContainsKey(component.GlobalObjectId)
                        && !globalObjectIdToComponentLogicalId.ContainsKey(component.GlobalObjectId))
                    {
                        pendingTargets.Add(component.GlobalObjectId);
                    }
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

                // A mapped PrefabInstance root has its own override membership diff
                // (snapshot=truth append / model-only drop) — never the plain component pass, whose
                // snapshot Components is always empty for an instance root anyway.
                if (ownerModel is PrefabInstanceNode instanceModel)
                {
                    ReconcileInstanceOverrides(
                        instanceModel,
                        snapshotEntry.Node,
                        ownerLogicalId,
                        anchors,
                        facadeCatalog,
                        ResolveOwnerHandle,
                        edits,
                        conflicts,
                        addedAssets,
                        resolvableTargets,
                        pendingTargets,
                        assetCatalog,
                        defaultsIndex);
                    continue;
                }

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
                    addedAssets,
                    assetCatalog,
                    chainedComponentSet,
                    defaultsIndex,
                    componentHandles,
                    listenerCallSpans,
                    memberSpellingsIndex);
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
                prefabPathByGuid,
                facadeCatalog,
                assetCatalog,
                defaultsIndex);

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

            // Every LogicalId targeted by an ObjectRef reachable at ANY depth (a bare field, a list
            // item, a nested member) ANYWHERE in the source model — cross-object references live
            // outside the structural parent/child + owner/component dependency graph DetectRemovals
            // otherwise walks, so a handle can be "still needed" by a sibling's field without being
            // a structural dependent of it. Consulted below so a structural delete-cascade never
            // strips a `var door = scene.Add(...)` declaration (or its own component statements) out
            // from under a surviving `.Set(x => x.target, door)` argument — that produces
            // non-compiling source (CS0103), never a style issue. A PrefabInstance's own override
            // values and added-component fields are not GameObjectNode Components, so they need
            // their own inclusion here.
            // A SELF-target is excluded: a node's own field can never be the reason its own
            // statement survives, or a self-referencing node deleted from the scene would be
            // silently retained forever instead of removed.
            var referencedByFieldTargets = new HashSet<string>(StringComparer.Ordinal);
            void AddReferencedTargets(ValueNode value, string ownLogicalId)
            {
                foreach (var target in ObjectRefValues.Targets(value))
                {
                    if (target != ownLogicalId)
                    {
                        referencedByFieldTargets.Add(target);
                    }
                }
            }

            foreach (var node in modelByLogicalId.Values)
            {
                foreach (var component in node.Components)
                {
                    foreach (var (_, value) in component.Fields)
                    {
                        AddReferencedTargets(value, node.LogicalId);
                    }
                }

                if (node is PrefabInstanceNode instance)
                {
                    foreach (var propertyOverride in instance.Overrides)
                    {
                        AddReferencedTargets(propertyOverride.Value, node.LogicalId);
                        if (propertyOverride.ObjectReference != null)
                        {
                            AddReferencedTargets(propertyOverride.ObjectReference, node.LogicalId);
                        }
                    }

                    foreach (var addedComponent in instance.AddedComponents)
                    {
                        foreach (var (_, value) in addedComponent.Component.Fields)
                        {
                            AddReferencedTargets(value, node.LogicalId);
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
                var entry = addedEntries.LastOrDefault(e => IdentityNodeIndex.IsNode(e) && e.GlobalObjectId == globalObjectId);
                return entry == null
                    ? null
                    : edits.OfType<AppendStatement>().FirstOrDefault(a => a.NewLogicalId == entry.LogicalId);
            }

            // The mapped arm of the retired `InjectExplicitId`: the unmapped (same-batch append) arm
            // is DEAD — a duplicate append already heads its own handle via DetectAppends' third
            // `headsHandle` clause, so a positional append never reaches this pass.
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

            // Keyless conflicts are EVENTS of this pass (unchanged meaning); a Conflict carrying a
            // RecurrenceKey is a STANDING condition of the scene+source that will recur on every
            // reconcile until the user changes one of them, so it is routed into Notes instead. The
            // same Conflict never appears in both arrays.
            return new ReconcileResult
            {
                Patch = new SourcePatch { Edits = edits.ToArray() },
                Conflicts = conflicts.Where(c => c.RecurrenceKey is null).ToArray(),
                AddedEntries = addedEntries.ToArray(),
                RemovedLogicalIds = removedLogicalIds.ToArray(),
                Skipped = skippedFields.ToArray(),
                AddedAssets = DedupAssetsByGuid(addedAssets),
                Notes = conflicts.Where(c => c.RecurrenceKey is not null).ToArray(),
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
            // D1: either side is a RectTransform -> AnchoredPosition owns X/Y, so `pos:` may only
            // ever carry a genuine Z drift. Held BEFORE the canonical comparison below so a UI
            // node's derived m_LocalPosition.x/y can never reach `.Transform(pos:)`.
            if (RectTransformDiff.Applies(model, snapshot))
            {
                snapshot = RectTransformDiff.HoldAnchoredXY(snapshot, model);
            }

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

        // Driven axes never sync scene->source: hold the source model's value on each driven
        // axis so it cannot differ, while free axes still reflect the scene. snapshot.DrivenChannels
        // is enabled-coupled; default None returns the snapshot unchanged for every non-spatial
        // node. The base-channel hold is shared with the code->scene direction
        // (RectTransformDiff.HoldForWrite) via DrivenTransform.HoldBase — never re-implement it here.
        private static TransformData MaskDriven(TransformData model, TransformData snapshot)
        {
            var d = snapshot.DrivenChannels;
            if (d == ChannelMask.None)
            {
                return snapshot;
            }

            // D5: per-axis rect-field driven hold, delegated to the shipped table-driven
            // implementation (never re-implement the loop here).
            return RectTransformFields.MaskDrivenRect(model, DrivenTransform.HoldBase(snapshot, model, d));
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
