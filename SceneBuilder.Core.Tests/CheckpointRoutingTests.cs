using System;
using SceneBuilder.Core.Sync;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class CheckpointRoutingTests
    {
        private static SyncCheckpoint BuildCheckpoint() => new SyncCheckpoint
        {
            SchemaVersion = SyncCheckpoint.CurrentSchemaVersion,
            Scene = "FooScene",
            LastSourceHash = "src0",
            LastSnapshotHash = "snap0",
            LastSidecarHash = "side0",
        };

        [Theory]
        [InlineData("src0", "snap0", "side0", SyncDirection.NoOp)]        // nothing moved
        [InlineData("src1", "snap0", "side0", SyncDirection.Materialize)] // source only
        [InlineData("src0", "snap1", "side0", SyncDirection.Reconcile)]   // snapshot only
        [InlineData("src1", "snap1", "side0", SyncDirection.Conflict)]    // both moved: never last-write-wins
        [InlineData("src0", "snap0", "side1", SyncDirection.Reconcile)]   // sidecar folds into scene axis
        [InlineData("src1", "snap0", "side1", SyncDirection.Conflict)]    // sidecar+source
        [InlineData("src0", "snap1", "side1", SyncDirection.Reconcile)]   // sidecar+snapshot
        [InlineData("src1", "snap1", "side1", SyncDirection.Conflict)]    // all three moved
        public void Decide_RoutesOnHashDeltas(
            string freshSourceHash, string freshSnapshotHash, string freshSidecarHash, SyncDirection expected)
        {
            var checkpoint = BuildCheckpoint();

            var actual = CheckpointRouter.Decide(checkpoint, freshSourceHash, freshSnapshotHash, freshSidecarHash);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Decide_NullCheckpoint_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CheckpointRouter.Decide(null!, "src0", "snap0", "side0"));
        }
    }
}
