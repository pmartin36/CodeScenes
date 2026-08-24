#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Validation;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The <see cref="IBuildSyncTarget"/> implementation for a prefab asset: the single
    /// prefab-writing site. <see cref="Build"/> is the entry point — parse, single-root validate,
    /// then route through <see cref="SceneBuilderBuild.RunCore"/> the same way the scene target does.
    /// On CREATE (no asset yet at the target path) the hierarchy is built into an isolated preview
    /// scene, then minted via <see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/> and
    /// re-keyed onto the freshly-loaded <see cref="PrefabUtility.LoadPrefabContents"/> copy so the
    /// identity stamp that follows resolves the same way an UPDATE build's would. On UPDATE (asset
    /// already on disk) the loaded contents are edited in place and saved back onto the same path, so
    /// an unchanged sibling keeps its local fileID / <see cref="GlobalObjectId"/> across the
    /// regeneration.
    /// </summary>
    internal sealed class PrefabBuildSyncTarget : IBuildSyncTarget
    {
        private readonly string _prefabAssetPath;

        private bool _initialized;
        private bool _isCreate;
        private Scene _scene;
        private Scene _previewScene;
        private GameObject? _contents;
        private PlanExecutor.ExecutionResult? _execution;

        public PrefabBuildSyncTarget(string prefabAssetPath)
        {
            _prefabAssetPath = prefabAssetPath;
        }

        public string TargetPath => _prefabAssetPath;

        public SceneSnapshot ReadSnapshot(
            Func<UnityEngine.Object, string?>? resolveSceneRef,
            Func<UnityEngine.Object?, ValueNode?>? resolveListenerRef)
        {
            if (!_initialized)
            {
                _initialized = true;
                _isCreate = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabAssetPath) == null;

                if (_isCreate)
                {
                    // No prior contents to read — build the whole hierarchy fresh into an isolated
                    // scene. Reading it now (empty) is what makes Materializer treat every authored
                    // object as new.
                    _previewScene = EditorSceneManager.NewPreviewScene();
                    _scene = _previewScene;
                }
                else
                {
                    _contents = PrefabUtility.LoadPrefabContents(_prefabAssetPath);
                    _scene = _contents.scene;
                }
            }

            return SceneSnapshotReader.Read(_scene, resolveSceneRef, resolveListenerRef);
        }

        public PlanExecutor.ExecutionResult Execute(Plan plan, IdentityMap map)
        {
            _execution = PlanExecutor.Execute(plan, map, _scene);
            return _execution;
        }

        public IDisposable BeginWrite() => NoOpDisposable.Instance;

        public void Persist()
        {
            if (_isCreate)
            {
                var previewRoots = _scene.GetRootGameObjects();
                if (previewRoots.Length == 0)
                {
                    // Nothing was authored — nothing to mint.
                    return;
                }

                var previewRoot = previewRoots[0];

                // Mints the asset from the loose preview-scene instance; import so the new asset's
                // fileIDs are committed, then load an editable copy.
                PrefabUtility.SaveAsPrefabAsset(previewRoot, _prefabAssetPath);
                CommitImport();

                _contents = PrefabUtility.LoadPrefabContents(_prefabAssetPath);
                var contentsRoot = _contents.scene.GetRootGameObjects()[0];

                // Re-key the execution result onto the loaded contents' objects so the identity
                // stamp that follows resolves the SAME way an UPDATE build's stamp would (both off
                // a LoadPrefabContents copy of this exact asset), not off the discarded preview-scene
                // instance.
                if (_execution != null)
                {
                    RemapExecutionToContents(previewRoot, contentsRoot, _execution);
                }

                _scene = _contents.scene;
            }
            else
            {
                PrefabUtility.SaveAsPrefabAsset(_contents!, _prefabAssetPath);
            }
        }

        public void Dispose()
        {
            if (_contents != null)
            {
                PrefabUtility.UnloadPrefabContents(_contents);
                _contents = null;
            }

            if (_isCreate && _previewScene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_previewScene);
            }
        }

        // Flushes + reimports the prefab asset so a just-written change is visible to a subsequent
        // AssetDatabase.LoadAssetAtPath/LoadPrefabContents call in the SAME pass — the same
        // SaveAssets + ImportAsset(ForceUpdate) pairing NestedRoundTripTests.RenameLeftTurretToCannon
        // uses after an edit-in-place save. Scoped to this one asset path, not a project-wide Refresh.
        private void CommitImport()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(_prefabAssetPath, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>
        /// Pairs <paramref name="previewRoot"/>'s tree with <paramref name="contentsRoot"/>'s (the
        /// just-saved asset's loaded contents — a structural clone with identical child order) and
        /// rewrites <paramref name="execution"/>'s dictionaries in place so every LogicalId now points
        /// at the prefab-internal object instead of the discarded preview-scene one.
        /// </summary>
        private static void RemapExecutionToContents(
            GameObject previewRoot, GameObject contentsRoot, PlanExecutor.ExecutionResult execution)
        {
            var goMap = new Dictionary<GameObject, GameObject>();
            var compMap = new Dictionary<Component, Component>();
            PairTransforms(previewRoot.transform, contentsRoot.transform, goMap, compMap);

            foreach (var key in new List<string>(execution.GameObjectsByLogicalId.Keys))
            {
                var previewGo = execution.GameObjectsByLogicalId[key];
                if (previewGo != null && goMap.TryGetValue(previewGo, out var contentsGo))
                {
                    execution.GameObjectsByLogicalId[key] = contentsGo;
                }
            }

            foreach (var key in new List<string>(execution.ComponentsByLogicalId.Keys))
            {
                var previewComp = execution.ComponentsByLogicalId[key];
                if (previewComp != null && compMap.TryGetValue(previewComp, out var contentsComp))
                {
                    execution.ComponentsByLogicalId[key] = contentsComp;
                }
            }
        }

        private static void PairTransforms(
            Transform preview, Transform contents,
            Dictionary<GameObject, GameObject> goMap, Dictionary<Component, Component> compMap)
        {
            goMap[preview.gameObject] = contents.gameObject;

            var previewComponents = preview.GetComponents<Component>();
            var contentsComponents = contents.GetComponents<Component>();
            var componentCount = Math.Min(previewComponents.Length, contentsComponents.Length);
            for (var i = 0; i < componentCount; i++)
            {
                if (previewComponents[i] != null && contentsComponents[i] != null)
                {
                    compMap[previewComponents[i]] = contentsComponents[i];
                }
            }

            var childCount = Math.Min(preview.childCount, contents.childCount);
            for (var i = 0; i < childCount; i++)
            {
                PairTransforms(preview.GetChild(i), contents.GetChild(i), goMap, compMap);
            }
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Parse <paramref name="builderPath"/>, refuse a multi-root prefab before writing anything,
        /// refuse a single root whose authored name does not match
        /// <paramref name="prefabAssetPath"/>'s filename stem (<see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/>
        /// unconditionally renames the persisted root to that stem, so a mismatch would silently rename
        /// the authored root on every build), then build (code-&gt;prefab) through the shared
        /// <see cref="SceneBuilderBuild.RunCore"/> pipeline against <paramref name="prefabAssetPath"/>.
        /// </summary>
        internal static SceneBuilderBuild.BuildResult Build(
            string builderPath, string prefabAssetPath, string sidecarPath)
        {
            var builderName = Path.GetFileNameWithoutExtension(builderPath);
            var source = File.ReadAllText(builderPath);
            var parse = BuilderParser.Parse(source);
            var validation = PrefabSingleRootValidator.Validate(parse, source, builderPath);
            if (!validation.Ok)
            {
                return SceneBuilderBuildStatus.RecordRefused(builderName, validation.Diagnostics);
            }

            var roots = parse.Model.Roots;
            if (roots.Length == 1)
            {
                var root = roots[0];
                var stem = Path.GetFileNameWithoutExtension(prefabAssetPath);
                if (root.Name != stem)
                {
                    var (line, col) = LocationOfAnchor(source, parse.Anchors, root.LogicalId);
                    var diagnostic = new Diagnostic
                    {
                        File = builderPath,
                        Line = line,
                        Col = col,
                        Code = "SB2402",
                        Severity = DiagnosticSeverity.Error,
                        Message = $"The root's authored name '{root.Name}' does not match the prefab " +
                            $"filename '{stem}'. SaveAsPrefabAsset would silently rename it on every build.",
                        Suggestion = $"Rename the root to '{stem}', or rename the target prefab to '{root.Name}.prefab'.",
                    };
                    return SceneBuilderBuildStatus.RecordRefused(builderName, new[] { diagnostic });
                }
            }

            using var target = new PrefabBuildSyncTarget(prefabAssetPath);
            var result = SceneBuilderBuild.RunCore(target, builderPath, sidecarPath);

            if (result.Diagnostics.Count == 0 && AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) != null)
            {
                SelfRegister(prefabAssetPath, sidecarPath);
            }

            return result;
        }

        /// <summary>
        /// Folds the prefab's OWN <see cref="AssetEntry"/> ({ Guid, LastKnownPath, TypeHint="Prefab" })
        /// into the sidecar's <see cref="IdentityMap.Assets"/>, so a builder-authored prefab carries the
        /// same self-describing cache entry an in-scene reference to it would harvest — without a scene
        /// ever referencing it. Idempotent: an unchanged second build merges to the same entry
        /// (<see cref="AssetCacheMerge.Result.ChangedCount"/> == 0) and skips the write.
        /// </summary>
        private static void SelfRegister(string prefabAssetPath, string sidecarPath)
        {
            var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
            var selfEntry = new AssetEntry
            {
                Guid = AssetDatabase.AssetPathToGUID(prefabAssetPath),
                LastKnownPath = prefabAssetPath,
                TypeHint = "Prefab",
            };

            var merge = AssetCacheMerge.Merge(map.Assets, new[] { selfEntry });
            if (merge.ChangedCount == 0)
            {
                return;
            }

            var updated = map with { Assets = merge.Merged };
            SceneBuilderPaths.WriteIfChanged(sidecarPath, IdentityMapJson.Serialize(updated));
        }

        /// <summary>
        /// Prefab reconcile (prefab-&gt;code): routes this target through the existing
        /// target-generic <see cref="SceneBuilderSync.SyncCore"/> reconcile core, so an edit made to
        /// the loaded prefab contents (a field, add, remove, rename, reparent, or reorder) patches
        /// <paramref name="builderPath"/> through the shared structural-matching identity model.
        /// </summary>
        internal static SceneBuilderSync.SyncResult Sync(string builderPath, string prefabAssetPath, string sidecarPath)
        {
            using var target = new PrefabBuildSyncTarget(prefabAssetPath);
            return SceneBuilderSync.SyncCore(target, builderPath, sidecarPath, preAssembled: null);
        }

        // Verbatim copy of PrefabSingleRootValidator's offset -> line/col conversion so the two
        // located-diagnostic sites agree on how they locate an anchor, without pulling the target's
        // Editor assembly into a Core-internal helper.
        private static (int Line, int Col) LocationOfAnchor(
            string source, IReadOnlyDictionary<string, SourceSpan> anchors, string logicalId) =>
            anchors.TryGetValue(logicalId, out var span) ? (LineOf(source, span.Start), ColumnOf(source, span.Start)) : (0, 0);

        private static int LineOf(string source, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static int ColumnOf(string source, int offset)
        {
            var lineStart = source.LastIndexOf('\n', Math.Min(offset, source.Length - 1));
            return offset - lineStart;
        }
    }
}
