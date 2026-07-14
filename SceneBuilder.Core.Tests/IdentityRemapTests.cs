using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class IdentityRemapTests
    {
        [Fact]
        public void CollectIdentityEntries_TwoRootsWithOrderedChildren_PopulatesNameAndSiblingIndexPerParent()
        {
            var result = BuilderParser.Parse(BuilderFixtures.TwoRootsWithOrderedChildren);

            var gameObjectEntries = result.IdentityMap.Entries.Where(e => e.Kind == "GameObject").ToList();

            var root1 = gameObjectEntries.Single(e => e.Name == "Root1");
            Assert.Equal(0, root1.SiblingIndex);

            var root2 = gameObjectEntries.Single(e => e.Name == "Root2");
            Assert.Equal(1, root2.SiblingIndex);

            var childA = gameObjectEntries.Single(e => e.Name == "ChildA");
            Assert.Equal(0, childA.SiblingIndex);

            var childB = gameObjectEntries.Single(e => e.Name == "ChildB");
            Assert.Equal(1, childB.SiblingIndex);

            var childC = gameObjectEntries.Single(e => e.Name == "ChildC");
            Assert.Equal(0, childC.SiblingIndex);
        }

        [Fact]
        public void Remap_RenamedObject_InheritsPriorGlobalObjectId()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject", Name = "OldName", SiblingIndex = 0 },
                },
            };

            var current = new SceneModel
            {
                Roots = new[] { new GameObjectNode { LogicalId = "root-2", Name = "NewName" } },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            var entry = Assert.Single(remapped.Entries, e => e.LogicalId == "root-2");
            Assert.Equal("goid-root", entry.GlobalObjectId);
        }

        [Fact]
        public void Remap_DeletedObject_PreservedAsOrphanForDeletion()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject", Name = "Kept", SiblingIndex = 0 },
                    new IdentityMapEntry { LogicalId = "deleted-1", GlobalObjectId = "goid-deleted", Kind = "GameObject", Name = "Deleted", SiblingIndex = 1 },
                },
            };

            var current = new SceneModel
            {
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Kept" } },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            var orphan = Assert.Single(remapped.Entries, e => e.LogicalId == "deleted-1");
            Assert.Equal("goid-deleted", orphan.GlobalObjectId);
            Assert.True(remapped.IsManaged("goid-deleted"));
        }

        [Fact]
        public void Remap_ReorderedObject_MatchesByName()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "a-1", GlobalObjectId = "goid-a", Kind = "GameObject", Name = "A", SiblingIndex = 0 },
                    new IdentityMapEntry { LogicalId = "b-1", GlobalObjectId = "goid-b", Kind = "GameObject", Name = "B", SiblingIndex = 1 },
                },
            };

            // Current roots swap order relative to prior, and get fresh LogicalIds (as a real
            // re-parse would), so tier (a) LogicalId equality cannot fire for either.
            var current = new SceneModel
            {
                Roots = new[]
                {
                    new GameObjectNode { LogicalId = "b-2", Name = "B" },
                    new GameObjectNode { LogicalId = "a-2", Name = "A" },
                },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            var aEntry = Assert.Single(remapped.Entries, e => e.LogicalId == "a-2");
            Assert.Equal("goid-a", aEntry.GlobalObjectId);

            var bEntry = Assert.Single(remapped.Entries, e => e.LogicalId == "b-2");
            Assert.Equal("goid-b", bEntry.GlobalObjectId);
        }

        [Fact]
        public void Remap_UnchangedScene_IsIdentityOnGoids()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject", Name = "Root", SiblingIndex = 0 },
                    new IdentityMapEntry { LogicalId = "child-1", GlobalObjectId = "goid-child", Kind = "GameObject", Name = "Child", SiblingIndex = 0, ParentLogicalId = "root-1" },
                },
            };

            var current = new SceneModel
            {
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Children = new[] { new GameObjectNode { LogicalId = "child-1", Name = "Child" } },
                    },
                },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            Assert.Equal("goid-root", Assert.Single(remapped.Entries, e => e.LogicalId == "root-1").GlobalObjectId);
            Assert.Equal("goid-child", Assert.Single(remapped.Entries, e => e.LogicalId == "child-1").GlobalObjectId);
        }

        [Fact]
        public void Remap_NewObject_HasEmptyGlobalObjectId()
        {
            var prior = new IdentityMap { Scene = "Assets/Scenes/Demo.unity" };

            var current = new SceneModel
            {
                Roots = new[] { new GameObjectNode { LogicalId = "new-1", Name = "New" } },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            var entry = Assert.Single(remapped.Entries, e => e.LogicalId == "new-1");
            Assert.Equal("", entry.GlobalObjectId);
        }

        [Fact]
        public void Remap_ComponentUnderRenamedOwner_InheritsComponentGoid()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject", Name = "Old", SiblingIndex = 0 },
                    new IdentityMapEntry
                    {
                        LogicalId = "root-1/UnityEngine.Rigidbody#0",
                        GlobalObjectId = "goid-rb",
                        Kind = "Component",
                        ComponentType = "UnityEngine.Rigidbody",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            var current = new SceneModel
            {
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-2",
                        Name = "New",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "root-2/UnityEngine.Rigidbody#0", Type = new TypeRef("UnityEngine.Rigidbody") },
                        },
                    },
                },
            };

            var remapped = IdentityRemapper.Remap(current, prior);

            var compEntry = Assert.Single(remapped.Entries, e => e.LogicalId == "root-2/UnityEngine.Rigidbody#0");
            Assert.Equal("goid-rb", compEntry.GlobalObjectId);
            Assert.Equal("Component", compEntry.Kind);
        }

        [Fact]
        public void Remap_ThenMaterialize_RenameUpdatesInPlaceNoDuplicate()
        {
            var prior = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject", Name = "OldName", SiblingIndex = 0 },
                },
            };

            var current = new SceneModel
            {
                Roots = new[] { new GameObjectNode { LogicalId = "root-2", Name = "NewName" } },
            };

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "OldName" };
            var snapshot = new SceneSnapshot { Roots = new[] { snapshotRoot } };

            var remapped = IdentityRemapper.Remap(current, prior);
            var plan = Materializer.Materialize(current, snapshot, remapped);

            Assert.Contains(plan.Ops.OfType<SetName>(), op => op.LogicalId == "root-2" && op.Name == "NewName");
            Assert.Empty(plan.Ops.OfType<CreateObject>());
            Assert.True(remapped.IsManaged("goid-root"));
        }
    }
}
