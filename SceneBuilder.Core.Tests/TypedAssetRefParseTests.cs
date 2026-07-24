using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: BuilderParser.Parse(source, existingMap, facadeCatalog, assetCatalog) — an
    // `Assets.<Group>.<folders...>.<Leaf>` member chain resolves, via the AssetCatalog, to the
    // SAME ValueNode.AssetRef the Asset(path[,sub]) string form yields. A catalog-shaped miss
    // (known Group, unresolved chain) yields exactly one located Conflict
    // (Kind=UnknownFacadeReference), never a silent enum. An unknown-Group chain, or a null
    // catalog, falls through to ValueNode.Enum exactly as before this feature (the pinned
    // false-hijack guard — spec's "biggest structural risk"). See research.md.
    public class TypedAssetRefParseTests
    {
        private const string StonePath = "Assets/Environment/Rocks/Stone.mat";
        private const string BarrelPath = "Assets/Models/Props/Barrel.fbx";
        private const long BarrelFileId = 4321;

        private static AssetCatalog CatalogWithStoneAndBarrel() => new AssetCatalog
        {
            Entries = new[]
            {
                new AssetCatalogEntry
                {
                    Group = "Materials",
                    FolderSegments = new[] { "Environment", "Rocks" },
                    MemberName = "Stone",
                    Guid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Path = StonePath,
                },
                new AssetCatalogEntry
                {
                    Group = "Meshes",
                    FolderSegments = new[] { "Models", "Props" },
                    MemberName = "Barrel",
                    Guid = "cccccccccccccccccccccccccccccccc",
                    FileId = BarrelFileId,
                    Path = BarrelPath,
                    SubAsset = "Barrel",
                },
            },
        };

        private const string TypedStoneSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Rock"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Assets.Materials.Environment.Rocks.Stone));
    }
}
";

        private const string StringStoneSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Rock"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Asset(""Assets/Environment/Rocks/Stone.mat"")));
    }
}
";

        private const string TypedBarrelSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Barrel"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", Assets.Meshes.Models.Props.Barrel));
    }
}
";

        private const string StringBarrelSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Barrel"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", Asset(""Assets/Models/Props/Barrel.fbx"", ""Barrel"")));
    }
}
";

        private const string UnknownLeafSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Rock"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Assets.Materials.Environment.Rocks.Nope));
    }
}
";

        private const string UnknownGroupSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Rock"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Assets.Foo.Bar));
    }
}
";

        // #2
        [Fact]
        public void TypedAssetRef_ParsesToSameAssetRefAsString()
        {
            var typedResult = BuilderParser.Parse(TypedStoneSource, null, null, CatalogWithStoneAndBarrel());
            var stringResult = BuilderParser.Parse(StringStoneSource);

            var typedComponent = Assert.Single(Assert.Single(typedResult.Model.Roots).Components);
            var stringComponent = Assert.Single(Assert.Single(stringResult.Model.Roots).Components);

            var typedField = Assert.IsType<ValueNode.AssetRef>(typedComponent.Fields["sharedMaterial"]);
            var stringField = Assert.IsType<ValueNode.AssetRef>(stringComponent.Fields["sharedMaterial"]);

            Assert.NotNull(typedField.Ref);
            Assert.Equal(stringField.Ref!.DisplayPath, typedField.Ref!.DisplayPath);
            Assert.Equal(stringField.Ref.SubAsset, typedField.Ref.SubAsset);
            Assert.Empty(typedResult.Ambiguities);
        }

        // #2 sub-asset variant
        [Fact]
        public void TypedAssetRef_SubAsset_ParsesToSameAssetRefAsString()
        {
            var typedResult = BuilderParser.Parse(TypedBarrelSource, null, null, CatalogWithStoneAndBarrel());
            var stringResult = BuilderParser.Parse(StringBarrelSource);

            var typedComponent = Assert.Single(Assert.Single(typedResult.Model.Roots).Components);
            var stringComponent = Assert.Single(Assert.Single(stringResult.Model.Roots).Components);

            var typedField = Assert.IsType<ValueNode.AssetRef>(typedComponent.Fields["m_Mesh"]);
            var stringField = Assert.IsType<ValueNode.AssetRef>(stringComponent.Fields["m_Mesh"]);

            Assert.NotNull(typedField.Ref);
            Assert.Equal(stringField.Ref!.DisplayPath, typedField.Ref!.DisplayPath);
            Assert.Equal(stringField.Ref.SubAsset, typedField.Ref.SubAsset);
            Assert.Empty(typedResult.Ambiguities);
        }

        // #3
        [Fact]
        public void UnknownTypedAssetRef_YieldsLocatedConflict()
        {
            var result = BuilderParser.Parse(UnknownLeafSource, null, null, CatalogWithStoneAndBarrel());

            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
            Assert.NotNull(conflict.Location);
        }

        // Regression: the false-hijack guard. Neither case is claimed by the new arm.
        [Fact]
        public void NonResolvingAssetsChain_FallsThroughToEnum()
        {
            // (a) `Foo` is not a known group -> falls through to the ordinary enum arm.
            var unknownGroupResult = BuilderParser.Parse(UnknownGroupSource, null, null, CatalogWithStoneAndBarrel());
            var unknownGroupComponent = Assert.Single(Assert.Single(unknownGroupResult.Model.Roots).Components);
            Assert.Equal(
                new ValueNode.Enum("Assets.Foo", new[] { "Bar" }, IsFlags: false),
                unknownGroupComponent.Fields["sharedMaterial"]);
            Assert.Empty(unknownGroupResult.Ambiguities);

            // (b) a null catalog -> falls through to the ordinary enum arm even for an
            // otherwise-resolvable chain.
            var nullCatalogResult = BuilderParser.Parse(TypedStoneSource, null, null, null);
            var nullCatalogComponent = Assert.Single(Assert.Single(nullCatalogResult.Model.Roots).Components);
            Assert.Equal(
                new ValueNode.Enum("Assets.Materials.Environment.Rocks", new[] { "Stone" }, IsFlags: false),
                nullCatalogComponent.Fields["sharedMaterial"]);
            Assert.Empty(nullCatalogResult.Ambiguities);
        }
    }
}
