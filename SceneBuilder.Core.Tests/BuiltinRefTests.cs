using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t2: Builtin("Name") / Builtin("Name", "TypeHint") parsing in ValueNodeParser.
    // b1-t3: emit + text round-trip cases (#6-#9).
    public class BuiltinRefTests
    {
        private const string BuiltinStringLiteralSource = @"
public class BuiltinStringLiteralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", Builtin(""Cube"")));
    }
}
";

        private const string BuiltinWithTypeHintSource = @"
public class BuiltinWithTypeHintScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Sprite"").Component<UnityEngine.SpriteRenderer>(sr => sr.Set(""m_Sprite"", Builtin(""UISprite"", ""Sprite"")));
    }
}
";

        private const string BuiltinUnsupportedShapesSource = @"
public class BuiltinUnsupportedShapesScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Weird"").Component<UnityEngine.MeshFilter>(mf =>
        {
            mf.Set(""noArgs"", Builtin());
            mf.Set(""nullArg"", Builtin(null));
            mf.Set(""nonLiteral"", Builtin(someVar));
            mf.Set(""tooManyArgs"", Builtin(""a"",""b"",""c""));
        });
    }
}
";

        private const string QualifiedBuiltinInvocationSource = @"
public class QualifiedBuiltinInvocationScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", AssetRefs.Builtin(""Cube"")));
    }
}
";

        private const string AssetAndBuiltinInSameListSource = @"
public class AssetAndBuiltinInSameListScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Renderer"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""m_Materials"", new[] { Asset(""Assets/M/Red.mat""), Builtin(""Cube""), Asset(null) }));
    }
}
";

        [Fact]
        public void Parse_BuiltinStringLiteral_YieldsBuiltinAssetRefWithNameEmptyGuid()
        {
            var result = BuilderParser.Parse(BuiltinStringLiteralSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["m_Mesh"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("Cube", field.Ref!.DisplayPath);
            Assert.True(field.Ref.IsBuiltin);
            Assert.Equal("", field.Ref.Guid);
            Assert.Equal("", field.Ref.TypeHint);
        }

        [Fact]
        public void Parse_BuiltinWithTypeQualifier_YieldsTypeHint()
        {
            var result = BuilderParser.Parse(BuiltinWithTypeHintSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["m_Sprite"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("UISprite", field.Ref!.DisplayPath);
            Assert.Equal("Sprite", field.Ref.TypeHint);
            Assert.True(field.Ref.IsBuiltin);
        }

        [Fact]
        public void Parse_BuiltinWithNonStringArgOrWrongArity_YieldsUnsupported()
        {
            var result = BuilderParser.Parse(BuiltinUnsupportedShapesSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Unsupported("Builtin()"), component.Fields["noArgs"]);
            Assert.Equal(new ValueNode.Unsupported("Builtin(null)"), component.Fields["nullArg"]);
            Assert.Equal(new ValueNode.Unsupported("Builtin(someVar)"), component.Fields["nonLiteral"]);
            Assert.Equal(new ValueNode.Unsupported("Builtin(\"a\",\"b\",\"c\")"), component.Fields["tooManyArgs"]);
        }

        [Fact]
        public void Parse_QualifiedBuiltinInvocation_YieldsUnsupported()
        {
            var result = BuilderParser.Parse(QualifiedBuiltinInvocationSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new ValueNode.Unsupported("AssetRefs.Builtin(\"Cube\")"), component.Fields["m_Mesh"]);
        }

        [Fact]
        public void Parse_AssetAndBuiltinInSameList_YieldsBothKinds()
        {
            var result = BuilderParser.Parse(AssetAndBuiltinInSameListSource);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var list = Assert.IsType<ValueNode.List>(component.Fields["m_Materials"]);
            Assert.Equal(3, list.Items.Count);

            var first = Assert.IsType<ValueNode.AssetRef>(list.Items[0]);
            Assert.NotNull(first.Ref);
            Assert.Equal("Assets/M/Red.mat", first.Ref!.DisplayPath);
            Assert.False(first.Ref.IsBuiltin);

            var second = Assert.IsType<ValueNode.AssetRef>(list.Items[1]);
            Assert.NotNull(second.Ref);
            Assert.Equal("Cube", second.Ref!.DisplayPath);
            Assert.True(second.Ref.IsBuiltin);

            var third = Assert.IsType<ValueNode.AssetRef>(list.Items[2]);
            Assert.Null(third.Ref);
        }

        [Fact]
        public void SourceExpr_BuiltinRefWithoutQualifier_EmitsBareBuiltinCall()
        {
            var node = new ValueNode.AssetRef(new AssetRef { IsBuiltin = true, DisplayPath = "Cube", TypeHint = "" });

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("Builtin(\"Cube\")", text);
        }

        [Fact]
        public void SourceExpr_BuiltinRefWithQualifier_EmitsQualifiedBuiltinCall()
        {
            var node = new ValueNode.AssetRef(new AssetRef { IsBuiltin = true, DisplayPath = "UISprite", TypeHint = "Sprite" });

            var text = SourceExpr.ValueNodeLiteral(node);

            Assert.Equal("Builtin(\"UISprite\", \"Sprite\")", text);
        }

        [Fact]
        public void SourceExpr_NonBuiltinRef_StillEmitsAssetCall()
        {
            var populated = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/M/Red.mat" });
            var none = new ValueNode.AssetRef(null);

            Assert.Equal("Asset(\"Assets/M/Red.mat\")", SourceExpr.ValueNodeLiteral(populated));
            Assert.Equal("Asset(null)", SourceExpr.ValueNodeLiteral(none));
        }

        [Fact]
        public void Parse_EmitBuiltin_TextRoundTripsIdentically()
        {
            var bareResult = BuilderParser.Parse(BuiltinStringLiteralSource);
            var bareNode = Assert.Single(bareResult.Model.Roots);
            var bareComponent = Assert.Single(bareNode.Components);
            Assert.Equal("Builtin(\"Cube\")", SourceExpr.ValueNodeLiteral(bareComponent.Fields["m_Mesh"]));

            var qualifiedResult = BuilderParser.Parse(BuiltinWithTypeHintSource);
            var qualifiedNode = Assert.Single(qualifiedResult.Model.Roots);
            var qualifiedComponent = Assert.Single(qualifiedNode.Components);
            Assert.Equal("Builtin(\"UISprite\", \"Sprite\")", SourceExpr.ValueNodeLiteral(qualifiedComponent.Fields["m_Sprite"]));
        }
    }
}
