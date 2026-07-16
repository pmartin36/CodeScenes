using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Coverage for the standalone IntroduceHandle : SourceEdit carrier (b2-t1). Unlike
    // AppendStatement/MoveStatement/AppendComponentStatement, this edit's only purpose is to rewrite
    // an anchored statement into `var <Handle> = ...;` — no accompanying placement edit required.
    public class IntroduceHandleApplyTests
    {
        private const string TwoEnemiesFixture = @"
public class TwoEnemiesScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // First enemy, untouched by this patch.
        scene.Add(""Enemy"");

        // Second enemy gains a handle.
        scene.Add(""Enemy"");
    }
}
";

        [Fact]
        public void IntroduceHandle_RewritesAnchoredStatementToVar_LeavesOthersByteIdentical()
        {
            var source = TwoEnemiesFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceHandle { Anchor = "Enemy/1", Handle = "enemy" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains("        var enemy = scene.Add(\"Enemy\");", result);
            Assert.Contains("        // First enemy, untouched by this patch.\n        scene.Add(\"Enemy\");", result);

            var reparsed = BuilderParser.Parse(result);
            Assert.True(reparsed.Anchors.ContainsKey("enemy"));
        }

        [Fact]
        public void IntroduceHandle_AndAppendIntroduceParent_SameAnchorSameHandle_YieldsOneVar()
        {
            var source = TwoEnemiesFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceHandle { Anchor = "Enemy/1", Handle = "enemy" },
                    new AppendStatement
                    {
                        NewLogicalId = "enemy/Child/0",
                        ParentAnchor = "Enemy/1",
                        ParentHandle = "enemy",
                        IntroduceParentHandle = true,
                        Name = "Child",
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var varCount = result.Split(new[] { "var enemy =" }, System.StringSplitOptions.None).Length - 1;
            Assert.Equal(1, varCount);
            Assert.Contains("enemy.Add(\"Child\");", result);
        }

        [Fact]
        public void IntroduceHandle_ConflictingHandleForSameAnchor_Throws()
        {
            var source = TwoEnemiesFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceHandle { Anchor = "Enemy/1", Handle = "h1" },
                    new AppendStatement
                    {
                        NewLogicalId = "h2/Child/0",
                        ParentAnchor = "Enemy/1",
                        ParentHandle = "h2",
                        IntroduceParentHandle = true,
                        Name = "Child",
                    },
                },
            };

            var ex = Assert.Throws<PatchException>(() => SourcePatchApplier.Apply(source, patch, anchors));
            Assert.Contains("Conflicting handle introductions", ex.Message);
        }

        [Fact]
        public void IntroduceHandle_AnchorNotInSource_IsNoOp()
        {
            var source = TwoEnemiesFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new IntroduceHandle { Anchor = "enemy/NewChild/0", Handle = "child" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Equal(source, result);
        }
    }
}
