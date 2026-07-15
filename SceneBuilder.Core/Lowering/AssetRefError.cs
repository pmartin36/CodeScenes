namespace SceneBuilder.Core.Lowering
{
    // Located, never-null-coerced error for a GUID that maps to nothing (cache miss
    // AND resolver miss). Exact wording pinned by spec §7 — see research.md.
    public sealed record AssetRefError(
        string ObjectName, string ComponentType, string FieldName,
        string Guid, string LastKnownPath)
    {
        public string Message =>
            $"{ObjectName} > {ComponentType}.{FieldName}: asset {Guid} (was '{LastKnownPath}') not found";
    }
}
