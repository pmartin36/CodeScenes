using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class DifferTests
    {
        [Fact]
        public void Diff_ModelVsEmptySnapshot_ProducesOnlyCreates()
        {
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child" };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
            var adds = changeSet.Ops.OfType<AddNode>().ToArray();
            Assert.Equal(2, adds.Length);
            var rootAdd = Assert.Single(adds, a => a.LogicalId == "root-1");
            Assert.Null(rootAdd.ParentLogicalId);
            var childAdd = Assert.Single(adds, a => a.LogicalId == "child-1");
            Assert.Equal("root-1", childAdd.ParentLogicalId);
        }

        [Fact]
        public void Diff_ModelVsEqualSnapshot_ProducesEmptyChangeSet()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Transform = transform };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform, Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "Child", Transform = transform };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = transform, Children = new[] { snapshotChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops);
        }

        [Fact]
        public void Diff_ModelVsDriftedSnapshot_ProducesInPlaceEdits_NeverRemoveThenAdd()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child", Transform = transform };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform, Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedRootTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "OldChildName", Transform = transform };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedRootTransform, Children = new[] { snapshotChild } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Contains(changeSet.Ops.OfType<SetTransform>(), op => op.LogicalId == "root-1");
            Assert.Contains(changeSet.Ops.OfType<SetName>(), op => op.LogicalId == "child-1");
            Assert.Empty(changeSet.Ops.OfType<AddNode>());
            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
        }

        [Fact]
        public void Diff_UnmappedActualAbsentFromDesired_IsNeverInDestroySet()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var userCreated = new SnapshotNode { GlobalObjectId = "goid-user-created", Name = "UserCreated" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot, userCreated } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
        }

        [Fact]
        public void Diff_MappedActualRemovedFromCode_ProducesDestroy()
        {
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root" };
            var snapshotRemoved = new SnapshotNode { GlobalObjectId = "goid-removed", Name = "Removed" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot, snapshotRemoved } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "removed-1", GlobalObjectId = "goid-removed", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            var removed = Assert.Single(changeSet.Ops.OfType<RemoveNode>());
            Assert.Equal("removed-1", removed.LogicalId);
            Assert.DoesNotContain(changeSet.Ops.OfType<RemoveNode>(), op => op.LogicalId == "root-1");
        }

        [Fact]
        public void Diff_DriftedMappedActual_IsUpdatedInPlace_PreservingGlobalObjectId_NeverRemoveThenAdd()
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = transform };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var driftedTransform = transform with { Position = new Vec3(9, 9, 9) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = driftedTransform };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Contains(changeSet.Ops.OfType<SetTransform>(), op => op.LogicalId == "root-1");
            Assert.Empty(changeSet.Ops.OfType<AddNode>());
            Assert.Empty(changeSet.Ops.OfType<RemoveNode>());
            Assert.Equal("goid-root", Assert.Single(map.Entries, e => e.LogicalId == "root-1").GlobalObjectId);
        }
    }
}
