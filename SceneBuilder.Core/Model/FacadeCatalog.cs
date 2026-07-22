using System;
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
