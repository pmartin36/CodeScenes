#nullable enable
using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The <see cref="IBuildSyncTarget"/> implementation for a live scene: reads/executes/persists
    /// against the passed <see cref="Scene"/> the same way <see cref="SceneBuilderBuild"/> and
    /// <see cref="SceneBuilderSync"/> always have.
    /// </summary>
    internal sealed class SceneBuildSyncTarget : IBuildSyncTarget
    {
        private readonly Scene _scene;
        private readonly string _targetPath;

        public SceneBuildSyncTarget(Scene scene, string targetPath)
        {
            _scene = scene;
            _targetPath = targetPath;
        }

        public string TargetPath => _targetPath;

        public SceneSnapshot ReadSnapshot(
            Func<UnityEngine.Object, string?>? resolveSceneRef,
            Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef)
            => SceneSnapshotReader.Read(_scene, resolveSceneRef, resolveListenerRef);

        public PlanExecutor.ExecutionResult Execute(Plan plan, IdentityMap map)
            => PlanExecutor.Execute(plan, map, _scene);

        public IDisposable BeginWrite() => SuppressionScope.SuppressScene();

        public void Persist()
        {
            EditorSceneManager.MarkSceneDirty(_scene);
            EditorSceneManager.SaveScene(_scene, _targetPath);
        }

        public void Dispose()
        {
        }
    }
}
