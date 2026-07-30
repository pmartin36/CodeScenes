using System.Linq;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b3-t3: SourcePatchApplier applies RectTransform PatchArguments (patch/insert/introduce/fold),
    // formatting-preservingly, resolved against the anchored NODE's OWN fluent chain rather than its
    // enclosing STATEMENT. The statement-scoped lookup (FindFlagInvocation/GetChainExpression) is
    // wrong whenever a statement contains more than one node's calls — the `Add(string,
    // Action<NodeHandle>)` configure-closure form, which is parser-supported (BuilderParser.cs:344-
    // 359) and used throughout the suite. See research.md REFINED #2 for the two corruption cases
    // this task fixes.
    public class RectTransformPatchApplyTests
    {
        // ---- Deliverable clauses already generalized by b3-t2's ArgumentCall plumbing -------------
        // (regression locks: pin the shipped behavior so this task's chain-resolution rewrite cannot
        // silently regress it.)

        private const string FiveArgFixture = @"
public class FiveArgScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Comment above panel.
        var panel = scene.Add(""Panel"").RectTransform(
            anchoredPos: (-10f, -10f),
            sizeDelta: (200f, 120f),
            anchorMin: (1f, 1f),
            anchorMax: (1f, 1f),
            pivot: (1f, 1f));

        // Comment above other.
        var other = scene.Add(""Other"");
    }
}
";

        [Fact]
        public void Reconcile_AnchorOrPivotChanged_PatchesOnlyThatArgument_FormattingPreserved()
        {
            var source = FiveArgFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "panel", ArgName = "anchorMin", NewExpr = "(0.5f, 0.5f)" },
                    new PatchArgument { Anchor = "panel", ArgName = "pivot", NewExpr = "(0f, 0f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            var expected = source
                .Replace("anchorMin: (1f, 1f)", "anchorMin: (0.5f, 0.5f)")
                .Replace("pivot: (1f, 1f)", "pivot: (0f, 0f)");
            Assert.Equal(expected, result);

            var reparsed = BuilderParser.Parse(result);
            var panelNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Panel");
            Assert.Equal(0.5f, panelNode.Transform!.AnchorMin!.Value.X);
            Assert.Equal(0f, panelNode.Transform!.Pivot!.Value.X);
        }

        private const string TransformOnlyFixture = @"
public class TransformOnlyScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var icon = scene.Add(""Icon"").Transform(pos: (1f, 2f, 3f));
    }
}
";

        [Fact]
        public void Apply_PromotedNodeWithoutRectTransformCall_IntroducesOneCall()
        {
            var source = TransformOnlyFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "icon", ArgName = "anchoredPos", NewExpr = "(5f, 6f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "sizeDelta", NewExpr = "(7f, 8f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "anchorMin", NewExpr = "(0f, 0f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "anchorMax", NewExpr = "(1f, 1f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "pivot", NewExpr = "(0.5f, 0.5f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Equal(1, result.Split(".RectTransform(").Length - 1);
            Assert.Contains(".Transform(pos: (1f, 2f, 3f))", result);

            var reparsed = BuilderParser.Parse(result);
            var iconNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Icon");
            Assert.Equal("RectTransform", iconNode.Transform!.Kind);
            Assert.Equal(5f, iconNode.Transform!.AnchoredPosition!.Value.X);
            Assert.Equal(6f, iconNode.Transform!.AnchoredPosition!.Value.Y);
            Assert.Equal(7f, iconNode.Transform!.SizeDelta!.Value.X);
        }

        [Fact]
        public void Apply_ThreeRectArgsOneAnchor_ProducesOneCall()
        {
            const string source = @"
public class BareAddScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var icon = scene.Add(""Icon"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "icon", ArgName = "anchoredPos", NewExpr = "(1f, 1f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "sizeDelta", NewExpr = "(2f, 2f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "pivot", NewExpr = "(3f, 3f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Equal(1, result.Split(".RectTransform(").Length - 1);
        }

        private const string PartialCallFixture = @"
public class PartialCallScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var icon = scene.Add(""Icon"").RectTransform(pivot: (1f, 1f));
    }
}
";

        [Fact]
        public void Apply_RectArgIntoPartialCall_InsertsInCanonicalOrder()
        {
            var source = PartialCallFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "icon", ArgName = "anchoredPos", NewExpr = "(5f, 6f)" },
                    new PatchArgument { Anchor = "icon", ArgName = "sizeDelta", NewExpr = "(7f, 8f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                "RectTransform(anchoredPos:(5f, 6f),sizeDelta:(7f, 8f),pivot: (1f, 1f))",
                result);
        }

        [Fact]
        public void Reconcile_UnanchoredRectNode_YieldsLocatedConflictAndNoEdit()
        {
            var model = new SceneBuilder.Core.Model.GameObjectNode
            {
                LogicalId = "root-1",
                Name = "Root",
                Transform = RectTransformFixtures.Rect(anchoredPos: new SceneBuilder.Core.Model.Vec2(9f, 9f)),
            };
            var snapshotRoot = new SceneBuilder.Core.Model.SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Transform = RectTransformFixtures.Rect(),
            };

            var sceneModel = new SceneBuilder.Core.Model.SceneModel { SchemaVersion = 1, Roots = new[] { model } };
            var snapshot = new SceneBuilder.Core.Model.SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };
            var map = new SceneBuilder.Core.Identity.IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new SceneBuilder.Core.Identity.IdentityMapEntry
                    {
                        LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject",
                    },
                },
            };

            var result = SceneBuilder.Core.Reconcile.Reconciler.Reconcile(
                sceneModel, snapshot, map, anchors: new System.Collections.Generic.Dictionary<string, SourceSpan>());

            Assert.Empty(result.Patch.Edits);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(SceneBuilder.Core.Reconcile.ConflictKind.MissingSourceAnchor, conflict.Kind);
            Assert.Equal("root-1", conflict.LogicalId);
        }

        // ---- REFINED #2: the genuine cross-node corruption this task fixes -----------------------
        // A statement can carry more than one node's calls via the `Add(string, Action<NodeHandle>)`
        // configure-closure form. The statement-scoped `FindFlagInvocation`/`GetChainExpression`
        // cannot tell which node's call it found; resolution must be scoped to the anchored node's
        // OWN fluent chain instead.

        [Fact]
        public void Apply_ClosureChildRectPatch_LandsInsideClosure_NotOnParent()
        {
            const string source = @"
public class ClosureChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"", p => p.Add(""Label""));
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;
            Assert.True(anchors.ContainsKey("Panel/0/Label/0"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "Panel/0/Label/0", ArgName = "anchoredPos", NewExpr = "(9f, 9f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Correct: the introduced call lands on the CHILD's own chain, inside the closure.
            Assert.Contains(
                "p.Add(\"Label\").RectTransform(anchoredPos:(9f, 9f))",
                result);

            // Wrong (today's bug): the call must NOT land on the outer Panel chain instead.
            Assert.DoesNotContain(
                "scene.Add(\"Panel\", p => p.Add(\"Label\")).RectTransform(",
                result);

            var reparsed = BuilderParser.Parse(result);
            var panelNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Panel");
            Assert.Equal("Transform", panelNode.Transform!.Kind);
            var labelNode = Assert.Single(panelNode.Children, c => c.Name == "Label");
            Assert.Equal("RectTransform", labelNode.Transform!.Kind);
            Assert.Equal(9f, labelNode.Transform!.AnchoredPosition!.Value.X);
        }

        [Fact]
        public void Apply_ParentRectPatch_DoesNotRewriteClosureChildsRectCall()
        {
            const string source = @"
public class ClosureChildWithRectScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Panel"", p => { p.Add(""Label"").RectTransform(anchoredPos: (1f, 1f)); });
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;
            Assert.True(anchors.ContainsKey("Panel/0"));

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchArgument { Anchor = "Panel/0", ArgName = "anchoredPos", NewExpr = "(9f, 9f)" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // The child's own call must survive byte-identical.
            Assert.Contains("p.Add(\"Label\").RectTransform(anchoredPos: (1f, 1f));", result);

            var reparsed = BuilderParser.Parse(result);
            var panelNode = Assert.Single(reparsed.Model.Roots, r => r.Name == "Panel");
            Assert.Equal("RectTransform", panelNode.Transform!.Kind);
            Assert.Equal(9f, panelNode.Transform!.AnchoredPosition!.Value.X);

            var labelNode = Assert.Single(panelNode.Children, c => c.Name == "Label");
            Assert.Equal(1f, labelNode.Transform!.AnchoredPosition!.Value.X);
        }
    }
}
