using System.Collections.Generic;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Tests
{
    internal static class SourcePatchTestHelpers
    {
        // Merges GameObject Anchors with component Anchors, mirroring the merged dict the
        // Reconcile caller passes to Apply (research DATA_FLOW).
        internal static Dictionary<string, SourceSpan> MergeAnchors(SceneBuilder.Core.Parsing.ParseResult parsed)
        {
            var merged = new Dictionary<string, SourceSpan>(parsed.Anchors);
            foreach (var kv in parsed.ComponentAnchors)
            {
                merged[kv.Key] = kv.Value;
            }
            return merged;
        }
    }
}
