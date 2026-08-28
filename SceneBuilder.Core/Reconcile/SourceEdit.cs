using System;
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
        // Equals the AddedEntry.LogicalId the Reconciler emits,
        // and equals what BuilderParser assigns after the Applier rewrites source.
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

        // §13: driven-masked UI layout to render as a chained `.RectTransform(...)`.
        // null => emit no call (non-UI node, or every rect axis driven). Its PRESENCE is what
        // carries Kind=="RectTransform" into re-parsed source, so a UI node whose five values all
        // equal the RectTransformFields defaults still renders a BARE `.RectTransform()`.
        public TransformData? RectTransform { get; init; }

        public bool? Active { get; init; }
        public string? Tag { get; init; }
        public int? Layer { get; init; }
        public bool? IsStatic { get; init; }

        // Variable name THIS statement declares (`var <Handle> = ...`) when it heads a
        // subtree whose descendants reference it. null => no declaration.
        // When set, Handle == NewLogicalId.
        public string? Handle { get; init; }

        // Receiver variable for a CHILD append (the parent's handle, existing or newly
        // introduced). null for a root append.
        public string? ParentHandle { get; init; }

        // true => the (currently handle-less) parent statement must be rewritten to declare
        // ParentHandle before the child is appended.
        public bool IntroduceParentHandle { get; init; }

        // Non-null => this append is a prefab-instance root; render as
        // `<receiver>.Instance(SourcePrefabPath)` instead of `<receiver>.Add(Name)`, suppressing
        // Tag/Layer/Active/IsStatic (InstanceHandle exposes no such calls). Path re-derived from
        // SourcePrefabGuid via identityMap.Assets at emit time (Reconciler), not rendered here.
        public string? SourcePrefabPath { get; init; }

        // Non-null => the FacadeCatalog
        // Guid->PropertyName reverse lookup (Reconciler, via facadeCatalog) resolved this
        // instance's SourcePrefabGuid to a façade entry. When set, rendering MUST prefer
        // `<receiver>.Instance(Prefabs.<SourcePropertyName>)` over the SourcePrefabPath string
        // form (SourcePrefabPath stays populated regardless, as the identity/fallback source).
        public string? SourcePropertyName { get; init; }
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
        // Component-kind AddedEntry.LogicalId and what BuilderParser assigns after apply,
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
        // ComponentData.Fields; do not reinvent a raw List. Applier renders each value via
        // SourceExpr.ValueNodeLiteral, matching AppendStatement's "structured-in, render-at-apply".
        public FieldMap Fields { get; init; } = FieldMap.Empty;

        // Pre-rendered ObjectRef expression overrides, keyed by field-key. null => no
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

    // spec 54: the diff-independent self-heal. An authored typed selector (`sel => sel.M`) over a
    // member the adapter has NOT proven safe, whose VALUE has not changed (a changed value already
    // downgrades via PatchComponentField's own selector-downgrade arm) -- rewrites ONLY the selector
    // argument to its string-key form, leaving the value argument untouched, so the heal cannot
    // itself reformat a value that never moved.
    public sealed record DowngradeComponentSelector : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId.
        // Serialized field key the selector resolves to -- locates the enclosing `.Set(...)` call.
        public string FieldKey { get; init; } = "";
        // The AUTHORED selector identifier text (e.g. "m_Secret") rewritten to a string literal.
        public string MemberName { get; init; } = "";
    }

    // A source-authored field whose LIVE value returned to the type's constructed default —
    // the `.Set(...)` call is REMOVED from source, never patched to the default literal (spec 32 C2).
    // The inverse of IntroduceComponentField; both key on the same component anchor.
    public sealed record RemoveComponentField : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId (trace + conflict-merge attribution).
        // Span of the VALUE argument of the `.Set(...)` to delete — the same
        // ParseResult.FieldArgumentSpans[compId][key] entry PatchComponentField resolves by. The
        // applier walks up from it to the enclosing `.Set(...)` invocation.
        public SourceSpan ValueSpan { get; init; }
        // Serialized field key, carried explicitly so SceneBuilderSync.KeyOfSourceEdit can attribute
        // this edit directly instead of reverse-looking-up the span.
        public string FieldKey { get; init; } = "";
    }

    // m8: an in-place rewrite of ONE listener call in a UnityEvent field (target/method/arg/
    // callState change), keyed by its own per-listener span so a sibling listener's statement is
    // never touched. Mirrors PatchComponentField's span-replace shape.
    public sealed record PatchListenerCall : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId (informational/trace).
        // Span of the ONE listener call being replaced (dot through this call's own closing
        // paren) — from ParseResult.ListenerCallSpans[compId][fieldKey][index].
        public SourceSpan CallSpan { get; init; }
        // Serialized field key ("m_OnClick" or a custom UnityEvent field's serialized path).
        public string FieldKey { get; init; } = "";
        // Pre-rendered replacement, operator-inclusive (`.OnClick(...)` / `.OnEvent(...)`).
        public string NewExpr { get; init; } = "";
    }

    // m8: a listener present in source but absent from the snapshot at its index — its call
    // statement is deleted. An emptied field collapses to a bare `.Component<T>()`, the same
    // collapse RemoveComponentField's applier already performs for `.Set(...)`.
    public sealed record RemoveListenerCall : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId (informational/trace).
        public SourceSpan CallSpan { get; init; }
        public string FieldKey { get; init; } = "";
    }

    // m8: a listener present in the snapshot with no source-side counterpart at its index — a NEW
    // call is inserted into the component's closure (never a whole-field overwrite of an existing
    // sibling listener's statement).
    public sealed record AppendListenerCall : SourceEdit
    {
        // Inherited Anchor = owning component LogicalId (applier locates the closure via merged
        // ComponentAnchors, same as IntroduceComponentField).
        public string FieldKey { get; init; } = "";
        // Receiver-less call body (`OnClick(...)` / `OnEvent(...)`); the applier prefixes the
        // closure's own receiver, mirroring BuildSetCallText's `t.OnClick(...)` construction.
        public string CallExpr { get; init; } = "";
    }

    // Newly-detected field on an existing component: insert a raw `.Set("m_Path", value)` into its closure.
    public sealed record IntroduceComponentField : SourceEdit
    {
        // Inherited Anchor = target component LogicalId (applier locates the closure via merged
        // ComponentAnchors).
        public string FieldKey { get; init; } = "";
        // Unrendered value; applier renders via SourceExpr.ValueNodeLiteral, mirroring
        // AppendComponentStatement's per-field rendering (both create new `.Set` calls) — used
        // ONLY when NewExpr is null.
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");
        // Pre-rendered expression override (mirrors PatchComponentField.NewExpr /
        // AppendComponentStatement.FieldExpressions). null => Value renders via
        // SourceExpr.ValueNodeLiteral (the pure, context-free formatter, which has NO ObjectRef
        // arm). Set at EMIT time for an ObjectRef field so the applier never has to render one.
        public string? NewExpr { get; init; }
    }

    // ---- Instance-root override authoring (append + revert) ------------------------------------

    public enum InstanceCallKind { Override, AddComponent, RemoveComponent, AddChild, RemoveChild }

    // Append `.Override(e => e.Set(a).Set(b))` onto the anchor's `scene.Instance(...)` chain.
    public sealed record AppendInstanceOverride : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public IReadOnlyList<OverrideSetSpec> Sets { get; init; } = Array.Empty<OverrideSetSpec>();
    }

    // One `.Set(...)` inside an Override closure. Value/ValueExpression mirror
    // AppendComponentStatement.Fields/FieldExpressions: Value renders via SourceExpr.ValueNodeLiteral;
    // ValueExpression (a pre-rendered ObjectRef handle expression) overrides it when non-null.
    public sealed record OverrideSetSpec
    {
        public string TypeFullName { get; init; } = ""; // root component type the override targets.
        // "member:<name>" => typed-selector form `(T x) => x.<name>`; else a raw serialized path => `Set<T>("path", ...)`.
        public string PropertyPath { get; init; } = "";
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");
        public string? ValueExpression { get; init; }
    }

    // Append `.AddComponent<T>()` or `.AddComponent<T>(c => c.Set(...))` onto the instance chain.
    public sealed record AppendInstanceAddComponent : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public string TypeFullName { get; init; } = "";
        public FieldMap Fields { get; init; } = FieldMap.Empty;
        public IReadOnlyDictionary<string, string>? FieldExpressions { get; init; }
    }

    // Append `.RemoveComponent<T>()` onto the instance chain.
    public sealed record AppendInstanceRemoveComponent : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public string TypeFullName { get; init; } = "";
    }

    // Drop a previously-authored chained call (revert to prefab default): removes the WHOLE matching
    // call from the instance's chain, leaving the rest intact.
    public sealed record DropInstanceCall : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public InstanceCallKind Kind { get; init; }
        public string? TypeFullName { get; init; } // AddComponent/RemoveComponent: match the <T>.
        public string? PropertyPath { get; init; } // Override: match the closure whose .Set targets this path; null => the sole Override.
    }

    // ---- Scoped `.On(...)` closure authoring (append + revert) ----------------------------------

    // One op to place inside a `.On(...)` closure. Reuses the M10 root op payload shapes verbatim.
    public abstract record ScopedOp;

    public sealed record ScopedOverrideOp : ScopedOp
    {
        public IReadOnlyList<OverrideSetSpec> Sets { get; init; } = Array.Empty<OverrideSetSpec>();
    }

    public sealed record ScopedAddComponentOp : ScopedOp
    {
        public string TypeFullName { get; init; } = "";
        public FieldMap Fields { get; init; } = FieldMap.Empty;
        public IReadOnlyDictionary<string, string>? FieldExpressions { get; init; }
    }

    public sealed record ScopedRemoveComponentOp : ScopedOp
    {
        public string TypeFullName { get; init; } = "";
    }

    // Append ops under a sub-object's `.On`, find-or-creating the closure. Inherited Anchor =
    // instance LogicalId.
    public sealed record AppendScopedOn : SourceEdit
    {
        // Resolved-child identity (ScopedOverride.ChildPath, e.g. "Turret/Barrel"); the
        // applier matches an existing `.On` whose selector normalises to this. NOT literal text/span
        // order.
        public string SelectorMatchKey { get; init; } = "";

        // Pre-rendered typed selector lambda for a NEW `.On` (rendered via FacadeCatalog reverse
        // lookup, e.g. "sel => sel.Turret.Barrel"). Only consumed on CREATE.
        public string SelectorExpr { get; init; } = "";

        public IReadOnlyList<ScopedOp> Ops { get; init; } = Array.Empty<ScopedOp>();
    }

    // Drop one op from inside a sub-object's `.On`; drop the whole `.On` when it was the last op.
    public sealed record DropScopedOnCall : SourceEdit
    {
        public string SelectorMatchKey { get; init; } = "";
        public InstanceCallKind Kind { get; init; } // REUSE M10 enum (Override|AddComponent|RemoveComponent)
        public string? TypeFullName { get; init; } // Add/RemoveComponent: match the <T>.
        public string? PropertyPath { get; init; } // Override: match the closure whose .Set targets this path; null => sole Override.
    }

    // ---- Child GameObject add/remove authoring ---------------------------------------------------
    // Emitted by ReconcilerInstances.Nested.cs from an AddedGameObjects/RemovedGameObjects diff;
    // rendered/reverted by SourcePatchApplier.Instances.cs.

    // Top-level `.AddChild(<parent>, "<name>")` on the instance chain (NOT inside .On). The parent is
    // rendered as the typed façade selector (`sel => sel.A.B`) when the catalog resolves it, else the
    // string path — matching `.On`'s typed-by-default emit (specs/27).
    public sealed record AppendInstanceAddChild : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public string ParentPath { get; init; } = ""; // AddedGameObject.Parent.ChildPath ("" == instance root).
        public string ParentSelectorExpr { get; init; } = ""; // Rendered parent arg: `sel => sel.A.B` or a quoted string.
        public string Name { get; init; } = ""; // AddedGameObject.Node.Name.
        public GameObjectNode Node { get; init; } = new(); // Payload; components rendered into cfg closure.

        // Pre-rendered handle expressions for Node's component fields, ONE slice PER COMPONENT,
        // aligned 1:1 by index with Node.Components (a null entry = that component has no
        // pre-rendered field; the whole list null = no component does). Unlike the single-component
        // siblings this otherwise mirrors (AppendInstanceAddComponent.FieldExpressions), AddChild is
        // multi-component (Node.Components renders N `cfg.Component<T>(...)` calls), so a single
        // flat field-keyed map shared across components would let two components with a same-named
        // field (e.g. two scripts each with `target`) collide. Consulted by the apply-time
        // component-closure renderer before ValueNodeLiteral, so an ObjectRef/catalogued-AssetRef
        // field on an added child's component renders a real handle instead of throwing.
        public IReadOnlyList<IReadOnlyDictionary<string, string>?>? FieldExpressions { get; init; }

        // Driven-masked, X/Y-held UI layout to render as a
        // chained `cfg.RectTransform(...)` call INSIDE the AddChild closure, mirroring
        // AppendStatement.RectTransform verbatim in shape and doc intent. null => emit no call
        // (non-UI added child, or every rect axis driven).
        public TransformData? RectTransform { get; init; }
    }

    // Top-level `.RemoveChild(<child>)`. The child is rendered as the typed façade selector
    // (`sel => sel.A.B`) when the catalog resolves it, else the string path (specs/27).
    public sealed record AppendInstanceRemoveChild : SourceEdit
    {
        // Inherited Anchor = the instance's LogicalId.
        public string ChildPath { get; init; } = ""; // RemovedGameObjects target.ChildPath.
        public string SelectorExpr { get; init; } = ""; // Rendered child arg: `sel => sel.A.B` or a quoted string.
    }
}
