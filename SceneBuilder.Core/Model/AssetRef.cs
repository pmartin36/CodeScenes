using System;

namespace SceneBuilder.Core.Model
{
    // Identity-keyed on (Guid, FileId, IsBuiltin) — DisplayPath/TypeHint are non-authoritative
    // for equality. IsBuiltin discriminates the built-in namespace from the project-path
    // namespace: post-lowering the GUID already implies it, so it never splits two equal refs;
    // it exists so two UNRESOLVED refs (Guid == "") — Asset("Cube") vs Builtin("Cube") — cannot
    // collide. When IsBuiltin is true, DisplayPath carries the built-in object NAME ("Cube"), not
    // a project path, and TypeHint carries the authored type qualifier (empty when the bare name
    // suffices). See spec §Core deliverables (specs/17-builtin-resources.md).
    public sealed record AssetRef
    {
        public string Guid { get; init; } = "";
        public long FileId { get; init; }
        public string TypeHint { get; init; } = "";
        public string DisplayPath { get; init; } = "";
        public bool IsBuiltin { get; init; }

        public bool Equals(AssetRef? other) =>
            other is not null
            && string.Equals(Guid, other.Guid, StringComparison.Ordinal)
            && FileId == other.FileId
            && IsBuiltin == other.IsBuiltin;

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Guid, StringComparer.Ordinal);
            hash.Add(FileId);
            hash.Add(IsBuiltin);
            return hash.ToHashCode();
        }
    }
}
