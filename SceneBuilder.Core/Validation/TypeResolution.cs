using System.Collections.Generic;

namespace SceneBuilder.Core.Validation
{
    public abstract record TypeResolution
    {
        public sealed record Resolved(string FullName) : TypeResolution;

        public sealed record Unresolved(IReadOnlyList<string> Suggestions) : TypeResolution;

        public sealed record Ambiguous(IReadOnlyList<string> Candidates) : TypeResolution;

        public sealed record Deferred : TypeResolution;
    }
}
