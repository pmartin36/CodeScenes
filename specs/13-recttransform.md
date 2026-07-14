# M-UI — RectTransform sync (both directions)

### Additions to the contract
Concepts §3's **RectTransform note** names but leaves untyped; this milestone gives them concrete shape
and owns their round-trip. Names are reused verbatim by later UI work (M8).

- **`TransformData` UI fields (Kind=="RectTransform")** — extends the §3 `TransformData` variant with
  five `Vec2` fields, authoritative for UI layout:
  ```
  TransformData                              // Kind=="RectTransform" only
    ... Position, Rotation, Scale (as §3; owned by M1/M2, see interaction rule)
    AnchoredPosition : Vec2                   // == RectTransform.anchoredPosition  (m_AnchoredPosition)
    SizeDelta        : Vec2                   // == RectTransform.sizeDelta         (m_SizeDelta)
    AnchorMin        : Vec2                   // == RectTransform.anchorMin         (m_AnchorMin)
    AnchorMax        : Vec2                   // == RectTransform.anchorMax         (m_AnchorMax)
    Pivot            : Vec2                   // == RectTransform.pivot             (m_Pivot)
  ```
  `offsetMin`/`offsetMax` (named in §3's note) are **derived**, not stored: they are an alternate
  encoding of `(AnchoredPosition, SizeDelta, AnchorMin, AnchorMax, Pivot)` and are computed on demand, so
  there is a single source of truth and no drift. When `Kind=="Transform"` these five fields are absent
  (unset); no other milestone reads or writes them. This types the §3 note: RectTransform UI fields stop
  being `Unsupported` (preserved/flagged) once this milestone lands.
- **`ChangeOp` `SetRectTransform`** — the differ output for a changed UI layout, analogous to M1's
  `SetTransform` (`ChangeSet`/`ChangeOp`, M1). Lowers to constrained `SetField` ops (below).
- **Reused, NOT new:** `SetField` `PlanOp` (§5) constrained to the five RectTransform serialized paths
  `m_AnchoredPosition`, `m_SizeDelta`, `m_AnchorMin`, `m_AnchorMax`, `m_Pivot` (exactly as M1 constrained
  `SetField` to the three Transform paths); `PatchArgument` `SourceEdit` (M2) for the `.RectTransform(…)`
  argument write-back; `SourceExpr.Float` (M2b, `SceneBuilder.Core/Reconcile/SourceExpr.cs`) for
  f-suffixed float emission — a `SourceExpr.Vec2Literal` helper is added there **beside** the existing
  `Vec3Literal`, sharing `SourceExpr.Float` (both directions format identically; REUSE, do not reinvent).

Everything else binds to `00-foundation.md` verbatim: §2 seam, §3 `Vec2`, `SceneModel`,
`GameObjectNode`, `TransformData`, `SceneSnapshot`; §4 identity; §5 directions; §7 conflict philosophy;
§8 testing (Core TDD + adapter compile-gate); §13 create-with-payload.

---

## Goal
Author a UI element's RectTransform layout (anchors, pivot, anchored position, size) in the flat C#
builder, Materialize it onto the live RectTransform component (code→scene), and Reconcile editor
drags/resizes back into the builder source (scene→code) — reusing the M2/M2b edit machinery and the
`SourceExpr` float formatter. This is the UI-layout substrate M8 (Button/OnClick) depends on: a Button
lives on a RectTransform under a Canvas.

## In scope
- The five `Vec2` UI fields on `TransformData` when `Kind=="RectTransform"`
  (`AnchoredPosition`/`SizeDelta`/`AnchorMin`/`AnchorMax`/`Pivot`) — model, canonical serialization,
  value-equality, and both-direction round-trip.
- **Kind detection** at parse (a node authored with `.RectTransform(…)` has `Kind=="RectTransform"`) and
  at snapshot read (a GameObject whose live transform is a `RectTransform` reads `Kind=="RectTransform"`).
- Authoring surface `.RectTransform(anchoredPos:, sizeDelta:, anchorMin:, anchorMax:, pivot:)` — a UI
  sibling of M1's `.Transform(…)`; marks the node as UI and sets the five fields (all optional; omitted
  args keep Unity/RectTransform defaults).
- **Materialize** (code→scene): a changed UI layout lowers via `SetRectTransform` to `SetField` ops on
  the five `m_*` paths, executed **in place** on the existing RectTransform (never wipe-and-recreate, §5).
- **Reconcile** (scene→code): a drag/resize/anchor/pivot change observed in the `SceneSnapshot` emits a
  `PatchArgument` rewriting only the changed `.RectTransform(…)` argument(s), formatting-preserving (§5),
  floats f-suffixed via `SourceExpr`. A UI node created in the editor appends a `.RectTransform(…)` onto
  its `AppendStatement` (M2b) — participating in the §13 create-with-payload composition.
- **Interaction rule (double-authority):** for a `RectTransform` node, `AnchoredPosition` is authoritative
  for X/Y placement; M1/M2's `Position` X/Y is **not** independently diffed for that node (Unity derives
  `m_LocalPosition.x/y` from anchors+pivot+anchoredPosition). `Position.z`, `Rotation`, and `Scale`
  continue to sync via M1/M2 unchanged. This prevents a `.Transform(pos:)` vs `.RectTransform(anchoredPos:)`
  fight over the same pixels.

## Out of scope
- **Canvas / EventSystem creation** — documented as a **dependency**, not built here (see Dependencies +
  Risks). A `Canvas` (+`CanvasScaler`/`GraphicRaycaster`) is an ordinary component authored via M3
  `.Component<Canvas>(…)`; an `EventSystem` is an ordinary GameObject+component. This milestone neither
  reinvents component authoring nor auto-injects a Canvas; it owns RectTransform layout only. The adapter's
  sole Canvas-adjacent duty is ensuring the node carries a `RectTransform` component (below).
- UI graphics/content (`Image`, `Text`, `TMP_Text`, sprites) — ordinary M3/M4 component + asset fields.
- `Button.OnClick` / any `UnityEvent` wiring — **M8** (this milestone authors the Button's *layout*; M8
  authors its *wiring*).
- Layout components that drive RectTransforms at runtime (`LayoutGroup`, `ContentSizeFitter`,
  `LayoutElement`) — ordinary M3 components; their runtime-driven RectTransform mutations are not treated
  as user edits here (see Risks).
- `offsetMin`/`offsetMax` as an *authoring* form — only the five stored fields are authored; offsets are a
  derived convenience, not a builder argument.

## Core deliverables

### Types added/changed (referencing §3 contract)
- Extend `TransformData` (§3) with the five `Vec2` UI fields, populated **only** when
  `Kind=="RectTransform"` (flagged above). `Vec2` is used exactly as typed in §3.
- New `ChangeOp` `SetRectTransform` (flagged) — sibling of M1's `SetTransform`.
- `SceneModel`, `GameObjectNode`, `SceneSnapshot`, `Plan`/`SetField`, `SourcePatch`/`PatchArgument`,
  `ChangeSet` used as already typed (M1/M2/M2b) — not additions.

### Functions/behaviors (each a testable contract)
- **Parse `.RectTransform(…)` → model.** A node authored with
  `.RectTransform(anchoredPos:(10,20), sizeDelta:(100,30), anchorMin:(0,1), anchorMax:(0,1), pivot:(0,1))`
  yields `TransformData.Kind=="RectTransform"` with the five `Vec2` fields set to those values; a bare
  `.RectTransform()` sets `Kind=="RectTransform"` and leaves the five fields at RectTransform defaults
  (anchoredPos `(0,0)`, sizeDelta `(0,0)`, anchorMin/anchorMax `(0,0)`… authored-omitted == default).
- **Kind stays Transform without the call.** A node with only `.Transform(…)` (or nothing) has
  `Kind=="Transform"` and no UI fields — RectTransform authoring is opt-in.
- **Canonical serialization.** A `RectTransform` `TransformData` serializes deterministically and
  byte-identically across runs: the five `Vec2` fields plus base P/R/S, floats round-trip-invariant
  (invariant culture), Kind emitted. Two structurally equal models serialize identically; `Vec2(0,1)` vs
  `Vec2(0, 1.0000001)` diff as **changed** (exact equality, consistent with M3).
- **Diff → SetRectTransform.** Given an expected `RectTransform` node and a `SceneSnapshot` whose same
  `GlobalObjectId` differs in `AnchoredPosition` (or any of the five), `Diff` yields one
  `SetRectTransform` op for that node; identical UI layout yields **no** op (idempotent).
- **X/Y double-authority suppressed.** For a `RectTransform` node, a snapshot difference confined to
  `m_LocalPosition.x/y` (with equal anchoredPosition) produces **no** `SetTransform` op — only
  `AnchoredPosition` drives X/Y; `Position.z`/`Rotation`/`Scale` still diff normally.
- **Materialize lowers to constrained SetField.** `SetRectTransform` lowers to `SetField` ops on paths
  `m_AnchoredPosition`/`m_SizeDelta`/`m_AnchorMin`/`m_AnchorMax`/`m_Pivot` **only** — never the three
  Transform paths, never an arbitrary path; one `SetField` per changed field.
- **Reconcile move/resize → argument patch.** A snapshot RectTransform whose `AnchoredPosition` changed
  emits a `PatchArgument` rewriting only the `anchoredPos:` argument of that node's `.RectTransform(…)`
  call (floats f-suffixed via `SourceExpr.Vec2Literal`); a size change patches `sizeDelta:`; an anchor
  change patches `anchorMin:`/`anchorMax:`; unrelated args and file formatting are byte-unchanged (M2
  preservation rule).
- **Reconcile created UI node → appended layout (§13).** An editor-created GameObject carrying a
  `RectTransform` (unmapped `GlobalObjectId`) appends its `.Add(...)` statement (M2b) **with** a
  `.RectTransform(…)` chained call capturing the five fields, in a single Reconcile pass; a second Sync of
  the unchanged scene is a no-op. Cites §13 (create-with-payload).
- **Missing `.RectTransform` on a UI snapshot node → append the call.** If the expected node is authored
  with only `.Transform(…)` but the live object is now a RectTransform (e.g. a UI component was added in
  the editor, promoting Transform→RectTransform), Reconcile appends a `.RectTransform(…)` call to that
  node's statement rather than fighting the promotion — the Kind change is represented, not dropped.
- **f-suffixed floats via SourceExpr.** Every emitted RectTransform literal (append or patch) formats
  through the shared `SourceExpr.Float`/`Vec2Literal`, identical to M2b's Vec3 emission
  (`(10f, 20f)`), invariant culture, shortest round-trippable form.
- **Idempotent.** Materialize → parse → re-materialize yields no ops; Reconcile → apply → re-parse →
  re-reconcile yields no further edits.
- **Fail loud, located (§7).** A RectTransform snapshot field that cannot be localized to the node's
  `.RectTransform(…)` construct (e.g. the object has no source anchor) surfaces a `Conflict` naming the
  node + field + location, never a malformed patch.

## Editor adapter deliverables
> **Built by the pipeline, gated by the Unity-DLL compile-check** (`SceneBuilder.Editor.CompileCheck`,
> §8) — NOT hand-wired. Runtime behavior is confirmed only by the user's checklist below.

- **RectTransform presence.** When materializing a node whose `Transform.Kind=="RectTransform"`, ensure
  the live GameObject carries a `RectTransform` rather than a plain `Transform`. Unity does not allow
  `AddComponent<RectTransform>` on an occupied Transform slot; the adapter obtains a RectTransform the
  supported way — create UI GameObjects via `new GameObject(name, typeof(RectTransform))` /
  `ObjectFactory.CreateGameObject(…, typeof(RectTransform))`, or rely on Unity's automatic
  Transform→RectTransform promotion when a `Graphic`/UI component is present. Never destroy+recreate a
  mapped object to swap the transform (§5); if a mapped plain-Transform object must become UI, surface it
  as a located note, not a silent wipe.
- **Read.** In the full snapshot reader (M2), detect `transform is RectTransform`; when so, stamp
  `Kind=="RectTransform"` and read `anchoredPosition`/`sizeDelta`/`anchorMin`/`anchorMax`/`pivot` (via the
  RectTransform API or `SerializedObject` at `m_AnchoredPosition`/`m_SizeDelta`/`m_AnchorMin`/`m_AnchorMax`/
  `m_Pivot`) into the `TransformData` UI fields, stamping the object's `GlobalObjectId`. A plain Transform
  reads `Kind=="Transform"` and no UI fields.
- **Write.** Execute the five RectTransform `SetField` ops **in place** on the existing RectTransform via
  `SerializedObject` + `ApplyModifiedProperties` (or the typed RectTransform properties), resolving the
  target through the IdentityMap `GlobalObjectId → object` (§2 responsibility #4). No mode/diff logic in
  the adapter — Core decides which fields changed (§2 logic-light).
- **No Canvas logic.** The adapter does not create/configure Canvas or EventSystem (out of scope); those
  arrive as ordinary components/objects through M3/M1.

## Authoring API added
`.RectTransform(anchoredPos?, sizeDelta?, anchorMin?, anchorMax?, pivot?)` on a node handle — the UI
sibling of M1's `.Transform(…)`; each argument is a `Vector2` (authored as a `(x, y)` tuple, lowered to
`Vec2`), all optional. Presence of the call marks `Kind=="RectTransform"`.

```csharp
public class HudScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        // Canvas is an ordinary component (M3); EventSystem an ordinary object — prerequisites, not owned here.
        var canvas = scene.Add("Canvas")
                          .Component<Canvas>(c => c.Set("m_RenderMode", 0))   // ScreenSpaceOverlay (M3)
                          .Component<CanvasScaler>(_ => { })
                          .Component<GraphicRaycaster>(_ => { });
        scene.Add("EventSystem").Component<EventSystem>(_ => { });            // (M3)

        // A top-right anchored panel — layout owned by THIS milestone
        var panel = canvas.Add("Panel").RectTransform(
            anchoredPos: (-10, -10), sizeDelta: (200, 120),
            anchorMin: (1, 1), anchorMax: (1, 1), pivot: (1, 1));

        // A Button: its RectTransform layout here; its OnClick wiring is M8.
        panel.Add("QuitButton")
             .RectTransform(anchoredPos: (0, 0), sizeDelta: (160, 40), pivot: (0.5f, 0.5f))
             .Component<Image>(_ => { });                                     // graphic (M3/M4)
        // M8 will add: .Component<Button>(b => b.OnClick(app, nameof(App.Quit)))
    }
}
```
- Lowering (Editor→Core): `.RectTransform(...)` → `TransformData.Kind="RectTransform"` + the five `Vec2`
  fields; omitted args stay at RectTransform defaults. Base `.Transform(rot:/scale:)` may still be chained
  for rotation/scale/z on the same UI node.

## IdentityMap / sidecar changes
- **None new.** RectTransform nodes are ordinary `Kind="GameObject"` `Entries[]` with `GlobalObjectId`,
  `ParentLogicalId`, LogicalId exactly as M1. The RectTransform component itself is the object's transform
  (not a separately-mapped `Component` entry — the Transform/RectTransform is never a `Components[]` item,
  consistent with M1/M3). `SchemaVersion` bumps only if the persisted `TransformData` shape is versioned in
  the sidecar; the five fields are additive.

## Core test plan
`SceneBuilder.Core.Tests` (xUnit, headless — §8). RED tests, behaviors not impl:
- `Parse_RectTransformCall_SetsKindAndFiveVec2Fields`.
- `Parse_BareRectTransform_KindRectTransform_DefaultsForOmittedArgs`.
- `Parse_NoRectTransformCall_KindStaysTransform_NoUiFields`.
- `CanonicalSerializer_RectTransform_IsByteIdentical_FloatsRoundTripInvariant`.
- `Diff_ChangedAnchoredPosition_ProducesSingleSetRectTransform`.
- `Diff_EqualRectTransformLayout_ProducesNoOp` (idempotent).
- `Diff_RectTransform_LocalPositionXYDrift_ProducesNoSetTransform` (double-authority suppressed).
- `Materialize_SetRectTransform_LowersToConstrainedRectFieldPaths` (the five `m_*` only, never the three
  Transform paths).
- `Reconcile_MovedRect_ProducesAnchoredPosArgumentPatch_FSuffixed`.
- `Reconcile_ResizedRect_ProducesSizeDeltaArgumentPatch`.
- `Reconcile_AnchorOrPivotChanged_PatchesOnlyThatArgument_FormattingPreserved`.
- `Reconcile_CreatedUiNode_AppendsAddWithRectTransform_SecondSyncNoOp` (§13 create-with-payload).
- `Reconcile_TransformPromotedToRect_AppendsRectTransformCall_NotDropped`.
- `Append_RectTransformLiteral_UsesSharedSourceExprVec2_FSuffixedFloats`.
- `RectTransform_FullRoundTrip_IsIdempotent`.

## Unity confirmation checklist
1. Author `HudScene` (sample above) with a `Canvas`, `EventSystem`, an anchored `Panel`, and a
   `QuitButton`; trigger Build. *Expected:* the scene gains a Canvas with the Panel/Button under it; the
   Panel's RectTransform shows `anchoredPosition (-10,-10)`, `sizeDelta (200,120)`, anchors top-right
   `(1,1)/(1,1)`, pivot `(1,1)`; the Button's RectTransform matches its `.RectTransform(…)`; scene saves
   without errors.
2. Inspect `<Scene>.sbmap.json`. *Expected:* one `GameObject` entry per UI node with a non-empty
   `GlobalObjectId` and correct `ParentLogicalId`; no separate transform component entries.
3. In Unity, **drag** the Panel to a new position (change anchoredPosition) → Sync. *Expected:* the
   Panel's `.RectTransform(anchoredPos: …)` argument updates to the new value (floats f-suffixed); no
   other line changes; same `GlobalObjectId` (no duplicate).
4. In Unity, **resize** the Button (drag a handle → sizeDelta changes) → Sync. *Expected:* its
   `.RectTransform(sizeDelta: …)` argument updates; nothing else.
5. In Unity, change the Panel's **anchor preset** (e.g. top-right → bottom-left) and/or its **pivot** →
   Sync. *Expected:* the `anchorMin:`/`anchorMax:`/`pivot:` arguments update to match.
6. Edit an `anchoredPos` value in source and re-Build. *Expected:* the existing RectTransform moves **in
   place** (same object, no wipe-and-recreate).
7. Re-Build with no source change, then re-Sync with no scene change. *Expected:* both are no-ops
   (idempotent).
8. In Unity, create a **new** UI GameObject under the Canvas (it gets a RectTransform) → Sync.
   *Expected:* a `.Add("…").RectTransform(…)` statement appears under the Canvas handle, compiles, and a
   second Sync is a no-op.

## Dependencies
- **M0** — harness, Plan, sidecar, layout.
- **M1** — `SceneModel`/`GameObjectNode`/`TransformData`/`Vec2`, `SetTransform`/`SetField`, Materialize,
  IdentityMap with `GlobalObjectId`s; the `.Transform(…)` authoring pattern this mirrors.
- **M2** — full scene-driven `SceneSnapshot` reader, `Reconcile`, `PatchArgument` `SourceEdit`,
  formatting-preserving Roslyn apply.
- **M2b** — `AppendStatement` (for created UI nodes) + the shared `SourceExpr` float/vec emitter this
  milestone extends with `Vec2Literal`; §13 create-with-payload single-pass convergence.
- **M3** — components (Canvas/CanvasScaler/GraphicRaycaster/Image/EventSystem are ordinary components; the
  UI prerequisites are authored through M3, not here).
- Enables **M8** (Button/OnClick wiring) — a Button needs a RectTransform under a Canvas, both provided by
  this milestone + M3.

## Risks/notes
- **Transform→RectTransform swap** is the highest-risk adapter piece: Unity replaces a GameObject's
  Transform with a RectTransform automatically only when a UI `Graphic`/component is present or the object
  is created with `typeof(RectTransform)`. The adapter must obtain the RectTransform the supported way and
  **never** destroy+recreate a mapped object to force it (§5). A mapped plain-Transform object that must
  become UI is surfaced as a located note.
- **Canvas is a documented prerequisite, not owned here.** A Button/UI without a Canvas ancestor +
  EventSystem will not function at runtime; this milestone authors layout only and relies on M3 for the
  Canvas stack. Confirmation checklists that exercise UI must include a Canvas/EventSystem.
- **Double-authority (localPosition vs anchoredPosition).** The suppression rule (X/Y owned solely by
  `AnchoredPosition` for RectTransform nodes) is load-bearing — without it, M1/M2's `Position` and this
  milestone's `AnchoredPosition` would emit competing ops for the same pixels. Tests pin that a pure
  `m_LocalPosition.x/y` drift on a RectTransform produces no `SetTransform`.
- **Runtime-driven RectTransforms.** A RectTransform under a `LayoutGroup`/`ContentSizeFitter` is mutated
  by Unity at layout time; those derived values are noise for scene→code. Treat layout-driven RectTransform
  mutation as not a user edit (out of scope here); if it causes churn it is surfaced, not silently
  written — refine under M7 robustness if it appears.
- **offsetMin/offsetMax as a single source of truth.** Storing both the five fields and offsets would
  double-encode the same rect and invite drift; offsets remain derived. If an author needs offset-style
  layout, it is expressed through `anchorMin/anchorMax/anchoredPosition/sizeDelta/pivot`.
- **Sample seed (§12):** the `HudScene` panel/button is the UI seed of the shipped `Samples~/RoundTripDemo`
  once M8 wires its OnClick; authored in the test project's `Assets` first, promoted verbatim once the
  round-trip is proven.
