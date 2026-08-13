using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A nested prefab-instance node has no independent authoring representation for its name -- it
    // is always emitted as `Instance(<path>)` / `Instance(Prefabs.X)`, and that statement's first
    // argument IS the source path, not a name. A live rename must surface as a located Conflict
    // instead of rewriting that argument, which would repoint the reference at a nonexistent asset.
    public class NestedPrefabReconcileTests
    {
        private const string PrefabGuid = "guid-b-prefab";
        private const string PrefabPath = "Assets/Prefabs/B.prefab";

        private static (PrefabInstanceNode instance, SnapshotNode snapshotInstance, IdentityMap map) BuildRenamedInstance()
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "B",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Renamed",
                SourcePrefabGuid = PrefabGuid,
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                    },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            return (instance, snapshotInstance, map);
        }

        [Fact]
        public void InstanceNodeRename_EmitsConflict_NoPatchArgument()
        {
            var (instance, snapshotInstance, map) = BuildRenamedInstance();
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<PatchArgument>().Where(e => e.Anchor == "instance-1"));

            var conflict = Assert.Single(
                result.Conflicts.Where(c => c.Kind == ConflictKind.UnrepresentableInstanceRename));
            Assert.Equal("instance-1", conflict.LogicalId);
        }
    }
}
