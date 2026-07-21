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

        // b3-t2: instance-override diff + materialize. Target key is the (PrefabId, ObjectId) pair,
        // independent of the instance's own LogicalId/SourcePrefab.
        private static OverrideTarget Target() => new() { PrefabId = "prefab-guid-1", ObjectId = 12345 };

        private static (PrefabInstanceNode instance, SnapshotNode snapshotInstance, IdentityMap map) BuildMatched(
            PropertyOverride[]? desiredOverrides = null,
            PropertyOverride[]? snapshotOverrides = null,
            AddedComponent[]? desiredAddedComponents = null,
            AddedComponent[]? snapshotAddedComponents = null,
            OverrideTarget[]? desiredRemovedComponents = null,
            OverrideTarget[]? snapshotRemovedComponents = null)
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
                Overrides = desiredOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedComponents = desiredAddedComponents ?? System.Array.Empty<AddedComponent>(),
                RemovedComponents = desiredRemovedComponents ?? System.Array.Empty<OverrideTarget>(),
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance",
                Name = "Enemy",
                Transform = transform,
                SourcePrefabGuid = "prefab-guid-1",
                Overrides = snapshotOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedComponents = snapshotAddedComponents ?? System.Array.Empty<AddedComponent>(),
                RemovedComponents = snapshotRemovedComponents ?? System.Array.Empty<OverrideTarget>(),
            };

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

            return (instance, snapshotInstance, map);
        }

        [Fact]
        public void Materialize_AuthoredAddedComponent_AbsentFromSnapshot_EmitsAddInstanceComponent()
        {
            var target = Target();
            var added = new AddedComponent { Target = target, Component = new ComponentData { LogicalId = "light-1", Type = new TypeRef("UnityEngine.Light") } };
            var (instance, snapshotInstance, map) = BuildMatched(desiredAddedComponents: new[] { added });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var op = Assert.Single(plan.Ops.OfType<AddInstanceComponent>());
            Assert.Equal(target, op.Target);
            Assert.Equal("UnityEngine.Light", op.Component.Type.FullName);
        }

        [Fact]
        public void Materialize_AuthoredRemovedComponent_AbsentFromSnapshot_EmitsRemoveInstanceComponent()
        {
            var target = Target();
            var (instance, snapshotInstance, map) = BuildMatched(desiredRemovedComponents: new[] { target });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var op = Assert.Single(plan.Ops.OfType<RemoveInstanceComponent>());
            Assert.Equal(target, op.Target);
        }

        [Fact]
        public void Materialize_SnapshotOverride_AbsentFromDesired_EmitsRevert_NotSetToDefault()
        {
            var target = Target();
            var snapshotOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatched(snapshotOverrides: new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var revert = Assert.Single(plan.Ops.OfType<RevertInstanceOverride>());
            Assert.Equal(target, revert.Target);
            Assert.Equal("m_Health", revert.PropertyPath);
            Assert.Empty(plan.Ops.OfType<SetInstanceOverride>());
        }

        [Fact]
        public void Materialize_SnapshotAddedComponent_AbsentFromDesired_EmitsRevertAddedComponent()
        {
            var target = Target();
            var snapshotAdded = new AddedComponent { Target = target, Component = new ComponentData { LogicalId = "light-1", Type = new TypeRef("UnityEngine.Light") } };
            var (instance, snapshotInstance, map) = BuildMatched(snapshotAddedComponents: new[] { snapshotAdded });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var revert = Assert.Single(plan.Ops.OfType<RevertAddedComponent>());
            Assert.Equal(target, revert.Target);
            Assert.Equal("light-1", revert.ComponentLogicalId);
        }

        [Fact]
        public void Materialize_MatchedInstance_WithAuthoredOverride_EmitsNoInstantiatePrefab()
        {
            var target = Target();
            var desired = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatched(desiredOverrides: new[] { desired });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<InstantiatePrefab>());
        }

        [Fact]
        public void Materialize_DesiredOverride_AbsentFromSnapshot_EmitsSetInstanceOverride_CarryingValue()
        {
            var target = Target();
            var desired = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatched(desiredOverrides: new[] { desired });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var op = Assert.Single(plan.Ops.OfType<SetInstanceOverride>());
            Assert.Equal(target, op.Target);
            Assert.Equal("m_Health", op.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Int(50), op.Value);
        }

        [Fact]
        public void Materialize_DesiredObjectReferenceOverride_CarriesObjectReferenceThrough()
        {
            var target = Target();
            var assetRefValue = new ValueNode.AssetRef(new AssetRef { Guid = "mat-guid-1", DisplayPath = "Assets/Materials/Enemy.mat" });
            var desired = new PropertyOverride { Target = target, PropertyPath = "m_Material", Value = new ValueNode.Unsupported(""), ObjectReference = assetRefValue };
            var (instance, snapshotInstance, map) = BuildMatched(desiredOverrides: new[] { desired });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            var op = Assert.Single(plan.Ops.OfType<SetInstanceOverride>());
            Assert.Equal(assetRefValue, op.ObjectReference);
        }

        [Fact]
        public void Materialize_DesiredOverride_MatchesSnapshotExactly_EmitsNoSetInstanceOverride()
        {
            var target = Target();
            var matching = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = ValueNode.Primitive.Int(50) };
            var (instance, snapshotInstance, map) = BuildMatched(
                desiredOverrides: new[] { matching },
                snapshotOverrides: new[] { matching });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops.OfType<SetInstanceOverride>());
            Assert.Empty(plan.Ops.OfType<RevertInstanceOverride>());
        }
    }
}
