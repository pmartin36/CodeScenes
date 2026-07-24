using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class AssetCatalogTests
    {
        // Type-first + folder-path entries: a catalogued material (nested folders), a
        // sub-asset mesh (fbx sub-mesh, non-zero FileId + SubAsset), and an entry directly
        // under Assets/ (empty FolderSegments).
        private static AssetCatalog SampleCatalog() => new AssetCatalog
        {
            SchemaVersion = 1,
            Entries = new[]
            {
                new AssetCatalogEntry
                {
                    Group = "Materials",
                    FolderSegments = new[] { "Environment", "Rocks" },
                    MemberName = "Stone",
                    Guid = "11111111111111111111111111111111",
                    FileId = 0,
                    Path = "Assets/Environment/Rocks/Stone.mat",
                    SubAsset = "",
                },
                new AssetCatalogEntry
                {
                    Group = "Meshes",
                    FolderSegments = new[] { "Models", "Props" },
                    MemberName = "Barrel",
                    Guid = "22222222222222222222222222222222",
                    FileId = 987654321,
                    Path = "Assets/Models/Props/Barrel.fbx",
                    SubAsset = "Barrel",
                },
                new AssetCatalogEntry
                {
                    Group = "Scenes",
                    FolderSegments = System.Array.Empty<string>(),
                    MemberName = "MainMenu",
                    Guid = "33333333333333333333333333333333",
                    FileId = 0,
                    Path = "Assets/MainMenu.unity",
                    SubAsset = "",
                },
            },
        };

        [Fact]
        public void Manifest_RoundTrips_ByteStable()
        {
            var catalog = SampleCatalog();

            var jsonA = catalog.Serialize();
            var back = AssetCatalog.Deserialize(jsonA);
            var jsonB = back.Serialize();

            Assert.Equal(jsonA, jsonB);

            Assert.Equal(catalog.Entries.Length, back.Entries.Length);

            var barrel = back.Entries[1];
            Assert.Equal("Meshes", barrel.Group);
            Assert.Equal(new[] { "Models", "Props" }, barrel.FolderSegments);
            Assert.Equal("Barrel", barrel.MemberName);
            Assert.Equal(987654321L, barrel.FileId);
            Assert.Equal("Barrel", barrel.SubAsset);

            var mainMenu = back.Entries[2];
            Assert.Empty(mainMenu.FolderSegments);
            Assert.Equal("MainMenu", mainMenu.MemberName);
        }

        [Fact]
        public void ReverseLookup_CataloguedHit_BuiltinMiss()
        {
            var catalog = SampleCatalog();

            var found = catalog.TryGetMember("11111111111111111111111111111111", 0, out var group, out var folderSegments, out var member);
            Assert.True(found);
            Assert.Equal("Materials", group);
            Assert.Equal(new[] { "Environment", "Rocks" }, folderSegments);
            Assert.Equal("Stone", member);

            // Built-in ref: empty guid never matches a real entry.
            var builtinMiss = catalog.TryGetMember("", 0, out _, out _, out _);
            Assert.False(builtinMiss);

            // Compound key: right guid, wrong fileId must miss (guards against guid-only matching).
            var wrongFileId = catalog.TryGetMember("11111111111111111111111111111111", 999, out _, out _, out _);
            Assert.False(wrongFileId);

            // Compound key: right fileId (0, the common default), wrong guid must miss.
            var wrongGuid = catalog.TryGetMember("99999999999999999999999999999999", 0, out _, out _, out _);
            Assert.False(wrongGuid);
        }

        [Fact]
        public void ForwardLookup_FolderSegmentsArePartOfKey()
        {
            var catalog = SampleCatalog();

            var hit = catalog.TryGetEntry("Materials", new[] { "Environment", "Rocks" }, "Stone", out var entry);
            Assert.True(hit);
            Assert.Equal("11111111111111111111111111111111", entry.Guid);

            // Empty-FolderSegments entry (asset directly under Assets/) resolves too.
            var rootHit = catalog.TryGetEntry("Scenes", System.Array.Empty<string>(), "MainMenu", out var rootEntry);
            Assert.True(rootHit);
            Assert.Equal("33333333333333333333333333333333", rootEntry.Guid);

            // Same Group + MemberName but wrong folder path must miss (proves FolderSegments
            // participates in the key, not just Group+MemberName).
            var wrongFolder = catalog.TryGetEntry("Materials", new[] { "Characters", "Skin" }, "Stone", out _);
            Assert.False(wrongFolder);
        }
    }
}
