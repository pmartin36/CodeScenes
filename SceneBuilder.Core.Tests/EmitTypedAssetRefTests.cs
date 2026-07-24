using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: SourceExpr.ValueNodeLiteral becomes catalog-aware — a catalogued project AssetRef
    // ((Guid, FileId) present in the catalog) renders as its typed `Assets.<Group>.<folders...>.<Member>`
    // member chain instead of `Asset("path")`; an un-catalogued project ref and every built-in fall back
    // to the existing string forms unchanged. See specs/28-typed-asset-catalogs.md checklist #10.
    public class EmitTypedAssetRefTests
    {
        private const string StoneGuid = "abc123";

        private static AssetCatalog CatalogWithStoneMaterial() => new AssetCatalog
        {
            Entries = new[]
            {
                new AssetCatalogEntry
                {
                    Group = "Materials",
                    FolderSegments = new[] { "Environment", "Rocks" },
                    MemberName = "Stone",
                    Guid = StoneGuid,
                    FileId = 0,
                    Path = "Assets/Environment/Rocks/Stone.mat",
                },
            },
        };

        [Fact]
        public void EmitTyped_ByDefault_FallsBackWhenUncatalogued()
        {
            var catalog = CatalogWithStoneMaterial();

            var catalogued = new ValueNode.AssetRef(new AssetRef
            {
                Guid = StoneGuid,
                FileId = 0,
                DisplayPath = "Assets/Environment/Rocks/Stone.mat",
            });
            var uncatalogued = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "zzz999",
                FileId = 0,
                DisplayPath = "Assets/Other/Rusty.mat",
            });
            var builtin = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "0000000000000000e000000000000000",
                FileId = 10202,
                IsBuiltin = true,
                DisplayPath = "Cube",
            });

            Assert.Equal("Assets.Materials.Environment.Rocks.Stone", SourceExpr.ValueNodeLiteral(catalogued, catalog));
            Assert.Equal("Asset(\"Assets/Other/Rusty.mat\")", SourceExpr.ValueNodeLiteral(uncatalogued, catalog));
            Assert.Equal("Builtin(\"Cube\")", SourceExpr.ValueNodeLiteral(builtin, catalog));
        }

        // Regression: no catalog supplied (the default/existing call shape) renders exactly the
        // pre-b4-t1 string forms — every existing caller/test must stay green unchanged.
        [Fact]
        public void NullCatalog_EmitsStringFormsUnchanged()
        {
            var node = new ValueNode.AssetRef(new AssetRef
            {
                Guid = StoneGuid,
                FileId = 0,
                DisplayPath = "Assets/Environment/Rocks/Stone.mat",
            });

            Assert.Equal("Asset(\"Assets/Environment/Rocks/Stone.mat\")", SourceExpr.ValueNodeLiteral(node));
        }

        // A catalogued entry with a non-zero FileId (a sub-asset, e.g. an fbx sub-mesh) reverse-looks-up
        // by (Guid, FileId) to its typed member chain — the typed form ignores DisplayPath/SubAsset.
        [Fact]
        public void CataloguedSubAssetRef_EmitsTypedChainIgnoringDisplayPathAndSubAsset()
        {
            var catalog = new AssetCatalog
            {
                Entries = new[]
                {
                    new AssetCatalogEntry
                    {
                        Group = "Meshes",
                        FolderSegments = new[] { "Models", "Props" },
                        MemberName = "Barrel",
                        Guid = "fbxGuid",
                        FileId = 2,
                        Path = "Assets/Models/Props/Barrel.fbx",
                        SubAsset = "BarrelMesh",
                    },
                },
            };
            var node = new ValueNode.AssetRef(new AssetRef
            {
                Guid = "fbxGuid",
                FileId = 2,
                DisplayPath = "Assets/Models/Props/Barrel.fbx",
                SubAsset = "BarrelMesh",
            });

            Assert.Equal("Assets.Meshes.Models.Props.Barrel", SourceExpr.ValueNodeLiteral(node, catalog));
        }
    }
}
