using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Core.Reconcile
{
    // Snapshot+map-driven component pass for MAPPED owners (owner already has a GameObject
    // IdentityMap entry). Mirrors Reconciler.DetectAppends/DetectRemovals architecturally but
    // does NOT consume Differ's component ChangeOps (those are Materialize-directed: desired
    // side = source -> scene; Reconcile needs the inverse, scene -> source.
    internal static partial class ComponentReconciler
    {
        // Handles add/remove/reorder, the field-value loop, and the conflict path. Keep the
        // signature stable across all call sites.
        internal static void ReconcileComponents(
            string ownerLogicalId,
            ComponentData[] sourceComponents,
            ComponentData[] snapshotComponents,
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? componentAnchors,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            // Two scene-derived membership sets, built ONCE by Reconciler and threaded through
            // every call. sceneLiveTargets = "object exists in the current scene" (Detection 1, a
            // deleted source target). resolvableTargets = "reference can be honored to a real object"
            // (Detection 2, a phantom snapshot target) — a strict superset of sceneLiveTargets. The
            // two predicates are NOT interchangeable.
            ISet<string> sceneLiveTargets,
            ISet<string> resolvableTargets,
            // Same-batch create-candidate identities (unmapped snapshot GlobalObjectIds —
            // about to be mapped by DetectAppends THIS pass). A ref to one of these is PENDING, not
            // dangling: it resolves on the guaranteed second Sync. Distinct address space from
            // resolvableTargets (LogicalIds) — see Reconciler.cs pendingTargets build site.
            ISet<string> pendingTargets,
            // Reconciler.ResolveOwnerHandle — the ONE handle-introduction path, shared with reparent
            // and child-append emission. Deriving a handle locally here instead produced a SECOND
            // name for an owner another edit had already named, and a second Introduce flag the
            // applier rejects. Side-effecting: call it only when about to emit.
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<string> removedLogicalIds,
            List<Conflict> conflicts,
            List<SkippedField> skippedFields,
            List<AssetEntry> addedAssets,
            // Catalogued AssetRef fields render as their typed `Assets.<...>` member chain
            // instead of `Asset("path")`. Optional/trailing so every pre-existing call site/test
            // stays green unchanged.
            AssetCatalog? assetCatalog = null,
            // LogicalIds of this owner's components whose source construct is a chained
            // call (Reconciler.cs's converted ParseResult.ChainedComponents). `null` = "no source
            // information" (every hand-built test call), identical contract to componentAnchors.
            ISet<string>? chainedComponents = null,
            // The ONE per-type default-field index (ComponentDefaultOmission.Index.Build,
            // built ONCE per Reconciler.Reconcile), threaded through to EmitComponentAppend and the
            // introduce branch below. `null` = Index.Empty semantics — keep every field (every
            // hand-built test call that doesn't supply ComponentDefaults stays green unchanged).
            ComponentDefaultOmission.Index? defaults = null,
            // m8: componentLogicalId -> the authored `.Ref<T>()` var name, and, per component+field,
            // the ORDERED per-listener call spans — Reconciler.Reconcile's own trailing params,
            // threaded straight through. BOTH must be non-null for the UnityEventListeners intercept
            // below to emit a patch/append/remove delta; either null keeps the report-only
            // behavior described below (every pre-existing call/test stays green unchanged).
            IReadOnlyDictionary<string, string>? componentHandles = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>? listenerCallSpans = null,
            // The per-TYPE public C# spelling of a serialized UnityEvent field other than
            // `m_OnClick` (Reconciler.Reconcile builds this ONCE from `actual.MemberSpellings`) — an
            // `.OnEvent(...)`-form listener with no entry here is UnsyncableListener
            // (UnresolvedEventMemberName), never guessed. `null` = Empty (every hand-built test call
            // that doesn't supply MemberSpellings stays green unchanged).
            MemberSpellingIndex? memberSpellings = null)
        {
            // Canonicalize FitSize-before-AlignTo BEFORE the ADD/REORDER passes so both emit
            // in canonical order and the REORDER pass compares canonical-vs-canonical for the
            // spatial pair (never churns on live GetComponents order).
            var snapshotComps = SpatialComponentSource.OrderForEmit(ExcludeTransform(snapshotComponents));
            var snapshotKeys = ComputeComponentKeys(snapshotComps);
            var snapshotKeySet = new HashSet<(string TypeFullName, int Ordinal)>(snapshotKeys);

            // Managed Component entries already represented on this owner, grouped by type;
            // entry index within its group == its ordinal (mirrors Differ.cs:178-181,223).
            var managedEntriesByType = identityMap.Entries
                .Where(e => e.Kind == "Component" && e.ParentLogicalId == ownerLogicalId)
                .GroupBy(e => e.ComponentType ?? "")
                .ToDictionary(g => g.Key, g => g.ToList());

            var managedKeySet = new HashSet<(string TypeFullName, int Ordinal)>();
            foreach (var group in managedEntriesByType)
            {
                for (var ordinal = 0; ordinal < group.Value.Count; ordinal++)
                {
                    managedKeySet.Add((group.Key, ordinal));
                }
            }

            // A snapshot component key that ALSO has a source-model counterpart at the same
            // (type,ordinal) is handled precisely by the (4) FIELD-VALUE DIFF pass below (which
            // harvests only fields that actually changed). Guards the (1) ADD harvest below from
            // double-counting/leaking identity-equal asset refs when the IdentityMap simply
            // hasn't recorded this component's entry yet (edit emission is unaffected).
            // The REORDER pass (3) below compares source physical order against
            // snapshotComps' CANONICAL (FitSize-before-AlignTo) order — so source must be canonicalized
            // identically, or an untouched node whose live GetComponents() order simply differs from
            // canonical (e.g. authored MeshFilter/MeshRenderer/FitSize/AlignTo) spuriously looks
            // reordered every sync (a no-op churn the applier can't actually apply to a fluent chain,
            // surfacing as the "convergence defect" byte-identical-patch guard). Matches the
            // "REORDER pass compares canonical-vs-canonical for the spatial pair" intent above.
            var sourceComps = SpatialComponentSource.OrderForEmit(ExcludeTransform(sourceComponents));
            var sourceKeys = ComputeComponentKeys(sourceComps);
            var sourceKeySet = new HashSet<(string TypeFullName, int Ordinal)>(sourceKeys);

            // (1) ADD: snapshot component with no managed Component entry at its (type,ordinal).
            // The owner handle is resolved LAZILY — on the first component actually being emitted —
            // because resolving it registers an introduction, and an owner with nothing to add must
            // not acquire a handle it never uses.
            string? ownerHandle = null;
            var introduceOwnerHandle = false;
            var ownerHandleResolved = false;

            for (var i = 0; i < snapshotComps.Length; i++)
            {
                var key = snapshotKeys[i];
                if (managedKeySet.Contains(key))
                {
                    continue;
                }

                // A component ALSO present in source (but with no managed Component entry yet) is
                // authored by the (4) FIELD-VALUE DIFF pass below, which rewrites its EXISTING
                // `.Component<T>()` construct in place. Appending a statement here too authors the
                // SAME component a second time (the double-emit defect) — the diff pass owns it, so
                // only a component ABSENT from source needs a new append statement here.
                if (sourceKeySet.Contains(key))
                {
                    continue;
                }

                var harvestSink = sourceKeySet.Contains(key) ? null : addedAssets;

                if (!ownerHandleResolved)
                {
                    (ownerHandle, introduceOwnerHandle) = resolveOwnerHandle(ownerLogicalId);
                    ownerHandleResolved = true;
                }

                EmitComponentAppend(
                    ownerLogicalId,
                    ownerHandle ?? ownerLogicalId,
                    key.TypeFullName,
                    key.Ordinal,
                    i,
                    snapshotComps[i].Fields,
                    resolveOwnerHandle,
                    resolvableTargets,
                    pendingTargets,
                    conflicts,
                    // This component is ALSO present in source at the same key precisely when
                    // the FIELD-VALUE DIFF pass (4) below is ALSO about to run for it (the identical
                    // sourceKeySet.Contains(key) test as `harvestSink` above) — that pass already
                    // reports every dangling field of an in-source component, so this path must not
                    // double-report. A genuinely-new object (DetectAppends) never has this overlap.
                    reportUnresolvable: !sourceKeySet.Contains(key),
                    ownerHandle,
                    introduceOwnerHandle,
                    edits,
                    addedEntries,
                    harvestSink,
                    assetCatalog,
                    defaults);

                // Only the FIRST component statement carries the introduction.
                introduceOwnerHandle = false;
            }

            // (2) REMOVE: managed Component entry with no snapshot component at its (type,ordinal).
            foreach (var group in managedEntriesByType)
            {
                for (var ordinal = 0; ordinal < group.Value.Count; ordinal++)
                {
                    var key = (TypeFullName: group.Key, Ordinal: ordinal);
                    if (snapshotKeySet.Contains(key))
                    {
                        continue;
                    }

                    var entry = group.Value[ordinal];

                    if (componentAnchors != null && !componentAnchors.ContainsKey(entry.LogicalId))
                    {
                        conflicts.Add(ConflictDetector.UnanchorableComponentEdit(entry.LogicalId, "remove"));
                        continue;
                    }

                    edits.Add(new RemoveStatement { Anchor = entry.LogicalId });
                    removedLogicalIds.Add(entry.LogicalId);
                }
            }

            // (3) REORDER: only when the represented set is unchanged (no add/remove above), AND
            // No source component on this owner is a CHAINED call — its physical order is
            // partly frozen inside another statement (a chained component's
            // anchor resolves to its ENCLOSING statement, so ReorderStatement's absolute component
            // index would move a SIBLING scene-graph statement instead), so the owner's absolute
            // component order is unrepresentable and no reorder is emitted for it at all — never a
            // partial/best-effort one. Same family, same shape as the canonical-order guard
            // this condition already carries.
            if (managedKeySet.SetEquals(snapshotKeySet) && !HasChainedComponent(sourceComps, chainedComponents))
            {
                var sourceIndexByKey = new Dictionary<(string TypeFullName, int Ordinal), int>();
                for (var i = 0; i < sourceComps.Length; i++)
                {
                    sourceIndexByKey[sourceKeys[i]] = i;
                }

                for (var j = 0; j < snapshotComps.Length; j++)
                {
                    var key = snapshotKeys[j];
                    if (sourceIndexByKey.TryGetValue(key, out var sourceIndex) && sourceIndex != j)
                    {
                        var reorderedLogicalId = sourceComps[sourceIndex].LogicalId;

                        if (componentAnchors != null && !componentAnchors.ContainsKey(reorderedLogicalId))
                        {
                            conflicts.Add(ConflictDetector.UnanchorableComponentEdit(reorderedLogicalId, "reorder"));
                            continue;
                        }

                        edits.Add(new ReorderStatement { Anchor = reorderedLogicalId, NewSiblingIndex = j });
                    }
                }
            }

            // (4) FIELD-VALUE DIFF: components matched by key in BOTH source model and snapshot.
            // Orthogonal to add/remove/reorder above — runs for every matched pair regardless.
            var sourceByKey = new Dictionary<(string TypeFullName, int Ordinal), ComponentData>();
            for (var i = 0; i < sourceComps.Length; i++)
            {
                sourceByKey[sourceKeys[i]] = sourceComps[i];
            }

            for (var i = 0; i < snapshotComps.Length; i++)
            {
                if (!sourceByKey.TryGetValue(snapshotKeys[i], out var sourceComp))
                {
                    continue;
                }

                var snapshotComp = snapshotComps[i];

                // An AlignTo axis renders as ONE argument (`z: AxisAlign.AbutMax.Offset(0.75f)`) but
                // is carried as TWO fields (mode + offset), reconciled as a unit (see
                // TryReconcileAlignAxis) so an introduced/edited offset never leaks a bare `zOffset:`.
                var handledAlignAxes = new HashSet<string>();

                foreach (var (fieldKey, snapVal) in snapshotComp.Fields)
                {
                    if (TryReconcileAlignAxis(
                        sourceComp, snapshotComp, fieldKey, fieldArgumentSpans, handledAlignAxes, edits, conflicts))
                    {
                        continue;
                    }

                    // A `[SerializeReference]` field authored via `.SetRef(...)` is owned entirely
                    // by the managed-ref intercept, before EVERY other branch below — including the
                    // default-reset branch, whose applier has no form for a `.SetRef(...)` statement
                    // and would throw if a live-null managed ref reached it as an at-default `.Set`
                    // field (see ComponentReconciler.ManagedRef.cs).
                    if (TryReconcileManagedRefField(
                        snapVal, sourceComp, fieldKey, fieldArgumentSpans, resolveOwnerHandle, ownerLogicalId,
                        assetCatalog, edits, conflicts, addedAssets))
                    {
                        continue;
                    }

                    // m8: a listener target names a component LogicalId, never a GameObject one —
                    // the generic ObjectRef render below (and its Unsupported/dangling handling)
                    // does not apply. Intercepted before every other check so a listener field
                    // never reaches them. Equal to source -> no-op (idempotence). Differing, WITH
                    // source information (componentHandles + listenerCallSpans both supplied) ->
                    // EmitListenerFieldDelta emits the index-matched patch/append/remove delta
                    // (ComponentReconciler.Listeners.cs). Differing, with NEITHER supplied -> the
                    // pre-delta report-only behavior: every Unsyncable listener gets one located,
                    // recurrence-keyed UnsyncableListener report, never a patch and never the
                    // generic DanglingReference/UnrepresentableListItem kind.
                    if (snapVal is ValueNode.UnityEventListeners snapListeners)
                    {
                        if (sourceComp.Fields.TryGetValue(fieldKey, out var srcListenerVal)
                            && Equals(srcListenerVal, snapVal))
                        {
                            continue;
                        }

                        if (componentHandles != null && listenerCallSpans != null)
                        {
                            EmitListenerFieldDelta(
                                sourceComp, snapListeners, fieldKey, memberSpellings,
                                componentHandles, listenerCallSpans, resolvableTargets, pendingTargets,
                                assetCatalog, edits, conflicts, addedAssets);
                            continue;
                        }

                        for (var listenerIndex = 0; listenerIndex < snapListeners.Listeners.Count; listenerIndex++)
                        {
                            var listener = snapListeners.Listeners[listenerIndex];
                            var resolution = ClassifyListenerTarget(
                                listener.Target, listener.MethodName, resolvableTargets, pendingTargets);
                            if (resolution.Kind == ListenerTargetKind.Unsyncable)
                            {
                                conflicts.Add(Conflict.UnsyncableListener(
                                    sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, listenerIndex,
                                    resolution.Reason!.Value));
                            }
                        }

                        continue;
                    }

                    if (snapVal is ValueNode.Unsupported)
                    {
                        // Never overwrite; suppresses a would-be introduce too.
                        skippedFields.Add(new SkippedField
                        {
                            LogicalId = sourceComp.LogicalId,
                            Path = fieldKey,
                            Reason = "Unsupported",
                        });

                        continue;
                    }

                    // The type's constructed default for this field, when known — the ONE basis both
                    // the comparison below and the emitted value's reduction share, per spec 32 C4.
                    var fieldDefault = defaults?.TryGetDefault(sourceComp.Type.FullName, fieldKey);

                    if (sourceComp.Fields.TryGetValue(fieldKey, out var srcVal))
                    {
                        if (Equals(NestedValueEmission.Emittable(srcVal, fieldDefault), NestedValueEmission.Emittable(snapVal, fieldDefault))
                            && AuthoredTextIsCurrent(srcVal, snapVal))
                        {
                            // Nothing to patch, but a Nested field whose projection excludes every
                            // member still owes its standing note here -- an Unsupported member's
                            // marker can never prove the live value UNCHANGED, so this branch would
                            // otherwise be the one place that decision is never reached at all.
                            if (snapVal is ValueNode.Nested)
                            {
                                NestedValueEmission.Project(snapVal, fieldDefault)
                                    .IsEmptyNested(sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, conflicts);
                            }

                            continue;
                        }

                        // Detection 1: a target named at ANY depth (bare, list item, nested member)
                        // by the source vanished from the scene (Unity nulls the field slot when its
                        // referenced GameObject is deleted) -> one located DanglingReference conflict
                        // per vanished target, NEVER a silent NodeHandle.None patch. A target that IS
                        // still live falls through to the ordinary None-patch below (legit clear).
                        var vanishedTargets = ObjectRefValues.VanishedTargets(srcVal, snapVal, sceneLiveTargets).ToList();
                        if (vanishedTargets.Count > 0)
                        {
                            foreach (var vanishedTarget in vanishedTargets)
                            {
                                conflicts.Add(ConflictDetector.DanglingReference(
                                    sourceComp.LogicalId, fieldKey, vanishedTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            }

                            continue;
                        }

                        // Detection 2 (handled through the shared classifier): the
                        // snapshot points at a target that resolves to nothing (no live map entry, no
                        // model node, no known handle) -> a located DanglingReference conflict, never
                        // a phantom-handle render. A same-batch create-candidate (not yet mapped,
                        // about to be appended THIS pass) is PENDING, not dangling — suppress both the
                        // patch and the conflict and converge quietly on the guaranteed second Sync.
                        var fieldValueDiffClassification = ClassifySnapshotRef(
                            NestedValueEmission.Emittable(snapVal, fieldDefault), resolvableTargets, pendingTargets);
                        if (fieldValueDiffClassification.Resolution == RefResolution.Unemittable)
                        {
                            conflicts.Add(ConflictDetector.UnrepresentableListItem(
                                sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        if (fieldValueDiffClassification.Resolution == RefResolution.Pending)
                        {
                            continue;
                        }

                        if (fieldValueDiffClassification.Resolution == RefResolution.Dangling)
                        {
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, fieldValueDiffClassification.DanglingTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        // The live value returned to the type's constructed default — remove
                        // the `.Set(...)` from source rather than patch it to the default literal
                        // (spec 32 C2). ONE decision function owns not-default / closed-grammar /
                        // span-present / span-absent; a `true` result fully handles this field.
                        if (ComponentDefaultOmission.TryEmitDefaultReset(
                            sourceComp, fieldKey, snapVal, defaults, fieldArgumentSpans, edits, conflicts))
                        {
                            continue;
                        }

                        if (fieldArgumentSpans != null
                            && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                            && compSpans.TryGetValue(fieldKey, out var valueSpan))
                        {
                            // Render only the members that differ from the type's constructed default
                            // (spec 32 C4) — never the live value's full member set. The located
                            // conflict for an excluded member is raised only now that this pass has
                            // committed to emitting the rest of the value, never during projection.
                            var projection = NestedValueEmission.Project(snapVal, fieldDefault);
                            var emittedVal = projection.Value;

                            // A spatial enum-axis flip (AlignTo xMode/yMode/zMode: AbutMax->AbutMin)
                            // patches the WHOLE-argument span (see BuilderParser.Spatial) with the
                            // authoring keyword form `up: true` via RenderKeyValue — the keyword itself
                            // carries the member, so the value-only ValueNodeLiteral would splice an
                            // invalid `AlignTo+Mode.AbutMin` FQN into the `y:` slot.
                            var patchExpr = SpatialComponentSource.IsSpatial(sourceComp.Type.FullName) && emittedVal is ValueNode.Enum
                                ? SpatialComponentSource.RenderKeyValue(fieldKey, emittedVal, string.Empty)
                                : RenderFieldValue(emittedVal, sourceComp.Type.FullName, resolveOwnerHandle, edits, ownerLogicalId, assetCatalog);
                            edits.Add(new PatchComponentField
                            {
                                Anchor = sourceComp.LogicalId,
                                ValueSpan = valueSpan,
                                NewExpr = patchExpr,
                            });

                            projection.ReportExclusions(sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, conflicts);
                            CollectAssetEntries(emittedVal, addedAssets);
                        }
                        else if (fieldArgumentSpans != null)
                        {
                            // Span absent but the editor supplied field-argument-span data:
                            // non-localizable to a single source construct -> conflict, never
                            // silently dropped.
                            conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"patch field '{fieldKey}'"));
                        }

                        // fieldArgumentSpans == null: legacy no-op, no conflict.
                    }
                    else if (snapVal is ValueNode.Nested)
                    {
                        // Newly-detected NESTED field: present in snapshot, absent from source. Its
                        // default-ness is decided per-MEMBER (spec 32 C4) via Project rather than
                        // whole-node equality -- an Unsupported member carries no observable value
                        // (its marker text never encodes the live value), so whole-node equality can
                        // never prove it unchanged. A field that projects to nothing is never
                        // introduced; IsEmptyNested still reports a standing note when it excluded a
                        // member, so a struct with no representable member is never dropped in silence.
                        var introducedProjection = NestedValueEmission.Project(snapVal, fieldDefault);
                        if (introducedProjection.IsEmptyNested(
                            sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, conflicts))
                        {
                            continue;
                        }

                        var introducedVal = introducedProjection.Value;

                        // A Nested field whose own member carries a reference (bare, list item)
                        // classifies the same as every other introduced value below — resolvable
                        // pre-renders a handle-aware NewExpr (ValueNodeLiteral has no ObjectRef arm
                        // and would throw), pending defers silently, dangling/unemittable report a
                        // located conflict and emit nothing.
                        var nestedIntroduceClassification = ClassifySnapshotRef(introducedVal, resolvableTargets, pendingTargets);
                        if (nestedIntroduceClassification.Resolution == RefResolution.Pending)
                        {
                            continue;
                        }

                        if (nestedIntroduceClassification.Resolution == RefResolution.Dangling)
                        {
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, nestedIntroduceClassification.DanglingTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        if (nestedIntroduceClassification.Resolution == RefResolution.Unemittable)
                        {
                            conflicts.Add(ConflictDetector.UnrepresentableListItem(
                                sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        edits.Add(new IntroduceComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            FieldKey = fieldKey,
                            Value = introducedVal,
                            NewExpr = nestedIntroduceClassification.Resolution == RefResolution.Resolvable
                                ? RenderFieldValue(introducedVal, sourceComp.Type.FullName, resolveOwnerHandle, edits, ownerLogicalId, assetCatalog)
                                : null,
                        });

                        introducedProjection.ReportExclusions(sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, conflicts);

                        CollectAssetEntries(introducedVal, addedAssets);
                    }
                    else
                    {
                        // Newly-detected field: present in snapshot, absent from source. A
                        // field being newly AUTHORED that equals the type's constructed default is
                        // omitted entirely — never introduced (this never applies to a field already
                        // present in source, only to what's about to be newly authored).
                        if (defaults != null && defaults.IsDefault(sourceComp.Type.FullName, fieldKey, snapVal))
                        {
                            continue;
                        }

                        // Newly-detected field: present in snapshot, absent from source. An ObjectRef
                        // reached at any depth here must classify the same as Detection 2 above —
                        // resolvable/null pre-renders a handle-aware NewExpr (the applier's
                        // ValueNodeLiteral has no ObjectRef arm and would throw), pending defers
                        // silently, dangling reports a located conflict and emits nothing.
                        var introduceClassification = ClassifySnapshotRef(
                            NestedValueEmission.Emittable(snapVal, fieldDefault), resolvableTargets, pendingTargets);
                        if (introduceClassification.Resolution == RefResolution.Unemittable)
                        {
                            conflicts.Add(ConflictDetector.UnrepresentableListItem(
                                sourceComp.LogicalId, sourceComp.Type.FullName, fieldKey, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        if (introduceClassification.Resolution == RefResolution.Pending)
                        {
                            continue;
                        }

                        if (introduceClassification.Resolution == RefResolution.Dangling)
                        {
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, introduceClassification.DanglingTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        edits.Add(new IntroduceComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            FieldKey = fieldKey,
                            Value = snapVal,
                            NewExpr = introduceClassification.Resolution == RefResolution.Resolvable
                                ? RenderFieldValue(snapVal, sourceComp.Type.FullName, resolveOwnerHandle, edits, ownerLogicalId, assetCatalog)
                                : null,
                        });

                        CollectAssetEntries(snapVal, addedAssets);
                    }
                }
            }
        }

        // Whether ANY of this owner's SOURCE components is a chained call — `chained` is
        // `null` for "no source information" (every hand-built test model), which is never
        // treated as chained (identical null contract to componentAnchors elsewhere in this file).
        private static bool HasChainedComponent(ComponentData[] sourceComps, ISet<string>? chained)
        {
            if (chained == null)
            {
                return false;
            }

            foreach (var component in sourceComps)
            {
                if (chained.Contains(component.LogicalId))
                {
                    return true;
                }
            }

            return false;
        }

        // The ONE place the liveness/pending/dangling/unemittable decision for a snapshot field
        // value is made — present-field Detection 2, both introduce branches, and every append-emit
        // site (SnapshotFieldEmission.EmitFieldSet) route through this instead of re-inlining
        // `!resolvableTargets.Contains(...) / pendingTargets.Contains(...)` a fourth time. Internal
        // (not private): SnapshotFieldEmission is its other caller.
        internal enum RefResolution { NotObjectRef, Resolvable, Pending, Dangling, Unemittable }

        // Verdict precedence over EVERY ref reached in `snapVal`, at any depth: a List reaching an
        // item with no compiling emission form => Unemittable, checked FIRST and independent of
        // whether the value carries an ObjectRef at all (a list of only unmappable items has none);
        // else no ref at all => NotObjectRef; any unresolvable-and-not-pending target => Dangling
        // (with the FIRST such target named, pre-order); else any pending target => Pending; else
        // Resolvable. Dangling outranks Pending because dangling is a permanent condition owing a
        // loud report, pending is transient and self-heals on the guaranteed second sync.
        internal static (RefResolution Resolution, string? DanglingTarget) ClassifySnapshotRef(
            ValueNode snapVal, ISet<string> resolvableTargets, ISet<string> pendingTargets)
        {
            if (ListValueEmission.HasUnemittableItem(snapVal))
            {
                return (RefResolution.Unemittable, null);
            }

            // A bare (non-list) Unsupported with no compiling emission form: a naked scene-read
            // sentinel that can only bind to an in-scope symbol the generated builder never
            // declares. Checked before the ObjectRef scan below since it is not an ObjectRef at all.
            if (snapVal is ValueNode.Unsupported bareUnsupported
                && ListValueEmission.IsUnemittableUnsupported(bareUnsupported))
            {
                return (RefResolution.Unemittable, null);
            }

            if (!ObjectRefValues.Contains(snapVal))
            {
                return (RefResolution.NotObjectRef, null);
            }

            var anyPending = false;
            foreach (var (_, r) in ObjectRefValues.Enumerate(snapVal))
            {
                var target = r.TargetLogicalId;
                if (target == null || resolvableTargets.Contains(target))
                {
                    continue;
                }

                if (pendingTargets.Contains(target))
                {
                    anyPending = true;
                    continue;
                }

                return (RefResolution.Dangling, target);
            }

            return anyPending ? (RefResolution.Pending, null) : (RefResolution.Resolvable, null);
        }

        // Reconcile-side per-listener target resolution: reads the target KIND off the already-
        // Classify-produced snapshot ValueNode and delegates the ObjectRef membership decision to
        // ClassifySnapshotRef above (no parallel identity resolver).
        internal enum ListenerTargetKind { ResolvableScene, ResolvableAsset, Pending, Unsyncable }

        internal readonly record struct ListenerTargetResolution(
            ListenerTargetKind Kind, ValueNode? Resolved, ListenerReportReason? Reason);

        internal static ListenerTargetResolution ClassifyListenerTarget(
            ValueNode? target, string methodName, ISet<string> resolvableTargets, ISet<string> pendingTargets)
        {
            if (string.IsNullOrEmpty(methodName))
            {
                return new ListenerTargetResolution(ListenerTargetKind.Unsyncable, null, ListenerReportReason.EmptyMethodName);
            }

            if (target is null)
            {
                return new ListenerTargetResolution(ListenerTargetKind.Unsyncable, null, ListenerReportReason.MissingTarget);
            }

            if (target is ValueNode.AssetRef)
            {
                return new ListenerTargetResolution(ListenerTargetKind.ResolvableAsset, target, null);
            }

            // Whatever remains is an already-whole reference/marker — an ObjectRef, or the
            // read-side Unsupported marker (UnityEventListener.ValidateTarget allows no other
            // kind). Delegated to the shared reconcile-side ref resolver rather than a bespoke
            // ObjectRef type check: ObjectRefValues.Contains(target) (inside ClassifySnapshotRef)
            // is false for Unsupported, so it falls straight through to Unsyncable below.
            var classification = ClassifySnapshotRef(target, resolvableTargets, pendingTargets);
            return classification.Resolution switch
            {
                RefResolution.Resolvable => new ListenerTargetResolution(ListenerTargetKind.ResolvableScene, target, null),
                // A same-batch new target: no LogicalId and no handle exist for it yet — defer
                // rather than render (would emit the raw goid as a variable name) or report
                // (it converges on the guaranteed second Sync once DetectAppends maps it).
                RefResolution.Pending => new ListenerTargetResolution(ListenerTargetKind.Pending, target, null),
                _ => new ListenerTargetResolution(ListenerTargetKind.Unsyncable, null, ListenerReportReason.UnresolvedTarget),
            };
        }

        // Gate for the EmitComponentAppend pre-render branch above — a project AssetRef whose
        // (Guid, FileId) resolves in the catalog must be pre-rendered into FieldExpressions (its typed
        // member chain), same as an ObjectRef. Built-ins never hit the reverse lookup.
        internal static bool IsCataloguedAssetRef(ValueNode value, AssetCatalog? assetCatalog) =>
            value is ValueNode.AssetRef(var r)
            && r is { IsBuiltin: false }
            && assetCatalog?.TryGetMember(r.Guid, SourceExpr.CatalogLookupFileId(r), out _, out _, out _) == true;

        // THE ONE gate for "this field must be pre-rendered into FieldExpressions instead of being
        // left in Fields for SourceExpr.ValueNodeLiteral to format at apply time" — an ObjectRef
        // reached at any depth needs a handle table, and a catalogued AssetRef needs the catalog;
        // neither belongs in ValueNodeLiteral's pure, context-free formatting.
        internal static bool NeedsPreRender(ValueNode value, AssetCatalog? assetCatalog) =>
            ObjectRefValues.Contains(value) || IsCataloguedAssetRef(value, assetCatalog);

        // The identical span lookup used by the span-based patch emission below, reused for a
        // DanglingReference conflict's Location. Emitted even when the span is absent (returns null) —
        // "never a silent null" outranks having a located span.
        // internal (not private): ComponentDefaultOmission.TryEmitDefaultReset reuses this
        // SAME lookup for its own conflict Location, rather than re-deriving it.
        internal static SourceSpan? DanglingFieldSpan(
            string componentLogicalId,
            string fieldKey,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans) =>
            fieldArgumentSpans != null
                && fieldArgumentSpans.TryGetValue(componentLogicalId, out var compSpans)
                && compSpans.TryGetValue(fieldKey, out var valueSpan)
                ? valueSpan
                : null;

        // Renders a changed field's replacement expression. SourceExpr.ValueNodeLiteral has
        // no ObjectRef arm (it is a pure, context-free formatter) because rendering an ObjectRef is
        // BOTH context-dependent (needs the handle table) and side-effecting (may need to introduce
        // a handle on the target) — neither belongs in a pure formatter, so this intercepts every
        // ObjectRef reached anywhere in `value` (bare, list item, nested member) before delegating
        // the rest to ValueNodeLiteral unchanged.
        // `ownerNodeLogicalId` is REQUIRED (no default), placed before the optional `assetCatalog`,
        // so a new call site fails to compile until it supplies the node the value is being written
        // onto — see SelfReferenceEmission for why: a self-target must render as the variable-free
        // `NodeHandle.Self` selector, never the owner's own local, which would be CS0841 inside the
        // statement that declares it.
        internal static string RenderFieldValue(
            ValueNode value,
            string typeFullName,
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits,
            string ownerNodeLogicalId,
            AssetCatalog? assetCatalog = null)
        {
            // A [SerializeReference] instance renders through its own formatter: a DIRECT
            // ObjectRef/AssetRef child is a real C# field assignment inside `new T { ... }` (not a
            // `.Set(...)` call argument), so the bare handle/asset token needs a `.As<T>()` cast to
            // the field's own declared type or the emitted initializer fails to compile — see
            // ManagedReferenceEmission for the one place that resolves and applies it.
            if (value is ValueNode.ManagedReference managedReference)
            {
                return ManagedReferenceEmission.Render(
                    managedReference, resolveOwnerHandle, edits, ownerNodeLogicalId, assetCatalog);
            }

            if (ObjectRefValues.Contains(value))
            {
                // The per-ref render function: replaces every ObjectRef reached in `value` at any
                // depth (bare, list item, nested member) with its rendered token, wrapped in
                // ValueNode.Unsupported so SourceExpr.ValueNodeLiteral's existing raw-token
                // passthrough renders the whole substituted tree with no new formatter and no
                // ObjectRef arm — a substituted list renders as `new[] { door, other }`, a
                // substituted bare ref renders as `door`.
                var substituted = ObjectRefValues.Substitute(value, (ValueNode.ObjectRef objectRef) =>
                    new ValueNode.Unsupported(
                        RenderObjectRefToken(objectRef.TargetLogicalId, resolveOwnerHandle, edits, ownerNodeLogicalId)));

                return SourceExpr.ValueNodeLiteral(substituted, assetCatalog);
            }

            // A FitSize/AlignTo field patch/introduce must render through the dedicated
            // formatter (SourceExpr.Float/Vec3Literal) so it stays byte-identical to the append
            // form — never the generic ValueNodeLiteral fallback.
            return SpatialComponentSource.IsSpatial(typeFullName)
                ? SpatialComponentSource.RenderFieldValue(value)
                : SourceExpr.ValueNodeLiteral(value, assetCatalog);
        }
    }
}
