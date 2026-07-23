using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t4: canonical serialization order of PrefabInstanceNode's override collections must be
    // deterministic regardless of input order (spec #10 ordering clause, #11 shuffle-stability).
    public class OverrideDeterminismTests
    {
        private static PrefabInstanceNode BaseInstance() => new PrefabInstanceNode
        {
            LogicalId = "Root",
            Name = "Root",
            SourcePrefab = new AssetRef { Guid = "abc123def456", FileId = 100100 },
        };

        private static SceneModel ModelWith(PrefabInstanceNode instance) =>
            new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };

        [Fact]
        public void ShuffledOverrides_SerializeIdentically()
        {
            var target1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var target2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var ov1 = new PropertyOverride { Target = target1, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(1) };
            var ov2 = new PropertyOverride { Target = target1, PropertyPath = "m_Mana", Value = ValueNode.Primitive.Int(2) };
            var ov3 = new PropertyOverride { Target = target2, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(3) };

            var modelA = ModelWith(BaseInstance() with { Overrides = new[] { ov1, ov2, ov3 } });
            var modelB = ModelWith(BaseInstance() with { Overrides = new[] { ov3, ov2, ov1 } });

            Assert.Equal(SceneModelSerializer.Serialize(modelA), SceneModelSerializer.Serialize(modelB));
        }

        [Fact]
        public void ShuffledAddedComponents_SerializeIdentically()
        {
            var target1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var target2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var c1 = new AddedComponent { Target = target1, Component = new ComponentData { LogicalId = "Root/A", Type = new TypeRef("UnityEngine.BoxCollider") } };
            var c2 = new AddedComponent { Target = target1, Component = new ComponentData { LogicalId = "Root/B", Type = new TypeRef("UnityEngine.Light") } };
            var c3 = new AddedComponent { Target = target2, Component = new ComponentData { LogicalId = "Root/C", Type = new TypeRef("UnityEngine.Rigidbody") } };

            var modelA = ModelWith(BaseInstance() with { AddedComponents = new[] { c1, c2, c3 } });
            var modelB = ModelWith(BaseInstance() with { AddedComponents = new[] { c3, c2, c1 } });

            Assert.Equal(SceneModelSerializer.Serialize(modelA), SceneModelSerializer.Serialize(modelB));
        }

        [Fact]
        public void ShuffledRemovedComponentsAndGameObjects_SerializeIdentically()
        {
            var t1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var t2 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 50 } };
            var t3 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 1 } };

            var modelA = ModelWith(BaseInstance() with { RemovedComponents = new[] { t2, t1, t3 }, RemovedGameObjects = new[] { t2, t1, t3 } });
            var modelB = ModelWith(BaseInstance() with { RemovedComponents = new[] { t3, t1, t2 }, RemovedGameObjects = new[] { t3, t1, t2 } });

            Assert.Equal(SceneModelSerializer.Serialize(modelA), SceneModelSerializer.Serialize(modelB));
        }

        [Fact]
        public void CanonicalOrder_OverridesSortedBySubKeyThenComponentTypeThenPropertyPath()
        {
            var target1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var target2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var ov1 = new PropertyOverride { Target = target1, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(1) };
            var ov2 = new PropertyOverride { Target = target1, PropertyPath = "m_Mana", Value = ValueNode.Primitive.Int(2) };
            var ov3 = new PropertyOverride { Target = target2, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(3) };

            // input order deliberately scrambled relative to expected canonical order
            var model = ModelWith(BaseInstance() with { Overrides = new[] { ov3, ov2, ov1 } });

            var json = SceneModelSerializer.Serialize(model);
            using var doc = JsonDocument.Parse(json);
            var overrides = doc.RootElement.GetProperty("roots")[0].GetProperty("overrides");

            Assert.Equal("m_Health", overrides[0].GetProperty("propertyPath").GetString());
            Assert.Equal("m_Mana", overrides[1].GetProperty("propertyPath").GetString());
            Assert.Equal("m_Health", overrides[2].GetProperty("propertyPath").GetString());
            Assert.Equal(200, overrides[0].GetProperty("target").GetProperty("subKey").GetProperty("targetObjectId").GetInt64());
            Assert.Equal(100, overrides[2].GetProperty("target").GetProperty("subKey").GetProperty("targetObjectId").GetInt64());
        }

        [Fact]
        public void Determinism_NestedCollections_OrderingIsPairKeyed()
        {
            var target1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var target2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var ov1 = new PropertyOverride { Target = target1, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(1) };
            var ov2 = new PropertyOverride { Target = target2, PropertyPath = "m_Mana", Value = ValueNode.Primitive.Int(2) };

            var ac1 = new AddedComponent { Target = target1, Component = new ComponentData { LogicalId = "Root/A", Type = new TypeRef("UnityEngine.BoxCollider") } };
            var ac2 = new AddedComponent { Target = target2, Component = new ComponentData { LogicalId = "Root/B", Type = new TypeRef("UnityEngine.Light") } };

            var rc1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var rc2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var rg1 = new OverrideTarget { SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var rg2 = new OverrideTarget { SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            // AddedGameObjects: lower-pair parent (1,200) carries ChildPath "zzz" (lexicographically
            // LARGER), higher-pair parent (2,100) carries ChildPath "aaa" (lexicographically SMALLER).
            // If ordering ever keyed on ChildPath instead of SubKey pair, this would sort in reverse.
            var agParent1 = new OverrideTarget { SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 }, ChildPath = "zzz" };
            var agParent2 = new OverrideTarget { SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 }, ChildPath = "aaa" };
            var ag1 = new AddedGameObject { Parent = agParent1, Node = new GameObjectNode { Name = "ChildA" } };
            var ag2 = new AddedGameObject { Parent = agParent2, Node = new GameObjectNode { Name = "ChildB" } };

            var modelA = ModelWith(BaseInstance() with
            {
                Overrides = new[] { ov1, ov2 },
                AddedComponents = new[] { ac1, ac2 },
                RemovedComponents = new[] { rc1, rc2 },
                AddedGameObjects = new[] { ag1, ag2 },
                RemovedGameObjects = new[] { rg1, rg2 },
            });
            var modelB = ModelWith(BaseInstance() with
            {
                Overrides = new[] { ov2, ov1 },
                AddedComponents = new[] { ac2, ac1 },
                RemovedComponents = new[] { rc2, rc1 },
                AddedGameObjects = new[] { ag2, ag1 },
                RemovedGameObjects = new[] { rg2, rg1 },
            });

            var jsonA = SceneModelSerializer.Serialize(modelA);
            var jsonB = SceneModelSerializer.Serialize(modelB);
            Assert.Equal(jsonA, jsonB);

            using var doc = JsonDocument.Parse(jsonA);
            var addedGameObjects = doc.RootElement.GetProperty("roots")[0].GetProperty("addedGameObjects");

            // SubKey pair (1,200) sorts before (2,100) even though its ChildPath ("zzz") is
            // lexicographically larger than the other's ("aaa") -- ChildPath must NOT participate.
            Assert.Equal("ChildA", addedGameObjects[0].GetProperty("node").GetProperty("name").GetString());
            Assert.Equal("ChildB", addedGameObjects[1].GetProperty("node").GetProperty("name").GetString());
        }

        [Fact]
        public void RoundTrip_AfterShuffle_ByteStable()
        {
            var target1 = new OverrideTarget { ComponentType = "aaa", SubKey = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 200 } };
            var target2 = new OverrideTarget { ComponentType = "bbb", SubKey = new PrefabInstanceKey { TargetPrefabId = 2, TargetObjectId = 100 } };

            var ov1 = new PropertyOverride { Target = target1, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(1) };
            var ov2 = new PropertyOverride { Target = target2, PropertyPath = "m_Mana", Value = ValueNode.Primitive.Int(2) };

            var shuffled = ModelWith(BaseInstance() with { Overrides = new[] { ov2, ov1 } });
            var sorted = ModelWith(BaseInstance() with { Overrides = new[] { ov1, ov2 } });

            var shuffledJson = SceneModelSerializer.Serialize(shuffled);
            var back = SceneModelSerializer.Deserialize(shuffledJson);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(SceneModelSerializer.Serialize(sorted), roundTripJson);
        }
    }
}
