using System;

namespace SceneBuilder.Core.Model
{
    // Identity-keyed on (Guid, FileId) ONLY — DisplayPath/TypeHint are non-authoritative
    // for equality (see research.md Blueprint/INTERFACES).
    public sealed record AssetRef
    {
        public string Guid { get; init; } = "";
        public long FileId { get; init; }
        public string TypeHint { get; init; } = "";
        public string DisplayPath { get; init; } = "";

        public bool Equals(AssetRef? other) =>
            other is not null
            && string.Equals(Guid, other.Guid, StringComparison.Ordinal)
            && FileId == other.FileId;

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Guid, StringComparer.Ordinal);
            hash.Add(FileId);
            return hash.ToHashCode();
        }
    }
}
