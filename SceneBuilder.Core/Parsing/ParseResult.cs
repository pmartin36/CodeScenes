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

        // b3-t1 stub: one entry per parsed component, keyed by the component's LogicalId,
        // slicing the source to its `.Component<T>(...)` call. Kept SEPARATE from Anchors
        // (GameObject-only). Population is BuilderParser's job (CollectComponentAnchors);
        // this default (always empty) is a compile-only stub for the test-writer's RED tests.
        public IReadOnlyDictionary<string, SourceSpan> ComponentAnchors { get; init; } = new Dictionary<string, SourceSpan>();

        // One entry per parsed node, keyed by the SAME final LogicalId as Anchors, recording
        // which of .Tag/.Layer/.Active/.Static physically appear in the node's builder chain.
        public IReadOnlyDictionary<string, FlagPresence> FlagPresence { get; init; } = new Dictionary<string, FlagPresence>();

        // outer key = component LogicalId, inner key = field key -> the value argument's
        // SourceSpan (b3-t2). Feed-forward for b5's span-local field-argument patching.
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldArgumentSpans { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>();

        // b1-t1 stub: one entry per parsed node with an AUTHORED handle (a `var x = ...`
        // declaration at the two ctx.Handles[handleName]=node registration spots), keyed by
        // the node's FINAL LogicalId, mapping to its handle (var) name. Closure-parameter
        // transient bindings (e.g. `m => ...`) must NOT appear here. Population is
        // BuilderParser's job (BuildHandles/CollectHandles); this default (always empty) is
        // a compile-only stub for the test-writer's RED tests.
        public IReadOnlyDictionary<string, string> Handles { get; init; } = new Dictionary<string, string>();
    }
}
