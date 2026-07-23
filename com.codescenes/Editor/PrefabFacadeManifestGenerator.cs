#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Regenerates <see cref="SceneBuilderPaths.FacadeManifestPath"/> — the typed prefab-façade
    /// manifest (spec 25) — as plain <see cref="System.IO.File"/> IO outside <c>Assets/</c>, so it
    /// never triggers a domain reload. Mirrors <see cref="ProjectCatalogGenerator"/>.
    /// </summary>
    /// <remarks>
    /// Holds a static in-memory cache of <see cref="PrefabHierarchy"/> keyed by prefab GUID. The
    /// expensive Unity READ (<see cref="PrefabHierarchyReader"/>) is scoped: primed once via
    /// <see cref="PrefabHierarchyReader.ReadAll"/>, thereafter only the prefabs actually touched by a
    /// postprocess batch are re-read (or dropped, on delete). The BUILD+WRITE (cheap) always rebuilds
    /// the WHOLE catalog from the cache — this is what makes a composing parent re-reflect a nested
    /// prefab's edit even when the parent itself was not re-imported (<see cref="FacadeCatalogBuilder"/>
    /// composes nested children BY REFERENCE across the full set).
    /// </remarks>
    [InitializeOnLoad]
    public class PrefabFacadeManifestGenerator : AssetPostprocessor
    {
        static PrefabFacadeManifestGenerator()
        {
            // Defer past editor load so the first regen never fights initial-domain-load work.
            EditorApplication.delayCall += () => Regenerate();
        }

        // Guid -> PrefabHierarchy. Static: resets on domain reload, re-primed lazily on first use.
        private static Dictionary<string, PrefabHierarchy>? _cache;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            EnsurePrimed();

            var changed = false;

            foreach (var path in importedAssets)
            {
                if (IsPrefabOrModelAsset(path))
                {
                    ReReadInto(path);
                    changed = true;
                }
            }

            foreach (var path in movedAssets)
            {
                // Re-read at the NEW path so the AssetPath-derived data tracks the rename; the GUID
                // (the cache key) is unchanged by a move, so this overwrites the existing entry.
                if (IsPrefabOrModelAsset(path))
                {
                    ReReadInto(path);
                    changed = true;
                }
            }

            foreach (var path in deletedAssets)
            {
                if (RemoveByAssetPath(path))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Rebuild();
            }
        }

        /// <summary>
        /// Primes the cache if needed (first call only — a full <see cref="PrefabHierarchyReader.ReadAll"/>)
        /// then rebuilds+writes the whole catalog. Returns true iff bytes on disk actually changed.
        /// </summary>
        public static bool Regenerate()
        {
            EnsurePrimed();
            return Rebuild();
        }

        private static void EnsurePrimed()
        {
            if (_cache != null)
            {
                return;
            }

            _cache = new Dictionary<string, PrefabHierarchy>(StringComparer.Ordinal);
            foreach (var prefab in PrefabHierarchyReader.ReadAll())
            {
                _cache[prefab.Guid] = prefab;
            }
        }

        private static void ReReadInto(string assetPath)
        {
            var hierarchy = PrefabHierarchyReader.Read(assetPath);
            _cache![hierarchy.Guid] = hierarchy;
        }

        private static bool RemoveByAssetPath(string assetPath)
        {
            string? guidToRemove = null;
            foreach (var kvp in _cache!)
            {
                if (string.Equals(kvp.Value.AssetPath, assetPath, StringComparison.Ordinal))
                {
                    guidToRemove = kvp.Key;
                    break;
                }
            }

            if (guidToRemove == null)
            {
                return false;
            }

            _cache.Remove(guidToRemove);
            return true;
        }

        private static bool Rebuild()
        {
            SceneBuilderPaths.EnsureGeneratedDirectory();
            var manifestPath = SceneBuilderPaths.FacadeManifestPath;
            var oldCatalog = TryReadExistingCatalog(manifestPath);

            var newCatalog = FacadeCatalogBuilder.Build(_cache!.Values.ToArray());
            var json = newCatalog.Serialize();
            var wrote = SceneBuilderPaths.WriteTextIfChanged(manifestPath, json);

            if (wrote && oldCatalog != null)
            {
                PatchBuildersForRename(oldCatalog, newCatalog);
            }

            return wrote;
        }

        // Reads + deserializes the prior on-disk manifest as the OLD catalog for rename-patching.
        // Never throws: absent or unparseable baseline means "no rename to reconcile" (first-ever
        // generation), so the caller skips patching.
        private static FacadeCatalog? TryReadExistingCatalog(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return FacadeCatalog.Deserialize(File.ReadAllText(manifestPath));
            }
            catch
            {
                return null;
            }
        }

        // Rewrites every live builder under SceneBuilders/ so a renamed-but-durable-stable façade
        // accessor keeps compiling. Routes through WriteIfChanged (not WriteTextIfChanged) — builder
        // .cs IS watched by the code->scene FileSystemWatcher, so a real write must record a
        // SuppressionScope self-echo drop.
        private static void PatchBuildersForRename(FacadeCatalog oldCatalog, FacadeCatalog newCatalog)
        {
            var buildersDirectory = SceneBuilderPaths.BuildersDirectory;
            if (!Directory.Exists(buildersDirectory))
            {
                return;
            }

            foreach (var builderPath in Directory.GetFiles(buildersDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(builderPath);
                var patched = FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog);
                SceneBuilderPaths.WriteIfChanged(builderPath, patched);
            }
        }

        // Both `.prefab` assets and imported-model main assets resolve to typeof(GameObject) — the
        // same test PrefabHierarchyReader.ReadAll's "t:Prefab t:Model" query relies on.
        private static bool IsPrefabOrModelAsset(string assetPath) =>
            AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(GameObject);
    }
}
