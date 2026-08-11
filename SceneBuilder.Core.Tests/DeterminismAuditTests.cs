using System.Collections.Generic;
using System.Globalization;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Characterization proofs that Canonicalize(model) is a pure function of the value:
    // culture-independent and insensitive to author field order or float construction path.
    public class DeterminismAuditTests
    {
        private static SceneModel ModelWithFloats(
            IEnumerable<KeyValuePair<string, ValueNode>> fields) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "Root",
                    Name = "Root",
                    Transform = new TransformData
                    {
                        Position = new Vec3(1.5f, 2.25f, 3.75f),
                        Rotation = Quat.Identity,
                        Scale = Vec3.One,
                    },
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = "Root#Rigidbody",
                            Type = new TypeRef("UnityEngine.Rigidbody"),
                            Fields = new FieldMap(fields),
                        },
                    },
                },
            },
        };

        private static IdentityMap SampleIdentityMap() => new IdentityMap
        {
            SchemaVersion = 1,
            Scene = "Assets/Scenes/Demo.unity",
            Entries = new IdentityMapEntry[]
            {
                new IdentityMapEntry { LogicalId = "Root", GlobalObjectId = "", Kind = "GameObject", ParentLogicalId = null },
                new IdentityMapEntry { LogicalId = "Root/Child", GlobalObjectId = "", Kind = "GameObject", ParentLogicalId = "Root" },
            },
            Assets = System.Array.Empty<AssetEntry>(),
        };

        private static string UnderCulture(string cultureName, System.Func<string> produce)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);
                return produce();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void SceneModel_WithFloats_ByteIdentical_UnderDeDE()
        {
            var model = ModelWithFloats(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.1f)),
            });

            var invariant = SceneModelSerializer.Serialize(model);
            var underDeDE = UnderCulture("de-DE", () => SceneModelSerializer.Serialize(model));

            Assert.Equal(invariant, underDeDE);
            Assert.Contains("0.1", underDeDE);
            Assert.DoesNotContain("0,1", underDeDE);
        }

        [Fact]
        public void IdentityMap_ReSerialize_ByteIdentical_AndUnderDeDE()
        {
            var map = SampleIdentityMap();

            var json1 = IdentityMapJson.Serialize(map);
            var json2 = IdentityMapJson.Serialize(map);
            var underDeDE = UnderCulture("de-DE", () => IdentityMapJson.Serialize(map));

            Assert.Equal(json1, json2);
            Assert.Equal(json1, underDeDE);
        }

        [Fact]
        public void SceneModel_FieldAuthorOrder_CanonicalizesIdentically()
        {
            var massFirst = ModelWithFloats(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.1f)),
            });
            var dragFirst = ModelWithFloats(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.1f)),
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
            });

            var massFirstJson = SceneModelSerializer.Serialize(massFirst);
            var dragFirstJson = SceneModelSerializer.Serialize(dragFirst);

            Assert.Equal(massFirstJson, dragFirstJson);
        }

        [Fact]
        public void SceneModel_FloatRepresentation_CanonicalizesIdentically()
        {
            var literalFloats = ModelWithFloats(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(0.1f)),
            });
            var computedFloats = ModelWithFloats(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(10f / 2f)),
                new KeyValuePair<string, ValueNode>("m_Drag", ValueNode.Primitive.Float(1f / 10f)),
            });

            var literalJson = SceneModelSerializer.Serialize(literalFloats);
            var computedJson = SceneModelSerializer.Serialize(computedFloats);

            Assert.Equal(literalJson, computedJson);
        }
    }
}
