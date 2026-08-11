#nullable enable
using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Sync;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reload/focus/scene-open resync: reads checkpoint + sidecar + source from disk and a fresh
    /// cold scene snapshot, routes via <see cref="CheckpointRouter"/>, and dispatches to the
    /// Reconcile/Materialize executors — recovering an external edit that fired no
    /// <c>ObjectChangeEvent</c> and surviving a domain reload (no sync-critical state in statics).
    /// </summary>
    internal static class SceneBuilderResync
    {
        /// <summary>
        /// The single resync body and the sole caller of <see cref="CheckpointRouter.Decide"/>.
        /// Returns the decided direction (test seam; production ignores the return value).
        /// </summary>
        internal static SyncDirection ResyncRoute(BuilderRoute route, Scene scene)
        {
            try
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return SyncDirection.NoOp; // nothing to read
                }

                if (!File.Exists(route.BuilderPath) || !File.Exists(route.SidecarPath))
                {
                    return SyncDirection.NoOp; // nothing to sync into
                }

                var statePath = SceneBuilderPaths.StateForSidecar(route.SidecarPath);
                if (!File.Exists(statePath))
                {
                    // First-run / pre-checkpoint project: establish via Materialize (idempotent when
                    // already converged) rather than calling Decide against an absent checkpoint.
                    RunMaterialize(route, scene);
                    return SyncDirection.Materialize;
                }

                var checkpoint = SyncCheckpointJson.Deserialize(File.ReadAllText(statePath));
                var map = IdentityMapJson.Deserialize(File.ReadAllText(route.SidecarPath));
                var source = File.ReadAllText(route.BuilderPath);

                var facadeCatalog = FacadeCatalogLoader.Load();
                var assetCatalog = AssetCatalogLoader.Load();
                var desired = DesiredModelLoader.Load(source, map, facadeCatalog, assetCatalog).Desired;

                var sceneRef = ObjectReferenceResolver.BuildSceneRefResolver(map);
                var snapshot = SceneSnapshotReader.Read(scene, sceneRef);

                var freshSourceHash = CanonicalHash.Of(desired);
                var freshSnapshotHash = CanonicalHash.Of(snapshot);
                var freshSidecarHash = CanonicalHash.Of(map);

                var direction = CheckpointRouter.Decide(checkpoint, freshSourceHash, freshSnapshotHash, freshSidecarHash);

                switch (direction)
                {
                    case SyncDirection.Reconcile:
                        RunReconcile(route, scene);
                        break;
                    case SyncDirection.Materialize:
                        RunMaterialize(route, scene);
                        break;
                    case SyncDirection.Conflict:
                        // Surface only — never write to either side (no last-write-wins).
                        Debug.LogError(
                            $"[CodeScenes] Conflict: both the source and the scene changed since the last sync checkpoint for '{route.BuilderName}' — auto-resync skipped to avoid overwriting either side. Resolve with Build or Sync.");
                        break;
                    case SyncDirection.NoOp:
                    default:
                        break;
                }

                return direction;
            }
            catch (Exception e)
            {
                // A throw anywhere in the resync body — a corrupt/stale on-disk checkpoint, sidecar,
                // or source, or a throwing executor — must not wedge the focus/scene-open/reload
                // trigger that called this.
                Debug.LogException(e);
                return SyncDirection.NoOp;
            }
        }

        private static void RunReconcile(BuilderRoute route, Scene scene)
        {
            SceneBuilderSync.Run(route.BuilderPath, route.SidecarPath, scene);
        }

        private static void RunMaterialize(BuilderRoute route, Scene scene)
        {
            SceneBuilderBuild.Run(route.BuilderPath, route.ScenePath, route.SidecarPath, scene);
        }

        /// <summary>Focus-regain hook target: resolves and resyncs the active scene's governing route.</summary>
        internal static void ResyncActiveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            if (!SceneBuilderRouter.TryRouteScene(scene, out var route))
            {
                return;
            }

            ResyncRoute(route, scene);
        }

        /// <summary>Scene-open hook target: resyncs the opened scene's governing route.</summary>
        internal static void ResyncScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            if (!SceneBuilderRouter.TryRouteScene(scene, out var route))
            {
                return;
            }

            ResyncRoute(route, scene);
        }

        /// <summary>Domain-reload hook target: resyncs every discovered builder whose scene is open.</summary>
        internal static void ResyncAllOpenScenes()
        {
            foreach (var route in SceneBuilderRouter.Discover())
            {
                if (SceneBuilderRouter.TryGetOpenScene(route, out var scene))
                {
                    ResyncRoute(route, scene);
                }
            }
        }
    }
}
