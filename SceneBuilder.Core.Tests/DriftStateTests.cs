using SceneBuilder.Core.Drift;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b1-t1: pure, Unity-free drift-determination contract. CodeAhead is Plan-derived;
    // SceneAhead is grounded in the APPLIED-BYTE delta of the reconcile patch (not raw edit
    // count) plus map delta — never latched by a lone Skipped/Conflicts entry (research.md).
    public class DriftStateTests
    {
        private const string RenameFixture = @"
public class RenameScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""OldName"");
    }
}
";

        [Fact]
        public void Drift_InSync_EmptyPlanAndEmptyPatch()
        {
            var plan = new Plan.Plan();
            var reconcile = new ReconcileResult();

            var drift = DriftState.Compute(plan, reconcile, RenameFixture, BuilderParser.Parse(RenameFixture).Anchors);

            Assert.False(drift.CodeAhead);
            Assert.False(drift.SceneAhead);
            Assert.True(drift.InSync);
            Assert.False(drift.Conflict);
        }

        [Fact]
        public void Drift_CodeOnlyChange_NonEmptyPlan_EmptyPatch()
        {
            var plan = new Plan.Plan
            {
                Ops = new PlanOp[] { new SetName { LogicalId = "root", Name = "NewName" } },
            };
            var reconcile = new ReconcileResult();

            var drift = DriftState.Compute(plan, reconcile, RenameFixture, BuilderParser.Parse(RenameFixture).Anchors);

            Assert.True(drift.CodeAhead);
            Assert.False(drift.SceneAhead);
        }

        [Fact]
        public void Drift_SceneOnlyChange_EmptyPlan_NonEmptyPatch()
        {
            var source = RenameFixture;
            var anchors = BuilderParser.Parse(source).Anchors;
            var plan = new Plan.Plan();
            var reconcile = new ReconcileResult
            {
                Patch = new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new PatchArgument { Anchor = "root", ArgName = "name", NewExpr = "\"NewName\"" },
                    },
                },
            };

            var drift = DriftState.Compute(plan, reconcile, source, anchors);

            Assert.False(drift.CodeAhead);
            Assert.True(drift.SceneAhead);
        }

        [Fact]
        public void Drift_BothChanged_NonEmptyPlanAndPatch()
        {
            var source = RenameFixture;
            var anchors = BuilderParser.Parse(source).Anchors;
            var plan = new Plan.Plan
            {
                Ops = new PlanOp[] { new SetName { LogicalId = "root", Name = "CodeSideName" } },
            };
            var reconcile = new ReconcileResult
            {
                Patch = new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        new PatchArgument { Anchor = "root", ArgName = "name", NewExpr = "\"SceneSideName\"" },
                    },
                },
            };

            var drift = DriftState.Compute(plan, reconcile, source, anchors);

            Assert.True(drift.CodeAhead);
            Assert.True(drift.SceneAhead);
            Assert.True(drift.Conflict);
        }

        // Anti-deadlock pin (blocker 2): a patch whose applied text is byte-identical to the
        // existing source (e.g. re-emitting the same value) must NOT count as scene-ahead, or
        // the loop latches into re-applying a no-op forever.
        [Fact]
        public void Drift_SpuriousPatchAppliesByteIdentical_IsNotDrift()
        {
            var source = RenameFixture;
            var anchors = BuilderParser.Parse(source).Anchors;
            var plan = new Plan.Plan();
            var reconcile = new ReconcileResult
            {
                Patch = new SourcePatch
                {
                    Edits = new SourceEdit[]
                    {
                        // Same value as already on disk -> Apply(...) reproduces source byte-for-byte.
                        new PatchArgument { Anchor = "root", ArgName = "name", NewExpr = "\"OldName\"" },
                    },
                },
            };

            var drift = DriftState.Compute(plan, reconcile, source, anchors);

            Assert.False(drift.SceneAhead);
        }

        // An Unsupported/Unresolved Skipped field is not actionable and must never latch
        // scene-ahead — same failure mode the spurious-patch guard prevents.
        [Fact]
        public void Drift_UnresolvedSkippedField_DoesNotCountAsSceneAhead()
        {
            var source = RenameFixture;
            var anchors = BuilderParser.Parse(source).Anchors;
            var plan = new Plan.Plan();
            var reconcile = new ReconcileResult
            {
                Skipped = new SkippedField[]
                {
                    new SkippedField { LogicalId = "root", Path = "m_SomeField", Reason = "Unsupported" },
                },
            };

            var drift = DriftState.Compute(plan, reconcile, source, anchors);

            Assert.False(drift.SceneAhead);
        }
    }
}
