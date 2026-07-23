using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class PrefabInstanceDiffTests
    {
        [Fact]
        public void Diff_NewPrefabInstance_EmitsAddInstance_NotAddNode()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.DoesNotContain(changeSet.Ops.OfType<AddNode>(), op => op.LogicalId == "instance-1");

            var add = Assert.Single(changeSet.Ops.OfType<AddInstance>());
            Assert.Equal("instance-1", add.LogicalId);
            Assert.Equal("prefab-guid-1", add.Guid);
            Assert.Null(add.ParentLogicalId);
            Assert.Equal(0, add.SiblingIndex);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>(), op => op.LogicalId == "instance-1");
            Assert.Equal(transform.Position, setTransform.Transform.Position);
        }

        [Fact]
        public void Diff_TwoSamePrefabInstances_DistinctGlobalObjectId_DoesNotCrossAssignTransforms()
        {
            // Snapshot positions are swapped relative to desired: instance-1's actual position is
            // instance-2's desired value and vice versa. Cross-assigned matching would compare each
            // desired instance against the WRONG snapshot entry and see no diff at all (a silent
            // missed update); correct pair-key matching must diff BOTH instances against their own
            // snapshot entry.
            var posA = new Vec3(1, 1, 1);
            var posB = new Vec3(2, 2, 2);

            var instance1 = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = new TransformData { Position = posB, Rotation = Quat.Identity, Scale = Vec3.One },
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
            };
            var instance2 = new PrefabInstanceNode
            {
                LogicalId = "instance-2",
                Name = "Enemy",
                Transform = new TransformData { Position = posA, Rotation = Quat.Identity, Scale = Vec3.One },
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance1, instance2 } };

            var snapshot1 = new SnapshotNode { GlobalObjectId = "goid-1", Name = "Enemy", Transform = new TransformData { Position = posA, Rotation = Quat.Identity, Scale = Vec3.One } };
            var snapshot2 = new SnapshotNode { GlobalObjectId = "goid-2", Name = "Enemy", Transform = new TransformData { Position = posB, Rotation = Quat.Identity, Scale = Vec3.One } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshot1, snapshot2 } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = "prefab-guid-1", PrefabKey = new PrefabInstanceKey { TargetPrefabId = 100UL, TargetObjectId = 1UL },
                    },
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-2", GlobalObjectId = "goid-2", Kind = "PrefabInstance",
                        SourcePrefabGuid = "prefab-guid-1", PrefabKey = new PrefabInstanceKey { TargetPrefabId = 100UL, TargetObjectId = 2UL },
                    },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<AddInstance>());
            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());

            var setTransforms = changeSet.Ops.OfType<SetTransform>().ToArray();
            Assert.Equal(2, setTransforms.Length);

            var t1 = Assert.Single(setTransforms, op => op.LogicalId == "instance-1");
            Assert.Equal(posB, t1.Transform.Position);

            var t2 = Assert.Single(setTransforms, op => op.LogicalId == "instance-2");
            Assert.Equal(posA, t2.Transform.Position);
        }

        // b3-t2: InstanceOverrideDiff added/removed child-GO diff + nested-override revert-at-depth.
        // Target key is (SubKey, ComponentType); a GO Parent's ComponentType is "" per research.
        private static OverrideTarget NestedTarget() =>
            new() { ComponentType = "UnityEngine.Light", SubKey = new PrefabInstanceKey { TargetObjectId = 12345 } };

        private static OverrideTarget ParentTarget(ulong subObjectId) =>
            new() { ComponentType = "", SubKey = new PrefabInstanceKey { TargetObjectId = subObjectId } };

        private static (PrefabInstanceNode instance, SnapshotNode snapshotInstance, IdentityMap map) BuildMatched(
            PropertyOverride[]? desiredOverrides = null,
            PropertyOverride[]? snapshotOverrides = null,
            AddedGameObject[]? desiredAddedGameObjects = null,
            AddedGameObject[]? snapshotAddedGameObjects = null,
            OverrideTarget[]? desiredRemovedGameObjects = null,
            OverrideTarget[]? snapshotRemovedGameObjects = null)
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1" },
                Overrides = desiredOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedGameObjects = desiredAddedGameObjects ?? System.Array.Empty<AddedGameObject>(),
                RemovedGameObjects = desiredRemovedGameObjects ?? System.Array.Empty<OverrideTarget>(),
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance",
                Name = "Enemy",
                Transform = transform,
                SourcePrefabGuid = "prefab-guid-1",
                Overrides = snapshotOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedGameObjects = snapshotAddedGameObjects ?? System.Array.Empty<AddedGameObject>(),
                RemovedGameObjects = snapshotRemovedGameObjects ?? System.Array.Empty<OverrideTarget>(),
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
        public void MaterializeReverts_NestedOverride_And_AddedChild()
        {
            var nestedTarget = NestedTarget();
            var snapshotOverride = new PropertyOverride { Target = nestedTarget, PropertyPath = "m_Intensity", Value = ValueNode.Primitive.Float(2f) };

            var parentTarget = ParentTarget(999);
            var addedNode = new GameObjectNode { LogicalId = "muzzle-1", Name = "MuzzleFlash" };
            var snapshotAdded = new AddedGameObject { Parent = parentTarget, Node = addedNode };

            var (instance, snapshotInstance, map) = BuildMatched(
                snapshotOverrides: new[] { snapshotOverride },
                snapshotAddedGameObjects: new[] { snapshotAdded });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var revertOverride = Assert.Single(changeSet.Ops.OfType<RevertInstanceOverride>());
            Assert.Equal(nestedTarget, revertOverride.Target);
            Assert.Equal("m_Intensity", revertOverride.PropertyPath);

            var revertChild = Assert.Single(changeSet.Ops.OfType<RevertAddedChild>());
            Assert.Equal(parentTarget, revertChild.Target);
            Assert.Equal("muzzle-1", revertChild.ChildLogicalId);

            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
            Assert.Empty(changeSet.Ops.OfType<SetInstanceOverride>());
        }

        [Fact]
        public void AuthoredAddedChild_AbsentFromSnapshot_EmitsAddInstanceChild()
        {
            var parentTarget = ParentTarget(999);
            var addedNode = new GameObjectNode { LogicalId = "muzzle-1", Name = "MuzzleFlash" };
            var desiredAdded = new AddedGameObject { Parent = parentTarget, Node = addedNode };

            var (instance, snapshotInstance, map) = BuildMatched(desiredAddedGameObjects: new[] { desiredAdded });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var op = Assert.Single(changeSet.Ops.OfType<AddInstanceChild>());
            Assert.Equal(parentTarget, op.Target);
            Assert.Equal("muzzle-1", op.Node.LogicalId);
            Assert.Equal("MuzzleFlash", op.Node.Name);
        }

        [Fact]
        public void RemovedChild_BothDirections_EmitRemoveThenRevertRemove()
        {
            var desiredOnlyTarget = ParentTarget(111);
            var snapshotOnlyTarget = ParentTarget(222);

            var (instance, snapshotInstance, map) = BuildMatched(
                desiredRemovedGameObjects: new[] { desiredOnlyTarget },
                snapshotRemovedGameObjects: new[] { snapshotOnlyTarget });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var changeSet = Differ.Diff(model, snapshot, map);

            var remove = Assert.Single(changeSet.Ops.OfType<RemoveInstanceChild>());
            Assert.Equal(desiredOnlyTarget, remove.Target);

            var revert = Assert.Single(changeSet.Ops.OfType<RevertRemovedChild>());
            Assert.Equal(snapshotOnlyTarget, revert.Target);
        }
    }
}
