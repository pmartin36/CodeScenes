using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // The ONE member-level rule for a nested (struct/class) field value, shared by both directions:
    // an omitted member means "the type's constructed default". Every comparison of an authored
    // value with a live one, and every value about to be written into builder source or applied to
    // a live component, projects/completes through this module so the two directions never disagree
    // about which members are present. Its own descent is ValueWalk.Map, the one recursion over the
    // value grammar, so it cannot skip a container kind the grammar grows.
    internal static class NestedValueEmission
    {
        // The single result of projecting a nested value onto what builder source can carry: the
        // members equal to the type default dropped, and the members with no compiling emission
        // form (AssetRef/ObjectRef/Unsupported/empty List) excluded and named here rather than
        // rendered. The SAME projection feeds both a source-vs-live comparison and an emission, so
        // the two never disagree about which members are present.
        internal readonly struct Projection
        {
            // The excluded members whose live value is PROVABLY not the type default. An
            // Unsupported member is deliberately not among them: its marker text names the MEMBER,
            // never its value, so two independently-read placeholders are always record-equal and
            // equality can prove neither that it changed nor that it did not.
            private readonly IReadOnlyList<string> _discardedMembers;
            private readonly bool _isEmptyNested;

            internal ValueNode Value { get; }
            internal IReadOnlyList<string> ExcludedMembers { get; }

            internal Projection(
                ValueNode value,
                IReadOnlyList<string> excludedMembers,
                IReadOnlyList<string> discardedMembers,
                bool isEmptyNested)
            {
                Value = value;
                ExcludedMembers = excludedMembers;
                _discardedMembers = discardedMembers;
                _isEmptyNested = isEmptyNested;
            }

            // Raises one located UnrepresentableValue conflict per excluded member. Called only by
            // a caller that has already committed to emitting `Value` -- a pass that emits nothing
            // for this field must never call this, or a settled scene reports forever.
            internal void ReportExclusions(
                string componentLogicalId, string componentTypeFullName, string fieldKey, List<Conflict> conflicts)
            {
                foreach (var memberPath in ExcludedMembers)
                {
                    conflicts.Add(ConflictDetector.UnrepresentableNestedMember(
                        componentLogicalId, componentTypeFullName, fieldKey, memberPath, null));
                }
            }

            // "The projection came to nothing, so emit no edit for this field" -- and it REPORTS as
            // it answers, which is why it takes the reporting context rather than being a bare
            // property. An empty projection that excluded a member is the one shape that otherwise
            // produces no edit, no conflict and no trace at any call site; making the answer
            // inseparable from the report is what stops the next caller of this decision from
            // reintroducing that silence. The report is always a STANDING condition (Conflict with
            // RecurrenceKey set), never a per-pass event -- Reconciler routes it into
            // ReconcileResult.Notes, and the adapter surfaces it at most once per session, so a
            // struct of this shape does not log on every single sync forever.
            //
            // Three outcomes:
            //   * nothing excluded at all (every member sits at its type default) -- silent; a
            //     settled scene must not log every sync.
            //   * at least one excluded member is PROVABLY non-default -- an INSTANCE-scoped note
            //     (this component's own value drifted).
            //   * every excluded member is an Unsupported placeholder (no public spelling) -- a
            //     TYPE-scoped note (a capability fact of the type, not of the instance: a stock
            //     scene with N components of this type costs one report, not N).
            internal bool IsEmptyNested(
                string componentLogicalId, string componentTypeFullName, string fieldKey, List<Conflict> conflicts)
            {
                if (!_isEmptyNested)
                {
                    return false;
                }

                if (_discardedMembers.Count > 0)
                {
                    conflicts.Add(ConflictDetector.UnrepresentableNestedField(
                        componentLogicalId, componentTypeFullName, fieldKey, _discardedMembers, null));
                }
                else if (ExcludedMembers.Count > 0)
                {
                    conflicts.Add(ConflictDetector.UnrepresentableNestedFieldMembers(
                        componentLogicalId, componentTypeFullName, fieldKey, ExcludedMembers, null));
                }

                return true;
            }
        }

        // The one projection both the comparison and the emission use: drop members equal to the
        // type default, then drop members with no compiling emission form (recording their dotted
        // path), recursing into nested-in-nested.
        internal static Projection Project(ValueNode value, ValueNode? defaultValue)
        {
            var excludedMembers = new List<string>();
            var discardedMembers = new List<string>();
            var projected = ExcludeUnrepresentable(Reduce(value, defaultValue), excludedMembers, discardedMembers);
            var isEmptyNested = value is ValueNode.Nested && projected is ValueNode.Nested projectedNested && projectedNested.Fields.Count == 0;
            return new Projection(projected, excludedMembers, discardedMembers, isEmptyNested);
        }

        // Project(value, defaultValue).Value -- the projected value with no access to the excluded
        // members, for a comparison site that only needs to know whether two values are the same
        // thing builder source could carry.
        internal static ValueNode Emittable(ValueNode value, ValueNode? defaultValue) => Project(value, defaultValue).Value;

        // Reduces `value` to only the members that differ from the same-keyed member of
        // `defaultValue`, recursing into nested-in-nested. Every non-Nested node, and any value with
        // no default to compare against, is returned unchanged -- never drop what there is no basis
        // to drop. An inner nested member whose recursive reduction leaves nothing is dropped whole
        // rather than emitted as an empty initializer. Exposed (not folded into Project) because the
        // default-only reduction, independent of representability, is its own useful invariant.
        //
        // The walk's context is the SAME-KEYED member of the default, so every depth compares
        // against its own default rather than the top-level one. A list's items carry no paired
        // default (the list is compared whole, as one value), so they are returned as they are.
        internal static ValueNode Reduce(ValueNode value, ValueNode? defaultValue)
        {
            if (value is not ValueNode.Nested || defaultValue is not ValueNode.Nested)
            {
                return value;
            }

            return ValueWalk.Map<ValueNode?>(
                value,
                defaultValue,
                (node, pairedDefault) =>
                {
                    if (pairedDefault is null)
                    {
                        // No basis to drop this member -- unknown key, or a list item.
                        return node;
                    }

                    if (node is ValueNode.Unsupported)
                    {
                        // An Unsupported member's marker text names the MEMBER, never its live
                        // value, so two independently-read placeholders for the same member are
                        // always record-equal regardless of whether the underlying value changed --
                        // equality can never prove it unchanged. Never drop it here; Project
                        // excludes it and raises the located conflict on the pass that actually
                        // emits the rest of the struct.
                        return node;
                    }

                    if (node is ValueNode.Nested && pairedDefault is ValueNode.Nested)
                    {
                        // The descent decides: an inner struct reduced to nothing is dropped whole.
                        return node;
                    }

                    return Equals(node, pairedDefault) ? null : node;
                },
                (parent, pairedDefault, memberKey) =>
                    memberKey is not null
                    && pairedDefault is ValueNode.Nested defaultNested
                    && defaultNested.Fields.TryGetValue(memberKey, out var memberDefault)
                        ? memberDefault
                        : null,
                dropEmptiedNested: true)!;
        }

        // Where a member sits while the representability filter walks: its dotted path from the
        // field root, and whether it is inside a LIST. A list's items are not member-initializer
        // slots -- an asset reference renders perfectly well as a list element and only fails
        // inside a struct initializer -- so the filter descends into a list without judging what it
        // finds there.
        private readonly struct ExclusionContext
        {
            internal string? Path { get; }
            internal bool InList { get; }

            internal ExclusionContext(string? path, bool inList)
            {
                Path = path;
                InList = inList;
            }
        }

        // Drops every member with no compiling emission form from an already-Reduce'd node,
        // recording its dotted member path instead of a conflict directly -- the caller decides
        // whether the projection is actually about to be emitted before turning these into
        // Conflicts via Projection.ReportExclusions. A dropped member is additionally recorded as
        // DISCARDED unless it is an Unsupported placeholder, whose marker can prove nothing either
        // way.
        private static ValueNode ExcludeUnrepresentable(
            ValueNode node, List<string> excludedMembers, List<string> discardedMembers)
        {
            if (node is not ValueNode.Nested)
            {
                return node;
            }

            return ValueWalk.Map(
                node,
                new ExclusionContext(null, inList: false),
                (n, context) =>
                {
                    if (context.InList || IsRepresentable(n))
                    {
                        return n;
                    }

                    excludedMembers.Add(context.Path!);
                    if (n is not ValueNode.Unsupported)
                    {
                        discardedMembers.Add(context.Path!);
                    }

                    return null;
                },
                (parent, context, memberKey) => memberKey is null
                    ? new ExclusionContext(context.Path, inList: true)
                    : new ExclusionContext(context.Path is null ? memberKey : context.Path + "." + memberKey, inList: false),
                dropEmptiedNested: true)!;
        }

        // No compiling initializer form exists for an asset or scene-object reference held inside a
        // nested struct/class, or for a member Reduce could not resolve a public spelling for
        // (Unsupported), or an empty List (renders as `new object[] { }`, which cannot assign to a
        // typed member).
        private static bool IsRepresentable(ValueNode value) => value switch
        {
            ValueNode.AssetRef => false,
            ValueNode.ObjectRef => false,
            ValueNode.Unsupported => false,
            ValueNode.List list => list.Items.Count > 0,
            _ => true,
        };

        // The inverse of Project: fills every representable member present in `defaultValue` and
        // absent from `value`, recursing into a member present in BOTH `value` and `defaultValue` as
        // a nested node so an inner struct's own omitted representable members are filled from ITS
        // default too. A member absent from `value` whose default is itself a nested node is filled
        // with that default run through ExcludeUnrepresentable (dropped entirely when the filtered
        // result is empty), so a member with no compiling emission form is never introduced at any
        // depth -- the same rule the top level applies via IsRepresentable, applied recursively.
        // Returns `value` UNCHANGED when it carries any key `defaultValue` does not know -- a
        // legacy/raw-spelling value is never completed from a default it cannot be proven to share a
        // shape with (this guard prevents overwriting a user's authored value with the type default
        // on the next code->scene build).
        //
        // This is the one nested pass ValueWalk.Map cannot express: its result's key set is the
        // UNION of the value's and the default's, so it adds members rather than only mapping the
        // ones already there.
        internal static ValueNode Complete(ValueNode value, ValueNode? defaultValue)
        {
            if (value is not ValueNode.Nested valueNested
                || defaultValue is not ValueNode.Nested defaultNested)
            {
                return value;
            }

            foreach (var (memberKey, _) in valueNested.Fields)
            {
                if (!defaultNested.Fields.ContainsKey(memberKey))
                {
                    return value;
                }
            }

            var filled = new List<KeyValuePair<string, ValueNode>>();
            foreach (var (memberKey, memberValue) in valueNested.Fields)
            {
                var memberDefault = defaultNested.Fields[memberKey];
                var completedMember = memberValue is ValueNode.Nested && memberDefault is ValueNode.Nested
                    ? Complete(memberValue, memberDefault)
                    : memberValue;

                filled.Add(new KeyValuePair<string, ValueNode>(memberKey, completedMember));
            }

            foreach (var (memberKey, memberDefault) in defaultNested.Fields)
            {
                if (valueNested.Fields.ContainsKey(memberKey))
                {
                    continue;
                }

                if (memberDefault is ValueNode.Nested)
                {
                    var filteredDefault = ExcludeUnrepresentable(memberDefault, new List<string>(), new List<string>());
                    if (filteredDefault is ValueNode.Nested filteredDefaultNested && filteredDefaultNested.Fields.Count == 0)
                    {
                        continue;
                    }

                    filled.Add(new KeyValuePair<string, ValueNode>(memberKey, filteredDefault));
                    continue;
                }

                if (!IsRepresentable(memberDefault))
                {
                    continue;
                }

                filled.Add(new KeyValuePair<string, ValueNode>(memberKey, memberDefault));
            }

            return new ValueNode.Nested(valueNested.TypeName, new FieldMap(filled));
        }
    }
}
