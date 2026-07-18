using System.Collections.Generic;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Reconcile
{
    public abstract record SourceEdit
    {
        public string Anchor { get; init; } = "";
    }

    public sealed record PatchArgument : SourceEdit
    {
        public string ArgName { get; init; } = "";
        public string NewExpr { get; init; } = "";
    }

    public sealed record MoveStatement : SourceEdit
    {
        // Parent LogicalId to move under; null => move to the scene root (receiver = scene param).
        public string? NewParentAnchor { get; init; }

        // Scene sibling index the moved object occupies under its NEW parent. A move is a
        // re-placement, so it owns its destination index for the same reason ReorderStatement does:
        // emitting the right parent at the wrong index does not round-trip.
        public int NewSiblingIndex { get; init; }

        // Receiver variable of the new parent (an existing handle, or one introduced this batch).
        // null => root move. Mirrors AppendStatement.ParentHandle.
        public string? NewParentHandle { get; init; }

        // true => the (currently handle-less) new-parent statement must be rewritten to declare
        // NewParentHandle. Mirrors AppendStatement.IntroduceParentHandle; both are honoured by the
        // applier's single handle-introduction pre-pass, so one parent never gets two handles.
        public bool IntroduceNewParentHandle { get; init; }
    }

    public sealed record ReorderStatement : SourceEdit
    {
        // Scene sibling index among the anchor's peers — its parent's children for a GameObject
        // anchor, its owner's components for a Component anchor. NOT a C# block index.
        public int NewSiblingIndex { get; init; }
    }

    public sealed record AppendStatement : SourceEdit
    {
        // Predicted synthesized/handle LogicalId for the created object.
        // Equals the AddedEntry.LogicalId the Reconciler emits (b1-t2 / b2-t1),
        // and equals what BuilderParser assigns after the Applier rewrites source (b4).
        public string NewLogicalId { get; init; } = "";

        // Parent LogicalId to insert under; null => root append (receiver = scene param).
        public string? ParentAnchor { get; init; }

        // Scene sibling index the created object occupies under its parent. The applier places the
        // statement by this rather than "right after the parent"; ignoring it emits the object at
        // sibling index 0 whatever the scene says, and the next sync re-Reorders it.
        public int NewSiblingIndex { get; init; }

        public string Name { get; init; } = "";

        // Non-default GameObject data. null => omit from emitted source (keep it clean).
        public TransformData? Transform { get; init; }
        public bool? Active { get; init; }
        public string? Tag { get; init; }
        public int? Layer { get; init; }
        public bool? IsStatic { get; init; }

        // Variable name THIS statement declares (`var <Handle> = ...`) when it heads a
        // subtree whose descendants reference it (b2-t3). null => no declaration.
        // When set, Handle == NewLogicalId.
        public string? Handle { get; init; }

        // Receiver variable for a CHILD append (the parent's handle, existing or newly
        // introduced). null for a root append.
        public string? ParentHandle { get; init; }

        // true => the (currently handle-less) parent statement must be rewritten to declare
        // ParentHandle before the child is appended (b2-t2 reconcile / b3-t3 apply).
        public bool IntroduceParentHandle { get; init; }

        // m6-b4-t1: non-null => this append is a prefab-instance root; render as
        // `<receiver>.Instance(SourcePrefabPath)` instead of `<receiver>.Add(Name)`, suppressing
        // Tag/Layer/Active/IsStatic (InstanceHandle exposes no such calls). Path re-derived from
        // SourcePrefabGuid via identityMap.Assets at emit time (Reconciler), not rendered here.
        public string? SourcePrefabPath { get; init; }
    }

    public sealed record RemoveStatement : SourceEdit
    {
        // Uses inherited SourceEdit.Anchor = the LogicalId of the statement to delete.
    }

    // Rewrites an anchored (currently handle-less) statement into `var <Handle> = ...;` on its own,
    // without requiring an accompanying Append/Move/AppendComponent to carry the introduce flag.
    // Emitted by the Reconciler/Healer (b2/b3) when a statement must gain a handle before a LATER
    // edit (in the SAME or a subsequent patch) can reference it. Resolved by the applier's existing
    // ResolveHandleIntroductions pre-pass (SourcePatchApplier.cs), which dedups against any other
    // handle request for the same anchor and throws on a conflicting handle.
    public sealed record IntroduceHandle : SourceEdit
    {
        // Inherited Anchor = the LogicalId of the statement to rewrite (its id BEFORE the rekey,
        // which is what `anchors` is still keyed by).
        public string Handle { get; init; } = "";
    }

    public enum FlagKind { Tag, Layer, Active, Static }

    public sealed record PatchFlagArgument : SourceEdit
    {
        public FlagKind Flag { get; init; }
        public string NewExpr { get; init; } = "";
    }

    public sealed record IntroduceFlagCall : SourceEdit
    {
        public FlagKind Flag { get; init; }
        public string? ArgExpr { get; init; }
    }

    public sealed record RemoveFlagCall : SourceEdit
    {
        public FlagKind Flag { get; init; }
    }

    // §13 attach + mapped-owner add: append `owner.Component<T>(c => c.Set(...));`
    public sealed record AppendComponentStatement : SourceEdit
    {
        // Inherited SourceEdit.Anchor = OWNER LogicalId (the GameObject to attach onto).
        // Mirrors AppendStatement.ParentAnchor: applier keys same-batch owner lookup on this
        // (appendAnnotations.ContainsKey(Anchor)) when the owner is itself appended this batch (§13).

        // Synthesized component LogicalId `{ownerLogicalId}/{Type.FullName}#{ordinal}`. MUST equal the
        // Component-kind AddedEntry.LogicalId (b2-t1/b4-t1) and what BuilderParser assigns after apply,
        // so a 2nd Sync is a no-op.
        public string ComponentLogicalId { get; init; } = "";

        // Fully-qualified component type for `Component<TypeFullName>()`.
        public string TypeFullName { get; init; } = "";

        // Index among the OWNER's representable components. Same role, and same reason, as
        // AppendStatement.NewSiblingIndex: a component list is ordered, so an append that ignores the
        // index emits the component ahead of ones already there and the next sync re-Reorders it.
        public int NewSiblingIndex { get; init; }

        // Ordered raw-path field setters to render as `.Set("key", <ValueNodeLiteral>)`.
        // REUSE FieldMap (ordered, insertion-preserving, deep value-equality) — same type as
        // ComponentData.Fields; do not reinvent a raw List. Applier (b3-t1) renders each value via
        // SourceExpr.ValueNodeLiteral (b1-t1), matching AppendStatement's "structured-in, render-at-apply".
        public FieldMap Fields { get; init; } = FieldMap.Empty;

        // b4-t3: pre-rendered ObjectRef expression overrides, keyed by field-key. null => no
        // overrides (every field renders via SourceExpr.ValueNodeLiteral, unchanged). Mirrors
        // PatchComponentField.NewExpr's pre-rendered-string pattern: SourceExpr stays a pure,
        // context-free formatter with no ObjectRef arm, so anything context-dependent (a handle
        // name) or side-effecting (an IntroduceHandle) is pre-rendered here at EMIT time, not
        // apply time.
        public IReadOnlyDictionary<string, string>? FieldExpressions { get; init; }

        // Receiver variable of the owner statement (existing handle, an introduced handle, or the
        // same-batch owner's Handle). null => resolved by applier. Mirrors AppendStatement.ParentHandle.
        public string? OwnerHandle { get; init; }

        // true => owner statement is currently handle-less (expression statement) and must be rewritten to
        // declare OwnerHandle before the component is attached. Mirrors AppendStatement.IntroduceParentHandle
        // and reuses BuildHandleDeclaration (StatementText.cs).
        public bool IntroduceOwnerHandle { get; init; }
    }

    // Field value changed in scene: replace ONLY the value argument at ValueSpan.
    public sealed record PatchComponentField : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId (informational/trace; applier resolves by ValueSpan).
        // Span of the value argument to replace (from ParseResult.FieldArgumentSpans[compId][key]).
        public SourceSpan ValueSpan { get; init; }
        // Pre-rendered replacement expr (SourceExpr.ValueNodeLiteral of the SNAPSHOT value). Matches the
        // string-NewExpr pattern of PatchArgument/PatchFlagArgument.
        public string NewExpr { get; init; } = "";
    }

    // Newly-detected field on an existing component: insert a raw `.Set("m_Path", value)` into its closure.
    public sealed record IntroduceComponentField : SourceEdit
    {
        // Inherited Anchor = target component LogicalId (applier locates the closure via merged
        // ComponentAnchors in b3-t2).
        public string FieldKey { get; init; } = "";
        // Unrendered value; applier renders via SourceExpr.ValueNodeLiteral, mirroring
        // AppendComponentStatement's per-field rendering (both create new `.Set` calls) — used
        // ONLY when NewExpr is null.
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");
        // b4-t3: pre-rendered expression override (mirrors PatchComponentField.NewExpr /
        // AppendComponentStatement.FieldExpressions). null => Value renders via
        // SourceExpr.ValueNodeLiteral (the pure, context-free formatter, which has NO ObjectRef
        // arm). Set at EMIT time for an ObjectRef field so the applier never has to render one.
        public string? NewExpr { get; init; }
    }
}
