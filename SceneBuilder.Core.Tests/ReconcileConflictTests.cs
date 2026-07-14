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

        [Fact]
        public void Reconcile_DeleteWithReferencedHandle_SurfacesConflict_NoRemoval()
        {
            var child = new GameObjectNode { LogicalId = "child-1", Name = "Child" };
            var parent = new GameObjectNode { LogicalId = "parent-1", Name = "Parent", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            // Parent's GlobalObjectId is absent from the snapshot (deleted in scene); the
            // child's GlobalObjectId is still present (reparented to root, surviving).
            var snapshotChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "Child" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotChild } };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "parent-1", GlobalObjectId = "goid-parent", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "child-1",
                        GlobalObjectId = "goid-child",
                        Kind = "GameObject",
                        ParentLogicalId = "parent-1",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var conflict = Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.ReferencedHandle);
            Assert.Equal("parent-1", conflict.LogicalId);
            Assert.Equal("goid-parent", conflict.GlobalObjectId);

            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "parent-1");
            Assert.DoesNotContain("parent-1", result.RemovedLogicalIds);
        }

        [Fact]
        public void Reconcile_DeleteWithAllChildrenAlsoDeleted_NoConflict_StillRemoves()
        {
            var child = new GameObjectNode { LogicalId = "child-2", Name = "Child" };
            var parent = new GameObjectNode { LogicalId = "parent-2", Name = "Parent", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            // Both parent and child are absent from the snapshot: the whole subtree was deleted.
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "parent-2", GlobalObjectId = "goid-parent2", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "child-2",
                        GlobalObjectId = "goid-child2",
                        Kind = "GameObject",
                        ParentLogicalId = "parent-2",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.ReferencedHandle);
            Assert.Contains(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "parent-2");
            Assert.Contains("parent-2", result.RemovedLogicalIds);
        }

        [Fact]
        public void Reconcile_UnanchorableStructuralChange_SurfacesConflict_DoesNotThrow()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "ghost-1", Name = "Ghost" } },
            };

            // Object was deleted in the scene: snapshot is empty.
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "ghost-1", GlobalObjectId = "goid-ghost", Kind = "GameObject" },
                },
            };

            // "ghost-1" deliberately has no entry in anchors -> its delete cannot be anchored.
            var anchors = new Dictionary<string, SourceSpan>();

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            var conflict = Assert.Single(result.Conflicts, c => c.Kind == ConflictKind.MissingSourceAnchor);
            Assert.Equal("ghost-1", conflict.LogicalId);
            Assert.Equal("goid-ghost", conflict.GlobalObjectId);

            Assert.DoesNotContain(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "ghost-1");
            Assert.DoesNotContain("ghost-1", result.RemovedLogicalIds);
        }

        [Fact]
        public void Reconcile_DeleteWithAnchorPresent_StillRemoves_NoMissingSourceAnchorConflict()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "anchored-1", Name = "Anchored" } },
            };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "anchored-1", GlobalObjectId = "goid-anchored", Kind = "GameObject" },
                },
            };

            var anchors = new Dictionary<string, SourceSpan>
            {
                ["anchored-1"] = new SourceSpan(0, 10),
            };

            var result = Reconciler.Reconcile(model, snapshot, map, anchors);

            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.MissingSourceAnchor);
            Assert.Contains(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "anchored-1");
            Assert.Contains("anchored-1", result.RemovedLogicalIds);
        }

        // §13 rule 4 (delete cascade): deleting a created/coded object removes its OWN statement AND the
        // payload statements authored on it (here, its child). Payload whose owner survives is untouched
        // (covered by Reconcile_DeleteWithReferencedHandle_SurfacesConflict_NoRemoval).
        [Fact]
        public void Reconcile_DeleteCascade_RemovesPayloadStatements()
        {
            var child = new GameObjectNode { LogicalId = "cascade-child", Name = "Child" };
            var parent = new GameObjectNode { LogicalId = "cascade-parent", Name = "Parent", Children = new[] { child } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { parent } };

            // Whole subtree deleted in the scene.
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "cascade-parent", GlobalObjectId = "goid-cp", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = "cascade-child",
                        GlobalObjectId = "goid-cc",
                        Kind = "GameObject",
                        ParentLogicalId = "cascade-parent",
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.DoesNotContain(result.Conflicts, c => c.Kind == ConflictKind.ReferencedHandle);
            // BOTH the object's own statement and the payload (child) statement are removed.
            Assert.Contains(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "cascade-parent");
            Assert.Contains(result.Patch.Edits, e => e is RemoveStatement rs && rs.Anchor == "cascade-child");
            Assert.Contains("cascade-parent", result.RemovedLogicalIds);
            Assert.Contains("cascade-child", result.RemovedLogicalIds);
        }

        // §13 create-with-payload: a newly-created scene object carrying a component (payload M2b does not
        // own) is appended as a GameObject AND its component is REPORTED (never silently dropped); the
        // AddedEntry maps it so a second Reconcile of the unchanged scene converges (no re-append).
        [Fact]
        public void Reconcile_CreatedObjectWithPayload_ConvergesNoSilentDrop()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var created = new SnapshotNode
            {
                GlobalObjectId = "goid-new",
                Name = "NewThing",
                Components = new[] { new ComponentData() },   // M3 fills this out; here it just marks payload
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { created } };
            var map = new IdentityMap { Entries = System.Array.Empty<IdentityMapEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            // Object appended (not dropped)...
            var append = Assert.Single(result.Patch.Edits.OfType<AppendStatement>());
            Assert.Equal("NewThing", append.Name);
            // ...component payload REPORTED, never silently dropped (§13)...
            Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.UnrepresentedComponents);
            // ...and an AddedEntry maps it so a second Reconcile converges (no re-append).
            Assert.Single(result.AddedEntries, e => e.GlobalObjectId == "goid-new");

            var map2 = new IdentityMap { Entries = result.AddedEntries };
            var result2 = Reconciler.Reconcile(model, snapshot, map2);
            Assert.DoesNotContain(result2.Patch.Edits, e => e is AppendStatement);
        }
    }
}
