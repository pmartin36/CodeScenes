using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: Asset("path") / Asset(null) / Asset.None parsing in ValueNodeParser.
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

        private const string WrongArityAndNonLiteralSource = @"
public class AssetUnsupportedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Weird"").Component<UnityEngine.MeshRenderer>(mr =>
        {
            mr.Set(""wrongArity"", Asset(""a"", ""b""));
            mr.Set(""nonLiteral"", Asset(GetPath()));
        });
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

        [Fact]
        public void Parse_AssetWithNonStringArgOrWrongArity_YieldsUnsupported()
        {
            var result = BuilderParser.Parse(WrongArityAndNonLiteralSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Unsupported("Asset(\"a\", \"b\")"), component.Fields["wrongArity"]);
            Assert.Equal(new ValueNode.Unsupported("Asset(GetPath())"), component.Fields["nonLiteral"]);
        }
    }
}
