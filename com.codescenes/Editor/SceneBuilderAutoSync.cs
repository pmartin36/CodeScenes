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
    public static class SceneBuilderAutoSync
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
        }

        // Matches SceneBuilderSync/SceneBuilderBuild's own copy of this literal — the single demo
        // builder this milestone scopes. A third copy; flagged, not refactored here (research.md).
        private const string BuilderName = "DemoScene";

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
        internal static SceneBuilder.Core.Model.SceneSnapshot? BaselineSnapshot;
        internal static string? BaselineSource;

        private static readonly HashSet<EntityId> _pendingSceneIds = new();
        private static bool _sceneDeadlineArmed;
        private static double _sceneDeadline;

        private static readonly HashSet<string> _pendingSourcePaths = new();
        private static bool _sourceDeadlineArmed;
        private static double _sourceDeadline;

        private static readonly object _watcherLock = new();
        private static readonly HashSet<string> _watcherPendingPaths = new();
        private static FileSystemWatcher? _watcher;

        // Session-local O(changed) snapshot assembler for the scene->code executor; wiped on reload
        // (a cold re-assemble rewarms). Reset alongside the rest of the pump's state for tests.
        private static ChangeScopedSnapshot? _snapshotAssembler;

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

            // Prefer the last builder/sidecar this session actually BUILT (SceneBuilderBuild.Run) —
            // covers the isolated-fixture case (build against a non-default path) as well as the
            // production default, since a normal session's Build/BuildDemo call always targets
            // SceneBuilderPaths.Builder(BuilderName) itself. Falls back to the fixed default when
            // nothing has been built yet this session (e.g. a fresh domain reload).
            var builderPath = SceneBuilderBuild.LastBuilderPath ?? SceneBuilderPaths.Builder(BuilderName);
            var sidecarPath = SceneBuilderBuild.LastSidecarPath ?? SceneBuilderPaths.Sidecar(BuilderName);
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

            _snapshotAssembler ??= new ChangeScopedSnapshot();
            // M5: resolve live scene-object reference fields to LogicalId (mapped) / raw GlobalObjectId
            // (unmapped) — the reconcile-feeding incremental read path, mirroring the cold Sync path.
            _snapshotAssembler.SceneRefResolver = ObjectReferenceResolver.BuildSceneRefResolver(map);
            var snapshot = ids.Count == 0
                ? _snapshotAssembler.AssembleCold(scene)          // sceneSaved catch-all
                : _snapshotAssembler.AssembleIncremental(scene, ids);

            SceneBuilderSync.Run(builderPath, sidecarPath, scene, snapshot);

            // Establish/refresh the b6-t1 conflict-aware baseline at this converged tail (scope-
            // validator finding, bucket-b6.md #1) — without this, a real session's baseline stays
            // null forever and every dual-trigger cycle silently degrades to the clobbering fallback.
            CaptureBaseline(EditorSceneManager.GetActiveScene());
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
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                return;
            }

            // Same last-built-pair discovery ExecuteSceneToCode uses (research.md), for symmetry.
            var builderPath = SceneBuilderBuild.LastBuilderPath ?? SceneBuilderPaths.Builder(BuilderName);
            var sidecarPath = SceneBuilderBuild.LastSidecarPath ?? SceneBuilderPaths.Sidecar(BuilderName);

            // Only build on a real change to the governing builder — defensive against a watcher
            // event for an unrelated file in the builders directory.
            var fullBuilderPath = Path.GetFullPath(builderPath);
            var isGoverningBuilderChanged = false;
            foreach (var path in paths)
            {
                if (string.Equals(Path.GetFullPath(path), fullBuilderPath, StringComparison.Ordinal))
                {
                    isGoverningBuilderChanged = true;
                    break;
                }
            }

            if (!isGoverningBuilderChanged || !File.Exists(builderPath))
            {
                return;
            }

            try
            {
                var result = SceneBuilderBuild.Run(builderPath, scene.path, sidecarPath, scene);
                foreach (var diagnostic in result.Diagnostics)
                {
                    Debug.LogError(
                        $"[CodeScenes] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): " +
                        $"{diagnostic.Message} — scene left untouched.");
                }

                // Establish/refresh the b6-t1 conflict-aware baseline at this converged tail (scope-
                // validator finding, bucket-b6.md #1) — without this, a real session's baseline stays
                // null forever and every dual-trigger cycle silently degrades to the clobbering fallback.
                CaptureBaseline(EditorSceneManager.GetActiveScene());
            }
            catch (ParseException e)
            {
                Debug.LogError(
                    $"[CodeScenes] Parse error in {builderPath} at line {e.Line}, column {e.Column}: " +
                    $"{e.Message} — scene left untouched.");
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
            var builderPath = SceneBuilderBuild.LastBuilderPath ?? SceneBuilderPaths.Builder(BuilderName);
            if (!File.Exists(builderPath))
            {
                BaselineSource = null;
                BaselineSnapshot = null;
                return;
            }

            BaselineSource = File.ReadAllText(builderPath);
            _snapshotAssembler ??= new ChangeScopedSnapshot();

            // M5: resolve live scene-object reference fields the same way the reconcile-feeding reads
            // do, so a baseline ObjectRef agrees with the field-diff (ExecuteBothChanged) that compares
            // against it. No sidecar yet -> resolver stays whatever it was (an ObjectRef read as
            // Unsupported is harmless here: the baseline is only used for desired-vs-desired code diffs
            // and scene-vs-baseline field attribution, never written back).
            var sidecarPath = SceneBuilderBuild.LastSidecarPath ?? SceneBuilderPaths.Sidecar(BuilderName);
            if (File.Exists(sidecarPath))
            {
                var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
                _snapshotAssembler.SceneRefResolver = ObjectReferenceResolver.BuildSceneRefResolver(map);
            }

            BaselineSnapshot = _snapshotAssembler.AssembleCold(scene);
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

            if (BaselineSource == null || BaselineSnapshot == null)
            {
                // Cold session: no last-converged baseline to attribute against — degrade safely to
                // the two single-direction executors rather than risk a stale-baseline clobber.
                InvokeExecutor(SceneToCodeExecutor, ids);
                InvokeExecutor(CodeToSceneExecutor, paths);
                return;
            }

            var builderPath = SceneBuilderBuild.LastBuilderPath ?? SceneBuilderPaths.Builder(BuilderName);
            var sidecarPath = SceneBuilderBuild.LastSidecarPath ?? SceneBuilderPaths.Sidecar(BuilderName);
            if (!File.Exists(builderPath) || !File.Exists(sidecarPath))
            {
                return; // nothing to sync back into
            }

            _snapshotAssembler ??= new ChangeScopedSnapshot();
            // M5: same reverse-map every reconcile-feeding read applies, so the live snapshot's
            // ObjectRefs agree with the baseline's for unchanged fields (idempotent field attribution).
            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
            _snapshotAssembler.SceneRefResolver = ObjectReferenceResolver.BuildSceneRefResolver(map);
            var liveSnapshot = ids.Count == 0
                ? _snapshotAssembler.AssembleCold(scene)
                : _snapshotAssembler.AssembleIncremental(scene, ids);

            SceneBuilderSync.RunConflictAware(
                builderPath, sidecarPath, scene, liveSnapshot, BaselineSource, BaselineSnapshot, new ConflictSurfacing());

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
            BaselineSnapshot = null;
            BaselineSource = null;
            _snapshotAssembler = null;
            SceneBuilderBuild.LastBuilderPath = null;
            SceneBuilderBuild.LastSidecarPath = null;

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
