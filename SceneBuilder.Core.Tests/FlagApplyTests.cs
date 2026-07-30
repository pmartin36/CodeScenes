using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // M2c (b3-t1): SourcePatchApplier applies PatchFlagArgument/IntroduceFlagCall/RemoveFlagCall
    // span-locally, composing with the existing PatchArgument/Move/Reorder/Remove/Append edits.
    public class FlagApplyTests
    {
        private const string IntroduceFlagFixture = @"
public class IntroduceFlagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Comment above enemy.
        var enemy = scene.Add(""Enemy"").Tag(""Foe"");

        // Comment above other.
        var other = scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Apply_IntroduceFlagCall_AppendsToChainBeforeSemicolon_FormattingPreserved()
        {
            var source = IntroduceFlagFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceFlagCall { Anchor = "enemy", Flag = FlagKind.Active, ArgExpr = "false" },
                    new IntroduceFlagCall { Anchor = "other", Flag = FlagKind.Static, ArgExpr = null },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Argument flag appended after the existing chain call, before the terminating `;`.
            Assert.Contains(
                "        var enemy = scene.Add(\"Enemy\").Tag(\"Foe\").Active(false);\n",
                result);

            // No-arg flag (.Static()) appended with empty parens.
            Assert.Contains(
                "        var other = scene.Add(\"Other\").Static();\n",
                result);

            // Unrelated comments survive untouched.
            Assert.Contains("// Comment above enemy.", result);
            Assert.Contains("// Comment above other.", result);

            var reparsed = BuilderParser.Parse(result);
            var enemyNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Enemy");
            Assert.False(enemyNode.Active);
            var otherNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Other");
            Assert.True(otherNode.IsStatic);
        }

        private const string PatchFlagFixture = @"
public class PatchFlagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add(""Player"").Tag(""Old"").Active(true);
    }
}
";

        [Fact]
        public void Apply_PatchFlagArgument_RewritesOnlyArgumentToken()
        {
            var source = PatchFlagFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchFlagArgument { Anchor = "player", Flag = FlagKind.Tag, NewExpr = "\"New\"" },
                    new PatchFlagArgument { Anchor = "player", Flag = FlagKind.Active, NewExpr = "false" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Only the two argument tokens changed; every other byte (receiver, other calls,
            // indentation, terminating `;`) is byte-identical.
            var expected = source.Replace("\"Old\"", "\"New\"").Replace("Active(true)", "Active(false)");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var player = Assert.Single(reparsed.Model.Roots, r => r.Name == "Player");
            Assert.Equal("New", player.Tag);
            Assert.False(player.Active);
        }

        private const string RemoveFlagFixture = @"
public class RemoveFlagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Tag(""Foe"").Active(true).Static();
    }
}
";

        [Fact]
        public void Apply_RemoveFlagCall_DeletesCall_PreservingSurroundingFormatting()
        {
            var source = RemoveFlagFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    // One terminal (.Static()) and one mid-chain (.Active(true)) removal on the
                    // SAME anchored statement, composed in one Apply.
                    new RemoveFlagCall { Anchor = "enemy", Flag = FlagKind.Static },
                    new RemoveFlagCall { Anchor = "enemy", Flag = FlagKind.Active },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                "        var enemy = scene.Add(\"Enemy\").Tag(\"Foe\");\n",
                result);
            Assert.DoesNotContain(".Active(", result);
            Assert.DoesNotContain(".Static(", result);

            var reparsed = BuilderParser.Parse(result);
            var enemyNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Enemy");
            Assert.Equal("Foe", enemyNode.Tag);
            Assert.True(enemyNode.Active);
            Assert.False(enemyNode.IsStatic);
        }

        private const string ClosureChildFlagFixture = @"
public class ClosureChildFlagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"", p => p.Add(""Label""));
    }
}
";

        // A statement can carry more than one node's calls via the
        // `Add(string, Action<NodeHandle>)` configure-closure form, so introducing a flag call
        // anchored on a CHILD resolves against the CHILD's own chain, not the enclosing statement —
        // the same invariant every chained-call resolver relies on, not rect-specific (the rect
        // analog is `Apply_ClosureChildRectPatch_LandsInsideClosure_NotOnParent` in
        // RectTransformPatchApplyTests.cs).
        [Fact]
        public void Apply_IntroduceFlagCall_OnClosureChild_LandsInsideClosure_NotOnParent()
        {
            var source = ClosureChildFlagFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceFlagCall { Anchor = "Panel/0/Label/0", Flag = FlagKind.Tag, ArgExpr = "\"ChildTag\"" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Correct: the introduced call lands on the CHILD's own chain, inside the closure.
            Assert.Contains("p.Add(\"Label\").Tag(\"ChildTag\")", result);

            // The introduced call must not land on the outer Panel chain instead.
            Assert.DoesNotContain("scene.Add(\"Panel\", p => p.Add(\"Label\")).Tag(", result);

            var reparsed = BuilderParser.Parse(result);
            var panelNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Panel");
            Assert.Equal("Untagged", panelNode.Tag);
            var labelNode = Assert.Single(panelNode.Children, c => c.Name == "Label");
            Assert.Equal("ChildTag", labelNode.Tag);
        }
    }
}
