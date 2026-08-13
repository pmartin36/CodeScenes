#nullable enable
using System;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The editor-boundary seam Build/Sync route their scene-vs-prefab-specific work through:
    /// reading the current hierarchy, executing a plan into it, and persisting the result. A scene
    /// target and a prefab target each implement this once; every other pipeline stage (parse,
    /// materialize, reconcile) stays shared and is called exactly once regardless of which target
    /// is in play.
    /// </summary>
    internal interface IBuildSyncTarget : IDisposable
    {
        /// <summary>Reads the current hierarchy this target governs into a snapshot.</summary>
        SceneSnapshot ReadSnapshot(
            Func<UnityEngine.Object, string?>? resolveSceneRef,
            Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef);

        /// <summary>Executes a materialized plan into the hierarchy this target governs.</summary>
        PlanExecutor.ExecutionResult Execute(Plan plan, IdentityMap map);

        /// <summary>Opens the mutation/echo-suppression scope spanning Execute and Persist.</summary>
        IDisposable BeginWrite();

        /// <summary>Persists the executed changes to disk.</summary>
        void Persist();

        /// <summary>The path written into the sidecar's target-path field and into log output.</summary>
        string TargetPath { get; }
    }
}
