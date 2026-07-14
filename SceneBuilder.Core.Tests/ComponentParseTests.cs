using System;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b3-t1: .Component<T>(c => c.Set(...)) parsing — ComponentData population, the
    // field-key convention (raw string / typed-selector `member:` / private-by-path),
    // component IdentityMap entries, and component anchors.
    public class ComponentParseTests
    {
        // test 1
        [Fact]
        public void Parse_ComponentWithRawField_YieldsComponentData()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ComponentWithRawField);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal("UnityEngine.Rigidbody", component.Type.FullName);
            Assert.Equal(ValueNode.Primitive.Float(12f), component.Fields["m_Mass"]);
        }

        // test 2
        [Fact]
        public void Parse_TypedSetter_YieldsProvisionalMemberKey()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ComponentWithTypedSetter);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(ValueNode.Primitive.Float(12f), component.Fields["member:mass"]);
        }

        // test 3
        [Fact]
        public void Parse_PrivateFieldByPath_YieldsPrimitive()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ComponentWithPrivateField);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal("Game.Health", component.Type.FullName);
            Assert.Equal(ValueNode.Primitive.Int(100), component.Fields["_maxHealth"]);
        }

        [Fact]
        public void Parse_ComponentsInSourceOrder_ProduceIdentityEntriesAndAnchors()
        {
            var source = BuilderFixtures.ComponentSourceOrder;
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);

            // Appended after existing components in SOURCE order.
            Assert.Equal(2, node.Components.Length);
            Assert.Equal("UnityEngine.Rigidbody", node.Components[0].Type.FullName);
            Assert.Equal("Game.Health", node.Components[1].Type.FullName);

            var componentEntries = result.IdentityMap.Entries.Where(e => e.Kind == "Component").ToList();
            Assert.Equal(2, componentEntries.Count);

            var rigidbodyEntry = componentEntries[0];
            Assert.Equal("UnityEngine.Rigidbody", rigidbodyEntry.ComponentType);
            Assert.Equal(node.LogicalId, rigidbodyEntry.ParentLogicalId);
            Assert.Equal("", rigidbodyEntry.GlobalObjectId);

            var healthEntry = componentEntries[1];
            Assert.Equal("Game.Health", healthEntry.ComponentType);
            Assert.Equal(node.LogicalId, healthEntry.ParentLogicalId);
            Assert.Equal("", healthEntry.GlobalObjectId);

            Assert.NotEqual(rigidbodyEntry.LogicalId, healthEntry.LogicalId);

            var rigidbodyAnchor = result.ComponentAnchors[rigidbodyEntry.LogicalId];
            var healthAnchor = result.ComponentAnchors[healthEntry.LogicalId];

            var expectedRigidbodyCall = ExtractBalancedCall(source, ".Component<UnityEngine.Rigidbody>");
            var expectedHealthCall = ExtractBalancedCall(source, ".Component<Game.Health>");

            Assert.Equal(expectedRigidbodyCall, source.Substring(rigidbodyAnchor.Start, rigidbodyAnchor.Length));
            Assert.Equal(expectedHealthCall, source.Substring(healthAnchor.Start, healthAnchor.Length));
        }

        // regression guard: a node with no .Component<T>() calls stays empty and produces
        // no Component-kind IdentityMap entries; GameObject-only Anchors are unaffected.
        [Fact]
        public void Parse_NodeWithoutComponents_ComponentsEmptyAndNoComponentIdentityEntries()
        {
            var result = BuilderParser.Parse(BuilderFixtures.BareAdd);

            var node = Assert.Single(result.Model.Roots);
            Assert.Empty(node.Components);
            Assert.DoesNotContain(result.IdentityMap.Entries, e => e.Kind == "Component");
            Assert.Empty(result.ComponentAnchors);
            Assert.Single(result.Anchors);
        }

        // b3-t2: test 4 — every ValueNode kind lowers correctly from its C# literal form.
        [Fact]
        public void Parse_EachValueNodeKind_ParsesCorrectly()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ComponentAllValueKinds);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);
            var fields = component.Fields;

            Assert.Equal(ValueNode.Primitive.Bool(true), fields["flagBool"]);
            Assert.Equal(ValueNode.Primitive.Int(7), fields["countInt"]);
            Assert.Equal(ValueNode.Primitive.Long(100L), fields["bigLong"]);
            Assert.Equal(ValueNode.Primitive.Float(12f), fields["massFloat"]);
            Assert.Equal(ValueNode.Primitive.Double(2.5), fields["ratioDouble"]);
            Assert.Equal(ValueNode.Primitive.String("hello"), fields["label"]);
            Assert.Equal(new ValueNode.Enum("Game.Faction", new[] { "Enemy" }, false), fields["faction"]);
            Assert.Equal(new ValueNode.Vec2(new Vec2(1f, 2f)), fields["dir2"]);
            Assert.Equal(new ValueNode.Vec3(new Vec3(1f, 2f, 3f)), fields["dir3"]);
            Assert.Equal(new ValueNode.Vec4(new Vec4(1f, 2f, 3f, 4f)), fields["dir4"]);
            Assert.Equal(new ValueNode.Quat(new Quat(0f, 0f, 0f, 1f)), fields["rot"]);
            Assert.Equal(new ValueNode.Color(new Color(1f, 0f, 0f, 1f)), fields["tint"]);

            var expectedNested = new ValueNode.Nested(new FieldMap(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, ValueNode>("damage", ValueNode.Primitive.Int(10)),
                new System.Collections.Generic.KeyValuePair<string, ValueNode>("knockback", ValueNode.Primitive.Float(2.5f)),
            }));
            Assert.Equal(expectedNested, fields["impact"]);

            var expectedList = new ValueNode.List(new ValueNode[]
            {
                ValueNode.Primitive.Int(3),
                ValueNode.Primitive.Int(1),
                ValueNode.Primitive.Int(2),
            });
            Assert.Equal(expectedList, fields["order"]);
        }

        // b3-t2: test 4b — flags-enum members are ordinal-sorted regardless of operand order,
        // so `Ground|Water` and `Water|Ground` parse EQUAL and serialize deterministically.
        [Fact]
        public void Parse_FlagsEnum_YieldsSortedOrCombination()
        {
            var resultGroundWater = BuilderParser.Parse(BuilderFixtures.ComponentFlagsEnumGroundWater);
            var resultWaterGround = BuilderParser.Parse(BuilderFixtures.ComponentFlagsEnumWaterGround);

            var expected = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, true);

            var nodeA = Assert.Single(resultGroundWater.Model.Roots);
            var componentA = Assert.Single(nodeA.Components);
            Assert.Equal(expected, componentA.Fields["layers"]);

            var nodeB = Assert.Single(resultWaterGround.Model.Roots);
            var componentB = Assert.Single(nodeB.Components);
            Assert.Equal(expected, componentB.Fields["layers"]);

            var json1 = SceneModelSerializer.Serialize(resultGroundWater.Model);
            var json2 = SceneModelSerializer.Serialize(resultGroundWater.Model);
            Assert.Equal(json1, json2);
        }

        // b3-t2: test 13 (parse-half) — an unrecognized value form is never fail-loud; it
        // lowers to Unsupported(rawToken) carrying the verbatim argument source text.
        [Fact]
        public void Parse_UnsupportedValue_YieldsUnsupportedRawToken()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ComponentUnsupportedValue);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Unsupported("SomeWeirdExpr()"), component.Fields["m_Weird"]);

            var json = SceneModelSerializer.Serialize(result.Model);
            Assert.Contains("SomeWeirdExpr()", json);
        }

        // Locates `marker` in `source` and returns the substring from the marker's start
        // through the matching close-paren of its immediately-following parenthesized
        // argument list (balanced-paren scan) — i.e. the exact `.Component<T>(...)` call text.
        private static string ExtractBalancedCall(string source, string marker)
        {
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Fixture does not contain expected marker '{marker}'.");

            var openParen = source.IndexOf('(', start);
            Assert.True(openParen >= 0, $"No '(' found after marker '{marker}'.");

            var depth = 0;
            var i = openParen;
            for (; i < source.Length; i++)
            {
                if (source[i] == '(')
                {
                    depth++;
                }
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            Assert.True(depth == 0, $"Unbalanced parens scanning call for marker '{marker}'.");

            return source.Substring(start, i - start + 1);
        }
    }
}
