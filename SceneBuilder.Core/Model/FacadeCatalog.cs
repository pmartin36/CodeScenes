using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Core.Model
{
    // The on-disk contract for Prefabs.sbfacade.json (FacadeManifest.FileName). Mirrors
    // ProjectCatalogManifest's shape/serialization conventions. See specs/25-typed-prefab-facades.md.
    public sealed record FacadeCatalog
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyOrder(1)]
        public FacadeEntry[] Entries { get; init; } = Array.Empty<FacadeEntry>();

        public string Serialize() => CanonicalJson.Serialize(this);

        public static FacadeCatalog Deserialize(string json) => CanonicalJson.Deserialize<FacadeCatalog>(json);

        /// <summary>
        /// Guid -> PropertyName reverse lookup (used by Reconcile emit, b4-t4). Ordinal match,
        /// first hit.
        /// </summary>
        public bool TryGetPropertyName(string guid, out string propertyName)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                foreach (var entry in Entries)
                {
                    if (string.Equals(entry.Guid, guid, StringComparison.Ordinal))
                    {
                        propertyName = entry.PropertyName;
                        return true;
                    }
                }
            }

            propertyName = "";
            return false;
        }

        /// <summary>
        /// PropertyName -> Guid forward lookup (used by Parse, b4-t1). Ordinal match, first hit.
        /// </summary>
        public bool TryGetGuid(string propertyName, out string guid)
        {
            if (!string.IsNullOrEmpty(propertyName))
            {
                foreach (var entry in Entries)
                {
                    if (string.Equals(entry.PropertyName, propertyName, StringComparison.Ordinal))
                    {
                        guid = entry.Guid;
                        return true;
                    }
                }
            }

            guid = "";
            return false;
        }

        /// <summary>
        /// Guid -> FacadeEntry lookup (used by Parse's `.On` resolution, b4-t2). Ordinal match,
        /// first hit.
        /// </summary>
        public bool TryGetEntryByGuid(string guid, out FacadeEntry entry)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                foreach (var candidate in Entries)
                {
                    if (string.Equals(candidate.Guid, guid, StringComparison.Ordinal))
                    {
                        entry = candidate;
                        return true;
                    }
                }
            }

            entry = new FacadeEntry();
            return false;
        }

        /// <summary>
        /// Resolves a `.On` selector's segments (typed member-chain PropertyName segments, or
        /// string path RealName segments) against the entry for `prefabGuid`, walking
        /// `FacadeNode.Children` one segment at a time. Builds `childPath` from the matched
        /// children's RealName (never the sanitized PropertyName), joined by '/'; `localId` is
        /// the leaf child's durable id. False (childPath="", localId=0) on an empty segment list,
        /// an unresolved entry, or any unmatched segment — never throws.
        /// </summary>
        public bool TryResolveSelector(string prefabGuid, IReadOnlyList<string> segments, bool byPropertyName, out string childPath, out long localId)
        {
            childPath = "";
            localId = 0;

            if (segments.Count == 0 || !TryGetEntryByGuid(prefabGuid, out var entry))
            {
                return false;
            }

            var node = entry.Root;
            var realNames = new List<string>(segments.Count);
            FacadeChild? matched = null;

            foreach (var segment in segments)
            {
                matched = null;
                foreach (var child in node.Children)
                {
                    var candidateKey = byPropertyName ? child.PropertyName : child.RealName;
                    if (string.Equals(candidateKey, segment, StringComparison.Ordinal))
                    {
                        matched = child;
                        break;
                    }
                }

                if (matched == null)
                {
                    return false;
                }

                realNames.Add(matched.RealName);
                node = matched.Node;
            }

            childPath = string.Join("/", realNames);
            localId = matched!.LocalId;
            return true;
        }
    }

    public sealed record FacadeEntry
    {
        [JsonPropertyOrder(0)]
        public string PropertyName { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string Guid { get; init; } = "";

        [JsonPropertyOrder(2)]
        public FacadeNode Root { get; init; } = new();
    }

    public sealed record FacadeNode
    {
        [JsonPropertyOrder(0)]
        public string TypeName { get; init; } = "";

        [JsonPropertyOrder(1)]
        public FacadeChild[] Children { get; init; } = Array.Empty<FacadeChild>();
    }

    public sealed record FacadeChild
    {
        [JsonPropertyOrder(0)]
        public string PropertyName { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string RealName { get; init; } = "";

        [JsonPropertyOrder(2)]
        public long LocalId { get; init; }

        [JsonPropertyOrder(3)]
        public FacadeNode Node { get; init; } = new();
    }
}
