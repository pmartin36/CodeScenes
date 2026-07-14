using SceneBuilder.Core.Identity;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class IdentityMapTests
    {
        private static IdentityMap SampleMap() => new IdentityMap
        {
            SchemaVersion = 1,
            Scene = "Assets/Scenes/Demo.unity",
            Entries = new IdentityMapEntry[]
            {
                new IdentityMapEntry { LogicalId = "Root", GlobalObjectId = "goid-root", Kind = "GameObject", ParentLogicalId = null },
                new IdentityMapEntry { LogicalId = "Root/Child", GlobalObjectId = "", Kind = "GameObject", ParentLogicalId = "Root" },
                new IdentityMapEntry { LogicalId = "Root/Comp", GlobalObjectId = "goid-comp", Kind = "Component", ComponentType = "UnityEngine.BoxCollider", ParentLogicalId = "Root" },
            },
            Assets = System.Array.Empty<AssetEntry>(),
        };

        [Fact]
        public void IsManaged_PresentGameObjectGoid_ReturnsTrue()
        {
            var map = SampleMap();

            Assert.True(map.IsManaged("goid-root"));
        }

        [Fact]
        public void IsManaged_UnmappedGoid_ReturnsFalse()
        {
            var map = SampleMap();

            Assert.False(map.IsManaged("goid-does-not-exist"));
        }

        [Fact]
        public void IsManaged_EmptyStringArg_ReturnsFalse_EvenWhenAnEntryCarriesEmptyGlobalObjectId()
        {
            var map = SampleMap();

            Assert.False(map.IsManaged(""));
        }

        [Fact]
        public void IsManaged_NullArg_ReturnsFalse()
        {
            var map = SampleMap();

            Assert.False(map.IsManaged(null!));
        }

        [Fact]
        public void IsManaged_ComponentKindGoid_ReturnsTrue()
        {
            var map = SampleMap();

            Assert.True(map.IsManaged("goid-comp"));
        }
    }
}
