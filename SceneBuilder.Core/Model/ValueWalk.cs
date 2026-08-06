using System;
using System.Collections.Generic;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// THE recursion over a value's container structure. Every pass that walks a
    /// <see cref="ValueNode"/> routes its descent through one of the four primitives here —
    /// <see cref="Map{TContext}"/> to transform, <see cref="Fold{T}"/> to reduce to something that is
    /// not a value, <see cref="Enumerate"/> to visit with paths, <see cref="Descend{TContext}"/> to
    /// enter parents before children carrying context down — instead of re-deriving
    /// <c>switch (node) { Nested => ..., List => ... }</c> for itself. Each pass then supplies only
    /// its own per-node decision, and none of them can ship having silently skipped a container kind:
    /// a kind added to the grammar is handled for all of them by editing this file. The
    /// <c>ValueContainerDescentScanTests</c> guard fails when a site outside this file re-derives
    /// that switch. See the remarks for which primitive each pass uses and why.
    /// </summary>
    /// <remarks>
    /// Primitive and context per pass:
    /// <list type="bullet">
    /// <item><description><c>AssetRefLowering.LowerNode</c> — <see cref="Map{TContext}"/>, no context
    /// (the resolvers are captured); <c>dropEmptiedNested: false</c>, since it replaces one leaf kind
    /// and drops nothing.</description></item>
    /// <item><description><c>PlanningValidator.WalkAssetValue</c> — <see cref="Enumerate"/>, no
    /// context; side-effecting, builds no value, and reports under the top-level field key, so the
    /// yielded path is unused.</description></item>
    /// <item><description><c>SourceExpr.ValueNodeLiteral</c> — <see cref="Fold{T}"/> with
    /// <c>T = string</c>, no context (the asset catalog is captured); the container delegates render
    /// array/initializer syntax, the leaf delegate the per-kind literal.</description></item>
    /// <item><description><c>BuiltinRefValidator.ValidateField</c> — <see cref="Enumerate"/>, context
    /// is the field key it prefixes onto each yielded path; <c>[i]</c> / <c>.member</c> are the
    /// spellings it reports.</description></item>
    /// <item><description><c>SerializedFieldBridge.WriteProperty</c> — <see cref="Descend{TContext}"/>,
    /// context is the live <c>SerializedProperty</c> plus the struct type resolved on the enclosing
    /// nested value. It needs a list item's index (to reach <c>GetArrayElementAtIndex</c>) and the
    /// parent entered first with its resolved type handed down (to reach
    /// <c>FindPropertyRelative</c> under the mapped member name).</description></item>
    /// </list>
    /// </remarks>
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

        /// <summary>
        /// True when <paramref name="predicate"/> holds for <paramref name="node"/> itself or for any
        /// member/item reachable from it at any depth (<see cref="ValueNode.Nested"/> members and
        /// <see cref="ValueNode.List"/> items; every other kind is a leaf).
        /// </summary>
        public static bool Any(ValueNode node, Func<ValueNode, bool> predicate)
        {
            if (predicate(node))
            {
                return true;
            }

            switch (node)
            {
                case ValueNode.Nested nested:
                    foreach (var (_, memberValue) in nested.Fields)
                    {
                        if (Any(memberValue, predicate))
                        {
                            return true;
                        }
                    }

                    return false;

                case ValueNode.List list:
                    foreach (var item in list.Items)
                    {
                        if (Any(item, predicate))
                        {
                            return true;
                        }
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Yields <paramref name="node"/> itself at path <c>""</c>, then every descendant in
        /// pre-order: a <see cref="ValueNode.List"/> item at <c>parent + "[" + i + "]"</c>, a
        /// <see cref="ValueNode.Nested"/> member at <c>parent + "." + memberKey</c>. Every other
        /// kind is a leaf. Callers concatenate the yielded path onto their own field key/path.
        /// </summary>
        public static IEnumerable<(string Path, ValueNode Node)> Enumerate(ValueNode node) =>
            EnumerateAt(node, "");

        /// <summary>
        /// Reduces <paramref name="node"/> bottom-up: every child is folded before its parent's
        /// container delegate runs, so a container delegate never combines an unfolded child into
        /// its result.
        /// </summary>
        /// <param name="list">
        /// Runs on a <see cref="ValueNode.List"/> after every item has been folded, given the
        /// original node and the folded items in source order.
        /// </param>
        /// <param name="nested">
        /// Runs on a <see cref="ValueNode.Nested"/> after every member has been folded, given the
        /// original node and the folded members in source order.
        /// </param>
        /// <param name="leaf">Runs on every non-container node, given the node itself.</param>
        public static T Fold<T>(
            ValueNode node,
            Func<ValueNode.List, IReadOnlyList<T>, T> list,
            Func<ValueNode.Nested, IReadOnlyList<KeyValuePair<string, T>>, T> nested,
            Func<ValueNode, T> leaf)
        {
            switch (node)
            {
                case ValueNode.List listNode:
                {
                    var items = new List<T>(listNode.Items.Count);
                    foreach (var item in listNode.Items)
                    {
                        items.Add(Fold(item, list, nested, leaf));
                    }

                    return list(listNode, items);
                }

                case ValueNode.Nested nestedNode:
                {
                    var members = new List<KeyValuePair<string, T>>(nestedNode.Fields.Count);
                    foreach (var (memberKey, memberValue) in nestedNode.Fields)
                    {
                        members.Add(new KeyValuePair<string, T>(memberKey, Fold(memberValue, list, nested, leaf)));
                    }

                    return nested(nestedNode, members);
                }

                // Every other kind is a leaf.
                default:
                    return leaf(node);
            }
        }

        /// <summary>
        /// Walks <paramref name="node"/> top-down for a side-effecting pass that builds no value:
        /// <paramref name="enter"/> runs on a node before its children, using the context its parent
        /// produced, and returns the context its own children derive from.
        /// </summary>
        /// <param name="enter">
        /// Runs on a node before its children, with the context its parent produced for it, and
        /// returns the context its children are derived from. A side-effecting pass acts here (write
        /// the leaf, set <c>arraySize</c>, resolve the struct type). A pass that must leave a subtree
        /// untouched expresses that through the context it returns: <c>ValueWalk</c> never inspects
        /// <typeparamref name="TContext"/>.
        /// </param>
        /// <param name="item">Supplies a <see cref="ValueNode.List"/> item's context, given the list, the context
        /// <paramref name="enter"/> returned for it, and the item's index.</param>
        /// <param name="member">Supplies a <see cref="ValueNode.Nested"/> member's context, given the nested node, the
        /// context <paramref name="enter"/> returned for it, and the member key.</param>
        public static void Descend<TContext>(
            ValueNode node,
            TContext context,
            Func<ValueNode, TContext, TContext> enter,
            Func<ValueNode.List, TContext, int, TContext> item,
            Func<ValueNode.Nested, TContext, string, TContext> member)
        {
            var childContext = enter(node, context);

            switch (node)
            {
                case ValueNode.List list:
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        Descend(list.Items[i], item(list, childContext, i), enter, item, member);
                    }

                    break;

                case ValueNode.Nested nested:
                    foreach (var (memberKey, memberValue) in nested.Fields)
                    {
                        Descend(memberValue, member(nested, childContext, memberKey), enter, item, member);
                    }

                    break;
            }
        }

        private static IEnumerable<(string Path, ValueNode Node)> EnumerateAt(ValueNode node, string path)
        {
            yield return (path, node);

            switch (node)
            {
                case ValueNode.Nested nested:
                    foreach (var (memberKey, memberValue) in nested.Fields)
                    {
                        foreach (var entry in EnumerateAt(memberValue, path + "." + memberKey))
                        {
                            yield return entry;
                        }
                    }

                    break;

                case ValueNode.List list:
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        foreach (var entry in EnumerateAt(list.Items[i], path + "[" + i + "]"))
                        {
                            yield return entry;
                        }
                    }

                    break;
            }
        }
    }
}
