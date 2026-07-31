using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // The ONE emit-side decision of what to omit from a newly-AUTHORED field set (component
    // append / field introduce) because it equals the type's constructed default. A field already
    // present in source (the patch branch) is never handled here.
    internal static class ComponentDefaultOmission
    {
        internal sealed class Index
        {
            internal static readonly Index Empty = new(new Dictionary<string, FieldMap>());

            private readonly IReadOnlyDictionary<string, FieldMap> _byTypeFullName;

            private Index(IReadOnlyDictionary<string, FieldMap> byTypeFullName)
            {
                _byTypeFullName = byTypeFullName;
            }

            // Grouped by Type.FullName, first-wins on a (shouldn't-happen) duplicate — mirrors
            // Differ's existing typeDefaults build (Differ.cs:35-37), same source array.
            internal static Index Build(ComponentData[] componentDefaults)
            {
                var byTypeFullName = new Dictionary<string, FieldMap>();
                foreach (var component in componentDefaults)
                {
                    if (!byTypeFullName.ContainsKey(component.Type.FullName))
                    {
                        byTypeFullName[component.Type.FullName] = component.Fields;
                    }
                }

                return new Index(byTypeFullName);
            }

            // Never claims default for a type it has no template for at all (construction failed, or
            // never registered), and never claims default for a field key absent from an otherwise-
            // present type's template — either case means "I have no default for this", not "this is
            // at its default" (never drop data you don't have a basis to omit).
            internal bool IsDefault(string typeFullName, string fieldKey, ValueNode value) =>
                _byTypeFullName.TryGetValue(typeFullName, out var template)
                && template.TryGetValue(fieldKey, out var defaultValue)
                && Equals(defaultValue, value);

            // The field's constructed-default value, when a template for the type carries one —
            // `null` means "no basis to reduce/complete this field", the same "no template" and
            // "field absent from an otherwise-present template" cases IsDefault above treats as
            // "not default" rather than guessing.
            internal ValueNode? TryGetDefault(string typeFullName, string fieldKey) =>
                _byTypeFullName.TryGetValue(typeFullName, out var template)
                && template.TryGetValue(fieldKey, out var defaultValue)
                    ? defaultValue
                    : null;
        }

        // "Is this live field value at its default" for the C2 removal rule. A non-Nested value uses
        // the plain whole-value equality gate. A Nested value is at its default when its
        // REPRESENTABLE projection is empty -- a residual member with no compiling emission form
        // (whose marker can never prove equality either way) must never block a removal, and never
        // route the field into the patch branch where it would render a senseless empty initializer.
        // Never claims default without a basis: a type with no template projects to itself unreduced,
        // never empty, so a template-less type is never claimed to be at its default.
        private static bool IsAtDefault(
            Index index,
            string typeFullName,
            string componentLogicalId,
            string fieldKey,
            ValueNode snapshotValue,
            List<Conflict> conflicts)
        {
            if (snapshotValue is not ValueNode.Nested)
            {
                return index.IsDefault(typeFullName, fieldKey, snapshotValue);
            }

            var fieldDefault = index.TryGetDefault(typeFullName, fieldKey);
            return fieldDefault != null
                && NestedValueEmission.Project(snapshotValue, fieldDefault)
                    .IsEmptyNested(componentLogicalId, typeFullName, fieldKey, conflicts);
        }

        // `defaults` null (Index.Empty semantics) keeps every field — never drop user data for a
        // type with no template. A NESTED field's default-ness is decided per-MEMBER (spec 32 C4) via
        // NestedValueEmission.Project rather than whole-node equality: an Unsupported member carries
        // no observable value (its marker text never encodes the live value), so whole-node equality
        // can never prove it unchanged, and the member must never be silently dropped as "at default"
        // on that basis alone. A non-Nested field keeps the plain whole-value equality gate, unchanged.
        internal static FieldMap OmitDefaults(
            string typeFullName, FieldMap fields, Index? defaults, string componentLogicalId, List<Conflict> conflicts)
        {
            defaults ??= Index.Empty;

            var kept = new List<KeyValuePair<string, ValueNode>>();
            foreach (var field in fields)
            {
                if (field.Value is not ValueNode.Nested)
                {
                    if (defaults.IsDefault(typeFullName, field.Key, field.Value))
                    {
                        continue;
                    }

                    kept.Add(field);
                    continue;
                }

                var fieldDefault = defaults.TryGetDefault(typeFullName, field.Key);
                var projection = NestedValueEmission.Project(field.Value, fieldDefault);
                if (projection.IsEmptyNested(componentLogicalId, typeFullName, field.Key, conflicts))
                {
                    continue;
                }

                kept.Add(new KeyValuePair<string, ValueNode>(field.Key, projection.Value));
                projection.ReportExclusions(componentLogicalId, field.Key, conflicts);
            }

            return new FieldMap(kept);
        }

        // The ONE scene->code default-reset decision — the mirror of OmitDefaults above, for a
        // field ALREADY present in source whose LIVE value has returned to the type's constructed
        // default. `true` => this field is fully handled here (emitted a RemoveComponentField, a
        // located conflict, or nothing at all for a "no basis to drop" case) and the caller must
        // `continue` rather than fall through to the ordinary span patch.
        internal static bool TryEmitDefaultReset(
            ComponentData sourceComp,
            string fieldKey,
            ValueNode snapshotValue,
            Index? defaults,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>>? fieldArgumentSpans,
            List<SourceEdit> edits,
            List<Conflict> conflicts)
        {
            var index = defaults ?? Index.Empty;

            // Buffered, then merged only on a branch that HANDLES the field. The decision reports
            // as it is made (a nested value with nothing representable left to say is otherwise
            // discarded in silence), but the last branch below declines the field entirely, and a
            // report for a field this pass did not handle would be raised again by whichever pass
            // does.
            var decisionConflicts = new List<Conflict>();
            if (!IsAtDefault(index, sourceComp.Type.FullName, sourceComp.LogicalId, fieldKey, snapshotValue, decisionConflicts))
            {
                return false;
            }

            // Closed grammar (FitSize/SurfaceSnap): the dedicated fluent call throws on an empty
            // argument list, so a removal is not representable either — a located conflict, never a
            // silent drop, and never a "vertical: " empty-argument splice.
            if (SpatialComponentSource.IsSpatial(sourceComp.Type.FullName))
            {
                conflicts.AddRange(decisionConflicts);
                conflicts.Add(ConflictDetector.UnremovableClosedGrammarField(
                    sourceComp.LogicalId, fieldKey,
                    ComponentReconciler.DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                return true;
            }

            if (fieldArgumentSpans != null
                && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                && compSpans.TryGetValue(fieldKey, out var valueSpan))
            {
                conflicts.AddRange(decisionConflicts);
                edits.Add(new RemoveComponentField
                {
                    Anchor = sourceComp.LogicalId,
                    FieldKey = fieldKey,
                    ValueSpan = valueSpan,
                });
                return true;
            }

            if (fieldArgumentSpans != null)
            {
                // Span data was supplied but this field has none: not localizable to a single source
                // construct — the SAME conflict the ordinary patch path raises for a span-less field,
                // never a silent drop of the pending removal.
                conflicts.AddRange(decisionConflicts);
                conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"remove field '{fieldKey}'"));
                return true;
            }

            // fieldArgumentSpans == null: legacy no-op, identical contract to the patch path's
            // equivalent branch — never claim to have handled it.
            return false;
        }
    }
}
