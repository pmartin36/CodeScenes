using System;
using SceneBuilder.Core.Drift;

namespace SceneBuilder.Core.Sync
{
    public enum SyncDirection { NoOp, Materialize, Reconcile, Conflict }

    // Pure hash-delta routing between a loaded SyncCheckpoint and freshly computed hashes.
    // Reuses DriftState's CodeAhead/SceneAhead lattice for the both/neither truth table; the
    // sidecar hash folds into the scene axis alongside the snapshot hash. Requires a loaded
    // checkpoint - the no-checkpoint-yet (first-run) case is handled by the caller, not here.
    public static class CheckpointRouter
    {
        public static SyncDirection Decide(
            SyncCheckpoint checkpoint,
            string freshSourceHash,
            string freshSnapshotHash,
            string freshSidecarHash)
        {
            if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));

            bool sourceChanged = !string.Equals(freshSourceHash, checkpoint.LastSourceHash, StringComparison.Ordinal);
            bool snapshotChanged = !string.Equals(freshSnapshotHash, checkpoint.LastSnapshotHash, StringComparison.Ordinal);
            bool sidecarChanged = !string.Equals(freshSidecarHash, checkpoint.LastSidecarHash, StringComparison.Ordinal);

            var drift = new DriftState(
                CodeAhead: sourceChanged,
                SceneAhead: snapshotChanged || sidecarChanged);

            if (drift.Conflict) return SyncDirection.Conflict;
            if (drift.InSync) return SyncDirection.NoOp;
            return drift.CodeAhead ? SyncDirection.Materialize : SyncDirection.Reconcile;
        }
    }
}
