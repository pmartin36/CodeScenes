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
    }
}
