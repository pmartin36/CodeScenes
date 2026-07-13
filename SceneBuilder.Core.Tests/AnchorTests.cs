using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class AnchorTests
    {
        [Fact]
        public void Parse_ReturnsAnchors_MappingLogicalIdToInvocationSpan()
        {
            var source = BuilderFixtures.TwoRootsWithOrderedChildren;
            var result = BuilderParser.Parse(source);

            // One anchor per parsed node: root1, ChildA, ChildB, root2, ChildC.
            Assert.Equal(5, result.Anchors.Count);

            AssertAnchorSlicesTo(source, result.Anchors, "root1", "scene.Add(\"Root1\")");
            AssertAnchorSlicesTo(source, result.Anchors, "root1/ChildA/0", "root1.Add(\"ChildA\")");
        }

        [Fact]
        public void Parse_ClosureNestedChild_AnchorsAreDistinctAndCoverNestedAddCall()
        {
            var source = BuilderFixtures.ClosureNestedChild;
            var result = BuilderParser.Parse(source);

            Assert.Equal(2, result.Anchors.Count);

            var rootSpan = result.Anchors["root"];
            var muzzleSpan = result.Anchors["root/Muzzle/0"];

            Assert.NotEqual(rootSpan, muzzleSpan);

            AssertAnchorSlicesTo(source, result.Anchors, "root", "scene.Add(\"Root\")");
            AssertAnchorSlicesTo(source, result.Anchors, "root/Muzzle/0", "root.Add(\"Muzzle\", m => m.Transform(pos: (0, 0, 1)))");
        }

        private static void AssertAnchorSlicesTo(string source, System.Collections.Generic.IReadOnlyDictionary<string, SceneBuilder.Core.Reconcile.SourceSpan> anchors, string logicalId, string expectedText)
        {
            Assert.True(anchors.ContainsKey(logicalId), $"Anchors missing entry for LogicalId '{logicalId}'.");

            var span = anchors[logicalId];
            var actualText = source.Substring(span.Start, span.Length);
            Assert.Equal(expectedText, actualText);
        }
    }
}
