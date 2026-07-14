using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class CanonicalValueNodeTests
    {
        private static ComponentData ComponentWithFields(params (string Key, ValueNode Value)[] fields)
        {
            var entries = new List<KeyValuePair<string, ValueNode>>();
            foreach (var (key, value) in fields)
            {
                entries.Add(new KeyValuePair<string, ValueNode>(key, value));
            }

            return new ComponentData
            {
                LogicalId = "comp-1",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(entries),
            };
        }

        private static SceneModel ModelWith(ComponentData component) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "go-1",
                    Name = "Root",
                    Components = new[] { component },
                },
            },
        };

        [Fact]
        public void Canonical_DeterminismPerKind_And_ExactEquality()
        {
            ValueNode[] everyKind =
            {
                ValueNode.Primitive.Bool(true),
                ValueNode.Primitive.Int(12),
                ValueNode.Primitive.Long(12L),
                ValueNode.Primitive.Float(1.5f),
                ValueNode.Primitive.Double(1.5d),
                ValueNode.Primitive.String("hello"),
                new ValueNode.Enum("Game.Faction", new[] { "Red" }, IsFlags: false),
                new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true),
                new ValueNode.Vec2(new Vec2(1, 2)),
                new ValueNode.Vec3(new Vec3(1, 2, 3)),
                new ValueNode.Vec4(new Vec4(1, 2, 3, 4)),
                new ValueNode.Quat(new Quat(0, 0, 0, 1)),
                new ValueNode.Color(new Color(1, 0, 0, 1)),
                new ValueNode.Nested(new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("zeta", ValueNode.Primitive.Int(1)),
                    new KeyValuePair<string, ValueNode>("alpha", ValueNode.Primitive.Int(2)),
                })),
                new ValueNode.List(new ValueNode[] { ValueNode.Primitive.Int(1), ValueNode.Primitive.Int(2) }),
                new ValueNode.Unsupported("SomeWeirdExpr()"),
            };

            foreach (var node in everyKind)
            {
                var model = ModelWith(ComponentWithFields(("value", node)));

                var json1 = SceneModelSerializer.Serialize(model);
                var json2 = SceneModelSerializer.Serialize(model);

                Assert.Equal(json1, json2);
            }
        }

        [Fact]
        public void Canonical_StructurallyEqualModels_WithDifferentFieldInsertionOrder_SerializeIdentically()
        {
            var modelA = ModelWith(ComponentWithFields(
                ("m_Mass", ValueNode.Primitive.Float(1.5f)),
                ("_health", ValueNode.Primitive.Int(100))));
            var modelB = ModelWith(ComponentWithFields(
                ("_health", ValueNode.Primitive.Int(100)),
                ("m_Mass", ValueNode.Primitive.Float(1.5f))));

            var jsonA = SceneModelSerializer.Serialize(modelA);
            var jsonB = SceneModelSerializer.Serialize(modelB);

            Assert.Equal(jsonA, jsonB);
        }

        [Fact]
        public void Canonical_Fields_EmittedInSortedKeyOrder()
        {
            var component = ComponentWithFields(
                ("tint", ValueNode.Primitive.Int(1)),
                ("m_Mass", ValueNode.Primitive.Float(1.5f)),
                ("_health", ValueNode.Primitive.Int(100)));
            var model = ModelWith(component);

            var json = SceneModelSerializer.Serialize(model);

            var healthIndex = json.IndexOf("\"_health\"");
            var massIndex = json.IndexOf("\"m_Mass\"");
            var tintIndex = json.IndexOf("\"tint\"");

            Assert.True(healthIndex >= 0 && massIndex >= 0 && tintIndex >= 0, "expected all field keys present verbatim (not camelCased)");
            Assert.True(healthIndex < massIndex, "expected ordinal-sorted keys: _health before m_Mass");
            Assert.True(massIndex < tintIndex, "expected ordinal-sorted keys: m_Mass before tint");
        }

        [Fact]
        public void Canonical_Nested_Fields_EmittedInSortedKeyOrder()
        {
            var nested = new ValueNode.Nested(new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("zeta", ValueNode.Primitive.Int(1)),
                new KeyValuePair<string, ValueNode>("alpha", ValueNode.Primitive.Int(2)),
            }));
            var model = ModelWith(ComponentWithFields(("nested", nested)));

            var json = SceneModelSerializer.Serialize(model);

            var alphaIndex = json.IndexOf("\"alpha\"");
            var zetaIndex = json.IndexOf("\"zeta\"");

            Assert.True(alphaIndex >= 0 && zetaIndex >= 0);
            Assert.True(alphaIndex < zetaIndex, "expected ordinal-sorted nested keys: alpha before zeta");
        }

        [Fact]
        public void Canonical_Primitive_HasSingleKindDiscriminator_AndDistinctPrimitiveTypeTag()
        {
            var model = ModelWith(ComponentWithFields(("value", ValueNode.Primitive.Float(12f))));

            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var valueElement = doc.RootElement
                .GetProperty("roots")[0]
                .GetProperty("components")[0]
                .GetProperty("fields")
                .GetProperty("value");

            // A duplicate "kind" key WITHIN the Primitive's own object would make it
            // ambiguous under strict parsing (or silently drop one value) — scope the
            // text scan to just this element's raw text, not the whole document (other
            // sibling objects, e.g. Transform, legitimately have their own "kind" property).
            var kindOccurrencesOnPrimitive = Regex.Matches(valueElement.GetRawText(), "\"kind\"").Count;
            Assert.Equal(1, kindOccurrencesOnPrimitive);

            Assert.Equal("Primitive", valueElement.GetProperty("kind").GetString());
            Assert.True(valueElement.TryGetProperty("primitiveType", out var primitiveType), "expected distinct primitiveType tag");
            Assert.Equal("Float", primitiveType.GetString());
        }

        [Fact]
        public void Canonical_Vec3_TinyFloatDifference_SerializesDifferently_ExactEquality()
        {
            var z = 3f;
            var zNextUlp = System.MathF.BitIncrement(z);
            Assert.NotEqual(z, zNextUlp); // sanity: literal really is a distinct float

            var a = new ValueNode.Vec3(new Vec3(1, 2, z));
            var b = new ValueNode.Vec3(new Vec3(1, 2, zNextUlp));

            var modelA = ModelWith(ComponentWithFields(("value", a)));
            var modelB = ModelWith(ComponentWithFields(("value", b)));

            var jsonA = SceneModelSerializer.Serialize(modelA);
            var jsonB = SceneModelSerializer.Serialize(modelB);

            Assert.NotEqual(jsonA, jsonB);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Canonical_Unsupported_SerializesVerbatimRawToken()
        {
            var model = ModelWith(ComponentWithFields(("value", new ValueNode.Unsupported("SomeWeirdExpr()"))));

            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var valueElement = doc.RootElement
                .GetProperty("roots")[0]
                .GetProperty("components")[0]
                .GetProperty("fields")
                .GetProperty("value");

            Assert.Equal("Unsupported", valueElement.GetProperty("kind").GetString());
            Assert.Equal("SomeWeirdExpr()", valueElement.GetProperty("rawToken").GetString());
        }
    }
}
