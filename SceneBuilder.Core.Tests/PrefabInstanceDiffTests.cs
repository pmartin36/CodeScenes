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
    }
}
