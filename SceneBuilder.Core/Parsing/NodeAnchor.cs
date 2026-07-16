using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Parsing
{
    // One entry per parsed node (GameObject), pre-order/document order, NEVER collapsed by
    // LogicalId — two nodes resolving to the same LogicalId (a colliding hand-authored
    // `.Id(...)`) produce TWO entries here, unlike ParseResult.Anchors (a dict, which
    // silently collapses them to one). Feeds b1-t3 (DuplicateLogicalIdConflicts) and b3-t1
    // (IdCollisionHealer).
    public sealed record NodeAnchor
    {
        public string LogicalId { get; init; } = "";

        public string Name { get; init; } = "";

        // The node's `Add(...)` invocation span — the same value ParseResult.Anchors carries.
        public SourceSpan Span { get; init; }

        // The authored `var` handle name, else null.
        public string? Handle { get; init; }

        // The span of the `.Id("...")` invocation (dot through closing paren), else null when
        // the node carries no explicit `.Id(...)` call.
        public SourceSpan? IdCallSpan { get; init; }
    }
}
