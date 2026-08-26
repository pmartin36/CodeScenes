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

        // b3-t1: one entry per parsed component, keyed by the component's LogicalId,
        // slicing the source to its `.Component<T>(...)` call. Kept SEPARATE from Anchors
        // (GameObject-only). Populated by BuilderParser (BuildComponentAnchors,
        // BuilderParser.cs:68), assigned at :103.
        public IReadOnlyDictionary<string, SourceSpan> ComponentAnchors { get; init; } = new Dictionary<string, SourceSpan>();

        // One entry per parsed node, keyed by the SAME final LogicalId as Anchors, recording
        // which of .Tag/.Layer/.Active/.Static physically appear in the node's builder chain.
        public IReadOnlyDictionary<string, FlagPresence> FlagPresence { get; init; } = new Dictionary<string, FlagPresence>();

        // outer key = component LogicalId, inner key = field key -> the value argument's
        // SourceSpan (b3-t2). Feed-forward for b5's span-local field-argument patching.
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> FieldArgumentSpans { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>();

        // b1-t1: one entry per parsed node with an AUTHORED handle (a `var x = ...`
        // declaration at the two ctx.Handles[handleName]=node registration spots), keyed by
        // the node's FINAL LogicalId, mapping to its handle (var) name. Closure-parameter
        // transient bindings (e.g. `m => ...`) must NOT appear here. Populated by BuilderParser
        // (BuildHandles, BuilderParser.cs:72), assigned at :103.
        public IReadOnlyDictionary<string, string> Handles { get; init; } = new Dictionary<string, string>();

        // One entry per parsed component, keyed by the component's FINAL LogicalId, mapping to its
        // handle (var) name — the component-space twin of Handles, populated from a
        // `.Ref<T>()`-captured var whose composed id matched an actual component. Populated by
        // BuilderParser (ResolveComponentRefs, BuilderParser.UnityEvents.cs) in ParseCore.
        public IReadOnlyDictionary<string, string> ComponentHandles { get; init; } = new Dictionary<string, string>();

        // b1-t2: one NodeAnchor per parsed node, pre-order/document order, NEVER collapsed
        // by LogicalId — two nodes resolving to the same LogicalId (a colliding hand-authored
        // `.Id(...)`) produce TWO entries here, unlike Anchors (a dict, which collapses them to
        // one). Populated by BuilderParser (BuildNodeAnchors, BuilderParser.cs:67), assigned at
        // :103. Feeds b1-t3 (DuplicateLogicalIdConflicts) and b3-t1 (IdCollisionHealer).
        public IReadOnlyList<NodeAnchor> NodeAnchors { get; init; } = new List<NodeAnchor>();

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

        // b1-t1 (unqualified-type-names): file-scope PLAIN `using` directives (no
        // `Alias`, no `StaticKeyword`), rendered dotted ("UnityEngine", "UnityEngine.UI"),
        // in document order. Source-level only — NEVER enters SceneModel/CanonicalJson/
        // identity; the adapter (b2) reads it to resolve short Component<T> names.
        // Population is BuilderParser's job (BuilderParser.cs:89,103, BuilderParser.Parse/
        // ParseCore).
        public IReadOnlyList<string> Usings { get; init; } = new List<string>();

        // b3-t5: the LogicalId of every parsed component whose source construct is a CHAINED call
        // (`.Component<T>`/`.FitSize`/`.AlignTo` inside an `Add`/`Instance` chain, a
        // multi-component chain, or an expression-bodied configure lambda) rather than its own
        // statement (`crate.Component<T>(...);`). A statement-scoped edit
        // (RemoveStatement/ReorderStatement) anchored on one of these must never resolve against
        // its ENCLOSING statement (see SourcePatchApplier.AnchorChain.cs). Populated by
        // BuilderParser.cs:102 via BuildChainedComponents/CollectChainedComponents
        // (BuilderParser.Projections.cs).
        public IReadOnlyCollection<string> ChainedComponents { get; init; } = new List<string>();

        // m8: componentLogicalId -> (fieldKey -> ORDERED per-listener call SourceSpan list), one
        // entry per parsed `.OnClick(...)`/`.OnEvent(...)` call, in source (scene) order — feeds
        // Reconcile's in-place listener patch/remove (index-matched against the snapshot's own
        // listener list). Populated by BuilderParser (BuildListenerCallSpans,
        // BuilderParser.Projections.cs) in ParseCore.
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> ListenerCallSpans { get; init; } =
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>>();
    }
}
