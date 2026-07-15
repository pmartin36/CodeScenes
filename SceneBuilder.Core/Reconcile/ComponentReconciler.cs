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
            var snapshotComps = ExcludeTransform(snapshotComponents);
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
            var sourceComps = ExcludeTransform(sourceComponents);
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

                        if (fieldArgumentSpans != null
                            && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                            && compSpans.TryGetValue(fieldKey, out var valueSpan))
                        {
                            edits.Add(new PatchComponentField
                            {
                                Anchor = sourceComp.LogicalId,
                                ValueSpan = valueSpan,
                                NewExpr = SourceExpr.ValueNodeLiteral(snapVal),
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
                        // Newly-detected field: present in snapshot, absent from source.
                        edits.Add(new IntroduceComponentField
                        {
                            Anchor = sourceComp.LogicalId,
                            FieldKey = fieldKey,
                            Value = snapVal,
                        });

                        CollectAssetEntries(snapVal, addedAssets);
                    }
                }
            }
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
            string? ownerHandle,
            bool introduceOwnerHandle,
            List<SourceEdit> edits,
            List<IdentityMapEntry> addedEntries,
            List<AssetEntry>? addedAssets)
        {
            var componentLogicalId = $"{ownerEffectiveId}/{typeFullName}#{ordinal}";

            edits.Add(new AppendComponentStatement
            {
                Anchor = ownerAnchor,
                ComponentLogicalId = componentLogicalId,
                TypeFullName = typeFullName,
                NewSiblingIndex = siblingIndex,
                Fields = fields,
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
                foreach (var (_, value) in fields)
                {
                    CollectAssetEntries(value, addedAssets);
                }
            }
        }

        // Asked ONLY of two values that already compare EQUAL: does the source's authored TEXT still
        // reflect the snapshot, or is it stale and in need of re-emission?
        //
        // It exists for exactly one case, by design. AssetRef identity is (Guid, FileId) ONLY —
        // DisplayPath is deliberately non-authoritative — so a MOVED/RENAMED asset is identity-EQUAL to
        // its source ref while the authored path in the source text now points somewhere that no longer
        // exists. Equality alone would skip it and the stale path would never be rewritten. Every other
        // ValueNode kind determines its own emission, so equality there already implies identical text
        // and this returns true.
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

                    return string.Equals(a.DisplayPath, b.DisplayPath, System.StringComparison.Ordinal);

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
        // snapshot ValueNode flowing into an emitted source edit. Cleared (AssetRef(null))
        // and empty-Guid refs contribute nothing. Recurses into List/Nested so no caller has
        // to special-case container shapes.
        internal static void CollectAssetEntries(ValueNode node, List<AssetEntry> sink)
        {
            switch (node)
            {
                case ValueNode.AssetRef(var r) when r != null && !string.IsNullOrEmpty(r.Guid):
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
