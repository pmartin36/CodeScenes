using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b3-t1: AssetReferencePatcher.Patch(source, oldCatalog, newCatalog) — formatting-preserving
    // rewrite of `Assets.<Group>.<folders...>.<Member>` chains keyed on the durable (Guid, FileId).
    // A rename rewrites the leaf; a move rewrites the folder segments (arity can change); an
    // unchanged target is left byte-identical; a durable target that vanished from the new catalog
    // is left untouched (safety net). See research.md.
    public class AssetReferencePatcherTests
    {
        private const string StoneGuid = "11111111111111111111111111111111";
        private const string DirtGuid = "22222222222222222222222222222222";
        private const string WoodGuid = "33333333333333333333333333333333";
        private const string MissingGuid = "44444444444444444444444444444444";

        private static AssetCatalogEntry Entry(string group, string[] folders, string member, string guid, string path) =>
            new AssetCatalogEntry
            {
                Group = group,
                FolderSegments = folders,
                MemberName = member,
                Guid = guid,
                FileId = 0,
                Path = path,
            };

        private static AssetCatalog OldCatalog() => new AssetCatalog
        {
            Entries = new[]
            {
                Entry("Materials", new[] { "Environment", "Rocks" }, "Stone", StoneGuid, "Assets/Environment/Rocks/Stone.mat"),
                Entry("Materials", new[] { "Environment", "Rocks" }, "Dirt", DirtGuid, "Assets/Environment/Rocks/Dirt.mat"),
                Entry("Materials", new[] { "Furniture" }, "Wood", WoodGuid, "Assets/Furniture/Wood.mat"),
                Entry("Materials", new[] { "Environment", "Rocks" }, "Missing", MissingGuid, "Assets/Environment/Rocks/Missing.mat"),
            },
        };

        // Stone renamed -> Granite (same folder); Dirt moved Environment/Rocks -> Foundations
        // (leaf unchanged); Wood unchanged; Missing removed entirely (vanished durable target).
        private static AssetCatalog NewCatalog() => new AssetCatalog
        {
            Entries = new[]
            {
                Entry("Materials", new[] { "Environment", "Rocks" }, "Granite", StoneGuid, "Assets/Environment/Rocks/Granite.mat"),
                Entry("Materials", new[] { "Foundations" }, "Dirt", DirtGuid, "Assets/Foundations/Dirt.mat"),
                Entry("Materials", new[] { "Furniture" }, "Wood", WoodGuid, "Assets/Furniture/Wood.mat"),
            },
        };

        [Fact]
        public void RewritesRenamedAndMovedMember_KeepsUnchanged_LeavesVanished()
        {
            const string source = @"
public class Scene1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var a = Assets.Materials.Environment.Rocks.Stone;
        var b = Assets.Materials.Environment.Rocks.Dirt;
        var c = Assets.Materials.Furniture.Wood;
        var d = Assets.Materials.Environment.Rocks.Missing;
    }
}
";
            var patched = AssetReferencePatcher.Patch(source, OldCatalog(), NewCatalog());

            Assert.Contains("var a = Assets.Materials.Environment.Rocks.Granite;", patched);
            Assert.Contains("var b = Assets.Materials.Foundations.Dirt;", patched);
            Assert.Contains("var c = Assets.Materials.Furniture.Wood;", patched);
            // Vanished durable target: left byte-identical, never guessed.
            Assert.Contains("var d = Assets.Materials.Environment.Rocks.Missing;", patched);
        }

        [Fact]
        public void MovedAsset_RewritesFolderSegments_NewPathResolved()
        {
            const string source = "var b = Assets.Materials.Environment.Rocks.Dirt;";

            var newCatalog = NewCatalog();
            var patched = AssetReferencePatcher.Patch(source, OldCatalog(), newCatalog);

            Assert.Contains("Assets.Materials.Foundations.Dirt", patched);
            Assert.DoesNotContain("Environment.Rocks.Dirt", patched);

            Assert.True(newCatalog.TryGetEntry("Materials", new[] { "Foundations" }, "Dirt", out var resolved));
            Assert.Equal("Assets/Foundations/Dirt.mat", resolved.Path);
        }

        [Fact]
        public void UnchangedCatalog_ReturnsSourceByteIdentical()
        {
            const string source = @"var a = Assets.Materials.Environment.Rocks.Stone;
var b = Assets.Materials.Furniture.Wood;";

            var catalog = OldCatalog();

            var patched = AssetReferencePatcher.Patch(source, catalog, catalog);

            Assert.Equal(source, patched);
        }

        [Fact]
        public void SecondPatchPass_OnAlreadyPatchedSource_IsNoOp()
        {
            const string source = "var a = Assets.Materials.Environment.Rocks.Stone;";

            var oldCatalog = OldCatalog();
            var newCatalog = NewCatalog();

            var patchedOnce = AssetReferencePatcher.Patch(source, oldCatalog, newCatalog);
            var patchedTwice = AssetReferencePatcher.Patch(patchedOnce, oldCatalog, newCatalog);

            Assert.Equal(patchedOnce, patchedTwice);
        }

        [Fact]
        public void PreservesSurroundingTrivia()
        {
            const string source = "scene.Instance(\n    // note\n    Assets.Materials.Environment.Rocks.Stone\n);";

            var patched = AssetReferencePatcher.Patch(source, OldCatalog(), NewCatalog());

            Assert.Contains("// note\n    Assets.Materials.Environment.Rocks.Granite\n);", patched);
        }
    }
}
