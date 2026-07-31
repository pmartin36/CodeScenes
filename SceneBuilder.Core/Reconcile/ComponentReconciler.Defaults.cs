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
        }

        // `defaults` null (Index.Empty semantics) keeps every field — never drop user data for a
        // type with no template.
        internal static FieldMap OmitDefaults(string typeFullName, FieldMap fields, Index? defaults)
        {
            defaults ??= Index.Empty;

            return new FieldMap(fields.Where(field => !defaults.IsDefault(typeFullName, field.Key, field.Value)));
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
            if (!index.IsDefault(sourceComp.Type.FullName, fieldKey, snapshotValue))
            {
                return false;
            }

            // Closed grammar (FitSize/SurfaceSnap): the dedicated fluent call throws on an empty
            // argument list, so a removal is not representable either — a located conflict, never a
            // silent drop, and never a "vertical: " empty-argument splice.
            if (SpatialComponentSource.IsSpatial(sourceComp.Type.FullName))
            {
                conflicts.Add(ConflictDetector.UnremovableClosedGrammarField(
                    sourceComp.LogicalId, fieldKey,
                    ComponentReconciler.DanglingFieldSpan(sourceComp.LogicalId, fieldKey, fieldArgumentSpans)));
                return true;
            }

            if (fieldArgumentSpans != null
                && fieldArgumentSpans.TryGetValue(sourceComp.LogicalId, out var compSpans)
                && compSpans.TryGetValue(fieldKey, out var valueSpan))
            {
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
                conflicts.Add(ConflictDetector.UnanchorableComponentEdit(sourceComp.LogicalId, $"remove field '{fieldKey}'"));
                return true;
            }

            // fieldArgumentSpans == null: legacy no-op, identical contract to the patch path's
            // equivalent branch — never claim to have handled it.
            return false;
        }
    }
}
