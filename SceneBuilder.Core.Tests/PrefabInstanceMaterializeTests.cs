using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class PrefabInstanceMaterializeTests
    {
        [Fact]
        public void Materialize_NewPrefabInstance_ProducesInstantiateThenTransformFields_NoCreateOrAddComponent()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<CreateObject>());
            Assert.Empty(plan.Ops.OfType<AddComponent>());

            Assert.Equal(4, plan.Ops.Length);
            var instantiate = Assert.IsType<InstantiatePrefab>(plan.Ops[0]);
            Assert.Equal("instance-1", instantiate.LogicalId);
            Assert.Equal("prefab-guid-1", instantiate.Guid);
            Assert.Null(instantiate.ParentLogicalId);
            Assert.Equal(0, instantiate.SiblingIndex);

            var setPosition = Assert.IsType<SetField>(plan.Ops[1]);
            Assert.Equal("m_LocalPosition", setPosition.Path);
            var setRotation = Assert.IsType<SetField>(plan.Ops[2]);
            Assert.Equal("m_LocalRotation", setRotation.Path);
            var setScale = Assert.IsType<SetField>(plan.Ops[3]);
            Assert.Equal("m_LocalScale", setScale.Path);
        }

        [Fact]
        public void Materialize_NestedPrefabInstance_InstantiateCarriesParentLogicalIdAndIndex()
        {
            var coin = new PrefabInstanceNode
            {
                LogicalId = "coin-1",
                Name = "Coin",
                SourcePrefab = new AssetRef { Guid = "coin-guid" },
            };
            var pickups = new GameObjectNode { LogicalId = "pickups-1", Name = "Pickups", Children = new GameObjectNode[] { coin } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { pickups } };

            var snapshotPickups = new SnapshotNode { GlobalObjectId = "goid-pickups", Name = "Pickups" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotPickups } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "pickups-1", GlobalObjectId = "goid-pickups", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<CreateObject>());
            var instantiate = Assert.Single(plan.Ops.OfType<InstantiatePrefab>());
            Assert.Equal("coin-1", instantiate.LogicalId);
            Assert.Equal("pickups-1", instantiate.ParentLogicalId);
            Assert.Equal(0, instantiate.SiblingIndex);
        }

        [Fact]
        public void Materialize_RemovedPrefabInstance_ProducesOnlyDestroy()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var snapshotInstance = new SnapshotNode { GlobalObjectId = "goid-instance", Name = "Enemy", SourcePrefabGuid = "prefab-guid-1" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance", Kind = "PrefabInstance",
                        SourcePrefabGuid = "prefab-guid-1",
                    },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var destroy = Assert.Single(plan.Ops);
            var destroyOp = Assert.IsType<DestroyObject>(destroy);
            Assert.Equal("instance-1", destroyOp.LogicalId);
        }

        [Fact]
        public void Materialize_MovedPrefabInstance_ReparentsOnly_NeverDestroyOrInstantiate()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
            };
            var parentA = new GameObjectNode { LogicalId = "parentA-1", Name = "ParentA" };
            var parentB = new GameObjectNode { LogicalId = "parentB-1", Name = "ParentB", Children = new GameObjectNode[] { instance } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parentA, parentB } };

            var snapshotInstance = new SnapshotNode { GlobalObjectId = "goid-instance", Name = "Enemy", Transform = transform, SourcePrefabGuid = "prefab-guid-1" };
            var snapshotParentA = new SnapshotNode { GlobalObjectId = "goid-parentA", Name = "ParentA", Children = new[] { snapshotInstance } };
            var snapshotParentB = new SnapshotNode { GlobalObjectId = "goid-parentB", Name = "ParentB" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParentA, snapshotParentB } };

            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "parentA-1", GlobalObjectId = "goid-parentA", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "parentB-1", GlobalObjectId = "goid-parentB", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "instance-1", GlobalObjectId = "goid-instance", Kind = "PrefabInstance", SourcePrefabGuid = "prefab-guid-1" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<InstantiatePrefab>());
            Assert.Empty(plan.Ops.OfType<DestroyObject>());

            var setParent = Assert.Single(plan.Ops.OfType<SetParent>(), op => op.LogicalId == "instance-1");
            Assert.Equal("parentB-1", setParent.ParentLogicalId);
        }
    }
}
