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

        [Fact]
        public void OverrideTarget_Equality_ByPair()
        {
            var a = new OverrideTarget { ComponentType = "abc123def456:100100", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } };
            var b = new OverrideTarget { ComponentType = "abc123def456:100100", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } };
            var differentComponentType = new OverrideTarget { ComponentType = "otherguid:200200", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } };
            var differentSubKey = new OverrideTarget { ComponentType = "abc123def456:100100", SubKey = new PrefabInstanceKey { TargetObjectId = 999999 } };

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, differentComponentType);
            Assert.NotEqual(a, differentSubKey);
        }

        [Fact]
        public void PrefabInstanceNode_PopulatedOverrideCollections_RoundTrip_ByteIdentical()
        {
            var target1 = new OverrideTarget { ComponentType = "abc123def456:100100", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } };
            var target2 = new OverrideTarget { ComponentType = "abc123def456:200200", SubKey = new PrefabInstanceKey { TargetObjectId = 200200 } };

            var instance = SampleInstance() with
            {
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = target1,
                        PropertyPath = "m_LocalPosition.x",
                        Value = ValueNode.Primitive.Float(3.5f),
                    },
                    new PropertyOverride
                    {
                        Target = target2,
                        PropertyPath = "m_Materials.Array.data[0]",
                        Value = new ValueNode.AssetRef(new AssetRef { Guid = "matguid", FileId = 2100000 }),
                        ObjectReference = new ValueNode.AssetRef(new AssetRef { Guid = "matguid", FileId = 2100000 }),
                        BaseValue = new ValueNode.AssetRef(new AssetRef { Guid = "defaultmatguid", FileId = 2100000 }),
                    },
                },
                AddedComponents = new[]
                {
                    new AddedComponent
                    {
                        Target = target1,
                        Component = new ComponentData { LogicalId = "Root/Added", Type = new TypeRef("UnityEngine.BoxCollider") },
                    },
                },
                RemovedComponents = new[] { target2 },
                AddedGameObjects = new[] { new AddedGameObject { Parent = target1, Node = new GameObjectNode { LogicalId = "Root/NewChild", Name = "NewChild" } } },
                RemovedGameObjects = new[] { target1 },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(json, roundTripJson);

            var backInstance = Assert.IsType<PrefabInstanceNode>(back.Roots[0]);
            Assert.Equal(2, backInstance.Overrides.Length);
            Assert.Single(backInstance.AddedComponents);
            Assert.Single(backInstance.RemovedComponents);
            Assert.Single(backInstance.AddedGameObjects);
            Assert.Single(backInstance.RemovedGameObjects);
        }

        [Fact]
        public void PrefabInstanceNode_AddedGameObject_RoundTrips_PreservesParentSubKeyAndNode()
        {
            var parentA = new OverrideTarget { ComponentType = "", ChildPath = "Turret", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } };
            var parentB = new OverrideTarget { ComponentType = "", ChildPath = "", SubKey = new PrefabInstanceKey { TargetObjectId = 200200 } };

            var instance = SampleInstance() with
            {
                AddedGameObjects = new[]
                {
                    new AddedGameObject { Parent = parentA, Node = new GameObjectNode { LogicalId = "Root/Turret/MuzzleFlash", Name = "MuzzleFlash" } },
                    new AddedGameObject { Parent = parentB, Node = new GameObjectNode { LogicalId = "Root/NewChild", Name = "NewChild" } },
                },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(json, roundTripJson);

            var backInstance = Assert.IsType<PrefabInstanceNode>(back.Roots[0]);
            Assert.Equal(2, backInstance.AddedGameObjects.Length);
            Assert.Equal(100100UL, backInstance.AddedGameObjects[0].Parent.SubKey.TargetObjectId);
            Assert.Equal("MuzzleFlash", backInstance.AddedGameObjects[0].Node.Name);
            Assert.Equal(200200UL, backInstance.AddedGameObjects[1].Parent.SubKey.TargetObjectId);
            Assert.Equal("NewChild", backInstance.AddedGameObjects[1].Node.Name);
        }

        [Fact]
        public void PropertyOverride_NullObjectReferenceAndBaseValue_OmitKeys()
        {
            var instance = SampleInstance() with
            {
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = new OverrideTarget { ComponentType = "abc123def456:100100", SubKey = new PrefabInstanceKey { TargetObjectId = 100100 } },
                        PropertyPath = "m_LocalPosition.x",
                        Value = ValueNode.Primitive.Float(3.5f),
                    },
                },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var json = SceneModelSerializer.Serialize(model);

            Assert.DoesNotContain("objectReference", json);
            Assert.DoesNotContain("baseValue", json);
        }

        [Fact]
        public void PrefabInstanceNode_EmptyOverrideCollections_EmitAsEmptyArrays()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { SampleInstance() } };
            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.GetProperty("roots")[0];

            Assert.Equal(JsonValueKind.Array, root.GetProperty("overrides").ValueKind);
            Assert.Equal(0, root.GetProperty("overrides").GetArrayLength());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("addedComponents").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("removedComponents").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("addedGameObjects").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("removedGameObjects").ValueKind);
        }
    }
}
