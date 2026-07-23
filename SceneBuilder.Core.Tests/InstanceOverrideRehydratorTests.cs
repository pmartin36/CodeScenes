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

        private static PropertyOverride Override(string componentType, string propertyPath, ValueNode? baseValue = null, PrefabInstanceKey? subKey = null, string childPath = "") =>
            new PropertyOverride
            {
                Target = new OverrideTarget { SubKey = subKey ?? new PrefabInstanceKey(), ComponentType = componentType, ChildPath = childPath },
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
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    ComponentType = "UnityEngine.BoxCollider",
                    Kind = "Property",
                    PropertyPath = "m_IsTrigger",
                    BaseValue = "0",
                },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Equal(ValueNode.Primitive.String("0"), instance.Overrides[0].BaseValue);
        }

        // m-nested-props b4-t3, spec #13: SubKey joins the rehydrate lookup key alongside
        // (ComponentType, PropertyPath). Two below-root overrides on DIFFERENT sub-objects sharing
        // the same ComponentType+PropertyPath must NOT cross-match — each threads its OWN record's
        // BaseValue by SubKey. A SubKey match survives an independently-renamed ChildPath (ChildPath
        // never participates in the key), matching InstanceOverrideRecord's own shape (no ChildPath
        // field to lose).
        [Fact]
        public void Sidecar_PersistsSubKey_ReDerivesChildPath()
        {
            var subKeyA = new PrefabInstanceKey { TargetObjectId = 1 };
            var subKeyB = new PrefabInstanceKey { TargetObjectId = 2 };

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[]
                    {
                        // Renamed in Unity since last save (ChildPath differs from any recorded shape) —
                        // must still match its OWN sidecar record by SubKey alone.
                        Override("UnityEngine.BoxCollider", "m_IsTrigger", subKey: subKeyA, childPath: "Cannon/Barrel"),
                        Override("UnityEngine.BoxCollider", "m_IsTrigger", subKey: subKeyB, childPath: "Turret/Barrel"),
                    }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord { SubKey = subKeyA, ComponentType = "UnityEngine.BoxCollider", Kind = "Property", PropertyPath = "m_IsTrigger", BaseValue = "0" },
                new InstanceOverrideRecord { SubKey = subKeyB, ComponentType = "UnityEngine.BoxCollider", Kind = "Property", PropertyPath = "m_IsTrigger", BaseValue = "1" },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Equal(ValueNode.Primitive.String("0"), instance.Overrides[0].BaseValue);
            Assert.Equal(ValueNode.Primitive.String("1"), instance.Overrides[1].BaseValue);
        }

        [Fact]
        public void Rehydrate_SubKeyMismatch_ComponentAndPathMatch_LeavesBaseValueNull()
        {
            var subKeyModel = new PrefabInstanceKey { TargetObjectId = 1 };
            var subKeyRecord = new PrefabInstanceKey { TargetObjectId = 2 };

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger", subKey: subKeyModel) }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord { SubKey = subKeyRecord, ComponentType = "UnityEngine.BoxCollider", Kind = "Property", PropertyPath = "m_IsTrigger", BaseValue = "0" },
            });

            var result = InstanceOverrideRehydrator.Rehydrate(model, sidecar);

            var instance = (PrefabInstanceNode)result.Roots[0];
            Assert.Null(instance.Overrides[0].BaseValue);
        }

        [Fact]
        public void Rehydrate_MismatchedComponentType_LeavesBaseValueNull()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    ComponentType = "UnityEngine.SphereCollider",
                    Kind = "Property",
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
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    ComponentType = "UnityEngine.BoxCollider",
                    Kind = "Property",
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
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger") }),
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
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger") }),
                },
            };
            var sidecar = Sidecar("instance-OTHER", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    ComponentType = "UnityEngine.BoxCollider",
                    Kind = "Property",
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
                    Instance("instance-1", new[] { Override("UnityEngine.BoxCollider", "m_IsTrigger", existing) }),
                },
            };
            var sidecar = Sidecar("instance-1", "PrefabInstance", new[]
            {
                new InstanceOverrideRecord
                {
                    ComponentType = "UnityEngine.BoxCollider",
                    Kind = "Property",
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
