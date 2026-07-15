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

        // Sibling groups this file CANNOT distinguish: >= 2 same-named siblings under one parent with
        // neither a handle nor an explicit `.Id(...)`, so only their position tells them apart (§4).
        // Located per §7.
        //
        // Populated on EVERY Parse — there is no opt-in flag a caller can forget, because
        // BuilderParser.Parse is the ONE call both directions reach and this hazard is silent and
        // destructive in both. Parse does NOT throw on these: Sync must be able to parse an ambiguous
        // file in order to heal it by injecting `.Id(...)`. Detection lives here; the POLICY is the
        // consumer's — Build REFUSES (never guesses), Sync HEALS.
        public IReadOnlyList<Conflict> Ambiguities { get; init; } = new List<Conflict>();
    }
}
