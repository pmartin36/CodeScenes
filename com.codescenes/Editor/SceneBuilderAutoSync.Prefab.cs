#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Editor
{
    // The prefab lane of the auto-sync pump (code<->prefab-asset), split out of SceneBuilderAutoSync.cs
    // per the project's file-size budget (the parent file stays under the 1000-line limit). Move-only
    // for everything except the two Default* probe methods, which are new.
    public static partial class SceneBuilderAutoSync
    {
        internal static int PrefabCodeToAssetCycleCount { get; private set; }
        internal static int PrefabAssetToCodeCycleCount { get; private set; }

        /// <summary>True while the active editor context is a Prefab Mode stage.</summary>
        internal static Func<bool> InPrefabModeProbe = DefaultInPrefabMode;

        /// <summary>The prefab builder route governing the current Prefab Mode stage, if any.</summary>
        internal static Func<PrefabBuilderRoute?> ActivePrefabRouteProbe = DefaultActivePrefabRoute;

        /// <summary>
        /// The one production definition of "a Prefab Mode stage is open" — true iff
        /// <see cref="PrefabStageUtility.GetCurrentPrefabStage"/> returns a stage. Wired at the field
        /// initializer above, <see cref="WireDefaultExecutors"/>, and <see cref="ResetForTests"/>; a
        /// test overrides <see cref="InPrefabModeProbe"/> directly rather than calling this.
        /// </summary>
        private static bool DefaultInPrefabMode()
            => PrefabStageUtility.GetCurrentPrefabStage() != null;

        /// <summary>
        /// The one production definition of "which prefab builder governs the open Prefab Mode stage" —
        /// resolves the open stage's asset path via <see cref="SceneBuilderRouter.TryRoutePrefabAsset"/>,
        /// or null when no stage is open or the open asset has no governing builder. Wired at the field
        /// initializer above, <see cref="WireDefaultExecutors"/>, and <see cref="ResetForTests"/>.
        /// </summary>
        private static PrefabBuilderRoute? DefaultActivePrefabRoute()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && SceneBuilderRouter.TryRoutePrefabAsset(stage.assetPath, out var route)
                ? route
                : (PrefabBuilderRoute?)null;
        }

        /// <summary>Diverts a Prefab Mode edit to the prefab asset-&gt;code lane instead of the scene lane; a non-prefab-mode change still reaches <see cref="NotifySceneChanged"/>.</summary>
        internal static void RouteEditorChange(IReadOnlyCollection<EntityId> ids)
        {
            if (InPrefabModeProbe())
            {
                var route = ActivePrefabRouteProbe();
                if (route != null)
                {
                    NotifyPrefabAssetChanged(route.Value);
                }

                return;
            }

            NotifySceneChanged(ids);
        }

        /// <summary>Accumulates an external write to a prefab builder .cs onto its own settle deadline, dropping it if disarmed or if it is our own reconcile write.</summary>
        internal static void NotifyPrefabSourceChanged(string path)
        {
            if (!IsArmed)
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (DropsAsOwnWrite(fullPath))
            {
                return;
            }

            // A duplicate signal for a path already pending (e.g. the real watcher's own echo of an
            // edit an explicit caller already accounted for) must not push an already-running settle
            // window further out — only a genuinely new path (re)arms the deadline.
            if (_pendingPrefabSourcePaths.Add(fullPath) || !_prefabSourceDeadlineArmed)
            {
                _prefabSourceDeadlineArmed = true;
                _prefabSourceDeadline = Clock() + SettleSeconds;
            }
        }

        /// <summary>Accumulates a Prefab Mode edit for <paramref name="route"/> onto its own settle deadline, dropping it if disarmed.</summary>
        internal static void NotifyPrefabAssetChanged(PrefabBuilderRoute route)
        {
            if (!IsArmed)
            {
                return;
            }

            _pendingPrefabAssetRoutes[route.BuilderName] = route;
            _prefabAssetDeadlineArmed = true;
            _prefabAssetDeadline = Clock() + SettleSeconds;
        }

        /// <summary>The prefab code-&gt;asset cycle body: builds each routed prefab builder path in place via <see cref="PrefabBuildSyncTarget.Build"/>.</summary>
        internal static void ExecutePrefabCodeToAsset(IReadOnlyCollection<string> paths)
        {
            foreach (var path in paths)
            {
                if (!SceneBuilderRouter.TryRoutePrefabBuilderFile(path, out var route))
                {
                    continue;
                }

                if (!File.Exists(route.BuilderPath))
                {
                    continue;
                }

                try
                {
                    EnsureAssetFolderExists(route.PrefabPath);
                    var result = PrefabBuildSyncTarget.Build(route.BuilderPath, route.PrefabPath, route.SidecarPath);
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        Debug.LogError(
                            $"[CodeScenes] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): " +
                            $"{diagnostic.Message} — prefab left untouched.");
                    }
                }
                catch (ParseException e)
                {
                    Debug.LogError(
                        $"[CodeScenes] Parse error in {route.BuilderPath} at line {e.Line}, column {e.Column}: " +
                        $"{e.Message} — prefab left untouched.");
                }
            }
        }

        /// <summary>
        /// Creates every missing folder segment on <paramref name="assetPath"/>'s parent chain (mirroring
        /// the on-disk builders directory's own auto-create) and refreshes the asset database so a
        /// first-ever prefab minted at the default convention path (<c>Assets/SceneBuilder/&lt;name&gt;.prefab</c>)
        /// never needs a folder to be created by hand first.
        /// </summary>
        private static void EnsureAssetFolderExists(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir))
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(SceneBuilderPaths.ProjectRoot, dir));
            AssetDatabase.Refresh();
        }

        /// <summary>The prefab asset-&gt;code cycle body: reconciles each routed prefab via <see cref="PrefabBuildSyncTarget.Sync"/>.</summary>
        internal static void ExecutePrefabAssetToCode(IReadOnlyCollection<PrefabBuilderRoute> routes)
        {
            foreach (var route in routes)
            {
                if (!File.Exists(route.BuilderPath) || !File.Exists(route.SidecarPath))
                {
                    continue;
                }

                PrefabBuildSyncTarget.Sync(route.BuilderPath, route.PrefabPath, route.SidecarPath);
            }
        }

        /// <summary>Runs a prefab-lane cycle body with the same "a throw must not wedge the pump" guarantee <see cref="InvokeExecutor{T}"/> gives the scene/source lanes. The prefab lanes call a fixed internal method rather than an injectable delegate, so this wraps the call directly.</summary>
        private static void RunPrefabCycle(Action cycle)
        {
            try
            {
                cycle();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
