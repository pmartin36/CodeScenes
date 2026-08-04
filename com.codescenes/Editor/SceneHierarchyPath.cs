#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Identity;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE one owner of "path from the scene root" for a report's location. A sync has TWO
    /// id-spaces: the sidecar's PRE-sync LogicalIds, and the POST-batch ids Core anchors its
    /// reports on once a rewrite introduces a handle for a handle-less object, reparents one, or
    /// detects a brand-new scene node (<c>ComponentReconciler</c>'s <c>ownerEffectiveId</c>;
    /// <c>Reconciler.Rekey</c>; <c>ReconcilerAppends</c>'s new-node entries). Resolution is LAZY
    /// and per-report: the LogicalId-&gt;GlobalObjectId index and every
    /// GlobalObjectIdentifierToObjectSlow call happen only for a report actually reaching the
    /// console, never per scene object.
    /// </summary>
    public sealed class SceneHierarchyPath
    {
        private readonly IdentityMap _map;
        private readonly IReadOnlyList<IdentityMapEntry> _postBatchEntries;
        private Dictionary<string, string>? _goidByLogicalId;
        private readonly Dictionary<string, string?> _cache = new();

        /// <summary>
        /// <paramref name="postBatchEntries"/> is the reconcile's post-batch entries (e.g.
        /// <c>ReconcileResult.AddedEntries</c>), overlaid onto the pre-sync map so an anchor a sync
        /// itself introduces still resolves to a live path.
        /// </summary>
        public SceneHierarchyPath(IdentityMap map, IReadOnlyList<IdentityMapEntry> postBatchEntries)
        {
            _map = map;
            _postBatchEntries = postBatchEntries;
        }

        /// <summary>
        /// The live hierarchy path of the GameObject a report's LogicalId anchors on
        /// ("Panel/Fixture"), or null when the anchor has no mapped / no longer resolvable live
        /// object. A component LogicalId ("{owner}/{TypeFullName}#{n}") resolves to its OWNER.
        /// </summary>
        public string? Of(string? logicalId)
        {
            if (string.IsNullOrEmpty(logicalId))
            {
                return null;
            }

            if (_cache.TryGetValue(logicalId, out var cached))
            {
                return cached;
            }

            var result = Resolve(logicalId);
            _cache[logicalId] = result;
            return result;
        }

        private string? Resolve(string logicalId)
        {
            _goidByLogicalId ??= BuildIndex();

            if (_goidByLogicalId.TryGetValue(logicalId, out var goid))
            {
                return FromGlobalObjectId(goid);
            }

            var lastSegment = logicalId.Substring(logicalId.LastIndexOf('/') + 1);
            if (lastSegment.Contains('#'))
            {
                var owner = PlanExecutor.OwnerOf(logicalId);
                if (owner != null && _goidByLogicalId.TryGetValue(owner, out var ownerGoid))
                {
                    return FromGlobalObjectId(ownerGoid);
                }
            }

            return null;
        }

        // LogicalIdToGlobalObjectId returns a fresh dictionary (ToDictionary), so overlaying the
        // post-batch entries in place shares nothing with the reconcile's own index.
        private Dictionary<string, string> BuildIndex()
        {
            var index = SceneBuilder.Core.Identity.IdentityNodeIndex.LogicalIdToGlobalObjectId(_map);
            foreach (var entry in _postBatchEntries)
            {
                // IsMappedNode first: an added node with no live GlobalObjectId must not blank out
                // a good pre-sync value, and a Kind=="Component" entry never belongs in this index.
                if (SceneBuilder.Core.Identity.IdentityNodeIndex.IsMappedNode(entry))
                {
                    index[entry.LogicalId] = entry.GlobalObjectId; // post-batch wins
                }
            }

            return index;
        }

        private static string? FromGlobalObjectId(string goidString)
        {
            if (!GlobalObjectId.TryParse(goidString, out var goid))
            {
                return null;
            }

            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(goid) is GameObject go ? Of(go) : null;
        }

        /// <summary>The live hierarchy path of a GameObject the adapter is already holding.</summary>
        public static string Of(GameObject go) => PrefabInstanceProbe.RelativePath(null, go);
    }
}
