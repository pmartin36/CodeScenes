using System;
using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // A `[SerializeReference]` field authored via `.SetRef(...)` is owned entirely here, before the
    // generic FIELD-VALUE DIFF pass in ComponentReconciler.cs ever sees it: a type change, a
    // field-only edit and a null<->non-null transition all re-render the WHOLE `.SetRef` value span
    // (no member-level patch — the reconcile-granularity note mandates the whole-value-span
    // rewrite), and a live value whose recorded type resolves to no loadable C# type is a located,
    // recurrence-keyed report rather than a silent null or an unpatched source. Ownership matters
    // because a live-null managed ref is otherwise indistinguishable from a `.Set(...)` field at its
    // constructed default, and that generic branch's applier has no form for a `.SetRef(...)`
    // statement.
    internal static partial class ComponentReconciler
    {
        // Returns true iff this call fully owned `fieldKey` (an edit/conflict/no-op was decided and
        // the caller must `continue` its loop) — false iff `snapVal` is not a managed-ref concern,
        // in which case the caller's existing generic logic still applies.
        internal static bool TryReconcileManagedRefField(
            ValueNode snapVal,
            ComponentData sourceComp,
            string fieldKey,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            Func<string?, (string? Handle, bool Introduce)> resolveOwnerHandle,
            string ownerLogicalId,
            AssetCatalog? assetCatalog,
            List<SourceEdit> edits,
            List<Conflict> conflicts,
            List<AssetEntry> addedAssets)
        {
            if (ManagedReferenceMissingType.TryUnwrap(snapVal, out var fullTypename))
            {
                conflicts.Add(ConflictDetector.ManagedReferenceMissingType(
                    sourceComp.LogicalId,
                    sourceComp.Type.FullName,
                    fieldKey,
                    fullTypename,
                    DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                return true;
            }

            if (snapVal is not ValueNode.ManagedReference || !sourceComp.Fields.TryGetValue(fieldKey, out var srcVal))
            {
                // Not a managed-ref field, or a managed ref newly present in the snapshot but ABSENT
                // from source — there is no introduce/remove `.SetRef` applier, so this case falls
                // through to the existing generic logic unchanged.
                return false;
            }

            if (Equals(srcVal, snapVal))
            {
                // Idempotence: managed refs render deterministically, so equal values need no
                // AuthoredTextIsCurrent check the generic branch performs for other field kinds.
                return true;
            }

            if (fieldArgumentSpans != null
                && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                && compSpans.TryGetValue(fieldKey, out var valueSpan))
            {
                edits.Add(new PatchComponentField
                {
                    Anchor = sourceComp.LogicalId,
                    ValueSpan = valueSpan,
                    NewExpr = RenderFieldValue(
                        snapVal, sourceComp.Type.FullName, resolveOwnerHandle, edits, ownerLogicalId, assetCatalog),
                });
                CollectAssetEntries(snapVal, addedAssets);
                return true;
            }

            if (fieldArgumentSpans != null)
            {
                // Span data was supplied but this field has none: not localizable to a single source
                // construct — a conflict, never a silent drop (mirrors the generic branch's own
                // span-absent handling).
                conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"patch field '{fieldKey}'"));
                return true;
            }

            // fieldArgumentSpans == null: legacy hand-built test call with no source-span
            // information — no-op, matching the generic patch branch's own legacy contract.
            return true;
        }

        // The reserved signal a live `[SerializeReference]` value whose `managedReferenceFullTypename`
        // resolves to no loadable C# type is carried through: ridden on the existing
        // `ValueNode.Unsupported` container (no new `ValueNode` inhabitant needed), distinguished
        // from every other Unsupported payload (e.g. a sharing/cycle marker, which keeps the
        // report-only skip policy) by the reserved prefix.
        public static class ManagedReferenceMissingType
        {
            public const string Marker = "__csManagedRefMissingType:";

            public static ValueNode Wrap(string fullTypename) => new ValueNode.Unsupported(Marker + fullTypename);

            public static bool TryUnwrap(ValueNode node, out string fullTypename)
            {
                if (node is ValueNode.Unsupported unsupported
                    && unsupported.RawToken.StartsWith(Marker, StringComparison.Ordinal))
                {
                    fullTypename = unsupported.RawToken.Substring(Marker.Length);
                    return true;
                }

                fullTypename = "";
                return false;
            }
        }
    }
}
