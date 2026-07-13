using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ReconcileConflictTests
    {
        [Fact]
        public void Reconcile_AmbiguousSynthesizedSiblings_SurfacesConflict_NoPatchForNode()
        {
            var enemy0 = new GameObjectNode { LogicalId = "Enemy/0", Name = "Enemy" };
            var enemy1 = new GameObjectNode { LogicalId = "Enemy/1", Name = "Enemy" };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { enemy0, enemy1 } };

            var snapshotEnemy0 = new SnapshotNode { GlobalObjectId = "goid-e0", Name = "Enemy" };
            var snapshotEnemy1 = new SnapshotNode { GlobalObjectId = "goid-e1", Name = "Enemy" };
            // Swapped order relative to the model -> a Reorder op fires against the group.
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotEnemy1, snapshotEnemy0 } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy/0", GlobalObjectId = "goid-e0", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "Enemy/1", GlobalObjectId = "goid-e1", Kind = "GameObject" },
                },
            };

            var anchors = new Dictionary<string, SourceSpan>
            {
                ["Enemy/0"] = new SourceSpan(0, 10),
                ["Enemy/1"] = new SourceSpan(20, 10),
            };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var conflict = Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.AmbiguousAnchor);
            Assert.Contains("Enemy/0", conflict.Reason);
            Assert.Contains("Enemy/1", conflict.Reason);

            Assert.DoesNotContain(result.Patch.Edits, e => e.Anchor == "Enemy/0" || e.Anchor == "Enemy/1");
        }

        [Fact]
        public void Reconcile_EditWithNoSourceAnchor_SurfacesLocatedConflict()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "OldName" } },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-root", Name = "NewName" } },
            };

            var map = new IdentityMap
            {
                Entries = new[] { new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" } },
            };

            // "root-1" deliberately has no entry in anchors -> no builder statement was found for it.
            var anchors = new Dictionary<string, SourceSpan>();

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var conflict = Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.MissingSourceAnchor);
            Assert.Equal("root-1", conflict.LogicalId);
            Assert.Equal("goid-root", conflict.GlobalObjectId);

            Assert.DoesNotContain(result.Patch.Edits, e => e.Anchor == "root-1");
        }
    }
}
