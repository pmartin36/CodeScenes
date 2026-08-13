#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Validation;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Bootstrap + trigger + debounce pump for the auto-sync loop (spec checklist #1, #2, #5).
    /// Separates TRANSPORT (event subscriptions, FileSystemWatcher, EditorApplication.update) from
    /// LOGIC (accumulate change set, per-direction settle timer, dispatch one cycle) so the LOGIC
    /// seams are provable deterministically via an injectable clock + explicit pump ticks, without
    /// wall-clock or async event timing.
    /// </summary>
    [InitializeOnLoad]
    public static partial class SceneBuilderAutoSync
    {
        static SceneBuilderAutoSync()
        {
            // Wire the production executors BEFORE arming so a fresh session (or a post-reload
            // re-arm) auto-syncs with real logic by default — no manual wiring on the happy path.
            WireDefaultExecutors();

            // Play-mode gate (b7-t1, checklist #12): subscribe once, idempotently, bound to the
            // class lifecycle (not Arm/Disarm) so the handler stays live while disarmed and can
            // re-arm on EnteredEditMode.
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Domain-reload survival: static ctor re-runs on every reload and re-arms iff the
            // persisted master toggle is on (SceneBuilderAutoToggle.Enabled defaults true).
            ApplyToggleState();

            // Reload resync: schedule AFTER ApplyToggleState() so the editor's scenes are restored
            // before it runs (delayCall fires post-load). Recovers an external edit that fired no
            // ObjectChangeEvent while this domain was unloaded.
            if (IsArmed)
            {
                EditorApplication.delayCall += SceneBuilderResync.ResyncAllOpenScenes;
            }
        }

        public static bool IsArmed { get; private set; }

        internal static Func<double> Clock = () => EditorApplication.timeSinceStartup;
        internal static double SettleSeconds = 0.4;

        internal static int SceneToCodeCycleCount { get; private set; }
        internal static int CodeToSceneCycleCount { get; private set; }

        internal static Action<IReadOnlyCollection<EntityId>>? SceneToCodeExecutor;
        internal static Action<IReadOnlyCollection<string>>? CodeToSceneExecutor;

        // b6-t1 seams (RED stub — pump routing + real merge land with the task's implementation).
        internal static Action<IReadOnlyCollection<EntityId>, IReadOnlyCollection<string>>? ConflictExecutor;
        internal static int ConflictCycleCount { get; private set; }

        // b2-t3 (multi-scene-builders): per-builder baseline state, keyed on BuilderRoute.BuilderName —
        // never a single global, so one builder's converged baseline can never be attributed to another.
        private static readonly Dictionary<string, (string source, SceneBuilder.Core.Model.SceneSnapshot snap)> _baselines = new();

        internal static string? BaselineSourceFor(string builderName) =>
            _baselines.TryGetValue(builderName, out var baseline) ? baseline.source : null;

        internal static SceneBuilder.Core.Model.SceneSnapshot? BaselineSnapshotFor(string builderName) =>
            _baselines.TryGetValue(builderName, out var baseline) ? baseline.snap : null;

        private static readonly HashSet<EntityId> _pendingSceneIds = new();
        private static bool _sceneDeadlineArmed;
        private static double _sceneDeadline;

        private static readonly HashSet<string> _pendingSourcePaths = new();
        private static bool _sourceDeadlineArmed;
        private static double _sourceDeadline;

        private static readonly HashSet<string> _pendingPrefabSourcePaths = new();
        private static bool _prefabSourceDeadlineArmed;
        private static double _prefabSourceDeadline;

        // Keyed on BuilderName to dedupe — PrefabBuilderRoute is a struct with no Equals, so not a HashSet.
        private static readonly Dictionary<string, PrefabBuilderRoute> _pendingPrefabAssetRoutes = new();
        private static bool _prefabAssetDeadlineArmed;
        private static double _prefabAssetDeadline;

        private static readonly object _watcherLock = new();
        private static readonly HashSet<string> _watcherPendingPaths = new();
        private static bool _watcherRouteSetDirty;
        private static FileSystemWatcher? _watcher;
        private static FileSystemWatcher? _prefabWatcher;

        // Session-local O(changed) snapshot assembler(s), one per builder — a shared instance would
        // leak one builder's incremental node/id cache into another's assemble (research.md). Wiped
        // on reload (a cold re-assemble rewarms). Reset alongside the rest of the pump's state for tests.
        private static readonly Dictionary<string, ChangeScopedSnapshot> _snapshotAssemblers = new();

        private static ChangeScopedSnapshot GetAssembler(string builderName)
        {
            if (!_snapshotAssemblers.TryGetValue(builderName, out var assembler))
            {
                assembler = new ChangeScopedSnapshot();
                _snapshotAssemblers[builderName] = assembler;
            }

            return assembler;
        }

        /// <summary>Arm iff the persisted master toggle is on; else disarm. Domain-reload survival + menu-flip wiring.</summary>
        public static void ApplyToggleState()
        {
            if (SceneBuilderAutoToggle.Enabled && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Arm();
            }
            else
            {
                Disarm();
            }
        }

        /// <summary>Idempotent: subscribes events + starts the FileSystemWatcher + starts the update pump.</summary>
        public static void Arm()
        {
            if (IsArmed)
            {
                return;
            }

            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.update += OnUpdate;
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            StartWatcher();

            IsArmed = true;
        }

        /// <summary>Idempotent: unsubscribes events + disposes the FileSystemWatcher + stops the update pump.</summary>
        public static void Disarm()
        {
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.update -= OnUpdate;
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            StopWatcher();

            IsArmed = false;
        }

        /// <summary>Focus-regain hook: recovers an external edit that fired no ObjectChangeEvent while the editor was unfocused.</summary>
        private static void OnFocusChanged(bool focused)
        {
            if (focused)
            {
                SceneBuilderResync.ResyncActiveScene();
            }
        }

        /// <summary>Scene-open hook: resyncs a scene the moment it is opened, before any edit is made in it.</summary>
        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) =>
            SceneBuilderResync.ResyncScene(scene);

        /// <summary>
        /// Play-mode gate (b7-t1, spec checklist #12): disarm on entering Play (no scene-edit
        /// cycles run while playing) and re-arm on returning to Edit mode iff the persisted
        /// master toggle is still on (toggle state survives the round trip).
        /// </summary>
        internal static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    Disarm();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    ApplyToggleState();
                    break;
            }
        }

        private static void StartWatcher()
        {
            if (_watcher != null)
            {
                return;
            }

            var dir = SceneBuilderPaths.EnsureBuildersDirectory();
            var watcher = new FileSystemWatcher(dir, "*.cs")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            watcher.Changed += OnWatcherEvent;
            watcher.Created += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;

            var prefabDir = SceneBuilderPaths.EnsurePrefabBuildersDirectory();
            var prefabWatcher = new FileSystemWatcher(prefabDir, "*.cs")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            prefabWatcher.Changed += OnWatcherEvent;
            prefabWatcher.Created += OnWatcherEvent;
            prefabWatcher.Renamed += OnWatcherEvent;
            prefabWatcher.Deleted += OnWatcherEvent;
            prefabWatcher.EnableRaisingEvents = true;
            _prefabWatcher = prefabWatcher;
        }

        private static void StopWatcher()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnWatcherEvent;
                _watcher.Created -= OnWatcherEvent;
                _watcher.Renamed -= OnWatcherEvent;
                _watcher.Deleted -= OnWatcherEvent;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_prefabWatcher != null)
            {
                _prefabWatcher.EnableRaisingEvents = false;
                _prefabWatcher.Changed -= OnWatcherEvent;
                _prefabWatcher.Created -= OnWatcherEvent;
                _prefabWatcher.Renamed -= OnWatcherEvent;
                _prefabWatcher.Deleted -= OnWatcherEvent;
                _prefabWatcher.Dispose();
                _prefabWatcher = null;
            }

            lock (_watcherLock)
            {
                _watcherPendingPaths.Clear();
                _watcherRouteSetDirty = false;
            }
        }

        /// <summary>Background-thread callback (A1): touches ONLY a lock-guarded set, no Unity calls.</summary>
        private static void OnWatcherEvent(object sender, FileSystemEventArgs e) => EnqueueWatcherPath(e.FullPath, e.ChangeType);

        /// <summary>
        /// The single choke point for both the real background watcher handler and a deterministic
        /// test seam (b6-t1). Queues the path for the source-settle debounce and, for a set-changing
        /// event (Created/Deleted/Renamed — NOT a plain content-save Changed), flags the route set
        /// dirty so <see cref="DrainWatcherPaths"/> invalidates <see cref="SceneBuilderRouter"/>'s
        /// memoized route cache on the main thread.
        /// </summary>
        internal static void EnqueueWatcherPath(string fullPath, WatcherChangeTypes changeType)
        {
            lock (_watcherLock)
            {
                _watcherPendingPaths.Add(fullPath);
                if (changeType != WatcherChangeTypes.Changed)
                {
                    _watcherRouteSetDirty = true;
                }
            }
        }

        /// <summary>
        /// changesPublished handler. Must be a named method with a by-ref parameter — the delegate
        /// (UnityEditor.ObjectChangeEvents.ObjectChangeEventsHandler) is by-ref and cannot be a lambda.
        /// </summary>
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            List<EntityId>? ids = null;
            for (var i = 0; i < stream.length; i++)
            {
                EntityId entityId;
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propsArgs);
                        entityId = propsArgs.entityId;
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var createArgs);
                        entityId = createArgs.entityId;
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structArgs);
                        entityId = structArgs.entityId;
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var structHierArgs);
                        entityId = structHierArgs.entityId;
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parentArgs);
                        entityId = parentArgs.entityId;
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyArgs);
                        entityId = destroyArgs.entityId;
                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var childrenOrderArgs);
                        entityId = childrenOrderArgs.entityId;
                        break;
                    case ObjectChangeKind.ChangeRootOrder:
                        stream.GetChangeRootOrderEvent(i, out var rootOrderArgs);
                        entityId = rootOrderArgs.entityId;
                        break;
                    default:
                        continue;
                }

                (ids ??= new List<EntityId>()).Add(entityId);
            }

            if (ids != null)
            {
                RouteEditorChange(ids);
            }
        }

        private static void OnSceneSaved(Scene scene)
        {
            // Coarse catch-all: an empty id set is a signal to the b5-t1 executor to do a cold
            // assemble rather than an incremental one.
            NotifySceneChanged(Array.Empty<EntityId>());
        }

        private static void OnUpdate() => PumpOnce();

        /// <summary>Drop if disarmed or SuppressionScope.SceneWriteSuppressed, else accumulate + (re)arm the scene settle deadline.</summary>
        internal static void NotifySceneChanged(IEnumerable<EntityId> ids)
        {
            if (!IsArmed || SuppressionScope.SceneWriteSuppressed)
            {
                return;
            }

            foreach (var id in ids)
            {
                _pendingSceneIds.Add(id);
            }

            _sceneDeadlineArmed = true;
            _sceneDeadline = Clock() + SettleSeconds;
        }

        /// <summary>Drop if disarmed or the write is our own (SuppressionScope's registry), else accumulate + (re)arm the source settle deadline.</summary>
        internal static void NotifySourceChanged(string path)
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

            _pendingSourcePaths.Add(fullPath);
            _sourceDeadlineArmed = true;
            _sourceDeadline = Clock() + SettleSeconds;
        }

        /// <summary>True iff <paramref name="fullPath"/> is on disk and its content hash matches a write we made ourselves (<see cref="SuppressionScope"/>'s own-write registry via <see cref="SceneBuilderPaths.WriteIfChanged"/>) — the loop-break shared by every source-change lane.</summary>
        private static bool DropsAsOwnWrite(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var hash = SuppressionScope.ComputeContentHash(File.ReadAllText(fullPath));
            return SuppressionScope.IsOwnWrite(fullPath, hash);
        }

        internal static void PumpOnce() => PumpOnce(Clock());

        internal static void PumpOnce(double now)
        {
            DrainWatcherPaths();

            var sceneDue = _sceneDeadlineArmed && now >= _sceneDeadline;
            var sourceDue = _sourceDeadlineArmed && now >= _sourceDeadline;

            // Dual-trigger: BOTH a scene deadline and a real external source deadline are due in this
            // SAME window. Route to the combined conflict-aware cycle INSTEAD OF the two single-
            // direction blocks below — running them independently would let one side's reconcile-
            // against-stale-baseline silently revert the other's edit (research.md Refinement 2).
            if (sceneDue && sourceDue && ConflictExecutor != null)
            {
                ConflictCycleCount++;
                var conflictIds = new List<EntityId>(_pendingSceneIds);
                _pendingSceneIds.Clear();
                _sceneDeadlineArmed = false;
                var conflictPaths = new List<string>(_pendingSourcePaths);
                _pendingSourcePaths.Clear();
                _sourceDeadlineArmed = false;
                InvokeExecutor(ConflictExecutor, conflictIds, conflictPaths);
                return;
            }

            if (sceneDue)
            {
                SceneToCodeCycleCount++;
                var ids = new List<EntityId>(_pendingSceneIds);
                _pendingSceneIds.Clear();
                _sceneDeadlineArmed = false;
                InvokeExecutor(SceneToCodeExecutor, ids);
            }

            if (sourceDue)
            {
                CodeToSceneCycleCount++;
                var paths = new List<string>(_pendingSourcePaths);
                _pendingSourcePaths.Clear();
                _sourceDeadlineArmed = false;
                InvokeExecutor(CodeToSceneExecutor, paths);
            }

            // Two independent prefab lanes — a separate direction pair, deliberately never folded
            // into the scene<->code conflict combine above.
            var prefabSourceDue = _prefabSourceDeadlineArmed && now >= _prefabSourceDeadline;
            if (prefabSourceDue)
            {
                PrefabCodeToAssetCycleCount++;
                var paths = new List<string>(_pendingPrefabSourcePaths);
                _pendingPrefabSourcePaths.Clear();
                _prefabSourceDeadlineArmed = false;
                RunPrefabCycle(() => ExecutePrefabCodeToAsset(paths));
            }

            var prefabAssetDue = _prefabAssetDeadlineArmed && now >= _prefabAssetDeadline;
            if (prefabAssetDue)
            {
                PrefabAssetToCodeCycleCount++;
                var routes = new List<PrefabBuilderRoute>(_pendingPrefabAssetRoutes.Values);
                _pendingPrefabAssetRoutes.Clear();
                _prefabAssetDeadlineArmed = false;
                RunPrefabCycle(() => ExecutePrefabAssetToCode(routes));
            }
        }

        private static void DrainWatcherPaths()
        {
            List<string>? drained = null;
            bool routeSetDirty;
            lock (_watcherLock)
            {
                routeSetDirty = _watcherRouteSetDirty;
                _watcherRouteSetDirty = false;

                if (_watcherPendingPaths.Count > 0)
                {
                    drained = new List<string>(_watcherPendingPaths);
                    _watcherPendingPaths.Clear();
                }
            }

            // Invalidate the memoized route set BEFORE the early-out so a delete-only cycle (no
            // source path enqueued for the settle debounce) still forces the next Discover() to
            // re-scan SceneBuilderPaths.BuildersDirectory.
            if (routeSetDirty)
            {
                SceneBuilderRouter.Invalidate();
            }

            if (drained == null)
            {
                return;
            }

            foreach (var path in drained)
            {
                if (SceneBuilderRouter.TryRoutePrefabBuilderFile(path, out _))
                {
                    NotifyPrefabSourceChanged(path);
                }
                else
                {
                    NotifySourceChanged(path);
                }
            }
        }

        private static void InvokeExecutor<T>(Action<IReadOnlyCollection<T>>? executor, IReadOnlyCollection<T> arg)
        {
            if (executor == null)
            {
                return;
            }

            try
            {
                executor(arg);
            }
            catch (Exception e)
            {
                // A throwing executor must not wedge the pump — the next debounce cycle must still run.
                Debug.LogException(e);
            }
        }

        private static void InvokeExecutor(
            Action<IReadOnlyCollection<EntityId>, IReadOnlyCollection<string>>? executor,
            IReadOnlyCollection<EntityId> ids,
            IReadOnlyCollection<string> paths)
        {
            if (executor == null)
            {
                return;
            }

            try
            {
                executor(ids, paths);
            }
            catch (Exception e)
            {
                // A throwing executor must not wedge the pump — the next debounce cycle must still run.
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// The real scene-&gt;code cycle body (spec checklist #7, #8; blocker 4): save-on-create for
        /// any live object in <paramref name="ids"/> that is not yet known to the sidecar's identity
        /// map (a genuinely new object, with no durable identity for the reconcile to key on), then
        /// assemble a change-scoped snapshot and reconcile via the pre-assembled-snapshot
        /// <see cref="SceneBuilderSync.Run(string, string, Scene, SceneBuilder.Core.Model.SceneSnapshot)"/>
        /// overload. An edit on an already-known object (e.g. a transform drag) never forces a save.
        /// </summary>
        internal static void ExecuteSceneToCode(IReadOnlyCollection<EntityId> ids)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            // Route the ACTIVE scene to its OWN governing builder — never the last-built builder's
            // paths (that silently reconciles the wrong builder, or writes an orphan scene's edits
            // into whichever builder happened to be built last). No governing route = clean no-op.
            if (!SceneBuilderRouter.TryRouteScene(scene, out var route))
            {
                return;
            }

            var builderPath = route.BuilderPath;
            var sidecarPath = route.SidecarPath;
            if (!File.Exists(builderPath) || !File.Exists(sidecarPath))
            {
                return; // nothing to sync back into
            }

            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));

            if (!string.IsNullOrEmpty(scene.path) && ids.Count > 0)
            {
                if (NeedsSaveForDurableId(ids, map))
                {
                    using (SuppressionScope.SuppressScene())
                    {
                        EditorSceneManager.SaveScene(scene); // in place; dropped as self-echo
                    }
                }
            }

            var assembler = GetAssembler(route.BuilderName);
            // M5: resolve live scene-object reference fields to LogicalId (mapped) / raw GlobalObjectId
            // (unmapped) — the reconcile-feeding incremental read path, mirroring the cold Sync path.
            var sceneRef = SceneRefResolver.ForMap(map);
            var snapshot = ids.Count == 0
                ? assembler.AssembleCold(scene, sceneRef)          // sceneSaved catch-all
                : assembler.AssembleIncremental(scene, ids, sceneRef);

            SceneBuilderSync.Run(builderPath, sidecarPath, scene, snapshot);

            // Establish/refresh the b6-t1 conflict-aware baseline at this converged tail (scope-
            // validator finding, bucket-b6.md #1) — without this, a real session's baseline stays
            // null forever and every dual-trigger cycle silently degrades to the clobbering fallback.
            CaptureBaseline(scene);
        }

        /// <summary>
        /// True iff any live object in <paramref name="ids"/> is NOT already known to
        /// <paramref name="map"/> (no entry carries its <see cref="GlobalObjectId"/>) — i.e. a
        /// genuinely new object the reconcile has never seen, which needs a save before it earns a
        /// durable identity to key on. A destroyed/unresolvable id is skipped (not a create).
        /// </summary>
        /// <remarks>
        /// NOT keyed on <c>GlobalObjectId.targetObjectId == 0</c> — falsified on 6000.5.3f1: once the
        /// active scene already has a saved path, a brand-new, never-saved GameObject already reports
        /// a nonzero, deterministically-hashed targetObjectId, so that check never fires in the
        /// realistic scenario. "Known to the sidecar" is the actual on-disk-durability signal.
        /// </remarks>
        private static bool NeedsSaveForDurableId(IReadOnlyCollection<EntityId> ids, IdentityMap map)
        {
            foreach (var id in ids)
            {
                var obj = EditorUtility.EntityIdToObject(id);
                var go = obj as GameObject;
                if (go == null && obj is Component component)
                {
                    go = component.gameObject;
                }

                if (go == null)
                {
                    continue; // destroyed or unresolved, not a create
                }

                var globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
                if (!map.IsManaged(globalObjectId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The code-&gt;scene cycle body (b5-t2, spec checklist #4): on a real external write to the
        /// governing builder, parse+validate+build in place via <see cref="SceneBuilderBuild.Run"/>;
        /// on a parse error or planning-phase diagnostic, log LOCATED and leave the scene untouched.
        /// </summary>
        internal static void ExecuteCodeToScene(IReadOnlyCollection<string> paths)
        {
            foreach (var path in paths)
            {
                if (!SceneBuilderRouter.TryRouteBuilderFile(path, out var route))
                {
                    continue;
                }

                if (!File.Exists(route.BuilderPath))
                {
                    continue;
                }

                if (!SceneBuilderRouter.TryGetOpenScene(route, out var scene))
                {
                    Debug.LogError(
                        $"[CodeScenes] {route.BuilderName}: scene {route.ScenePath} is not open — " +
                        "code->scene build skipped.");
                    continue;
                }

                try
                {
                    var result = SceneBuilderBuild.Run(route.BuilderPath, route.ScenePath, route.SidecarPath, scene);
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        Debug.LogError(
                            $"[CodeScenes] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): " +
                            $"{diagnostic.Message} — scene left untouched.");
                    }

                    // Establish/refresh the b6-t1 conflict-aware baseline at this converged tail (scope-
                    // validator finding, bucket-b6.md #1) — without this, a real session's baseline stays
                    // null forever and every dual-trigger cycle silently degrades to the clobbering fallback.
                    CaptureBaseline(scene);
                }
                catch (ParseException e)
                {
                    Debug.LogError(
                        $"[CodeScenes] Parse error in {route.BuilderPath} at line {e.Line}, column {e.Column}: " +
                        $"{e.Message} — scene left untouched.");
                }
            }
        }

        /// <summary>
        /// Captures the last-converged (source, scene-snapshot) baseline the combined conflict-aware
        /// cycle (<see cref="ExecuteBothChanged"/>) attributes both sides' field edits against (b6-t1,
        /// research.md Refinement 2). Called at the tail of a converged single-direction cycle and
        /// directly by tests to pin a deterministic baseline before making both-side edits. A no-op
        /// (baseline left null) when no builder has been built this session yet.
        /// </summary>
        internal static void CaptureBaseline(Scene scene)
        {
            if (!SceneBuilderRouter.TryRouteScene(scene, out var route))
            {
                return;
            }

            if (!File.Exists(route.BuilderPath))
            {
                _baselines.Remove(route.BuilderName);
                return;
            }

            var assembler = GetAssembler(route.BuilderName);

            // M5: resolve live scene-object reference fields the same way the reconcile-feeding reads
            // do, so a baseline ObjectRef agrees with the field-diff (ExecuteBothChanged) that compares
            // against it. Default to SceneRefResolver.None (scene refs read Unsupported) unless a
            // sidecar is found below: the baseline is only used for desired-vs-desired code diffs and
            // scene-vs-baseline field attribution, never written back, so an Unsupported read is
            // harmless here.
            var sceneRef = SceneRefResolver.None;
            if (File.Exists(route.SidecarPath))
            {
                var map = IdentityMapJson.Deserialize(File.ReadAllText(route.SidecarPath));
                sceneRef = SceneRefResolver.ForMap(map);
            }

            _baselines[route.BuilderName] = (File.ReadAllText(route.BuilderPath), assembler.AssembleCold(scene, sceneRef));
        }

        /// <summary>
        /// The combined conflict-aware cycle body (b6-t1, spec checklist #9, #10): a 3-way field-level
        /// merge of the last-converged baseline, the current on-disk source (code edits) and the live
        /// scene (scene edits), via <see cref="SceneBuilderSync.RunConflictAware"/>. Non-overlapping
        /// fields apply in their own direction; a true same-field-same-object overlap resolves
        /// scene-wins with the prior code value preserved in a `// CONFLICT:` marker, a located Console
        /// error, and a scene-view overlay registration — never a modal. Degrades to the two
        /// single-direction executors (never silently clobbering either side) when no baseline is
        /// established yet (a cold session).
        /// </summary>
        internal static void ExecuteBothChanged(IReadOnlyCollection<EntityId> ids, IReadOnlyCollection<string> paths)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                return;
            }

            // Route the ACTIVE scene to its OWN governing builder — an active scene with no governing
            // route is a clean no-op (this also subsumes the old empty-path guard, since "" matches no
            // route).
            if (!SceneBuilderRouter.TryRouteScene(scene, out var route))
            {
                return;
            }

            if (!_baselines.TryGetValue(route.BuilderName, out var baseline))
            {
                // Cold session: no last-converged baseline to attribute against — degrade safely to
                // the two single-direction executors rather than risk a stale-baseline clobber.
                InvokeExecutor(SceneToCodeExecutor, ids);
                InvokeExecutor(CodeToSceneExecutor, paths);
                return;
            }

            var builderPath = route.BuilderPath;
            var sidecarPath = route.SidecarPath;
            if (!File.Exists(builderPath) || !File.Exists(sidecarPath))
            {
                return; // nothing to sync back into
            }

            var assembler = GetAssembler(route.BuilderName);
            // M5: same reverse-map every reconcile-feeding read applies, so the live snapshot's
            // ObjectRefs agree with the baseline's for unchanged fields (idempotent field attribution).
            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
            var sceneRef = SceneRefResolver.ForMap(map);
            var liveSnapshot = ids.Count == 0
                ? assembler.AssembleCold(scene, sceneRef)
                : assembler.AssembleIncremental(scene, ids, sceneRef);

            SceneBuilderSync.RunConflictAware(
                builderPath, sidecarPath, scene, liveSnapshot, baseline.source, baseline.snap, new ConflictSurfacing());

            // Push CODE-only fields into the scene: the source RunConflictAware just wrote already
            // carries the scene-authoritative + conflict-resolved values, so this Build call no-ops
            // those and materializes only the fields the code alone changed. Scene write is
            // suppression-guarded internally (SceneBuilderBuild.Run), so it never re-triggers us.
            var currentScene = EditorSceneManager.GetActiveScene();
            var buildResult = SceneBuilderBuild.Run(builderPath, currentScene.path, sidecarPath, currentScene);
            foreach (var diagnostic in buildResult.Diagnostics)
            {
                Debug.LogError(
                    $"[CodeScenes] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): " +
                    $"{diagnostic.Message} — code-only field(s) left unmaterialized.");
            }

            CaptureBaseline(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// Wires the production executors (<see cref="ExecuteSceneToCode"/>,
        /// <see cref="ExecuteCodeToScene"/>) onto the pump's injection seam. Called from the static
        /// ctor before <see cref="ApplyToggleState"/> so auto-sync is wired to real logic by default.
        /// </summary>
        internal static void WireDefaultExecutors()
        {
            SceneToCodeExecutor = ExecuteSceneToCode;
            CodeToSceneExecutor = ExecuteCodeToScene;
            ConflictExecutor = ExecuteBothChanged;
            InPrefabModeProbe = DefaultInPrefabMode;
            ActivePrefabRouteProbe = DefaultActivePrefabRoute;
        }

        /// <summary>Test hygiene: full disarm + state reset, then re-arm to the default (auto-on) state the tests expect.</summary>
        internal static void ResetForTests()
        {
            Disarm();

            Clock = () => EditorApplication.timeSinceStartup;
            SettleSeconds = 0.4;
            SceneToCodeCycleCount = 0;
            CodeToSceneCycleCount = 0;
            SceneToCodeExecutor = null;
            CodeToSceneExecutor = null;
            ConflictExecutor = null;
            ConflictCycleCount = 0;
            PrefabCodeToAssetCycleCount = 0;
            PrefabAssetToCodeCycleCount = 0;
            InPrefabModeProbe = DefaultInPrefabMode;
            ActivePrefabRouteProbe = DefaultActivePrefabRoute;
            _baselines.Clear();
            _snapshotAssemblers.Clear();
            SceneBuilderBuild.LastBuilderPath = null;
            SceneBuilderBuild.LastSidecarPath = null;

            _pendingSceneIds.Clear();
            _sceneDeadlineArmed = false;
            _sceneDeadline = 0;

            _pendingSourcePaths.Clear();
            _sourceDeadlineArmed = false;
            _sourceDeadline = 0;

            _pendingPrefabSourcePaths.Clear();
            _prefabSourceDeadlineArmed = false;
            _prefabSourceDeadline = 0;

            _pendingPrefabAssetRoutes.Clear();
            _prefabAssetDeadlineArmed = false;
            _prefabAssetDeadline = 0;

            Arm();
        }
    }
}
