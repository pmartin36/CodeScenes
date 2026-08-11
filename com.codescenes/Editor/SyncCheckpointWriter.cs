#nullable enable
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Sync;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE sole construction site of a <see cref="SyncCheckpoint"/>: every commit path (Materialize,
    /// Reconcile) routes through <see cref="Write"/> rather than constructing one by hand, so a caller
    /// cannot forget a hash input and silently write a partial checkpoint — the required, defaultless
    /// parameters fail to compile instead.
    /// </summary>
    internal static class SyncCheckpointWriter
    {
        /// <summary>
        /// Writes a <see cref="SyncCheckpoint"/> to <paramref name="statePath"/> recording the hashes of
        /// the three POST-COMMIT on-disk artifacts for <paramref name="scenePath"/>: the committed scene
        /// (<paramref name="committedSnapshot"/>), the committed source
        /// (<paramref name="sourceModel"/>) and the committed sidecar (<paramref name="committedMap"/>).
        /// Every hash goes through <see cref="CanonicalHash"/>, the only hashing mechanism. Idempotent
        /// and atomic — see <see cref="SceneBuilderPaths.WriteTextAtomicIfChanged"/>.
        /// </summary>
        public static void Write(
            string statePath,
            string scenePath,
            SceneModel sourceModel,
            SceneSnapshot committedSnapshot,
            IdentityMap committedMap)
        {
            var checkpoint = new SyncCheckpoint
            {
                SchemaVersion = SyncCheckpoint.CurrentSchemaVersion,
                Scene = scenePath,
                LastSnapshotHash = CanonicalHash.Of(committedSnapshot),
                LastSourceHash = CanonicalHash.Of(sourceModel),
                LastSidecarHash = CanonicalHash.Of(committedMap),
            };

            SceneBuilderPaths.EnsureBuildersDirectory();
            SceneBuilderPaths.WriteTextAtomicIfChanged(statePath, SyncCheckpointJson.Serialize(checkpoint));
        }
    }
}
