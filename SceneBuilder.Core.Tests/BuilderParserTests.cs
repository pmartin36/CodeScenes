using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class BuilderParserTests
    {
        private const float Tolerance = 1e-5f;

        [Fact]
        public void Parse_RootWithOrderedChildren_ProducesMatchingSceneModel()
        {
            var result = BuilderParser.Parse(BuilderFixtures.TwoRootsWithOrderedChildren);

            Assert.Equal(2, result.Model.Roots.Length);

            var root1 = result.Model.Roots[0];
            Assert.Equal("Root1", root1.Name);
            Assert.Equal("Player", root1.Tag);
            Assert.Equal(8, root1.Layer);
            Assert.True(root1.Active);
            Assert.True(root1.IsStatic);
            Assert.Equal(2, root1.Children.Length);
            Assert.Equal("ChildA", root1.Children[0].Name);
            Assert.Equal("ChildB", root1.Children[1].Name);

            var root2 = result.Model.Roots[1];
            Assert.Equal("Root2", root2.Name);
            Assert.Single(root2.Children);
            Assert.Equal("ChildC", root2.Children[0].Name);
        }

        [Fact]
        public void Parse_Transform_StoresEulerAuthoredRotationAsQuaternion()
        {
            var result = BuilderParser.Parse(BuilderFixtures.TransformWithEulerRotation);

            var node = Assert.Single(result.Model.Roots);
            var expectedRotation = Rotation.EulerToQuat(new Vec3(0, 90, 0));

            Assert.Equal("Transform", node.Transform.Kind);
            Assert.Equal(1f, node.Transform.Position.X, Tolerance);
            Assert.Equal(2f, node.Transform.Position.Y, Tolerance);
            Assert.Equal(3f, node.Transform.Position.Z, Tolerance);
            Assert.Equal(expectedRotation.X, node.Transform.Rotation.X, Tolerance);
            Assert.Equal(expectedRotation.Y, node.Transform.Rotation.Y, Tolerance);
            Assert.Equal(expectedRotation.Z, node.Transform.Rotation.Z, Tolerance);
            Assert.Equal(expectedRotation.W, node.Transform.Rotation.W, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.X, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.Y, Tolerance);
            Assert.Equal(2f, node.Transform.Scale.Z, Tolerance);
        }

        [Fact]
        public void Parse_BareAdd_AppliesContractDefaults()
        {
            var result = BuilderParser.Parse(BuilderFixtures.BareAdd);

            var node = Assert.Single(result.Model.Roots);
            Assert.Equal("Bare", node.Name);
            Assert.Equal("Untagged", node.Tag);
            Assert.Equal(0, node.Layer);
            Assert.True(node.Active);
            Assert.False(node.IsStatic);
            Assert.Equal("Transform", node.Transform.Kind);
            Assert.Equal(Vec3.Zero, node.Transform.Position);
            Assert.Equal(Quat.Identity, node.Transform.Rotation);
            Assert.Equal(Vec3.One, node.Transform.Scale);
        }

        [Fact]
        public void Parse_ClosureNestedChild_ProducesOrderedChild()
        {
            var result = BuilderParser.Parse(BuilderFixtures.ClosureNestedChild);

            var root = Assert.Single(result.Model.Roots);
            Assert.Equal("Root", root.Name);
            var child = Assert.Single(root.Children);
            Assert.Equal("Muzzle", child.Name);
            Assert.Equal(0f, child.Transform.Position.X, Tolerance);
            Assert.Equal(0f, child.Transform.Position.Y, Tolerance);
            Assert.Equal(1f, child.Transform.Position.Z, Tolerance);
        }

        [Fact]
        public void Parse_InterleavedControlFlow_FailsLoudWithLocation()
        {
            var source = BuilderFixtures.InterleavedForLoop;
            var (expectedLine, expectedColumn) = LocateFirstOccurrence(source, "for (");

            var ex = Assert.Throws<ParseException>(() => BuilderParser.Parse(source));

            Assert.Equal(expectedLine, ex.Line);
            Assert.Equal(expectedColumn, ex.Column);
        }

        // Computes the 1-based (line, column) of the first character of `needle` in `source`,
        // so the expected location tracks the fixture text instead of being hard-coded.
        private static (int Line, int Column) LocateFirstOccurrence(string source, string needle)
        {
            var index = source.IndexOf(needle, System.StringComparison.Ordinal);
            Assert.True(index >= 0, $"Fixture does not contain expected marker '{needle}'.");

            var line = 1;
            var lastNewline = -1;
            for (var i = 0; i < index; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    lastNewline = i;
                }
            }

            var column = index - lastNewline;
            return (line, column);
        }
    }
}
