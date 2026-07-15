using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b5-t1: Guid->path re-derivation from Assets[] (cache-first, resolver-fallback),
    // located missing-GUID error, and sidecar round-trip. See research.md.
    public class AssetRefSidecarTests
    {
        [Fact]
        public void Move_SameGuidNewPath_ReDerivesDisplayPathNoIdentityChange()
        {
            var assets = new[]
            {
                new AssetEntry { Guid = "guid-1", LastKnownPath = "Assets/New/Path.mat", TypeHint = "Material" },
            };

            var reDerived = AssetRefResolver.ReDerive("guid-1", assets);

            Assert.Equal("Assets/New/Path.mat", reDerived);

            var oldRef = new AssetRef { Guid = "guid-1", FileId = 0, DisplayPath = "Assets/Old/Path.mat" };
            var newRef = new AssetRef { Guid = "guid-1", FileId = 0, DisplayPath = reDerived! };
            Assert.Equal(oldRef, newRef);
        }

        [Fact]
        public void MissingGuid_ProducesLocatedError()
        {
            var assets = System.Array.Empty<AssetEntry>();
            var assetRef = new AssetRef { Guid = "guid-x", DisplayPath = "Assets/Materials/Red.mat" };

            var result = AssetRefResolver.Resolve(
                "Player", "MeshRenderer", "sharedMaterial", assetRef, assets, _ => null);

            Assert.False(result.IsResolved);
            Assert.NotNull(result.Error);
            Assert.Equal(
                "Player > MeshRenderer.sharedMaterial: asset guid-x (was 'Assets/Materials/Red.mat') not found",
                result.Error!.Message);
        }

        [Fact]
        public void SidecarAssets_ReadWrite_RoundTrips()
        {
            var map = new IdentityMap
            {
                SchemaVersion = 1,
                Scene = "Assets/Scenes/Main.unity",
                Assets = new[]
                {
                    new AssetEntry { Guid = "guid-1", LastKnownPath = "Assets/Materials/Red.mat", TypeHint = "Material" },
                },
            };

            var json = IdentityMapJson.Serialize(map);
            var roundTripped = IdentityMapJson.Deserialize(json);

            var entry = Assert.Single(roundTripped.Assets);
            Assert.Equal("guid-1", entry.Guid);
            Assert.Equal("Assets/Materials/Red.mat", entry.LastKnownPath);
            Assert.Equal("Material", entry.TypeHint);

            var reDerived = AssetRefResolver.ReDerive("guid-1", roundTripped.Assets);
            Assert.Equal("Assets/Materials/Red.mat", reDerived);
        }

        [Fact]
        public void ReDerive_ResolverFallback_WhenCacheMisses()
        {
            var assets = System.Array.Empty<AssetEntry>();

            var reDerived = AssetRefResolver.ReDerive("guid-y", assets, guid => guid == "guid-y" ? "Assets/Fallback.mat" : null);

            Assert.Equal("Assets/Fallback.mat", reDerived);
        }

        [Fact]
        public void Resolve_NoneGuid_ReturnsNoErrorNoPath()
        {
            var assets = System.Array.Empty<AssetEntry>();
            var noneRef = new AssetRef { Guid = "", DisplayPath = "" };

            var result = AssetRefResolver.Resolve(
                "Player", "MeshRenderer", "sharedMaterial", noneRef, assets, _ => "should-not-be-used");

            Assert.True(result.IsResolved);
            Assert.Null(result.DisplayPath);
            Assert.Null(result.Error);
        }
    }
}
