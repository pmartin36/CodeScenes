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
    /// Regenerates <see cref="SceneBuilderPaths.AssetManifestPath"/> — the typed asset-catalog
    /// manifest (spec 28) — as plain <see cref="System.IO.File"/> IO outside <c>Assets/</c>, so it
    /// never triggers a domain reload. Mirrors <see cref="PrefabFacadeManifestGenerator"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PrefabFacadeManifestGenerator"/> (which caches because its per-prefab READ
    /// is expensive), the asset scan is a cheap <see cref="AssetDatabase.FindAssets(string)"/> pass
    /// per type group, so every regen is a stateless full rebuild — no per-GUID cache to invalidate.
    /// </remarks>
    [InitializeOnLoad]
    public class AssetCatalogGenerator : AssetPostprocessor
    {
        static AssetCatalogGenerator()
        {
            // Defer past editor load so the first regen never fights initial-domain-load work.
            EditorApplication.delayCall += () => Regenerate();
        }

        // (Group, FindAssets filter, the Unity runtime type an object must match to belong to it,
        // MainOnly = the group never has sub-objects so the scan must NOT call
        // AssetDatabase.LoadAllAssetsAtPath — for a Scene asset in particular that call is
        // unsupported (fires the native "Do not use ReadObjectThreaded on scene objects!" error)
        // and must never be issued, so Scenes stays main-asset-only.
        private static readonly (string Group, string Query, Type Type, bool MainOnly)[] Groups =
        {
            ("Materials", "t:Material", typeof(Material), true),
            ("Meshes", "t:Mesh", typeof(Mesh), false),
            ("Sprites", "t:Sprite", typeof(Sprite), false),
            ("Scenes", "t:SceneAsset", typeof(SceneAsset), true),
            ("ScriptableObjects", "t:ScriptableObject", typeof(ScriptableObject), true),
            ("AnimationClips", "t:AnimationClip", typeof(AnimationClip), false),
        };

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // Smallest correct change: never misses an add/move/delete/rename. WriteTextIfChanged
            // makes an unchanged rebuild a no-op write, so the watcher/IDE is not churned. Scoping
            // the scan to the touched asset type is a deferred optimization (spec §Risks), not v0.
            Regenerate();
        }

        /// <summary>
        /// Scans the AssetDatabase for every v0 group and builds the deterministic, type-first
        /// folder-path catalog. Testable pure-ish scan → <see cref="AssetCatalogBuilder.Build"/>.
        /// </summary>
        public static AssetCatalog BuildCatalog()
        {
            var raw = new List<AssetCatalogEntry>();
            var seen = new HashSet<(string Guid, long FileId)>();

            foreach (var (group, query, type, mainOnly) in Groups)
            {
                ScanGroup(group, query, type, mainOnly, raw, seen);
            }

            return AssetCatalogBuilder.Build(raw);
        }

        private static void ScanGroup(
            string group,
            string findAssetsQuery,
            Type type,
            bool mainOnly,
            List<AssetCatalogEntry> raw,
            HashSet<(string Guid, long FileId)> seen)
        {
            foreach (var guid in AssetDatabase.FindAssets(findAssetsQuery))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    continue; // Packages/ or a built-in — not a project asset the catalog covers.
                }

                var containerGuid = AssetDatabase.AssetPathToGUID(path);
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);

                if (mainOnly)
                {
                    // Never LoadAllAssetsAtPath a main-only group — unsupported (and log-erroring)
                    // for a Scene asset, and pointless for the others (no sub-objects to find).
                    if (mainAsset != null && type.IsInstanceOfType(mainAsset)
                        && seen.Add((containerGuid, 0L)))
                    {
                        raw.Add(new AssetCatalogEntry
                        {
                            Group = group,
                            FolderSegments = FolderSegmentsFor(path),
                            MemberName = "",
                            Guid = containerGuid,
                            FileId = 0,
                            Path = path,
                            SubAsset = "",
                        });
                    }

                    continue;
                }

                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (obj == null || !type.IsInstanceOfType(obj))
                    {
                        continue;
                    }

                    long fileId;
                    string subAsset;

                    if (ReferenceEquals(mainAsset, obj))
                    {
                        fileId = 0;
                        subAsset = "";
                    }
                    else if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var subGuid, out fileId)
                             && !string.IsNullOrEmpty(subGuid))
                    {
                        subAsset = obj.name;
                    }
                    else
                    {
                        continue; // Unidentifiable sub-object — skip rather than emit a bogus entry.
                    }

                    if (!seen.Add((containerGuid, fileId)))
                    {
                        continue;
                    }

                    raw.Add(new AssetCatalogEntry
                    {
                        Group = group,
                        FolderSegments = FolderSegmentsFor(path),
                        MemberName = "",
                        Guid = containerGuid,
                        FileId = fileId,
                        Path = path,
                        SubAsset = subAsset,
                    });
                }
            }
        }

        // The container path's directory with the leading "Assets" segment dropped. Empty for an
        // asset directly under Assets/.
        private static string[] FolderSegmentsFor(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
            if (string.IsNullOrEmpty(dir))
            {
                return Array.Empty<string>();
            }

            var parts = dir.Split('/');
            return parts.Length <= 1 ? Array.Empty<string>() : parts.Skip(1).ToArray();
        }

        /// <summary>
        /// Full rebuild + patch tail. Returns true iff bytes on disk actually changed.
        /// </summary>
        public static bool Regenerate()
        {
            SceneBuilderPaths.EnsureGeneratedDirectory();
            var manifestPath = SceneBuilderPaths.AssetManifestPath;
            var oldCatalog = TryReadExistingCatalog(manifestPath);

            var newCatalog = BuildCatalog();
            var json = newCatalog.Serialize();
            var wrote = SceneBuilderPaths.WriteTextIfChanged(manifestPath, json);

            if (wrote && oldCatalog != null)
            {
                PatchBuildersForRenameOrMove(oldCatalog, newCatalog);
            }

            return wrote;
        }

        // Reads + deserializes the prior on-disk manifest as the OLD catalog for the patch pass.
        // Never throws: absent or unparseable baseline means "no rename/move to reconcile" (first-
        // ever generation), so the caller skips patching.
        private static AssetCatalog? TryReadExistingCatalog(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return AssetCatalog.Deserialize(File.ReadAllText(manifestPath));
            }
            catch
            {
                return null;
            }
        }

        // Rewrites every live builder under SceneBuilders/ so a renamed OR moved-but-durable-stable
        // typed member keeps compiling. Routes through WriteIfChanged (not WriteTextIfChanged) —
        // builder .cs IS watched by the code->scene FileSystemWatcher, so a real write must record a
        // SuppressionScope self-echo drop.
        private static void PatchBuildersForRenameOrMove(AssetCatalog oldCatalog, AssetCatalog newCatalog)
        {
            var buildersDirectory = SceneBuilderPaths.BuildersDirectory;
            if (!Directory.Exists(buildersDirectory))
            {
                return;
            }

            foreach (var builderPath in Directory.GetFiles(buildersDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(builderPath);
                var patched = AssetReferencePatcher.Patch(source, oldCatalog, newCatalog);
                SceneBuilderPaths.WriteIfChanged(builderPath, patched);
            }
        }
    }
}
