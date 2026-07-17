using System;
using System.Collections.Generic;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Drift
{
    // Pure, Unity-free drift determination: CodeAhead from the Plan (dry-run materialization),
    // SceneAhead from the APPLIED-BYTE delta of the reconcile patch (not raw edit count) plus
    // map/asset deltas. See specs/14-m-auto-live-sync.md drift-determination gate.
    public readonly record struct DriftState(bool CodeAhead, bool SceneAhead)
    {
        public bool InSync => !CodeAhead && !SceneAhead;
        public bool Conflict => CodeAhead && SceneAhead;

        public static DriftState Compute(
            Plan.Plan plan,
            ReconcileResult reconcile,
            string source,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            bool assetDelta = false)
        {
            bool codeAhead = plan.Ops.Length > 0 || plan.Skipped.Length > 0;

            bool appliedChanged = false;
            if (reconcile.Patch.Edits.Length > 0)
            {
                var applied = SourcePatchApplier.Apply(source, reconcile.Patch, anchors);
                appliedChanged = !string.Equals(applied, source, StringComparison.Ordinal);
            }

            bool mapDelta = reconcile.AddedEntries.Length > 0 || reconcile.RemovedLogicalIds.Length > 0;

            bool sceneAhead = appliedChanged || mapDelta || assetDelta;
            return new DriftState(codeAhead, sceneAhead);
        }
    }
}
