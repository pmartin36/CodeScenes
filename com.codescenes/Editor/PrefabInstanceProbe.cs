#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Detects prefab-instance ROOTS in the live scene and reads their identity + opaque overrides —
    /// the single shared predicate/reader used by <see cref="SceneSnapshotReader"/> and
    /// <see cref="ChangeScopedSnapshot"/> so neither re-implements detection (see research.md b5-t2).
    /// </summary>
    internal static class PrefabInstanceProbe
    {
        /// <summary>True for the outermost root of a prefab instance in the live scene.</summary>
        internal static bool IsInstanceRoot(GameObject go) => PrefabUtility.IsAnyPrefabInstanceRoot(go);

        /// <summary>
        /// Reads the instance-root identity + opaque overrides. Callers MUST only invoke this when
        /// <see cref="IsInstanceRoot"/> is true.
        /// </summary>
        internal static (string? SourcePrefabGuid, PrefabInstanceKey Key, ValueNode.Unsupported? Overrides)
            ReadInstanceRoot(GameObject go)
        {
            string? guid = null;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go) as GameObject;
            if (source != null)
            {
                var path = AssetDatabase.GetAssetPath(source);
                if (!string.IsNullOrEmpty(path))
                {
                    guid = AssetDatabase.AssetPathToGUID(path);
                }
            }

            var goid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            var key = new PrefabInstanceKey { TargetPrefabId = goid.targetPrefabId, TargetObjectId = goid.targetObjectId };

            var overrides = ReadOpaqueOverrides(go, source);

            return (guid, key, overrides);
        }

        private static ValueNode.Unsupported? ReadOpaqueOverrides(GameObject go, GameObject? sourceRoot)
        {
            var mods = PrefabUtility.GetPropertyModifications(go);
            if (mods == null || mods.Length == 0)
            {
                return null;
            }

            var tokens = new List<string>();
            foreach (var mod in mods)
            {
                // PropertyModification.target refers to the CORRESPONDING OBJECT IN THE SOURCE
                // ASSET (not the live instance) — mods are stored as diffs against the template.
                // The root GameObject/name and the root Transform (position/rotation/scale/order)
                // are already modelled — excluding them here is the boundary between "modelled" and
                // "opaque" (not interpretation of override CONTENT). See research.md REFINED note.
                if (mod.target == null
                    || (sourceRoot != null && (mod.target == (Object)sourceRoot || mod.target == sourceRoot.transform)))
                {
                    continue;
                }

                tokens.Add(FormatModification(sourceRoot, mod));
            }

            if (tokens.Count == 0)
            {
                return null;
            }

            tokens.Sort(System.StringComparer.Ordinal);
            return new ValueNode.Unsupported(string.Join("\n", tokens));
        }

        private static string FormatModification(GameObject? root, PropertyModification mod)
        {
            var sb = new StringBuilder();
            sb.Append(RelativePath(root, mod.target));
            sb.Append('|');
            sb.Append(mod.target.GetType().FullName);
            sb.Append('|');
            sb.Append(mod.propertyPath);
            sb.Append('=');
            sb.Append(mod.value ?? "");
            if (mod.objectReference != null)
            {
                sb.Append('#');
                sb.Append(ObjectReferenceToken(mod.objectReference));
            }

            return sb.ToString();
        }

        // Structural path from the source-asset root to the modified target, independent of runtime
        // instance ids — stable across reads of the same override so the token doesn't spuriously
        // change when nothing about the override itself changed.
        private static string RelativePath(GameObject? root, Object target)
        {
            var t = target is GameObject g ? g.transform : (target as Component)?.transform;
            if (t == null)
            {
                return target.name;
            }

            if (root != null && t == root.transform)
            {
                return "";
            }

            var segments = new List<string>();
            var current = t;
            while (current != null && (root == null || current != root.transform))
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments);
        }

        private static string ObjectReferenceToken(Object reference)
        {
            var path = AssetDatabase.GetAssetPath(reference);
            if (!string.IsNullOrEmpty(path))
            {
                return AssetDatabase.AssetPathToGUID(path);
            }

            return reference.name;
        }
    }
}
