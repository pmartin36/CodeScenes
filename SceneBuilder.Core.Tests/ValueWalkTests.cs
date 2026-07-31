using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // ValueWalk.Map is THE recursion over a value's container structure. Every pass that transforms
    // a ValueNode routes its descent through it, so no pass can process a value while silently
    // skipping a container kind, and a kind added to the grammar reaches every pass through one edit.
    public class ValueWalkTests
    {
        private static ValueNode.Nested Node(string typeName, params (string Key, ValueNode Value)[] fields) =>
            new(typeName, new FieldMap(fields.Select(f => new KeyValuePair<string, ValueNode>(f.Key, f.Value))));

        private static ValueNode.Primitive I(int v) => ValueNode.Primitive.Int(v);

        private static ValueNode? Keep(ValueNode node, string _) => node;

        private static string Descend(ValueNode parent, string context, string? memberKey) =>
            memberKey == null ? context + "[]" : context + "." + memberKey;

        // The defect this exists to make impossible: a pass with a Primitive arm and no Nested arm
        // leaves an inner member untouched forever. Map visits every member at every depth.
        [Fact]
        public void Map_VisitsEveryNestedMember_AtEveryDepth()
        {
            var value = Node("Outer", ("a", I(1)), ("inner", Node("Inner", ("b", I(2)))));
            var visited = new List<string>();

            var mapped = ValueWalk.Map<string>(
                value,
                "root",
                (node, path) =>
                {
                    visited.Add(path);
                    return node is ValueNode.Primitive(PrimitiveKind.Int, int raw) ? I(raw + 10) : node;
                },
                Descend,
                dropEmptiedNested: false);

            Assert.Equal(Node("Outer", ("a", I(11)), ("inner", Node("Inner", ("b", I(12))))), mapped);
            Assert.Equal(new[] { "root", "root.a", "root.inner", "root.inner.b" }, visited);
        }

        [Fact]
        public void Map_VisitsEveryListItem()
        {
            var value = Node("Outer", ("items", new ValueNode.List(new ValueNode[] { I(1), I(2) })));
            var visited = new List<string>();

            ValueWalk.Map<string>(value, "root", (node, path) => { visited.Add(path); return node; }, Descend, dropEmptiedNested: false);

            Assert.Contains("root.items[]", visited);
            Assert.Equal(2, visited.Count(p => p == "root.items[]"));
        }

        // A null from `visit` drops the node from its parent; a nested member left with nothing is
        // itself dropped when the caller asks for it (an empty initializer is not a value), and the
        // ROOT is returned empty rather than dropped -- "this whole value came to nothing" is a
        // result the caller acts on.
        [Fact]
        public void Map_DropsVisitedNulls_AndCollapsesAnEmptiedNestedMember()
        {
            var value = Node("Outer", ("a", I(1)), ("inner", Node("Inner", ("b", I(2)))));

            var mapped = ValueWalk.Map<string>(
                value,
                "root",
                (node, _) => node is ValueNode.Primitive ? null : node,
                Descend,
                dropEmptiedNested: true);

            Assert.Equal(Node("Outer"), mapped);
        }

        [Fact]
        public void Map_KeepsAnEmptiedNestedMember_WhenTheCallerDoesNotAskForTheCollapse()
        {
            var value = Node("Outer", ("inner", Node("Inner", ("b", I(2)))));

            var mapped = ValueWalk.Map<string>(
                value,
                "root",
                (node, _) => node is ValueNode.Primitive ? null : node,
                Descend,
                dropEmptiedNested: false);

            Assert.Equal(Node("Outer", ("inner", Node("Inner"))), mapped);
        }
    }
}
