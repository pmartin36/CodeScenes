using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b6-t2 (iteration 3, seam fix): InstanceOverrideRehydrator.Rehydrate is the LOAD-path stage that
    // threads a persisted InstanceOverrideRecord.BaseValue back onto the matching desired-model
    // PropertyOverride.BaseValue before Diff/Reconcile. Without it, InstanceOverrideDiff.
    // DetectStaleOverrides always short-circuits on `desiredOverride.BaseValue is null` and the
    // stale-conflict deliverable never fires through the real adapter (scope/final.md finding).
    public class InstanceOverrideRehydratorTests
    {
        private static PrefabInstanceNode Instance(string logicalId, PropertyOverride[] overrides) =>
            new PrefabInstanceNode
            {
                LogicalId = logicalId,
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
                Overrides = overrides,
            };

        private static PropertyOverride Override(string prefabId, long objectId, string propertyPath, ValueNode? baseValue = null) =>
            new PropertyOverride
            {
                Target = new OverrideTarget { PrefabId = prefabId, ObjectId = objectId },
                PropertyPath = propertyPath,
                Value = ValueNode.Primitive.Bool(true),
                BaseValue = baseValue,
            };

        private static IdentityMap Sidecar(string logicalId, string kind, InstanceOverrideRecord[]? records) =>
            new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = logicalId,
                        Kind = kind,
                        Overrides = records,
                    },
                },
            };

        [Fact]
        public void Rehydrate_MatchingPairAndPath_ThreadsBaseValueOntoOverride()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    PrefabId = "type:UnityEngine.BoxCollider",
                    ObjectId = 0,
                    PropertyPath = "m_IsTrigger",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Equal(ValueNode.Primitive.String("0"), instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_MismatchedPrefabId_LeavesBaseValueNull()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    PrefabId = "type:UnityEngine.SphereCollider",
                    ObjectId = 0,
                    PropertyPath = "m_IsTrigger",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Null(instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_MismatchedPropertyPath_LeavesBaseValueNull()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    PrefabId = "type:UnityEngine.BoxCollider",
                    ObjectId = 0,
                    PropertyPath = "m_Enabled",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Null(instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_SidecarEntryHasNullOverrides_LeavesBaseValueNull()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", null);

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Null(instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_NoMatchingSidecarEntry_LeavesBaseValueNull()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-OTHER", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    PrefabId = "type:UnityEngine.BoxCollider",
                    ObjectId = 0,
                    PropertyPath = "m_IsTrigger",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Null(instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_AlreadySetBaseValue_NotClobbered()
        {
            var existing = ValueNode.Primitive.String("1");
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("type:UnityEngine.BoxCollider", 0, "m_IsTrigger", existing) }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    PrefabId = "type:UnityEngine.BoxCollider",
                    ObjectId = 0,
                    PropertyPath = "m_IsTrigger",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Equal(existing, instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_EmptyModelAndSidecar_ReturnsModelWithNoRoots()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };
            var sidecar = new IdentityMap();

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            Assert.Empty(result.Roots);
        }
    }
}
