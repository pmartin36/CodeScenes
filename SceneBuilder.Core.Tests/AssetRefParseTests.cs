using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: Asset("path") / Asset(null) / Asset.None / Asset("path","sub") parsing +
    // emit round-trip in ValueNodeParser / SourceExpr.
    public class AssetRefParseTests
    {
        private const string StringLiteralSource = @"
public class AssetStringLiteralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Asset(""Assets/Materials/Red.mat"")));
    }
}
";

        private const string NullLiteralSource = @"
public class AssetNullLiteralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Asset(null)));
    }
}
";

        private const string DotNoneSource = @"
public class AssetDotNoneScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Player"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Asset.None));
    }
}
";

        private const string OrdinaryEnumSource = @"
public class OrdinaryEnumScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Zone"").Component<Game.Trigger>(t => t.Set(""faction"", Game.Faction.Enemy));
    }
}
";

        // Asset("a", "b") is now a VALID 2-arg sub-asset form (b2-t1) — it moved out of this
        // fixture (see Parse_AssetWithSubAssetName_YieldsSubAsset) and is replaced here with the
        // shapes that stay genuinely unsupported: a non-literal/null 2nd arg, wrong arity, and a
        // non-literal 1st arg.
        private const string WrongArityAndNonLiteralSource = @"
public class AssetUnsupportedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Weird"").Component<UnityEngine.MeshRenderer>(mr =>
        {
            mr.Set(""nullSecondArg"", Asset(""a"", null));
            mr.Set(""nonLiteralSecondArg"", Asset(""a"", someVar));
            mr.Set(""noArgs"", Asset());
            mr.Set(""tooManyArgs"", Asset(""a"", ""b"", ""c""));
            mr.Set(""nonLiteral"", Asset(GetPath()));
        });
    }
}
";

        private const string SubAssetSource = @"
public class AssetSubAssetScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Barrel"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", Asset(""Assets/Models/Barrel.fbx"", ""BarrelMesh"")));
    }
}
";

        private const string MixedThreeKindsListSource = @"
public class AssetMixedThreeKindsListScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Renderer"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""m_Materials"", new[] { Asset(""Assets/Models/Barrel.fbx""), Builtin(""Cube""), Asset(""Assets/Models/Barrel.fbx"", ""BarrelMat"") }));
    }
}
";

        [Fact]
        public void Parse_AssetStringLiteral_YieldsAssetRefWithDisplayPathEmptyGuid()
        {
            var result = BuilderParser.Parse(StringLiteralSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("Assets/Materials/Red.mat", field.Ref!.DisplayPath);
            Assert.Equal("", field.Ref.Guid);
            // #2 regression: the 1-arg form still yields an empty SubAsset (b2-t1).
            Assert.Equal("", field.Ref.SubAsset);
        }

        // #1: Asset("p", "s") yields a populated SubAsset alongside the path.
        [Fact]
        public void Parse_AssetWithSubAssetName_YieldsSubAsset()
        {
            var result = BuilderParser.Parse(SubAssetSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["m_Mesh"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("Assets/Models/Barrel.fbx", field.Ref!.DisplayPath);
            Assert.Equal("BarrelMesh", field.Ref.SubAsset);
            Assert.Equal("", field.Ref.Guid);
            Assert.False(field.Ref.IsBuiltin);
        }

        [Fact]
        public void Parse_AssetNullLiteral_YieldsAssetRefNull()
        {
            var result = BuilderParser.Parse(NullLiteralSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.Null(field.Ref);
        }

        [Fact]
        public void Parse_AssetDotNone_YieldsAssetRefNull()
        {
            var result = BuilderParser.Parse(DotNoneSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.Null(field.Ref);
        }

        // Regression guard: the guarded Asset.None member-access arm must not swallow
        // ordinary enum member access.
        [Fact]
        public void Parse_OrdinaryEnumMemberAccess_StillYieldsEnum()
        {
            var result = BuilderParser.Parse(OrdinaryEnumSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Enum("Game.Faction", new[] { "Enemy" }, false), component.Fields["faction"]);
        }

        // #4: a null/non-literal 2nd arg, wrong arity (0 or 3 args), or a non-literal 1st arg all
        // yield Unsupported with the verbatim source token — the parser stays total, never throws.
        [Fact]
        public void Parse_AssetWithNonStringOrWrongAritySecondArg_YieldsUnsupported()
        {
            var result = BuilderParser.Parse(WrongArityAndNonLiteralSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Unsupported("Asset(\"a\", null)"), component.Fields["nullSecondArg"]);
            Assert.Equal(new ValueNode.Unsupported("Asset(\"a\", someVar)"), component.Fields["nonLiteralSecondArg"]);
            Assert.Equal(new ValueNode.Unsupported("Asset()"), component.Fields["noArgs"]);
            Assert.Equal(new ValueNode.Unsupported("Asset(\"a\", \"b\", \"c\")"), component.Fields["tooManyArgs"]);
            Assert.Equal(new ValueNode.Unsupported("Asset(GetPath())"), component.Fields["nonLiteral"]);
        }

        // #5: a list mixing a main-asset ref, a built-in ref, and a sub-asset ref — all three
        // ValueNode.AssetRef shapes coexist in one list, in order.
        [Fact]
        public void Parse_AssetBuiltinSubAssetInSameList_YieldsAllThreeKinds()
        {
            var result = BuilderParser.Parse(MixedThreeKindsListSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var list = Assert.IsType<ValueNode.List>(component.Fields["m_Materials"]);
            Assert.Equal(3, list.Items.Count);

            var mainAsset = Assert.IsType<ValueNode.AssetRef>(list.Items[0]);
            Assert.NotNull(mainAsset.Ref);
            Assert.Equal("Assets/Models/Barrel.fbx", mainAsset.Ref!.DisplayPath);
            Assert.Equal("", mainAsset.Ref.SubAsset);
            Assert.False(mainAsset.Ref.IsBuiltin);

            var builtin = Assert.IsType<ValueNode.AssetRef>(list.Items[1]);
            Assert.NotNull(builtin.Ref);
            Assert.True(builtin.Ref!.IsBuiltin);
            Assert.Equal("Cube", builtin.Ref.DisplayPath);

            var subAsset = Assert.IsType<ValueNode.AssetRef>(list.Items[2]);
            Assert.NotNull(subAsset.Ref);
            Assert.Equal("Assets/Models/Barrel.fbx", subAsset.Ref!.DisplayPath);
            Assert.Equal("BarrelMat", subAsset.Ref.SubAsset);
            Assert.False(subAsset.Ref.IsBuiltin);
        }

        // #8: parse -> emit round-trips to identical source text, for both the 1-arg main-asset
        // form and the 2-arg sub-asset form.
        [Fact]
        public void Parse_EmitSubAsset_TextRoundTripsIdentically()
        {
            var subResult = BuilderParser.Parse(SubAssetSource);
            var subComponent = Assert.Single(Assert.Single(subResult.Model.Roots).Components);
            // Must genuinely parse to a populated AssetRef, not fall back to Unsupported
            // round-tripping its own raw token (which would pass this text check vacuously).
            Assert.IsType<ValueNode.AssetRef>(subComponent.Fields["m_Mesh"]);
            Assert.Equal(
                "Asset(\"Assets/Models/Barrel.fbx\", \"BarrelMesh\")",
                SourceExpr.ValueNodeLiteral(subComponent.Fields["m_Mesh"]));

            var mainResult = BuilderParser.Parse(StringLiteralSource);
            var mainComponent = Assert.Single(Assert.Single(mainResult.Model.Roots).Components);
            Assert.Equal(
                "Asset(\"Assets/Materials/Red.mat\")",
                SourceExpr.ValueNodeLiteral(mainComponent.Fields["sharedMaterial"]));
        }
    }
}
