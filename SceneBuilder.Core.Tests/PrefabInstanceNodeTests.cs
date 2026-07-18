using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class PrefabInstanceNodeTests
    {
        private static PrefabInstanceNode SampleInstance() => new PrefabInstanceNode
        {
            LogicalId = "Root",
            Name = "Root",
            SourcePrefab = new AssetRef { Guid = "abc123def456", FileId = 100100 },
            OpaqueOverrides = new ValueNode.Unsupported("someRawToken"),
        };

        [Fact]
        public void PrefabInstance_InRoots_RoundTrips_ByteIdentical_AndPreservesFields()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[] { SampleInstance() },
            };

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(json, roundTripJson);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.GetProperty("roots")[0];
            Assert.True(root.TryGetProperty("$type", out var type), "expected \"$type\" discriminator on PrefabInstanceNode");
            Assert.Equal("PrefabInstance", type.GetString());
            Assert.Equal("abc123def456", root.GetProperty("sourcePrefab").GetProperty("guid").GetString());
            Assert.Equal("someRawToken", root.GetProperty("opaqueOverrides").GetProperty("rawToken").GetString());

            Assert.IsType<PrefabInstanceNode>(back.Roots[0]);
        }

        [Fact]
        public void PrefabInstance_NestedInChildren_RoundTrips_AndPreservesType()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "Parent",
                        Name = "Parent",
                        Children = new GameObjectNode[] { SampleInstance() with { LogicalId = "Parent/Instance", Name = "Instance" } },
                    },
                },
            };

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(json, roundTripJson);
            Assert.IsType<PrefabInstanceNode>(back.Roots[0].Children[0]);
        }

        [Fact]
        public void PrefabInstance_TwoSerializations_AreByteIdentical()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[] { SampleInstance() },
            };

            var json1 = SceneModelSerializer.Serialize(model);
            var json2 = SceneModelSerializer.Serialize(model);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void PlainNode_Serialization_Unchanged_NoTypeDiscriminator()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "Root",
                        Name = "Root",
                        Children = new GameObjectNode[]
                        {
                            new GameObjectNode { LogicalId = "Root/Child", Name = "Child" },
                        },
                    },
                },
            };

            var json = SceneModelSerializer.Serialize(model);

            Assert.DoesNotContain("$type", json);
        }

        [Fact]
        public void OpaqueOverrides_Null_OmitsKey_AndRoundTrips()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[] { SampleInstance() with { OpaqueOverrides = null } },
            };

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.DoesNotContain("opaqueOverrides", json);
            Assert.Equal(json, roundTripJson);
            Assert.IsType<PrefabInstanceNode>(back.Roots[0]);
        }
    }
}
