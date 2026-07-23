#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Reads a project prefab (or imported model prefab) off the AssetDatabase into the Unity-free
    /// <see cref="PrefabHierarchy"/> POCO (spec 25). Pure Unity-boundary reader — no façade logic
    /// (sanitize/dedup/manifest is <c>FacadeCatalogBuilder</c> / <c>PrefabFacadeManifestGenerator</c>).
    /// </summary>
    internal static class PrefabHierarchyReader
    {
        /// <summary>
        /// Reads one prefab by asset path. Loads via <see cref="AssetDatabase.LoadAssetAtPath{T}"/> —
        /// NOT <c>PrefabUtility.LoadPrefabContents</c>, which returns preview-scene copies whose
        /// objects carry no durable prefab-internal fileID. The returned asset is a shared cached
        /// object; callers MUST NOT <c>DestroyImmediate</c> it.
        /// </summary>
        internal static PrefabHierarchy Read(string assetPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
            {
                throw new InvalidOperationException(
                    $"[SceneBuilder] '{assetPath}' is not a loadable GameObject/model prefab.");
            }

            return new PrefabHierarchy
            {
                Guid = AssetDatabase.AssetPathToGUID(assetPath),
                AssetPath = assetPath,
                Root = ReadNode(root.transform, assetPath),
            };
        }

        /// <summary>
        /// Every project <c>.prefab</c> union every imported model prefab (an FBX/model main asset is
        /// a model prefab, M6), deduped by GUID. Multiple <c>t:</c> filters in one
        /// <see cref="AssetDatabase.FindAssets(string)"/> query are OR'd by Unity, so one query covers
        /// both sources.
        /// </summary>
        internal static IReadOnlyList<PrefabHierarchy> ReadAll()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab t:Model");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<PrefabHierarchy>();

            foreach (var guid in guids)
            {
                if (!seen.Add(guid))
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                {
                    // Matched the type filter but has no GameObject main asset (e.g. a Model asset
                    // whose main representation isn't a GameObject) — not a prefab we can enumerate.
                    continue;
                }

                results.Add(Read(path));
            }

            return results;
        }

        private static PrefabHierarchyNode ReadNode(Transform t, string ownerAssetPath)
        {
            var go = t.gameObject;

            long localId = 0;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out _, out var id))
            {
                localId = id;
            }

            var nestedPrefabGuid = ResolveNestedPrefabGuid(go, ownerAssetPath);

            IReadOnlyList<PrefabHierarchyNode> children;
            if (nestedPrefabGuid != null)
            {
                // Nested-prefab-instance root: STOP here. The nested subtree is composed BY REFERENCE
                // from that prefab's own PrefabHierarchy downstream (FacadeCatalogBuilder), never
                // inlined — inlining would duplicate the tree and reintroduce cross-prefab type-name
                // collisions the per-prefab container exists to prevent.
                children = Array.Empty<PrefabHierarchyNode>();
            }
            else
            {
                var childCount = t.childCount;
                var list = new List<PrefabHierarchyNode>(childCount);
                for (var i = 0; i < childCount; i++)
                {
                    list.Add(ReadNode(t.GetChild(i), ownerAssetPath));
                }

                children = list;
            }

            return new PrefabHierarchyNode
            {
                RealName = go.name,
                LocalId = localId,
                NestedPrefabGuid = nestedPrefabGuid,
                Children = children,
            };
        }

        // Non-null exactly when `go` is the topmost node of a contiguous nested-prefab-instance
        // subtree: GetCorrespondingObjectFromSource(go) resolves into a DIFFERENT asset than the one
        // being read. Callers never recurse past a detected nested root, so a node reached here always
        // has a parent belonging to the OWNER asset (never to an already-detected nested instance).
        private static string? ResolveNestedPrefabGuid(GameObject go, string ownerAssetPath)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go) as GameObject;
            if (source == null)
            {
                return null;
            }

            var sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath) || sourcePath == ownerAssetPath)
            {
                return null;
            }

            // At nesting depth >= 2, GetCorrespondingObjectFromSource can skip levels in the prefab
            // chain. Re-resolve against the candidate path so NestedPrefabGuid always names the
            // IMMEDIATE nested source, not a deeper ancestor.
            var atCandidate = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(go, sourcePath) as GameObject;
            var resolvedPath = atCandidate != null ? AssetDatabase.GetAssetPath(atCandidate) : sourcePath;
            if (string.IsNullOrEmpty(resolvedPath))
            {
                resolvedPath = sourcePath;
            }

            return AssetDatabase.AssetPathToGUID(resolvedPath);
        }
    }
}
