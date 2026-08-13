using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // BuilderParser.FindBuildMethod recognizes a class implementing IPrefabDefinition the same
    // way it recognizes ISceneDefinition: by walking its base-list interface identifiers, not by
    // falling back to "the only Build method in the file" (that fallback exists for files with no
    // recognized interface at all, and must not be the thing making a prefab builder parse).
    public class PrefabDefinitionParseTests
    {
        // A second, unrelated `Build` method in the same file makes the "single Build method in
        // the file" fallback ambiguous. Explicit interface-based recognition must find the
        // IPrefabDefinition class's Build method directly, without ever reaching that fallback.
        private const string PrefabWithUnrelatedBuildMethod = @"
public class UnrelatedHelper
{
    public void Build()
    {
    }
}

public class CarPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"");
        car.Add(""Body"");
    }
}
";

        [Fact]
        public void Parse_IPrefabDefinitionClass_RecognizedByInterface_NotByAmbiguousFallback()
        {
            var result = BuilderParser.Parse(PrefabWithUnrelatedBuildMethod);

            var carRoot = Assert.Single(result.Model.Roots);
            Assert.Equal("Car", carRoot.Name);
            var body = Assert.Single(carRoot.Children);
            Assert.Equal("Body", body.Name);
        }
    }
}
