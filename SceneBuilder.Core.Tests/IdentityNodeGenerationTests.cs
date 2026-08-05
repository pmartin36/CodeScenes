using SceneBuilder.Core.Identity;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The content key a ChangeScopedSnapshot cache is invalidated on: two maps whose mapped-node
    // (GameObject/PrefabInstance with a non-empty GlobalObjectId) projection is equal must produce
    // the same generation regardless of entry order, Assets[], or non-node entries; any change to
    // that projection must produce a different one.
    public class IdentityNodeGenerationTests
    {
        private static IdentityMapEntry Node(string logicalId, string goid, string? name = null) =>
            new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject", Name = name ?? logicalId };

        private static IdentityMap MapOf(params IdentityMapEntry[] entries) =>
            new IdentityMap { Entries = entries, Assets = System.Array.Empty<AssetEntry>() };

        [Fact]
        public void EqualMappedNodeContent_TwoDistinctMapInstances_ProducesEqualGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"), Node("Door", "goid-2"));
            var b = MapOf(Node("Opener", "goid-1"), Node("Door", "goid-2"));

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void EntryOrderPermuted_ProducesEqualGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"), Node("Door", "goid-2"));
            var b = MapOf(Node("Door", "goid-2"), Node("Opener", "goid-1"));

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void AssetsArrayDiffersOnly_ProducesEqualGeneration()
        {
            var a = new IdentityMap { Entries = new[] { Node("Opener", "goid-1") }, Assets = System.Array.Empty<AssetEntry>() };
            var b = new IdentityMap
            {
                Entries = new[] { Node("Opener", "goid-1") },
                Assets = new[] { new AssetEntry { Guid = "abc123", LastKnownPath = "Assets/Mat.mat" } },
            };

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void ExtraComponentKindEntry_IsNotAMappedNode_ProducesEqualGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"));
            var b = MapOf(
                Node("Opener", "goid-1"),
                new IdentityMapEntry { LogicalId = "Opener/Comp", GlobalObjectId = "goid-comp", Kind = "Component", ComponentType = "UnityEngine.BoxCollider" });

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void EntryWithEmptyGlobalObjectId_IsNotAMappedNode_ProducesEqualGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"));
            var b = MapOf(Node("Opener", "goid-1"), Node("Pending", ""));

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void MappedNodeAdded_ProducesDifferentGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"));
            var b = MapOf(Node("Opener", "goid-1"), Node("Door", "goid-2"));

            Assert.NotEqual(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void MappedNodeRemoved_ProducesDifferentGeneration()
        {
            var a = MapOf(Node("Opener", "goid-1"), Node("Door", "goid-2"));
            var b = MapOf(Node("Opener", "goid-1"));

            Assert.NotEqual(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void SameLogicalId_DifferentGlobalObjectId_ProducesDifferentGeneration()
        {
            var a = MapOf(Node("Door", "goid-2"));
            var b = MapOf(Node("Door", "goid-2-retargeted"));

            Assert.NotEqual(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void SameGlobalObjectId_DifferentLogicalId_ProducesDifferentGeneration()
        {
            var a = MapOf(Node("Door", "goid-2"));
            var b = MapOf(Node("DoorRenamed", "goid-2"));

            Assert.NotEqual(IdentityNodeIndex.MappedNodeGeneration(a), IdentityNodeIndex.MappedNodeGeneration(b));
        }

        [Fact]
        public void EmptyIdentityMap_GenerationIsStableAcrossCalls()
        {
            var map = new IdentityMap();

            Assert.Equal(IdentityNodeIndex.MappedNodeGeneration(map), IdentityNodeIndex.MappedNodeGeneration(map));
        }
    }
}
