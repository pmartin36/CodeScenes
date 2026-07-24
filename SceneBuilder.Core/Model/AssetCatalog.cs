using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Serialization;

namespace SceneBuilder.Core.Model
{
    // The on-disk contract for Assets.sbassets.json (AssetManifest.FileName). Mirrors
    // FacadeCatalog's shape/serialization conventions. See specs/28-typed-asset-catalogs.md.
    public sealed record AssetCatalog
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyOrder(1)]
        public AssetCatalogEntry[] Entries { get; init; } = Array.Empty<AssetCatalogEntry>();

        public string Serialize() => CanonicalJson.Serialize(this);

        public static AssetCatalog Deserialize(string json) => CanonicalJson.Deserialize<AssetCatalog>(json);

        /// <summary>
        /// Forward (Parse, b2): group + folder segments + member name -> entry. Ordinal
        /// first-hit; FolderSegments must sequence-equal (length + per-element Ordinal).
        /// </summary>
        public bool TryGetEntry(string group, IReadOnlyList<string> folderSegments, string member, out AssetCatalogEntry entry)
        {
            foreach (var candidate in Entries)
            {
                if (string.Equals(candidate.Group, group, StringComparison.Ordinal)
                    && string.Equals(candidate.MemberName, member, StringComparison.Ordinal)
                    && FolderSegmentsEqual(candidate.FolderSegments, folderSegments))
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = new AssetCatalogEntry();
            return false;
        }

        /// <summary>
        /// Reverse (emit b4 + patcher b3): (guid, fileId) -> (group, folderSegments, member).
        /// Ordinal first-hit compound key; guid must be non-empty to match.
        /// </summary>
        public bool TryGetMember(string guid, long fileId, out string group, out string[] folderSegments, out string member)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                foreach (var candidate in Entries)
                {
                    if (candidate.FileId == fileId && string.Equals(candidate.Guid, guid, StringComparison.Ordinal))
                    {
                        group = candidate.Group;
                        folderSegments = candidate.FolderSegments;
                        member = candidate.MemberName;
                        return true;
                    }
                }
            }

            group = "";
            folderSegments = Array.Empty<string>();
            member = "";
            return false;
        }

        private static bool FolderSegmentsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed record AssetCatalogEntry
    {
        [JsonPropertyOrder(0)]
        public string Group { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string[] FolderSegments { get; init; } = Array.Empty<string>();

        [JsonPropertyOrder(2)]
        public string MemberName { get; init; } = "";

        [JsonPropertyOrder(3)]
        public string Guid { get; init; } = "";

        [JsonPropertyOrder(4)]
        public long FileId { get; init; }

        [JsonPropertyOrder(5)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(6)]
        public string SubAsset { get; init; } = "";
    }
}
