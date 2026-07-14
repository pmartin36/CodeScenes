using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    public sealed class ParseResult
    {
        public SceneModel Model { get; init; } = new();

        public IdentityMap IdentityMap { get; init; } = new();

        public IReadOnlyDictionary<string, SourceSpan> Anchors { get; init; } = new Dictionary<string, SourceSpan>();

        // One entry per parsed node, keyed by the SAME final LogicalId as Anchors, recording
        // which of .Tag/.Layer/.Active/.Static physically appear in the node's builder chain.
        public IReadOnlyDictionary<string, FlagPresence> FlagPresence { get; init; } = new Dictionary<string, FlagPresence>();
    }
}
