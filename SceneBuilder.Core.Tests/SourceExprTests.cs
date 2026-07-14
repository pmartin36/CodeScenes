using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SourceExprTests
    {
        [Fact]
        public void StringLiteral_QuotesPlainValue()
        {
            Assert.Equal("\"Enemy\"", SourceExpr.StringLiteral("Enemy"));
        }

        [Fact]
        public void StringLiteral_EscapesQuoteAndBackslash()
        {
            Assert.Equal("\"a\\\"b\\\\c\"", SourceExpr.StringLiteral("a\"b\\c"));
        }

        [Fact]
        public void StringLiteral_EmptyString_ProducesEmptyQuotedLiteral()
        {
            Assert.Equal("\"\"", SourceExpr.StringLiteral(""));
        }

        [Fact]
        public void IntLiteral_PositiveValue_IsBareDigits()
        {
            Assert.Equal("6", SourceExpr.IntLiteral(6));
        }

        [Fact]
        public void IntLiteral_Zero_IsBareZero()
        {
            Assert.Equal("0", SourceExpr.IntLiteral(0));
        }

        [Fact]
        public void IntLiteral_Negative_IncludesMinusSign()
        {
            Assert.Equal("-1", SourceExpr.IntLiteral(-1));
        }

        // b1-t1: ValueNodeLiteral — per-kind rendering + round-trip through ValueNodeParser.
        // Oracle: text -> SyntaxFactory.ParseExpression -> ValueNodeParser.Parse -> ValueNode,
        // asserted Equal to the original node (spec: emitted syntax must be exactly what the
        // parser reads back).
        private static ValueNode Roundtrip(string text) =>
            ValueNodeParser.Parse(SyntaxFactory.ParseExpression(text));

        [Fact]
        public void ValueNodeLiteral_BoolTrue_RendersBareTrue()
        {
            var node = ValueNode.Primitive.Bool(true);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("true", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_BoolFalse_RendersBareFalse()
        {
            var node = ValueNode.Primitive.Bool(false);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("false", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_Int_RoundtripsIncludingNegative()
        {
            var positive = ValueNode.Primitive.Int(6);
            var negative = ValueNode.Primitive.Int(-6);

            Assert.Equal(positive, Roundtrip(SourceExpr.ValueNodeLiteral(positive)));
            Assert.Equal(negative, Roundtrip(SourceExpr.ValueNodeLiteral(negative)));
        }

        [Fact]
        public void ValueNodeLiteral_Long_HasLSuffixAndRoundtripsAsLongNotInt()
        {
            var node = ValueNode.Primitive.Long(12L);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.EndsWith("L", text);
            var roundtripped = Roundtrip(text);
            Assert.Equal(node, roundtripped);
            Assert.NotEqual(ValueNode.Primitive.Int(12), roundtripped);
        }

        [Fact]
        public void ValueNodeLiteral_Float12_RendersExactly12fPerDeliverable()
        {
            var node = ValueNode.Primitive.Float(12f);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("12f", text);
            var roundtripped = Roundtrip(text);
            Assert.Equal(node, roundtripped);
            Assert.NotEqual(ValueNode.Primitive.Int(12), roundtripped);
        }

        [Fact]
        public void ValueNodeLiteral_FloatFractional_Roundtrips()
        {
            var node = ValueNode.Primitive.Float(1.5f);

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_Double12_HasDSuffixAndRoundtripsAsDoubleNotIntOrFloat()
        {
            var node = ValueNode.Primitive.Double(12d);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.EndsWith("d", text);
            var roundtripped = Roundtrip(text);
            Assert.Equal(node, roundtripped);
            Assert.NotEqual(ValueNode.Primitive.Int(12), roundtripped);
            Assert.NotEqual(ValueNode.Primitive.Float(12f), roundtripped);
        }

        [Fact]
        public void ValueNodeLiteral_DoubleFractional_Roundtrips()
        {
            var node = ValueNode.Primitive.Double(1.5d);

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_String_UsesStringLiteralQuotingAndRoundtrips()
        {
            var node = ValueNode.Primitive.String("a\"b\\c");

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(SourceExpr.StringLiteral("a\"b\\c"), text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_EnumSingleMember_RendersFullyQualifiedAndRoundtripsNotFlags()
        {
            var node = new ValueNode.Enum("Game.Layers", new[] { "Ground" }, IsFlags: false);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("Game.Layers.Ground", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_EnumFlags_RendersExactOrPerDeliverableAndRoundtrips()
        {
            var node = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true);

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("Game.Layers.Ground | Game.Layers.Water", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_Vec2_RoundtripsIncludingNegativeComponent()
        {
            var node = new ValueNode.Vec2(new Vec2(-1.5f, 2f));

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_Vec3_Roundtrips()
        {
            var node = new ValueNode.Vec3(new Vec3(1f, 2f, 3f));

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_Vec4_Roundtrips()
        {
            var node = new ValueNode.Vec4(new Vec4(1f, 2f, 3f, 4f));

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_Quat_Roundtrips()
        {
            var node = new ValueNode.Quat(new Quat(0f, 0f, 0f, 1f));

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_Color_Roundtrips()
        {
            var node = new ValueNode.Color(new Color(1f, 0.5f, 0f, 1f));

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_List_OrderedNestedItems_Roundtrips()
        {
            var node = new ValueNode.List(new ValueNode[]
            {
                ValueNode.Primitive.Int(1),
                ValueNode.Primitive.Int(2),
                ValueNode.Primitive.String("x"),
            });

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_EmptyList_RendersNewObjectArrayAndRoundtrips()
        {
            var node = new ValueNode.List(System.Array.Empty<ValueNode>());

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("new object[] { }", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_Nested_StoredKeyOrder_ParseRoundtripsEqual()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("x", ValueNode.Primitive.Int(1)),
                new KeyValuePair<string, ValueNode>("y", ValueNode.Primitive.Int(2)),
            });
            var node = new ValueNode.Nested(fields);

            Assert.Equal(node, Roundtrip(SourceExpr.ValueNodeLiteral(node)));
        }

        [Fact]
        public void ValueNodeLiteral_UnsupportedSimpleToken_RendersRawTokenVerbatimPerDeliverable()
        {
            var node = new ValueNode.Unsupported("someToken");

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("someToken", text);
            Assert.Equal(node, Roundtrip(text));
        }

        [Fact]
        public void ValueNodeLiteral_UnsupportedExpression_RoundtripsVerbatim()
        {
            var node = new ValueNode.Unsupported("SomeWeirdExpr()");

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("SomeWeirdExpr()", text);
            Assert.Equal(node, Roundtrip(text));
        }
    }
}
