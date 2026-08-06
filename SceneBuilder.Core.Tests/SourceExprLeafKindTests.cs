using System;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // SourceExpr.ValueNodeLiteral throws NotSupportedException naming the concrete
    // ValueNode subtype for a kind it has no rendering for, whether that node sits at
    // the root or is reached by descending into a container, and throws naming the
    // PrimitiveKind for an unrecognized primitive.
    public class SourceExprLeafKindTests
    {
        private sealed record SyntheticLeafKind(string Token) : ValueNode;

        [Fact]
        public void ValueNodeLiteral_SyntheticLeafKindAtRoot_ThrowsNamingTheKind()
        {
            var ex = Assert.Throws<NotSupportedException>(
                () => SourceExpr.ValueNodeLiteral(new SyntheticLeafKind("x")));

            Assert.Equal("SourceExpr.ValueNodeLiteral: unsupported ValueNode kind SyntheticLeafKind", ex.Message);
        }

        [Fact]
        public void ValueNodeLiteral_SyntheticLeafKindBelowNestedAndList_ThrowsTheSameMessage()
        {
            var node = new ValueNode.Nested("Game.Wiring", new FieldMap(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, ValueNode>(
                    "items",
                    new ValueNode.List(new ValueNode[] { new SyntheticLeafKind("x") })),
            }));

            var ex = Assert.Throws<NotSupportedException>(() => SourceExpr.ValueNodeLiteral(node));

            Assert.Equal("SourceExpr.ValueNodeLiteral: unsupported ValueNode kind SyntheticLeafKind", ex.Message);
        }

        [Fact]
        public void ValueNodeLiteral_UnrecognizedPrimitiveKind_ThrowsNamingThePrimitiveKind()
        {
            var ex = Assert.Throws<NotSupportedException>(
                () => SourceExpr.ValueNodeLiteral(new ValueNode.Primitive((PrimitiveKind)99, "z")));

            Assert.Equal("SourceExpr.ValueNodeLiteral: unsupported PrimitiveKind 99", ex.Message);
        }
    }
}
