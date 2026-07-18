using System.Collections.Generic;

namespace SceneBuilder.Core.Validation
{
    public abstract record AssetResolution
    {
        public sealed record Resolved(string Guid, long FileId, string TypeHint) : AssetResolution;

        public sealed record Unresolved(IReadOnlyList<string> Suggestions) : AssetResolution;

        // b3-t4: path resolved but the named sub-object was not found on it — distinct from
        // Unresolved (main path missing) so the diagnostic message differs.
        public sealed record SubAssetUnresolved(string SubAsset, IReadOnlyList<string> Available) : AssetResolution;

        public sealed record Ambiguous(IReadOnlyList<string> Candidates) : AssetResolution;

        public sealed record Deferred : AssetResolution;
    }
}
