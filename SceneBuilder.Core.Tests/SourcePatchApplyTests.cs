using System;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SourcePatchApplyTests
    {
        // Three isolated groups so PatchArgument/MoveStatement/ReorderStatement don't interact
        // positionally: root1/childA (transform patch), root2/ChildB (reparent into root1),
        // root3's closure (own BlockSyntax, so ChildX/ChildY reorder is unambiguous).
        private const string PatchFixture = @"
public class PatchScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Root one holds a transformed child.
        var root1 = scene.Add(""Root1"");
        var childA = root1.Add(""ChildA"").Transform(pos: (1, 2, 3));

        // Root two holds a child that gets reparented.
        var root2 = scene.Add(""Root2"");
        root2.Add(""ChildB"");

        // Root three holds two children that swap order.
        scene.Add(""Root3"", r3 =>
        {
            r3.Add(""ChildX"");
            r3.Add(""ChildY"");
        });
    }
}
";

        [Fact]
        public void SourcePatch_Apply_PreservesUnrelatedFormattingAndComments()
        {
            var source = PatchFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "childA", ArgName = "pos", NewExpr = "(9, 9, 9)" },
                    new MoveStatement { Anchor = "root2/ChildB/0", NewParentAnchor = "root1" },
                    new ReorderStatement { Anchor = "Root3/2/ChildY/1", NewSiblingIndex = 0 },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Comments survive untouched.
            Assert.Contains("// Root one holds a transformed child.", result);
            Assert.Contains("// Root two holds a child that gets reparented.", result);
            Assert.Contains("// Root three holds two children that swap order.", result);

            // Statements untouched by any edit are byte-identical, including indentation.
            Assert.Contains("        var root1 = scene.Add(\"Root1\");", result);
            Assert.Contains("        var root2 = scene.Add(\"Root2\");", result);

            // PatchArgument: only the targeted Transform arg changed.
            Assert.Contains("pos: (9, 9, 9)", result);
            Assert.DoesNotContain("pos: (1, 2, 3)", result);

            // MoveStatement: ChildB relocated under root1, receiver rewritten to the new parent handle.
            Assert.Contains("root1.Add(\"ChildB\")", result);
            Assert.DoesNotContain("root2.Add(\"ChildB\")", result);

            // ReorderStatement: ChildY now precedes ChildX within root3's closure block.
            Assert.True(
                result.IndexOf("ChildY", StringComparison.Ordinal) < result.IndexOf("ChildX", StringComparison.Ordinal),
                "Expected ChildY to precede ChildX after reorder.");
        }

        [Fact]
        public void Apply_RenameNameArg_ReplacesOnlyLiteral()
        {
            const string source = @"
public class RenameScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""OldName"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;
            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "root", ArgName = "name", NewExpr = "\"NewName\"" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Only the name literal changes; every other byte (including quotes/whitespace) is identical.
            Assert.Equal(source.Replace("OldName", "NewName"), result);
        }

        [Fact]
        public void Apply_MissingAnchor_ThrowsLocatedPatchException()
        {
            var source = BuilderFixtures.BareAdd;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "ghost", ArgName = "name", NewExpr = "\"X\"" },
                },
            };

            var ex = Assert.Throws<PatchException>(() => SourcePatchApplier.Apply(source, patch, anchors));

            Assert.Contains("ghost", ex.Message);
            Assert.True(ex.Line >= 0);
            Assert.True(ex.Column >= 0);
        }
    }
}
