using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SourcePatchAppendStatementTests
    {
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
                        // Root sibling index 1 — the position this object occupies in the scene, and
                        // what both NewLogicalId's "/1" and the Roots[1] assertion below already mean.
                        NewSiblingIndex = 1,
                        Name = "New",
                        Transform = new TransformData { Position = new Vec3(1.5f, 2f, 3.25f) },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Unrelated statement + its comment are byte-identical.
            Assert.Contains("        // Existing root, untouched.\n        var existing = scene.Add(\"Existing\");", result);

            // New statement appended at end of Build body, own line, body indentation,
            // every float component f-suffixed (integral 2 -> 2f too, so it reads as a float).
            Assert.Contains("        scene.Add(\"New\").Transform(pos: (1.5f, 2f, 3.25f));\n", result);

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

        // ---- AppendStatement: same-batch new subtree + empty body (FINAL F1/F2) -----------

        private const string AppendSubtreeFixture = @"
public class AppendSubtreeScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Existing root, untouched.
        var existing = scene.Add(""Existing"");
    }
}
";

        [Fact]
        public void Append_NewSubtree_ParentThenChild_ChildLandsAfterParent_SameBatch()
        {
            // Exact shape b2-t3 emits (ReconcileTests.cs:481-498): a fresh parent AppendStatement
            // (Handle == NewLogicalId == "parent") followed by a child AppendStatement whose
            // ParentAnchor is that SAME batch's fresh handle, not a pre-existing source anchor.
            var source = AppendSubtreeFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement
                    {
                        NewLogicalId = "parent",
                        Name = "Parent",
                        Handle = "parent",
                    },
                    new AppendStatement
                    {
                        NewLogicalId = "parent/Child/0",
                        ParentAnchor = "parent",
                        ParentHandle = "parent",
                        Name = "Child",
                    },
                },
            };

            // Must NOT throw PatchException("No anchor found for logical id 'parent'").
            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Unrelated pre-existing statement + comment untouched.
            Assert.Contains("        // Existing root, untouched.\n        var existing = scene.Add(\"Existing\");", result);

            // Child lands on the line immediately after the parent, referencing its handle.
            Assert.Contains(
                "        var parent = scene.Add(\"Parent\");\n        parent.Add(\"Child\");\n",
                result);

            var reparsed = BuilderParser.Parse(result);
            var parent = Assert.Single(reparsed.Model.Roots, r => r.Name == "Parent");
            var child = Assert.Single(parent.Children);
            Assert.Equal("Child", child.Name);
        }

        [Fact]
        public void Append_TwoChildrenUnderFreshParent_PreservesSiblingOrder()
        {
            var source = AppendSubtreeFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement { NewLogicalId = "parent", Name = "Parent", Handle = "parent" },
                    new AppendStatement
                    {
                        NewLogicalId = "parent/Child0/0",
                        ParentAnchor = "parent",
                        ParentHandle = "parent",
                        Name = "Child0",
                    },
                    new AppendStatement
                    {
                        NewLogicalId = "parent/Child1/1",
                        ParentAnchor = "parent",
                        ParentHandle = "parent",
                        Name = "Child1",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.True(
                result.IndexOf("Child0", StringComparison.Ordinal) < result.IndexOf("Child1", StringComparison.Ordinal),
                "Expected Child0 to precede Child1 (emission order preserved for idempotent re-parse).");

            var reparsed = BuilderParser.Parse(result);
            var parent = Assert.Single(reparsed.Model.Roots, r => r.Name == "Parent");
            Assert.Equal(2, parent.Children.Length);
            Assert.Equal("Child0", parent.Children[0].Name);
            Assert.Equal("Child1", parent.Children[1].Name);
        }

        private const string AppendEmptyBodyFixture = @"
public class AppendEmptyBodyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        [Fact]
        public void Append_RootIntoEmptyBuildBody_DoesNotThrow_AppendsAtBodyIndent()
        {
            var source = AppendEmptyBodyFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendStatement { NewLogicalId = "New", Name = "New" },
                },
            };

            // Must NOT throw ArgumentOutOfRangeException (body.Statements.Last() on an empty body).
            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var reparsed = BuilderParser.Parse(result);
            var appended = Assert.Single(reparsed.Model.Roots);
            Assert.Equal("New", appended.Name);
        }

    }
}
