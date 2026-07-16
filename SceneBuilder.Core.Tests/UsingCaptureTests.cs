using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t1 (unqualified-type-names): ParseResult.Usings — file-scope PLAIN `using`
    // directive capture. Purely syntactic (no resolution); TypeRef stays the raw token.
    public class UsingCaptureTests
    {
        [Fact]
        public void Parse_FileScopeUsings_CapturedInOrder()
        {
            var result = BuilderParser.Parse(BuilderFixtures.TwoPlainUsings);

            Assert.Equal(new[] { "UnityEngine", "UnityEngine.UI" }, result.Usings);
        }

        [Fact]
        public void Parse_NoUsings_YieldsEmptyList()
        {
            var result = BuilderParser.Parse(BuilderFixtures.BareAdd);

            Assert.NotNull(result.Usings);
            Assert.Empty(result.Usings);
        }

        [Fact]
        public void Parse_UsingStaticAndAlias_AreExcluded()
        {
            var result = BuilderParser.Parse(BuilderFixtures.StaticAndAliasUsingsWithOnePlain);

            Assert.Equal(new[] { "UnityEngine" }, result.Usings);
        }

        [Fact]
        public void Parse_NamespaceNestedUsing_NotCaptured()
        {
            var result = BuilderParser.Parse(BuilderFixtures.NamespaceNestedUsing);

            Assert.Empty(result.Usings);
            // FindBuildMethod still finds the nested class despite the excluded using.
            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("X", node.Name);
        }

        [Fact]
        public void Parse_ShortComponentType_TypeRefKeepsRawToken()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ShortComponentTypeWithUsing);

            var node = Assert.Single(result.Model.Roots);
            var component = Assert.Single(node.Components);

            Assert.Equal(new[] { "UnityEngine" }, result.Usings);
            Assert.Equal("Rigidbody", component.Type.FullName);
        }
    }
}
