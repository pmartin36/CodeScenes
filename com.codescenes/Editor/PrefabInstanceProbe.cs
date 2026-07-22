#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Detects prefab-instance ROOTS in the live scene and reads their identity + overrides —
    /// the single shared predicate/reader used by <see cref="SceneSnapshotReader"/> and
    /// <see cref="ChangeScopedSnapshot"/> so neither re-implements detection (see research.md b5-t2).
    /// The structured-override classification (M10) lives in the <c>.Overrides.cs</c> partial.
    /// </summary>
    internal static partial class PrefabInstanceProbe
    {
        /// <summary>True for the outermost root of a prefab instance in the live scene.</summary>
        internal static bool IsInstanceRoot(GameObject go) => PrefabUtility.IsAnyPrefabInstanceRoot(go);

        /// <summary>
        /// The full result of reading an instance root: identity (<see cref="SourcePrefabGuid"/>,
        /// <see cref="Key"/>), the M6 opaque residue for below-root targets, and the M10 structured
        /// collections for root-target overrides. A named struct (not a tuple) so callers read by
        /// name, not position.
        /// </summary>
        internal readonly struct InstanceReadResult
        {
            internal readonly string? SourcePrefabGuid;
            internal readonly PrefabInstanceKey Key;
            internal readonly ValueNode.Unsupported? OpaqueOverrides;
            internal readonly PropertyOverride[] StructuredOverrides;
            internal readonly AddedComponent[] AddedComponents;
            internal readonly OverrideTarget[] RemovedComponents;

            internal InstanceReadResult(
                string? sourcePrefabGuid,
                PrefabInstanceKey key,
                ValueNode.Unsupported? opaqueOverrides,
                PropertyOverride[] structuredOverrides,
                AddedComponent[] addedComponents,
                OverrideTarget[] removedComponents)
            {
                SourcePrefabGuid = sourcePrefabGuid;
                Key = key;
                OpaqueOverrides = opaqueOverrides;
                StructuredOverrides = structuredOverrides;
                AddedComponents = addedComponents;
                RemovedComponents = removedComponents;
            }
        }

        /// <summary>
        /// Reads the instance-root identity + overrides. Callers MUST only invoke this when
        /// <see cref="IsInstanceRoot"/> is true. <paramref name="resolveSceneRef"/> (M5, see
        /// <see cref="ObjectReferenceResolver.BuildSceneRefResolver"/>) lowers an in-scene
        /// objectReference override's target; null leaves it <c>Unsupported</c> (build path).
        /// </summary>
        internal static InstanceReadResult ReadInstanceRoot(GameObject go, Func<UnityEngine.Object, string?>? resolveSceneRef = null)
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

            var overrides = ReadOverrides(go, source, resolveSceneRef);

            return new InstanceReadResult(
                guid, key, overrides.OpaqueOverrides, overrides.StructuredOverrides,
                overrides.AddedComponents, overrides.RemovedComponents);
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
        private static string RelativePath(GameObject? root, UnityEngine.Object target)
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

        private static string ObjectReferenceToken(UnityEngine.Object reference)
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
