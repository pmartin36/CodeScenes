using System;
using SceneBuilder.Core.Model;
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

        // Neighbors each own a comment as their leading trivia; the target's own comment is
        // its leading trivia too. Blank line before the target belongs to the target's
        // leading trivia (removed with it); blank line after the target belongs to the
        // "below" neighbor's leading trivia (preserved).
        private const string RemoveFixture = @"
public class RemoveScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Neighbor above, with its own comment.
        var above = scene.Add(""Above"");

        // Target statement to be removed.
        var target = scene.Add(""Target"");

        // Neighbor below, with its own comment.
        var below = scene.Add(""Below"");
    }
}
";

        [Fact]
        public void Remove_DeletesStatement_PreservingSurroundingFormatting()
        {
            var source = RemoveFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = "target" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Target statement and its own comment are gone entirely.
            Assert.DoesNotContain("Target", result);
            Assert.DoesNotContain("// Target statement to be removed.", result);

            // Neighbors are byte-identical, including indentation and their own comments.
            Assert.Contains("        // Neighbor above, with its own comment.\n        var above = scene.Add(\"Above\");", result);
            Assert.Contains("        // Neighbor below, with its own comment.\n        var below = scene.Add(\"Below\");", result);

            // Exactly one blank line survives between the neighbors (the target's own
            // leading blank line is removed with it; the "below" neighbor's leading
            // blank line is untouched) — proves neighbor trivia was not double-preserved
            // or accidentally collapsed.
            Assert.Contains(
                "        var above = scene.Add(\"Above\");\n\n        // Neighbor below, with its own comment.",
                result);

            // Result re-parses cleanly: no orphaned/malformed trivia broke the syntax.
            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("above"));
            Assert.True(reparsed.Anchors.ContainsKey("below"));
            Assert.False(reparsed.Anchors.ContainsKey("target"));
        }

        [Fact]
        public void Remove_MissingAnchor_ThrowsLocatedPatchException()
        {
            var source = BuilderFixtures.BareAdd;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new RemoveStatement { Anchor = "ghost" },
                },
            };

            var ex = Assert.Throws<PatchException>(() => SourcePatchApplier.Apply(source, patch, anchors));

            Assert.Contains("ghost", ex.Message);
            Assert.True(ex.Line >= 0);
            Assert.True(ex.Column >= 0);
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

        // ---- AppendStatement --------------------------------------------------------------

        private const string AppendRootFixture = @"
public class AppendRootScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing root, untouched.
        var existing = scene.Add(""Existing"");
    }
}
";

        [Fact]
        public void Append_AppliesWellFormedStatement_WithFSuffixedFloats_FormattingPreserved()
        {
            var source = AppendRootFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "New/1",
                        Name = "New",
                        Transform = new TransformData { Position = new Vec3(1.5f, 2f, 3.25f) },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Unrelated statement + its comment are byte-identical.
            Assert.Contains("        // Existing root, untouched.\n        var existing = scene.Add(\"Existing\");", result);

            // New statement appended at end of Build body, own line, body indentation,
            // non-integer floats f-suffixed, integer float (2) left bare.
            Assert.Contains("        scene.Add(\"New\").Transform(pos: (1.5f, 2, 3.25f));\n", result);

            // Re-parses cleanly and reads back with the intended name/transform.
            var reparsed = BuilderParser.Parse(result);
            Assert.Equal(2, reparsed.Model.Roots.Length);
            var appended = reparsed.Model.Roots[1];
            Assert.Equal("New", appended.Name);
            Assert.Equal(new Vec3(1.5f, 2f, 3.25f), appended.Transform.Position);
            Assert.Equal(Vec3.One, appended.Transform.Scale);
        }

        private const string AppendChildFixture = @"
public class AppendChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var parent = scene.Add(""Parent"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Append_ChildUnderHandledParent_InsertsAfterParentStatement()
        {
            var source = AppendChildFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "Parent/Child/0",
                        ParentAnchor = "parent",
                        ParentHandle = "parent",
                        Name = "Child",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Inserted immediately after the parent statement, receiver = parent handle.
            Assert.Contains(
                "        var parent = scene.Add(\"Parent\");\n        parent.Add(\"Child\");\n",
                result);

            // The unrelated sibling (and its comment/blank line) is untouched.
            Assert.Contains(
                "        // Unrelated sibling below, must stay untouched.\n        var sibling = scene.Add(\"Sibling\");",
                result);

            var reparsed = BuilderParser.Parse(result);
            var parent = Assert.Single(reparsed.Model.Roots, r => r.Name == "Parent");
            var child = Assert.Single(parent.Children);
            Assert.Equal("Child", child.Name);
        }

        private const string AppendHandleFixture = @"
public class AppendHandleScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var existing = scene.Add(""Existing"");
    }
}
";

        [Fact]
        public void Append_WithHandle_EmitsVarDeclaration()
        {
            var source = AppendHandleFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "newHandle",
                        Name = "Handled",
                        Handle = "newHandle",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains("        var newHandle = scene.Add(\"Handled\");\n", result);

            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("newHandle"));
        }

        private const string AppendFlagsFixture = @"
public class AppendFlagsScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var existing = scene.Add(""Existing"");
    }
}
";

        [Fact]
        public void Append_WithFlags_EmitsCanonicalChainOrder()
        {
            var source = AppendFlagsFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "Flagged/1",
                        Name = "Flagged",
                        Tag = "Player",
                        Layer = 5,
                        Active = false,
                        IsStatic = true,
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Canonical order: Transform, Tag, Layer, Active, Static (Transform omitted here).
            Assert.Contains(
                "        scene.Add(\"Flagged\").Tag(\"Player\").Layer(5).Active(false).Static();\n",
                result);

            var reparsed = BuilderParser.Parse(result);
            var flagged = Assert.Single(reparsed.Model.Roots, r => r.Name == "Flagged");
            Assert.Equal("Player", flagged.Tag);
            Assert.Equal(5, flagged.Layer);
            Assert.False(flagged.Active);
            Assert.True(flagged.IsStatic);
        }

        private const string AppendIntroduceHandleFixture = @"
public class AppendIntroduceHandleScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Parent has no handle yet.
        scene.Add(""Parent"");

        // Unrelated sibling below, must stay untouched.
        var sibling = scene.Add(""Sibling"");
    }
}
";

        [Fact]
        public void Append_IntroducesHandleOnHandlelessParent_RewritesParentAndReferencesHandle()
        {
            var source = AppendIntroduceHandleFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "parent/NewChild/0",
                        ParentAnchor = "Parent/0",
                        ParentHandle = "parent",
                        IntroduceParentHandle = true,
                        Name = "NewChild",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Parent statement is rewritten to declare the introduced handle (comment/indent
            // preserved), and the child is inserted immediately after referencing it.
            Assert.Contains(
                "        // Parent has no handle yet.\n        var parent = scene.Add(\"Parent\");\n        parent.Add(\"NewChild\");\n",
                result);

            // The unrelated sibling (and its comment) is untouched.
            Assert.Contains(
                "        // Unrelated sibling below, must stay untouched.\n        var sibling = scene.Add(\"Sibling\");",
                result);

            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("parent"));
            var parent = Assert.Single(reparsed.Model.Roots, r => r.Name == "Parent");
            var child = Assert.Single(parent.Children);
            Assert.Equal("NewChild", child.Name);
        }
    }
}
