#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    public static class SceneBuilderAutoSync
    {
        static SceneBuilderAutoSync()
        {
            // Domain-reload survival: static ctor re-runs on every reload and re-arms iff the
            // persisted master toggle is on (SceneBuilderAutoToggle.Enabled defaults true).
            ApplyToggleState();
        }

        public static bool IsArmed { get; private set; }

        internal static Func<double> Clock = () => EditorApplication.timeSinceStartup;
        internal static double SettleSeconds = 0.4;

        internal static int SceneToCodeCycleCount { get; private set; }
        internal static int CodeToSceneCycleCount { get; private set; }

        internal static Action<IReadOnlyCollection<EntityId>>? SceneToCodeExecutor;
        internal static Action<IReadOnlyCollection<string>>? CodeToSceneExecutor;

        private static readonly HashSet<EntityId> _pendingSceneIds = new();
        private static bool _sceneDeadlineArmed;
        private static double _sceneDeadline;

        private static readonly HashSet<string> _pendingSourcePaths = new();
        private static bool _sourceDeadlineArmed;
        private static double _sourceDeadline;

        private static readonly object _watcherLock = new();
        private static readonly HashSet<string> _watcherPendingPaths = new();
        private static FileSystemWatcher? _watcher;

        /// <summary>Arm iff the persisted master toggle is on; else disarm. Domain-reload survival + menu-flip wiring.</summary>
        public static void ApplyToggleState()
        {
            if (SceneBuilderAutoToggle.Enabled)
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
            StartWatcher();

            IsArmed = true;
        }

        /// <summary>Idempotent: unsubscribes events + disposes the FileSystemWatcher + stops the update pump.</summary>
        public static void Disarm()
        {
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.update -= OnUpdate;
            StopWatcher();

            IsArmed = false;
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
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }

        private static void StopWatcher()
        {
            if (_watcher == null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatcherEvent;
            _watcher.Created -= OnWatcherEvent;
            _watcher.Renamed -= OnWatcherEvent;
            _watcher.Dispose();
            _watcher = null;

            lock (_watcherLock)
            {
                _watcherPendingPaths.Clear();
            }
        }

        /// <summary>Background-thread callback (A1): touches ONLY a lock-guarded set, no Unity calls.</summary>
        private static void OnWatcherEvent(object sender, FileSystemEventArgs e)
        {
            lock (_watcherLock)
            {
                _watcherPendingPaths.Add(e.FullPath);
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
                NotifySceneChanged(ids);
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
            if (File.Exists(fullPath))
            {
                var hash = SuppressionScope.ComputeContentHash(File.ReadAllText(fullPath));
                if (SuppressionScope.IsOwnWrite(fullPath, hash))
                {
                    return;
                }
            }

            _pendingSourcePaths.Add(fullPath);
            _sourceDeadlineArmed = true;
            _sourceDeadline = Clock() + SettleSeconds;
        }

        internal static void PumpOnce() => PumpOnce(Clock());

        internal static void PumpOnce(double now)
        {
            DrainWatcherPaths();

            if (_sceneDeadlineArmed && now >= _sceneDeadline)
            {
                SceneToCodeCycleCount++;
                var ids = new List<EntityId>(_pendingSceneIds);
                _pendingSceneIds.Clear();
                _sceneDeadlineArmed = false;
                InvokeExecutor(SceneToCodeExecutor, ids);
            }

            if (_sourceDeadlineArmed && now >= _sourceDeadline)
            {
                CodeToSceneCycleCount++;
                var paths = new List<string>(_pendingSourcePaths);
                _pendingSourcePaths.Clear();
                _sourceDeadlineArmed = false;
                InvokeExecutor(CodeToSceneExecutor, paths);
            }
        }

        private static void DrainWatcherPaths()
        {
            List<string>? drained = null;
            lock (_watcherLock)
            {
                if (_watcherPendingPaths.Count > 0)
                {
                    drained = new List<string>(_watcherPendingPaths);
                    _watcherPendingPaths.Clear();
                }
            }

            if (drained == null)
            {
                return;
            }

            foreach (var path in drained)
            {
                NotifySourceChanged(path);
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

            _pendingSceneIds.Clear();
            _sceneDeadlineArmed = false;
            _sceneDeadline = 0;

            _pendingSourcePaths.Clear();
            _sourceDeadlineArmed = false;
            _sourceDeadline = 0;

            Arm();
        }
    }
}
