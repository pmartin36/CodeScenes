using System.Linq;
using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class AssetCatalogBuilderTests
    {
        // RAW input: MemberName is ignored by the builder (derived from SubAsset-or-filename).
        private static AssetCatalogEntry RawEntry(string group, string[] folderSegments, string path, string guid, long fileId = 0, string subAsset = "") =>
            new AssetCatalogEntry
            {
                Group = group,
                FolderSegments = folderSegments,
                Guid = guid,
                FileId = fileId,
                Path = path,
                SubAsset = subAsset,
            };

        [Fact]
        public void AssetCatalog_SameEntries_ByteIdentical()
        {
            var raw = new[]
            {
                RawEntry("Materials", new[] { "Environment", "Rocks" }, "Assets/Environment/Rocks/Stone.mat", "11111111111111111111111111111111"),
                // Residual sanitize-collision: "Rock-A" and "Rock A" both -> "Rock_A".
                RawEntry("Materials", new[] { "Environment", "Rocks" }, "Assets/Environment/Rocks/Rock-A.mat", "33333333333333333333333333333333"),
                RawEntry("Materials", new[] { "Environment", "Rocks" }, "Assets/Environment/Rocks/Rock A.mat", "44444444444444444444444444444444"),
                // Same fbx, two same-named sub-meshes (same Guid, different FileId).
                RawEntry("Meshes", new[] { "Models", "Props" }, "Assets/Models/Props/Barrel.fbx", "22222222222222222222222222222222", fileId: 200, subAsset: "Wheel"),
                RawEntry("Meshes", new[] { "Models", "Props" }, "Assets/Models/Props/Barrel.fbx", "22222222222222222222222222222222", fileId: 100, subAsset: "Wheel"),
            };
            var shuffled = raw.Reverse().ToArray();

            var a = AssetCatalogBuilder.Build(raw);
            var b = AssetCatalogBuilder.Build(shuffled);

            Assert.Equal(a.Serialize(), b.Serialize());

            var byKey = a.Entries.ToDictionary(e => (e.Guid, e.FileId));
            Assert.Equal("Stone", byKey[("11111111111111111111111111111111", 0L)].MemberName);

            // GUID-Ordinal-lower entry (33... < 44...) keeps the bare name; the higher gets the suffix.
            Assert.Equal("Rock_A", byKey[("33333333333333333333333333333333", 0L)].MemberName);
            Assert.Equal("Rock_A2", byKey[("44444444444444444444444444444444", 0L)].MemberName);

            // FileId-ordered suffix within the same (Guid) sub-object collision.
            Assert.Equal("Wheel", byKey[("22222222222222222222222222222222", 100L)].MemberName);
            Assert.Equal("Wheel2", byKey[("22222222222222222222222222222222", 200L)].MemberName);
        }

        [Fact]
        public void CrossFolderSameName_DistinctMembers_NoSuffix()
        {
            var raw = new[]
            {
                RawEntry("Materials", new[] { "Environment", "Rocks" }, "Assets/Environment/Rocks/Stone.mat", "11111111111111111111111111111111"),
                RawEntry("Materials", new[] { "Characters", "Skin" }, "Assets/Characters/Skin/Stone.mat", "22222222222222222222222222222222"),
            };

            var catalog = AssetCatalogBuilder.Build(raw);

            var byGuid = catalog.Entries.ToDictionary(e => e.Guid);
            Assert.Equal("Stone", byGuid["11111111111111111111111111111111"].MemberName);
            Assert.Equal(new[] { "Environment", "Rocks" }, byGuid["11111111111111111111111111111111"].FolderSegments);
            Assert.Equal("Stone", byGuid["22222222222222222222222222222222"].MemberName);
            Assert.Equal(new[] { "Characters", "Skin" }, byGuid["22222222222222222222222222222222"].FolderSegments);
        }

        [Fact]
        public void SanitizedSegments_KeywordLeadingDigitAndSpace_ProduceValidIdentifiers()
        {
            var raw = new[]
            {
                // Keyword folder + leading-digit leaf.
                RawEntry("Materials", new[] { "class" }, "Assets/class/2Fast.mat", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                // Space-in-folder-name.
                RawEntry("Materials", new[] { "Big Rocks" }, "Assets/Big Rocks/Foo.mat", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                // Residual folder sanitize-collision: "My Assets" and "My-Assets" both -> "My_Assets".
                RawEntry("Props", new[] { "My Assets" }, "Assets/My Assets/Widget.mat", "11111111111111111111111111111111"),
                RawEntry("Props", new[] { "My-Assets" }, "Assets/My-Assets/Gadget.mat", "22222222222222222222222222222222"),
            };

            var catalog = AssetCatalogBuilder.Build(raw);
            var byGuid = catalog.Entries.ToDictionary(e => e.Guid);

            Assert.Equal(new[] { "@class" }, byGuid["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].FolderSegments);
            Assert.Equal("_2Fast", byGuid["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].MemberName);

            Assert.Equal(new[] { "Big_Rocks" }, byGuid["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"].FolderSegments);

            // "My Assets" (guid 11...) sorts before "My-Assets" (guid 22...) in Guid-Ordinal order.
            Assert.Equal(new[] { "My_Assets" }, byGuid["11111111111111111111111111111111"].FolderSegments);
            Assert.Equal(new[] { "My_Assets2" }, byGuid["22222222222222222222222222222222"].FolderSegments);
        }

        [Fact]
        public void TypeGroups_AreIndependentNamespaces()
        {
            var raw = new[]
            {
                RawEntry("Materials", new[] { "Env" }, "Assets/Env/Stone.mat", "11111111111111111111111111111111"),
                RawEntry("Meshes", new[] { "Env" }, "Assets/Env/Stone.mesh", "22222222222222222222222222222222"),
                RawEntry("Sprites", new[] { "Env" }, "Assets/Env/Stone.png", "33333333333333333333333333333333"),
            };

            var catalog = AssetCatalogBuilder.Build(raw);
            var byGuid = catalog.Entries.ToDictionary(e => e.Guid);

            Assert.Equal("Materials", byGuid["11111111111111111111111111111111"].Group);
            Assert.Equal("Stone", byGuid["11111111111111111111111111111111"].MemberName);
            Assert.Equal("Meshes", byGuid["22222222222222222222222222222222"].Group);
            Assert.Equal("Stone", byGuid["22222222222222222222222222222222"].MemberName);
            Assert.Equal("Sprites", byGuid["33333333333333333333333333333333"].Group);
            Assert.Equal("Stone", byGuid["33333333333333333333333333333333"].MemberName);
        }

        [Fact]
        public void FolderVsLeafCollision_SharedNodeAllocator_Disambiguates()
        {
            var raw = new[]
            {
                // A subfolder "Stone" (created first, in Guid-Ordinal order) holding Detail.mat...
                RawEntry("Materials", new[] { "Env", "Stone" }, "Assets/Env/Stone/Detail.mat", "11111111111111111111111111111111"),
                // ...and a Stone.mat leaf directly in the SAME parent folder "Env" — same allocator
                // as the "Stone" folder class, so this must NOT collide silently (CS0102).
                RawEntry("Materials", new[] { "Env" }, "Assets/Env/Stone.mat", "22222222222222222222222222222222"),
            };

            var catalog = AssetCatalogBuilder.Build(raw);

            var detail = catalog.Entries.Single(e => e.Guid == "11111111111111111111111111111111");
            var leaf = catalog.Entries.Single(e => e.Guid == "22222222222222222222222222222222");

            Assert.Equal(new[] { "Env", "Stone" }, detail.FolderSegments);
            Assert.Equal(new[] { "Env" }, leaf.FolderSegments);
            Assert.Equal("Stone2", leaf.MemberName);
        }
    }
}
