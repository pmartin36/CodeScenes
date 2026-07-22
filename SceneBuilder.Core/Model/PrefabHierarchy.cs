using System;
using System.Collections.Generic;

namespace SceneBuilder.Core.Model
{
    // Unity-free POCO the adapter fills (reads the prefab asset) and FacadeCatalogBuilder consumes.
    // One per project prefab. See specs/25-typed-prefab-facades.md §Additions.
    public sealed record PrefabHierarchy
    {
        /// <summary>The prefab asset's .meta GUID (AssetRef.Guid) — AUTHORITATIVE.</summary>
        public string Guid { get; init; } = "";

        /// <summary>Re-derivable display path; NON-authoritative.</summary>
        public string AssetPath { get; init; } = "";

        public PrefabHierarchyNode Root { get; init; } = new();
    }

    // One per GameObject inside the prefab.
    public sealed record PrefabHierarchyNode
    {
        /// <summary>The live object's real name (e.g. "Front Wheel") — sanitize source.</summary>
        public string RealName { get; init; } = "";

        /// <summary>
        /// The object's DURABLE prefab-internal fileID (GlobalObjectId.targetObjectId within the
        /// asset) — reorder/rename STABLE. The disambiguator + selector -> pair-key resolution key
        /// on THIS, never on name or sibling index.
        /// </summary>
        public long LocalId { get; init; }

        /// <summary>
        /// Non-null when this node is itself a nested-prefab instance; the generator COMPOSES that
        /// prefab's façade type here.
        /// </summary>
        public string? NestedPrefabGuid { get; init; }

        /// <summary>Ordered by the prefab asset's child order — this type does not itself sort.</summary>
        public IReadOnlyList<PrefabHierarchyNode> Children { get; init; } = Array.Empty<PrefabHierarchyNode>();
    }
}
