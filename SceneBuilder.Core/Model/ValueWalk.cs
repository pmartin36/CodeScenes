using System;
using System.Collections.Generic;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// THE recursion over a value's container structure. Every pass that transforms a
    /// <see cref="ValueNode"/> — the source-side normalizer, the emit-side reduction, the
    /// representability filter — routes its descent through <see cref="Map{TContext}"/> instead of
    /// re-deriving <c>switch (node) { Nested => ..., List => ... }</c> for itself. Each pass then
    /// supplies only its own per-node decision, and none of them can ship having silently skipped a
    /// container kind: a kind added to the grammar is handled for all of them by editing this file.
    /// </summary>
    public static class ValueWalk
    {
        /// <summary>
        /// Rebuilds <paramref name="node"/> with <paramref name="visit"/> applied to it and, for a
        /// container, to every member/item at every depth.
        /// </summary>
        /// <param name="visit">
        /// Runs on a node BEFORE its children and returns the node to put in its place, or
        /// <c>null</c> to drop the node from its parent.
        /// </param>
        /// <param name="descend">
        /// Supplies a child's context from its parent's, given the parent node and the member key
        /// (<c>null</c> for a <see cref="ValueNode.List"/> item). A pass that must leave a subtree
        /// exactly as it found it says so through the context it returns here — see
        /// <c>SerializedEnumNormalizer</c>, which hands back a null path for an unresolvable struct
        /// type, and the emission projection, which does the same for a list's items.
        /// </param>
        /// <param name="dropEmptiedNested">
        /// When true, a <see cref="ValueNode.Nested"/> MEMBER left with no members is dropped from
        /// its parent rather than emitted as an empty initializer. The ROOT is never dropped by that
        /// rule: "this whole value came to nothing" is a result its caller acts on, so it is returned
        /// empty. Lists are never collapsed by it — an emptied list is a value, and a pass that
        /// wants it gone drops it from <paramref name="visit"/>.
        /// </param>
        public static ValueNode? Map<TContext>(
            ValueNode node,
            TContext context,
            Func<ValueNode, TContext, ValueNode?> visit,
            Func<ValueNode, TContext, string?, TContext> descend,
            bool dropEmptiedNested) =>
            MapNode(node, context, visit, descend, dropEmptiedNested, isRoot: true);

        private static ValueNode? MapNode<TContext>(
            ValueNode node,
            TContext context,
            Func<ValueNode, TContext, ValueNode?> visit,
            Func<ValueNode, TContext, string?, TContext> descend,
            bool dropEmptiedNested,
            bool isRoot)
        {
            var visited = visit(node, context);
            if (visited is null)
            {
                return null;
            }

            switch (visited)
            {
                case ValueNode.Nested nested:
                {
                    var kept = new List<KeyValuePair<string, ValueNode>>(nested.Fields.Count);
                    foreach (var (memberKey, memberValue) in nested.Fields)
                    {
                        var mapped = MapNode(
                            memberValue, descend(nested, context, memberKey), visit, descend, dropEmptiedNested, isRoot: false);
                        if (mapped is not null)
                        {
                            kept.Add(new KeyValuePair<string, ValueNode>(memberKey, mapped));
                        }
                    }

                    if (dropEmptiedNested && !isRoot && kept.Count == 0)
                    {
                        return null;
                    }

                    return new ValueNode.Nested(nested.TypeName, new FieldMap(kept));
                }

                case ValueNode.List list:
                {
                    var items = new List<ValueNode>(list.Items.Count);
                    foreach (var item in list.Items)
                    {
                        var mapped = MapNode(item, descend(list, context, null), visit, descend, dropEmptiedNested, isRoot: false);
                        if (mapped is not null)
                        {
                            items.Add(mapped);
                        }
                    }

                    return new ValueNode.List(items);
                }

                // Every other kind is a leaf: whatever `visit` returned is the result.
                default:
                    return visited;
            }
        }
    }
}
