using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    // Completes a `[SerializeReference]` value's partial, authored FieldMap (from a `.SetRef {...}`
    // initializer, which names only the members the author wrote) against a live-read ManagedReference
    // that carries EVERY serialized member — the ONE completion both the Differ's code->scene
    // comparison and the Reconciler's scene->code comparison need before testing equality, so the two
    // directions never independently disagree about which member is "unwritten, still at its
    // construction default" versus "genuinely different, must be re-applied/patched".
    internal static class ManagedReferenceFieldCompletion
    {
        // Fills every member `actual` (the live-read instance) carries but `desired` (the author's
        // partial `.SetRef {...}` initializer) omits, when that member's live value is the shape a
        // fresh `Activator.CreateInstance` + no explicit write leaves it at
        // (ManagedReferenceWriter.WriteInstance's own construction — every OTHER member starts there
        // and stays there unless the author's initializer names it). A member NOT at that shape is
        // left out, so the equality test the caller performs still reports a change and the whole
        // reference is re-applied/patched — a `.SetRef(...)` value owns its WHOLE instance, so a live
        // edit to a member the author never named is reverted, never adopted silently.
        internal static ValueNode.ManagedReference Complete(
            ValueNode.ManagedReference desired, ValueNode.ManagedReference? actual)
        {
            if (actual == null || actual.Fields.Count == desired.Fields.Count)
            {
                return desired;
            }

            List<KeyValuePair<string, ValueNode>>? completed = null;
            foreach (var (key, actualMember) in actual.Fields)
            {
                if (desired.Fields.ContainsKey(key) || !IsUnwrittenMemberDefault(actualMember))
                {
                    continue;
                }

                (completed ??= new List<KeyValuePair<string, ValueNode>>(desired.Fields))
                    .Add(new KeyValuePair<string, ValueNode>(key, actualMember));
            }

            return completed == null
                ? desired
                : new ValueNode.ManagedReference(desired.ConcreteType, new FieldMap(completed));
        }

        // The construction-default shape for every ValueNode kind a `[SerializeReference]` instance's
        // own member can hold: zero for a primitive, None for an object/asset reference, null for a
        // nested managed reference, empty for a list — what Activator.CreateInstance + no explicit
        // write leaves a member at. A Nested/Enum/Vec/Unsupported member has no default this
        // reflection-free check can prove, so it is never assumed unwritten.
        private static bool IsUnwrittenMemberDefault(ValueNode value) => value switch
        {
            ValueNode.Primitive p => p.Kind switch
            {
                PrimitiveKind.Bool => p.Value is false,
                PrimitiveKind.Int => p.Value is 0,
                PrimitiveKind.Long => p.Value is 0L,
                PrimitiveKind.Float => p.Value is 0f,
                PrimitiveKind.Double => p.Value is 0d,
                PrimitiveKind.String => string.IsNullOrEmpty(p.Value as string),
                _ => false,
            },
            ValueNode.ObjectRef(null) => true,
            ValueNode.AssetRef(null) => true,
            ValueNode.ManagedReference(null, _) => true,
            ValueNode.List { Items.Count: 0 } => true,
            _ => false,
        };
    }
}
