#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Regenerates <see cref="SceneBuilderPaths.ProjectCatalogManifestPath"/> — a snapshot of the
    /// live project's named tags/layers — as plain <see cref="System.IO.File"/> IO outside
    /// <c>Assets/</c>, so it never triggers a domain reload. b5-t2 injects the manifest path into the
    /// analyzer toolkit as an <c>&lt;AdditionalFiles&gt;</c> input; b3-t2's source generator reads it.
    /// </summary>
    [InitializeOnLoad]
    public class ProjectCatalogGenerator : AssetPostprocessor
    {
        static ProjectCatalogGenerator()
        {
            // Defer past editor load so the first regen never fights initial-domain-load work.
            EditorApplication.delayCall += () => Regenerate();
        }

        private const string TagManagerAssetPath = "ProjectSettings/TagManager.asset";

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var imported in importedAssets)
            {
                if (string.Equals(imported, TagManagerAssetPath, StringComparison.Ordinal))
                {
                    Regenerate();
                    return;
                }
            }
        }

        /// <summary>
        /// Snapshots the live project's named tags/layers into a <see cref="ProjectCatalogManifest"/>.
        /// Layers are named-only (empty slots skipped) and ordered by their real slot index; tags keep
        /// TagManager order.
        /// </summary>
        public static ProjectCatalogManifest BuildManifest()
        {
            var tags = InternalEditorUtility.tags;

            var layers = new List<LayerEntry>();
            foreach (var name in InternalEditorUtility.layers)
            {
                var index = LayerMask.NameToLayer(name);
                if (index < 0)
                {
                    continue;
                }

                layers.Add(new LayerEntry { Index = index, Name = name });
            }

            layers.Sort((a, b) => a.Index.CompareTo(b.Index));

            return new ProjectCatalogManifest
            {
                Tags = tags,
                Layers = layers.ToArray(),
            };
        }

        /// <summary>
        /// Rebuilds the manifest from the live project and writes it via the compare-before-write
        /// primitive. Returns true iff bytes on disk actually changed.
        /// </summary>
        public static bool Regenerate()
        {
            SceneBuilderPaths.EnsureGeneratedDirectory();
            var manifest = BuildManifest();
            var json = CanonicalJson.Serialize(manifest);
            return SceneBuilderPaths.WriteTextIfChanged(SceneBuilderPaths.ProjectCatalogManifestPath, json);
        }
    }
}
