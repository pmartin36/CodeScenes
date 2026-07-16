using System.Collections.Generic;

namespace SceneBuilder.Core.Validation
{
    public abstract record AssetResolution
    {
        public sealed record Resolved(string Guid, long FileId, string TypeHint) : AssetResolution;

        public sealed record Unresolved(IReadOnlyList<string> Suggestions) : AssetResolution;

        public sealed record Ambiguous(IReadOnlyList<string> Candidates) : AssetResolution;

        public sealed record Deferred : AssetResolution;
    }
}
