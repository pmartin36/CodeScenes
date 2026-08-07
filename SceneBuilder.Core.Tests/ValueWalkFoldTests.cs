using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ValueWalk.Fold reduces a value bottom-up: a container delegate runs only after every one of
    // its children has already been folded, so a pass built on it can never combine an unfolded
    // child into its parent's result. ValueWalk.Descend is the mirror shape for a side-effecting
    // pass: it enters a node BEFORE its children, with the context its parent resolved, so a
    // resolution done once on a parent (a struct type, say) reaches every child without being
    // re-derived per child.
    public class ValueWalkFoldTests
    {
        private static ValueNode.Nested Node(string typeName, params (string Key, ValueNode Value)[] fields) =>
            new(typeName, new FieldMap(fields.Select(f => new KeyValuePair<string, ValueNode>(f.Key, f.Value))));

        private static ValueNode.Primitive I(int v) => ValueNode.Primitive.Int(v);

        public static IEnumerable<object[]> LeafKindSamples()
        {
            yield return new object[] { ValueNode.Primitive.Int(1) };
            yield return new object[] { ValueNode.Enum.Canonical("My.Namespace.Type", new[] { "A" }) };
            yield return new object[] { new ValueNode.Vec2(new Vec2(1, 2)) };
            yield return new object[] { new ValueNode.Vec3(new Vec3(1, 2, 3)) };
            yield return new object[] { new ValueNode.Vec4(new Vec4(1, 2, 3, 4)) };
            yield return new object[] { new ValueNode.Quat(new Quat(0, 0, 0, 1)) };
            yield return new object[] { new ValueNode.Color(new Color(1, 1, 1, 1)) };
            yield return new object[] { new ValueNode.Unsupported("raw-token") };
            yield return new object[] { new ValueNode.AssetRef(new AssetRef { Guid = "abc" }) };
            yield return new object[] { new ValueNode.ObjectRef("target") };
        }

        // The DELIVERABLE fact: children fold before their parent, and the order the delegates ran
        // is part of the contract, not just the combined result. A pre-order fold would hand a
        // container delegate its children's SOURCE nodes instead of their folded values, and would
        // run the container delegate before its descendants -- both wrong under this order.
        [Fact]
        public void Fold_ThreeDeepValue_FoldsChildrenBeforeParents()
        {
            var value = Node(
                "Outer",
                ("a", I(1)),
                ("inner", Node(
                    "Inner",
                    ("items", new ValueNode.List(new ValueNode[]
                    {
                        I(2),
                        Node("Deep", ("d", I(3))),
                    })))));
            var order = new List<string>();

            var result = ValueWalk.Fold<string>(
                value,
                (listNode, items) => { order.Add("list"); return "[" + string.Join(",", items) + "]"; },
                (nestedNode, members) =>
                {
                    order.Add("nested(" + nestedNode.TypeName + ")");
                    return "{" + string.Join(",", members.Select(m => m.Key + ":" + m.Value)) + "}";
                },
                leaf =>
                {
                    var raw = ((ValueNode.Primitive)leaf).Value!;
                    order.Add("leaf(" + raw + ")");
                    return raw.ToString()!;
                },
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.Equal(
                new[] { "leaf(1)", "leaf(2)", "leaf(3)", "nested(Deep)", "list", "nested(Inner)", "nested(Outer)" },
                order);
            Assert.Equal("{a:1,inner:{items:[2,{d:3}]}}", result);
        }

        [Fact]
        public void Fold_PreservesListOrderAndNestedMemberOrder()
        {
            var listValue = new ValueNode.List(new ValueNode[] { I(10), I(20), I(30) });
            IReadOnlyList<string>? capturedItems = null;

            ValueWalk.Fold<string>(
                listValue,
                (listNode, items) => { capturedItems = items; return ""; },
                (nestedNode, members) => "",
                leaf => ((ValueNode.Primitive)leaf).Value!.ToString()!,
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.Equal(new[] { "10", "20", "30" }, capturedItems);

            var nestedValue = Node("Outer", ("p", I(1)), ("q", I(2)), ("r", I(3)));
            IReadOnlyList<KeyValuePair<string, string>>? capturedMembers = null;

            ValueWalk.Fold<string>(
                nestedValue,
                (listNode, items) => "",
                (nestedNode, members) => { capturedMembers = members; return ""; },
                leaf => ((ValueNode.Primitive)leaf).Value!.ToString()!,
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.Equal(new[] { "p", "q", "r" }, capturedMembers!.Select(m => m.Key));
            Assert.Equal(new[] { "1", "2", "3" }, capturedMembers!.Select(m => m.Value));
        }

        // ListValueEmission.ArrayPrefix(list) needs the ORIGINAL List node, not only its folded
        // items -- this is what makes that possible.
        [Fact]
        public void Fold_ContainerDelegates_ReceiveTheOriginalNode()
        {
            var listValue = new ValueNode.List(new ValueNode[] { I(1) });
            ValueNode.List? capturedList = null;

            ValueWalk.Fold<string>(
                listValue,
                (listNode, items) => { capturedList = listNode; return ""; },
                (nestedNode, members) => "",
                leaf => "",
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.Same(listValue, capturedList);

            var nestedValue = Node("Outer", ("a", I(1)));
            ValueNode.Nested? capturedNested = null;

            ValueWalk.Fold<string>(
                nestedValue,
                (listNode, items) => "",
                (nestedNode, members) => { capturedNested = nestedNode; return ""; },
                leaf => "",
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.Same(nestedValue, capturedNested);
        }

        [Fact]
        public void Fold_EmptyList_ReachesTheListDelegateWithNoChildren()
        {
            var value = new ValueNode.List(Array.Empty<ValueNode>());
            var listCalled = false;
            var leafCalled = false;

            ValueWalk.Fold<string>(
                value,
                (listNode, items) => { listCalled = true; Assert.Empty(items); return ""; },
                (nestedNode, members) => "",
                leaf => { leafCalled = true; return ""; },
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.True(listCalled);
            Assert.False(leafCalled);
        }

        [Fact]
        public void Fold_EmptyNested_ReachesTheNestedDelegateWithNoMembers()
        {
            var value = Node("Empty");
            var nestedCalled = false;
            var leafCalled = false;

            ValueWalk.Fold<string>(
                value,
                (listNode, items) => "",
                (nestedNode, members) => { nestedCalled = true; Assert.Empty(members); return ""; },
                leaf => { leafCalled = true; return ""; },
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran unexpectedly"));

            Assert.True(nestedCalled);
            Assert.False(leafCalled);
        }

        [Theory]
        [MemberData(nameof(LeafKindSamples))]
        public void Fold_EveryLeafKind_ReachesTheLeafDelegateExactlyOnce(ValueNode sample)
        {
            var leafCalls = new List<ValueNode>();

            ValueWalk.Fold<string>(
                sample,
                (listNode, items) => throw new Xunit.Sdk.XunitException("list delegate ran for a leaf sample"),
                (nestedNode, members) => throw new Xunit.Sdk.XunitException("nested delegate ran for a leaf sample"),
                leaf => { leafCalls.Add(leaf); return ""; },
                (listenersNode, slots) => throw new Xunit.Sdk.XunitException("listeners delegate ran for a leaf sample"));

            var onlyCall = Assert.Single(leafCalls);
            Assert.Same(sample, onlyCall);
        }

        // Guards the sample set above against silently going stale: a ValueNode kind added to the
        // grammar without a matching sample here would leave that kind's leaf-delegate contract
        // unchecked by Fold_EveryLeafKind_ReachesTheLeafDelegateExactlyOnce.
        [Fact]
        public void Fold_LeafKindSample_CoversEveryValueNodeKindExceptTheContainers()
        {
            var expected = typeof(ValueNode).GetNestedTypes()
                .Select(t => t.Name)
                .Where(name => name != nameof(ValueNode.List)
                    && name != nameof(ValueNode.Nested)
                    && name != nameof(ValueNode.UnityEventListeners))
                .ToHashSet();
            var sampled = LeafKindSamples().Select(args => ((ValueNode)args[0]).GetType().Name).ToHashSet();

            Assert.Equal(expected, sampled);
        }

        // The WriteProperty affordance: a list item's context must carry its INDEX, so the write
        // path can reach GetArrayElementAtIndex.
        [Fact]
        public void Descend_ListItems_ReceiveTheirIndex()
        {
            var value = new ValueNode.List(new ValueNode[] { I(10), I(20) });
            var indices = new List<int>();

            ValueWalk.Descend<string>(
                value,
                "root",
                (node, context) => context,
                (listNode, context, index) => { indices.Add(index); return context; },
                (nestedNode, context, memberKey) => context,
                (listenersNode, context, index, slotName) => throw new Xunit.Sdk.XunitException("listenerSlot delegate ran unexpectedly"));

            Assert.Equal(new[] { 0, 1 }, indices);
        }

        // The other WriteProperty affordance: a context resolved once on a parent (a struct type
        // resolution, say) reaches every one of that parent's children without being re-derived
        // per child.
        [Fact]
        public void Descend_ContextResolvedOnAParent_ReachesEveryChild()
        {
            var value = Node("Outer", ("a", I(1)), ("b", I(2)));
            var resolveCount = 0;
            var memberContexts = new List<string>();

            ValueWalk.Descend<string>(
                value,
                "root",
                (node, context) =>
                {
                    if (node is ValueNode.Nested)
                    {
                        resolveCount++;
                        return "resolved";
                    }

                    memberContexts.Add(context);
                    return context;
                },
                (listNode, context, index) => context,
                (nestedNode, context, memberKey) => context,
                (listenersNode, context, index, slotName) => throw new Xunit.Sdk.XunitException("listenerSlot delegate ran unexpectedly"));

            Assert.Equal(1, resolveCount);
            Assert.Equal(new[] { "resolved", "resolved" }, memberContexts);
        }

        [Fact]
        public void Descend_EntersAParentBeforeItsChildren()
        {
            var value = Node(
                "Outer",
                ("a", I(1)),
                ("inner", Node("Inner", ("b", I(2)))));
            var order = new List<string>();

            ValueWalk.Descend<string>(
                value,
                "root",
                (node, context) =>
                {
                    order.Add(node switch
                    {
                        ValueNode.Nested n => "nested(" + n.TypeName + ")",
                        ValueNode.Primitive(PrimitiveKind.Int, int raw) => "leaf(" + raw + ")",
                        _ => "?",
                    });
                    return context;
                },
                (listNode, context, index) => context,
                (nestedNode, context, memberKey) => context,
                (listenersNode, context, index, slotName) => throw new Xunit.Sdk.XunitException("listenerSlot delegate ran unexpectedly"));

            Assert.Equal(new[] { "nested(Outer)", "leaf(1)", "nested(Inner)", "leaf(2)" }, order);
        }

        // Models a missing SerializedProperty child: member hands back a sentinel context, enter
        // no-ops on it, and nothing under that subtree is ever acted on -- the recursion still
        // reaches every node, it just performs no operation there.
        [Fact]
        public void Descend_ChildContextThatSignalsSkip_LeavesTheSubtreeUntouched()
        {
            var value = Node("Outer", ("missing", Node("Inner", ("b", I(2)))));
            var operations = new List<string>();

            ValueWalk.Descend<string?>(
                value,
                "root",
                (node, context) =>
                {
                    if (context is null)
                    {
                        return null;
                    }

                    operations.Add(node is ValueNode.Nested n ? n.TypeName : "leaf");
                    return context;
                },
                (listNode, context, index) => context,
                (nestedNode, context, memberKey) => memberKey == "missing" ? null : context,
                (listenersNode, context, index, slotName) => throw new Xunit.Sdk.XunitException("listenerSlot delegate ran unexpectedly"));

            Assert.Equal(new[] { "Outer" }, operations);
        }

        [Fact]
        public void Descend_LeafRoot_EntersItOnceAndDoesNotDescend()
        {
            var value = I(42);
            var enterCount = 0;

            ValueWalk.Descend<string>(
                value,
                "root",
                (node, context) => { enterCount++; return context; },
                (listNode, context, index) => throw new Xunit.Sdk.XunitException("item delegate ran for a leaf root"),
                (nestedNode, context, memberKey) => throw new Xunit.Sdk.XunitException("member delegate ran for a leaf root"),
                (listenersNode, context, index, slotName) => throw new Xunit.Sdk.XunitException("listenerSlot delegate ran for a leaf root"));

            Assert.Equal(1, enterCount);
        }
    }
}
