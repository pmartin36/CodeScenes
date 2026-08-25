using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A `const string` prefix concatenated with a literal (or a bare reference to one) folds to
    // its constant value everywhere a bare string literal is required: the Instance(...)
    // path, Add(name), and Asset(path, sub) / Builtin(name) values. Folding accepts a class-level
    // const field, a Build-body const local, and a const built from another const.
    public class ConstStringFoldingTests
    {
        [Fact]
        public void ClassFieldConstConcat_InstancePath_FoldsToConstantValue()
        {
            var source = @"
public class ClassFieldConstInstanceScene : ISceneDefinition
{
    private const string Kit = ""Assets/K/"";

    public void Build(SceneRoot scene)
    {
        scene.Instance(Kit + ""start.fbx"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("Assets/K/start.fbx", instance.SourcePrefab.DisplayPath);
        }

        [Fact]
        public void ClassFieldConstConcat_AddName_FoldsToConstantValue()
        {
            var source = @"
public class ClassFieldConstAddScene : ISceneDefinition
{
    private const string Kit = ""Prefix_"";

    public void Build(SceneRoot scene)
    {
        scene.Add(Kit + ""Crate"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("Prefix_Crate", node.Name);
        }

        [Fact]
        public void BodyLocalConstConcat_InstancePath_FoldsAndIsNotABuilderChain()
        {
            var source = @"
public class BodyLocalConstInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        const string Kit = ""Assets/K/"";
        scene.Instance(Kit + ""start.fbx"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("Assets/K/start.fbx", instance.SourcePrefab.DisplayPath);
        }

        [Fact]
        public void BareClassFieldConstReference_InstancePath_FoldsToConstantValue()
        {
            var source = @"
public class BareConstReferenceScene : ISceneDefinition
{
    private const string EnemyPrefab = ""Assets/Prefabs/Enemy.prefab"";

    public void Build(SceneRoot scene)
    {
        scene.Instance(EnemyPrefab);
    }
}
";
            var result = BuilderParser.Parse(source);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("Assets/Prefabs/Enemy.prefab", instance.SourcePrefab.DisplayPath);
        }

        [Fact]
        public void ConstBuiltFromAnotherConst_Concat_FoldsTransitively()
        {
            var source = @"
public class ConstOfConstScene : ISceneDefinition
{
    private const string Root = ""Assets/"";
    private const string Kit = Root + ""K/"";

    public void Build(SceneRoot scene)
    {
        scene.Instance(Kit + ""start.fbx"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("Assets/K/start.fbx", instance.SourcePrefab.DisplayPath);
        }

        [Fact]
        public void AssetValue_ConstConcatDisplayPath_YieldsFoldedAssetRefNotUnsupported()
        {
            var source = @"
public class AssetValueConstConcatScene : ISceneDefinition
{
    private const string Kit = ""Assets/K/"";

    public void Build(SceneRoot scene)
    {
        scene.Add(""Barrel"").Component<UnityEngine.MeshFilter>(mf => mf.Set(""m_Mesh"", Asset(Kit + ""start.fbx"", ""start"")));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["m_Mesh"]);
            Assert.NotNull(field.Ref);
            Assert.Equal("Assets/K/start.fbx", field.Ref!.DisplayPath);
            Assert.Equal("start", field.Ref.SubAsset);
        }

        [Fact]
        public void BuiltinValue_ConstConcatName_YieldsFoldedAssetRefNotUnsupported()
        {
            var source = @"
public class BuiltinValueConstConcatScene : ISceneDefinition
{
    private const string Kit = ""Default"";

    public void Build(SceneRoot scene)
    {
        scene.Add(""Cube"").Component<UnityEngine.MeshRenderer>(mr => mr.Set(""sharedMaterial"", Builtin(Kit + ""-Material"")));
    }
}
";
            var result = BuilderParser.Parse(source);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            var field = Assert.IsType<ValueNode.AssetRef>(component.Fields["sharedMaterial"]);
            Assert.NotNull(field.Ref);
            Assert.True(field.Ref!.IsBuiltin);
            Assert.Equal("Default-Material", field.Ref.DisplayPath);
        }
    }
}
