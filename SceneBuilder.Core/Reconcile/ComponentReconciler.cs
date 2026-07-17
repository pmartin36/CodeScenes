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
    // side = source -> scene; Reconcile needs the inverse, scene -> source. See
    // b2-t1/research.md Verdict on assumptions).
    internal static class ComponentReconciler
    {
        // b2-t1 fills add/remove/reorder; b2-t2 adds the field-value loop; b2-t3 adds the
        // conflict path. Keep the signature stable across all three.
        internal static void ReconcileComponents(
            string ownerLogicalId,
            ComponentData[] sourceComponents,
            ComponentData[] snapshotComponents,
            IdentityMap identityMap,
            IReadOnlyDictionary<string, SourceSpan>? componentAnchors,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            // b4-t2: two scene-derived membership sets, built ONCE by Reconciler and threaded through
            // every call. sceneLiveTargets = "object exists in the current scene" (Detection 1, a
            // deleted source target). resolvableTargets = "reference can be honored to a real object"
            // (Detection 2, a phantom snapshot target) — a strict superset of sceneLiveTargets. See
            // research.md b4-t2 for why the two predicates are NOT interchangeable.
            ISet<string> sceneLiveTargets,
            ISet<string> resolvableTargets,
            // b4-t3: same-batch create-candidate identities (unmapped snapshot GlobalObjectIds —
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
            List<AssetEntry> addedAssets)
        {
            // b4-t1: canonicalize Sizer-before-Snapper BEFORE the ADD/REORDER passes so both emit
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
            // b7-t1 fix: the REORDER pass (3) below compares source physical order against
            // snapshotComps' CANONICAL (Sizer-before-Snapper) order — so source must be canonicalized
            // identically, or an untouched node whose live GetComponents() order simply differs from
            // canonical (e.g. authored MeshFilter/MeshRenderer/Sizer/Snapper) spuriously looks
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
                    // b4-t3: this component is ALSO present in source at the same key precisely when
                    // the FIELD-VALUE DIFF pass (4) below is ALSO about to run for it (the identical
                    // sourceKeySet.Contains(key) test as `harvestSink` above) — that pass already
                    // reports every dangling field of an in-source component, so this path must not
                    // double-report. A genuinely-new object (DetectAppends) never has this overlap.
                    reportUnresolvable: !sourceKeySet.Contains(key),
                    ownerHandle,
                    introduceOwnerHandle,
                    edits,
                    addedEntries,
                    harvestSink);

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

            // (3) REORDER: only when the represented set is unchanged (no add/remove above).
            if (managedKeySet.SetEquals(snapshotKeySet))
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
                foreach (var (fieldKey, snapVal) in snapshotComp.Fields)
                {
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

                    if (sourceComp.Fields.TryGetValue(fieldKey, out var srcVal))
                    {
                        if (Equals(srcVal, snapVal) && AuthoredTextIsCurrent(srcVal, snapVal))
                        {
                            continue;
                        }

                        // b4-t2 Detection 1: the source handle's target vanished from the scene (Unity
                        // nulls the field when its referenced GameObject is deleted) -> a located
                        // DanglingReference conflict, NEVER a silent NodeHandle.None patch. A target
                        // that IS still live falls through to the ordinary None-patch below (legit clear).
                        if (srcVal is ValueNode.ObjectRef(var srcTarget) && srcTarget != null
                            && snapVal is ValueNode.ObjectRef(null)
                            && !sceneLiveTargets.Contains(srcTarget))
                        {
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, srcTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        // b4-t2 Detection 2 (refactored b4-t3 onto the shared classifier): the
                        // snapshot points at a target that resolves to nothing (no live map entry, no
                        // model node, no known handle) -> a located DanglingReference conflict, never
                        // a phantom-handle render. A same-batch create-candidate (not yet mapped,
                        // about to be appended THIS pass) is PENDING, not dangling — suppress both the
                        // patch and the conflict and converge quietly on the guaranteed second Sync.
                        var fieldValueDiffResolution = ClassifySnapshotRef(snapVal, resolvableTargets, pendingTargets);
                        if (fieldValueDiffResolution == RefResolution.Pending)
                        {
                            continue;
                        }

                        if (fieldValueDiffResolution == RefResolution.Dangling)
                        {
                            var danglingTarget = ((ValueNode.ObjectRef)snapVal).TargetLogicalId;
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, danglingTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        if (fieldArgumentSpans != null
                            && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                            && compSpans.TryGetValue(fieldKey, out var valueSpan))
                        {
                            edits.Add(new PatchComponentField
                            {
                                Anchor = sourceComp.LogicalId,
                                ValueSpan = valueSpan,
                                NewExpr = RenderFieldValue(snapVal, sourceComp.Type.FullName, resolveOwnerHandle, edits),
                            });

                            CollectAssetEntries(snapVal, addedAssets);
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
                    else
                    {
                        // Newly-detected field: present in snapshot, absent from source. b4-t3
                        // (Finding 1): an ObjectRef here must classify the same as Detection 2 above —
                        // resolvable/null pre-renders a handle-aware NewExpr (the applier's
                        // ValueNodeLiteral has no ObjectRef arm and would throw), pending defers
                        // silently, dangling reports a located conflict and emits nothing.
                        var introduceResolution = ClassifySnapshotRef(snapVal, resolvableTargets, pendingTargets);
                        if (introduceResolution == RefResolution.Pending)
                        {
                            continue;
                        }

                        if (introduceResolution == RefResolution.Dangling)
                        {
                            var danglingTarget = ((ValueNode.ObjectRef)snapVal).TargetLogicalId;
                            conflicts.Add(ConflictDetector.DanglingReference(
                                sourceComp.LogicalId, fieldKey, danglingTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                            continue;
                        }

                        edits.Add(new IntroduceComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            FieldKey = fieldKey,
                            Value = snapVal,
                            NewExpr = introduceResolution == RefResolution.Resolvable
                                ? RenderFieldValue(snapVal, sourceComp.Type.FullName, resolveOwnerHandle, edits)
                                : null,
                        });

                        CollectAssetEntries(snapVal, addedAssets);
                    }
                }

                // b6-t1: a source-authored ObjectRef field that REGRESSED to its type default (null)
                // live is invisible to the loop above — SerializedFieldBridge default-filters a
                // null-valued reference field out of the snapshot entirely (indistinguishable from
                // "never touched" at that layer, which has no source-model context to tell the two
                // apart). Absent-from-snapshot + non-null in source can ONLY mean the live value is
                // now the type default (ObjectRef(null)): were it still authored-and-live, it would
                // compare equal above and never have been filtered; were it live-but-different, it
                // would be present in the snapshot and handled above. So this reconstructs the exact
                // same dangling-vs-clear branch (Detection 1) against the implied ObjectRef(null).
                foreach (var (fieldKey, srcVal) in sourceComp.Fields)
                {
                    if (snapshotComp.Fields.ContainsKey(fieldKey))
                    {
                        continue; // already handled by the loop above.
                    }

                    if (srcVal is not ValueNode.ObjectRef(var srcTarget) || srcTarget == null)
                    {
                        continue;
                    }

                    if (!sceneLiveTargets.Contains(srcTarget))
                    {
                        conflicts.Add(ConflictDetector.DanglingReference(
                            sourceComp.LogicalId, fieldKey, srcTarget, DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                        continue;
                    }

                    if (fieldArgumentSpans != null
                        && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var regressedCompSpans)
                        && regressedCompSpans.TryGetValue(fieldKey, out var regressedValueSpan))
                    {
                        edits.Add(new PatchComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            ValueSpan = regressedValueSpan,
                            NewExpr = "NodeHandle.None",
                        });
                    }
                    else if (fieldArgumentSpans != null)
                    {
                        conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"patch field '{fieldKey}'"));
                    }
                }
            }
        }

        // b4-t3: the ONE place the liveness/pending/dangling decision for a snapshot ObjectRef is
        // made — present-field Detection 2, the introduce-field branch, and the append loop all
        // route through this instead of re-inlining `!resolvableTargets.Contains(...) /
        // pendingTargets.Contains(...)` a fourth time.
        private enum RefResolution { NotObjectRef, Resolvable, Pending, Dangling }

        private static RefResolution ClassifySnapshotRef(
            ValueNode snapVal, ISet<string> resolvableTargets, ISet<string> pendingTargets)
        {
            if (snapVal is not ValueNode.ObjectRef(var target))
            {
                return RefResolution.NotObjectRef;
            }

            if (target == null || resolvableTargets.Contains(target))
            {
                return RefResolution.Resolvable;
            }

            return pendingTargets.Contains(target) ? RefResolution.Pending : RefResolution.Dangling;
        }

        // b4-t2: the identical span lookup used by the span-based patch emission below, reused for a
        // DanglingReference conflict's Location. Emitted even when the span is absent (returns null) —
        // "never a silent null" outranks having a located span (research.md CLEANLINESS).
        private static SourceSpan? DanglingFieldSpan(
            string componentLogicalId,
            string fieldKey,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans) =>
            fieldArgumentSpans != null
                && fieldArgumentSpans.TryGetValue(componentLogicalId, out var compSpans)
                && compSpans.TryGetValue(fieldKey, out var valueSpan)
                ? valueSpan
                : null;

        // b4-t1: renders a changed field's replacement expression. SourceExpr.ValueNodeLiteral has
        // no ObjectRef arm (it is a pure, context-free formatter) because rendering an ObjectRef is
        // BOTH context-dependent (needs the handle table) and side-effecting (may need to introduce
        // a handle on the target) — neither belongs in a pure formatter, so this intercepts
        // ValueNode.ObjectRef before delegating everything else to ValueNodeLiteral unchanged.
        private static string RenderFieldValue(
            ValueNode value,
            string typeFullName,
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            List<SourceEdit> edits)
        {
            if (value is ValueNode.ObjectRef(var targetLogicalId))
            {
                if (targetLogicalId == null)
                {
                    return "NodeHandle.None";
                }

                var (handle, introduce) = resolveOwnerHandle(targetLogicalId);
                if (introduce && handle != null)
                {
                    edits.Add(new IntroduceHandle { Anchor = targetLogicalId, Handle = handle });
                }

                return handle ?? targetLogicalId;
            }

            // b4-t2: a Sizer/Snapper field patch/introduce must render through the dedicated
            // formatter (SourceExpr.Float/Vec3Literal) so it stays byte-identical to the append
            // form — never the generic ValueNodeLiteral fallback.
            return SpatialComponentSource.IsSpatial(typeFullName)
                ? SpatialComponentSource.RenderFieldValue(value)
                : SourceExpr.ValueNodeLiteral(value);
        }

        // §13 one-pass attach (b4-t1) reuses this for the just-appended owner: same
        // `{owner}/{Type}#{ordinal}` id scheme, same AppendComponentStatement + Component
        // AddedEntry shape as the mapped-owner ADD path above — kept in exactly one place.
        internal static void EmitComponentAppend(
            // The owner's id in the CURRENT source — what the applier resolves its statement by.
            string ownerAnchor,
            // The owner's id AFTER this batch. These diverge exactly when a handle is introduced for
            // a handle-less owner: the rewrite makes the `var` name its LogicalId, so BuilderParser
            // will key the new component "{handle}/{Type}#{ordinal}". Keying it off the ANCHOR instead
            // strands the component on an id the re-parsed source no longer contains.
            string ownerEffectiveId,
            string typeFullName,
            int ordinal,
            // Index among the owner's representable components — where the statement must be PLACED.
            // Distinct from `ordinal`, which counts only same-TYPED components and keys the id.
            int siblingIndex,
            FieldMap fields,
            // b4-t3: reused verbatim from RenderFieldValue below — resolves/introduces a handle for
            // an ObjectRef field's target. Side-effecting; called only for a field actually emitted.
            System.Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            ISet<string> resolvableTargets,
            // b4-t3: same-batch create-candidate identities — a field targeting one of these is
            // PENDING, not dangling (converges on the guaranteed second Sync). Same set
            // ReconcileComponents/ClassifySnapshotRef consult; not a second liveness notion.
            ISet<string> pendingTargets,
            // b4-t3: only appended to when `reportUnresolvable` — see below.
            List<Conflict> conflicts,
            // b4-t3: true only when this append has NO overlapping FIELD-VALUE DIFF pass to report a
            // dangling field for it (a genuinely-new object, DetectAppends). False for the
            // mapped-owner ADD-path call when the component is ALSO in source, because that overlap's
            // dangling fields are already reported by Detection 2 / the introduce branch — reporting
            // here too would double-report the SAME field.
            bool reportUnresolvable,
            string? ownerHandle,
            bool introduceOwnerHandle,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<AssetEntry>? addedAssets)
        {
            var componentLogicalId = $"{ownerEffectiveId}/{typeFullName}#{ordinal}";

            // b4-t3: pre-render every ObjectRef field's expression at EMIT time (mirroring
            // RenderFieldValue's field-diff use below) instead of leaving it in Fields for
            // SourceExpr.ValueNodeLiteral to throw on at apply time. `filteredFields` stays null
            // (reuse `fields` unchanged) unless a field is actually dropped.
            List<KeyValuePair<string, ValueNode>>? filteredFields = null;
            Dictionary<string, string>? fieldExpressions = null;

            for (var i = 0; i < fields.Count; i++)
            {
                var (fieldKey, value) = fields[i];
                var resolution = ClassifySnapshotRef(value, resolvableTargets, pendingTargets);

                if (resolution == RefResolution.Dangling)
                {
                    // Genuinely unknown THIS pass — never a bogus handle, never {fileID:0}, never a
                    // thrown NotSupportedException. Omit the field; report it (never a permanent
                    // silent null) unless the FIELD-VALUE DIFF pass already owns this component's
                    // dangling report.
                    filteredFields ??= new List<KeyValuePair<string, ValueNode>>(fields.Take(i));
                    if (reportUnresolvable)
                    {
                        var targetId = ((ValueNode.ObjectRef)value).TargetLogicalId;
                        conflicts.Add(ConflictDetector.DanglingReference(componentLogicalId, fieldKey, targetId, null));
                    }

                    continue;
                }

                if (resolution == RefResolution.Pending)
                {
                    // A same-batch create-candidate not yet mapped — omit and converge on the
                    // guaranteed second Sync, by which point the target is mapped and the ref wires
                    // through the ordinary FIELD-VALUE DIFF pass (b4-t1/b4-t3) as a ONE-OFF patch.
                    filteredFields ??= new List<KeyValuePair<string, ValueNode>>(fields.Take(i));
                    continue;
                }

                filteredFields?.Add(new KeyValuePair<string, ValueNode>(fieldKey, value));

                if (value is ValueNode.ObjectRef)
                {
                    fieldExpressions ??= new Dictionary<string, string>();
                    fieldExpressions[fieldKey] = RenderFieldValue(value, typeFullName, resolveOwnerHandle, edits);
                }
            }

            var emittedFields = filteredFields != null ? new FieldMap(filteredFields) : fields;

            edits.Add(new AppendComponentStatement
            {
                Anchor = ownerAnchor,
                ComponentLogicalId = componentLogicalId,
                TypeFullName = typeFullName,
                NewSiblingIndex = siblingIndex,
                Fields = emittedFields,
                FieldExpressions = fieldExpressions,
                OwnerHandle = ownerHandle,
                IntroduceOwnerHandle = introduceOwnerHandle,
            });

            addedEntries.Add(new IdentityMapEntry
            {
                LogicalId = componentLogicalId,
                GlobalObjectId = "",
                Kind = "Component",
                ComponentType = typeFullName,
                ParentLogicalId = ownerEffectiveId,
            });

            if (addedAssets != null)
            {
                foreach (var (_, value) in emittedFields)
                {
                    CollectAssetEntries(value, addedAssets);
                }
            }
        }

        // Asked ONLY of two values that already compare EQUAL: does the source's authored TEXT still
        // reflect the snapshot, or is it stale and in need of re-emission?
        //
        // It exists for exactly one case, by design. AssetRef identity is (Guid, FileId, IsBuiltin) —
        // DisplayPath and TypeHint are deliberately non-authoritative — so a MOVED/RENAMED asset (or a
        // built-in whose live-derived name/qualifier changed) is identity-EQUAL to its source ref while
        // the authored text in the source no longer reflects it. Equality alone would skip it and the
        // stale text would never be rewritten. Every other ValueNode kind determines its own emission,
        // so equality there already implies identical text and this returns true.
        //
        // The emitted text is a function of (DisplayPath, IsBuiltin, TypeHint): a built-in renders as
        // `Builtin("name")` / `Builtin("name", "Qualifier")` (SourceExpr.ValueNodeLiteral).
        //
        // The inverse case — text equal but identity different (same path, different sub-object fileId)
        // — is why this is an ADDITIONAL condition on top of Equals and never a replacement for it.
        private static bool AuthoredTextIsCurrent(ValueNode source, ValueNode snapshot)
        {
            switch (source, snapshot)
            {
                case (ValueNode.AssetRef(var a), ValueNode.AssetRef(var b)):
                    if (a is null || b is null)
                    {
                        return (a is null) == (b is null);
                    }

                    return string.Equals(a.DisplayPath, b.DisplayPath, System.StringComparison.Ordinal)
                        && a.IsBuiltin == b.IsBuiltin
                        && string.Equals(a.TypeHint, b.TypeHint, System.StringComparison.Ordinal);

                case (ValueNode.List la, ValueNode.List lb):
                    if (la.Items.Count != lb.Items.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < la.Items.Count; i++)
                    {
                        if (!AuthoredTextIsCurrent(la.Items[i], lb.Items[i]))
                        {
                            return false;
                        }
                    }

                    return true;

                case (ValueNode.Nested na, ValueNode.Nested nb):
                    foreach (var (key, value) in na.Fields)
                    {
                        if (!nb.Fields.TryGetValue(key, out var other) || !AuthoredTextIsCurrent(value, other))
                        {
                            return false;
                        }
                    }

                    return true;

                default:
                    return true;
            }
        }

        // b4-t1: single choke-point harvest of every populated AssetRef reachable from a
        // snapshot ValueNode flowing into an emitted source edit. Cleared (AssetRef(null)),
        // empty-Guid, and built-in refs contribute nothing — a built-in's DisplayPath is a
        // live-derived object name, never a project path, and has no place in the asset
        // sidecar cache. Recurses into List/Nested so no caller has to special-case container
        // shapes.
        internal static void CollectAssetEntries(ValueNode node, List<AssetEntry> sink)
        {
            switch (node)
            {
                case ValueNode.AssetRef(var r) when r != null && !r.IsBuiltin && !string.IsNullOrEmpty(r.Guid):
                    sink.Add(new AssetEntry { Guid = r.Guid, LastKnownPath = r.DisplayPath, TypeHint = r.TypeHint });
                    break;

                case ValueNode.List l:
                    foreach (var item in l.Items)
                    {
                        CollectAssetEntries(item, sink);
                    }

                    break;

                case ValueNode.Nested n:
                    foreach (var (_, value) in n.Fields)
                    {
                        CollectAssetEntries(value, sink);
                    }

                    break;
            }
        }

        internal static ComponentData[] ExcludeTransform(ComponentData[] components) =>
            components.Where(c => c.Type.FullName != "UnityEngine.Transform").ToArray();

        // Mirrors Differ.ComputeComponentKeys (Differ.cs:257-270) — Differ.cs is out of this
        // slice's touch scope, so the tiny ordinal helper is duplicated here rather than shared.
        internal static (string TypeFullName, int Ordinal)[] ComputeComponentKeys(ComponentData[] components)
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
    }
}
