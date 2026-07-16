using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // M-Nested b1-t1: ValueNode.Nested(string TypeName, FieldMap Fields). The shipped bug
    // (SourceExpr rendered every Nested as the uncompilable "new object { ... }") plus the
    // parser/emitter contract that TypeName round-trips verbatim, fully-qualified. b1-t2
    // appends the equality/canonical-JSON/composition pins to this file.
    public class NestedValueTypeTests
    {
        private static ValueNode Roundtrip(string text) =>
            ValueNodeParser.Parse(SyntaxFactory.ParseExpression(text));

        private static SceneModel ModelWithField(string key, ValueNode value) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "go-1",
                    Name = "Root",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = "comp-1",
                            Type = new TypeRef("UnityEngine.Rigidbody"),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>(key, value),
                            }),
                        },
                    },
                },
            },
        };

        // #1 (RED regression) — the shipped bug: emit must never render "new object".
        [Fact]
        public void SourceExpr_NestedStruct_EmitsTypedInitializerNotObject()
        {
            var node = new ValueNode.Nested("Damage", new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("amount", ValueNode.Primitive.Float(5f)),
            }));

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("new Damage { amount = 5f }", text);
            Assert.DoesNotContain("new object", text);
        }

        // #2 (RED regression) — parse -> emit of hand-written source is byte-identical.
        [Fact]
        public void Parse_EmitNestedStruct_TextRoundTripsIdentically()
        {
            const string source = "new Damage { amount = 5f, kind = 1 }";

            var node = Roundtrip(source);
            var emitted = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(source, emitted);
            Assert.DoesNotContain("new object", emitted);
        }

        // #3 — parse captures the simple type name.
        [Fact]
        public void Parse_NestedStruct_CapturesTypeName()
        {
            var node = Assert.IsType<ValueNode.Nested>(Roundtrip("new Damage { amount = 5f }"));

            Assert.Equal("Damage", node.TypeName);
            Assert.Equal(ValueNode.Primitive.Float(5f), node.Fields["amount"]);
        }

        // #4 — the TypeNameOf-insufficiency pin: namespace must survive, NOT be dropped.
        [Fact]
        public void Parse_NamespacedNestedStruct_CapturesFullyQualifiedTypeName()
        {
            var node = Assert.IsType<ValueNode.Nested>(
                Roundtrip("new MyGame.Combat.Damage { amount = 5f }"));

            Assert.Equal("MyGame.Combat.Damage", node.TypeName);
        }

        // #5 — nested-in-nested: both levels capture their own TypeName.
        [Fact]
        public void Parse_NestedInNested_CapturesBothTypeNames()
        {
            var outer = Assert.IsType<ValueNode.Nested>(
                Roundtrip("new Outer { inner = new Inner { x = 1f } }"));

            Assert.Equal("Outer", outer.TypeName);
            var inner = Assert.IsType<ValueNode.Nested>(outer.Fields["inner"]);
            Assert.Equal("Inner", inner.TypeName);
            Assert.Equal(ValueNode.Primitive.Float(1f), inner.Fields["x"]);
        }

        // #6 — a malformed initializer element never throws; total parser, Unsupported verbatim.
        [Fact]
        public void Parse_NestedWithNonInitializerElement_YieldsUnsupported()
        {
            const string source = "new Damage { amount = 5f, SomeWeirdExpr() }";

            var node = Roundtrip(source);

            Assert.Equal(new ValueNode.Unsupported(source), node);
        }

        // #7 — emit is always namespace-qualified (no `using` synthesis).
        [Fact]
        public void SourceExpr_NamespacedNestedStruct_EmitsFullyQualifiedType()
        {
            var node = new ValueNode.Nested("MyGame.Combat.Damage", new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("amount", ValueNode.Primitive.Float(5f)),
            }));

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("new MyGame.Combat.Damage { amount = 5f }", text);
        }

        // #8 — a List of Nested renders each element typed; element type inferred, no "new object".
        [Fact]
        public void SourceExpr_ListOfNested_EmitsTypedArrayThatCompilesShape()
        {
            ValueNode.Nested Damage(float amount) => new("Damage", new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("amount", ValueNode.Primitive.Float(amount)),
            }));

            var node = new ValueNode.List(new ValueNode[] { Damage(1f), Damage(2f) });

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("new[] { new Damage { amount = 1f }, new Damage { amount = 2f } }", text);
            Assert.DoesNotContain("new object", text);
        }

        // #9 — equality (and hash) is keyed on TypeName as well as Fields.
        [Fact]
        public void Nested_EqualityKeyedOnTypeNameAndFields()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("amount", ValueNode.Primitive.Float(5f)),
            });

            var damage = new ValueNode.Nested("Damage", fields);
            var sameTypeSameFields = new ValueNode.Nested("Damage", fields);
            var differentType = new ValueNode.Nested("Heal", fields);

            Assert.Equal(damage, sameTypeSameFields);
            Assert.NotEqual(damage, differentType);
            Assert.NotEqual(damage.GetHashCode(), differentType.GetHashCode());
        }

        // #10 — canonical JSON round-trips TypeName, and camelCase "typeName" is actually present.
        // Uses an Enum field (not Primitive) to isolate the TypeName pin from the unrelated,
        // pre-existing gap where ValueNode.Primitive.Value round-trips through canonical JSON
        // as a boxed JsonElement rather than its original CLR type (out of this task's scope).
        [Fact]
        public void Nested_CanonicalRoundTrips()
        {
            var node = new ValueNode.Nested("MyGame.Combat.Damage", new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "faction", new ValueNode.Enum("Game.Faction", new[] { "Red" }, IsFlags: false)),
            }));
            var model = ModelWithField("value", node);

            var json = SceneModelSerializer.Serialize(model);
            var roundTripped = SceneModelSerializer.Deserialize(json);

            Assert.Contains("\"typeName\"", json);
            var value = roundTripped.Roots[0].Components[0].Fields["value"];
            var nested = Assert.IsType<ValueNode.Nested>(value);
            Assert.Equal("MyGame.Combat.Damage", nested.TypeName);
            Assert.Equal(node, nested);
        }

        // #11 — a list of Nested round-trips through parse -> emit byte-identically.
        [Fact]
        public void Parse_EmitNestedInList_TextRoundTripsIdentically()
        {
            const string source = "new[] { new Damage { amount = 1f }, new Damage { amount = 2f } }";

            var node = Roundtrip(source);
            var emitted = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(source, emitted);
        }

        // #12 — nested-in-nested round-trips through parse -> emit byte-identically.
        [Fact]
        public void Parse_EmitNestedInNested_TextRoundTripsIdentically()
        {
            const string source = "new Outer { inner = new Inner { x = 1f } }";

            var node = Roundtrip(source);
            var emitted = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal(source, emitted);
        }
    }
}
