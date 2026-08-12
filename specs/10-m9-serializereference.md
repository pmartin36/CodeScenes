# M9 — `[SerializeReference]` polymorphism

> **Note (2026-07-28):** buildable as-written, with one seam change since this spec was authored.
> `ValueNodeParser.Parse` is now 3-arg and catalog-threaded (`Parse(expr, ctx.AssetCatalog,
> ctx.FacadeConflicts)`), so M9's managed-instance value parsing must plug into that signature, and its
> `x => x.field` field selector should reuse the shipped `.Set(x => x.field)` selector parser. Asset
> fields inside a managed instance ride the typed asset catalog (spec 28, `Assets.<Type>…`), not string
> `Asset("path")`.

### Additions to the contract
This milestone introduces new types/ops not in `00-foundation.md`. They are flagged here per §3's rule.

- **`ValueNode.ManagedReference`** — new `ValueNode` variant (§3):
  ```
  ManagedReference(concreteType: TypeRef?, fields: Map<string, ValueNode>)
    concreteType : TypeRef?    // the runtime concrete type currently assigned; null == the field is null
    fields       : Map<string, ValueNode>   // the instance's OWN serialized fields, keyed by propertyPath,
                                             //   modeled exactly like ValueNode.Nested (§3). Empty when null.
  ```
  A field marked `[SerializeReference]` (interface-, abstract-, or base-typed) is stored in
  `ComponentData.Fields` under its serialized `propertyPath` with a `ManagedReference` value.
  `concreteType == null` is a first-class, valid state (the reference is null). `concreteType` **can
  change** between round-trips (polymorphism). `fields` may itself contain further `ManagedReference`
  values (nested managed refs recurse).
- **`Plan` op `SetManagedReference(path, concreteType, fields)`** — new op in the §5 enumeration.
  `concreteType == null` clears the reference (`managedReferenceValue = null`); otherwise it
  instantiates the concrete type and sets the instance fields. Distinct from `SetField`/`SetReference`
  because it must (re)instantiate a `[SerializeReference]` object, not assign a value or object pointer.

Everything else binds to `00-foundation.md` verbatim (§2 seam, §3 `TypeRef`/`Nested`/`ValueNode`
recursion, §4 identity, §5 directions, §7 conflict philosophy).

---

## Goal
Author and round-trip `[SerializeReference]` **managed-reference** fields — polymorphic serialized
fields typed as an interface / abstract / base class and holding a concrete instance — in both
directions, including null, concrete-type change, the instance's own fields, and nested managed refs.

## In scope
- Modeling any `[SerializeReference]` field as `ValueNode.ManagedReference{ concreteType, fields }`.
- `null` managed reference as a valid modeled/serialized state.
- **Concrete-type change** round-trip (the same field pointing at a different concrete type).
- The managed instance's own serialized fields — primitives/enums/vectors/colors/asset refs/object
  refs/lists/nested (reusing §3 `ValueNode` kinds under a `Nested`-style field map).
- **Nested** managed references (a `[SerializeReference]` field on the managed instance itself).
- Materialize: set `managedReferenceValue` to a fresh instance of `concreteType` and fill its fields.
- Reconcile: detect a type change or a field change on the managed instance → patch source.
- Concrete-type identity carried as a §3 `TypeRef` (`FullName` + `AssemblyHint`) — the assembly-aware
  identity Unity records as `managedReferenceFullTypename` (`"<assembly> <namespace.type>"`).

## Out of scope
- **Reference sharing / cyclic graphs**: Unity 2021+ allows a single managed object referenced by
  multiple fields (same `rid`) and cycles. M9 models each managed-reference field as an **owned tree**;
  a detected shared `rid` (referenced ≥2×) or cycle is round-tripped as `ValueNode.Unsupported`,
  flagged, never silently forked or lost (§7). Full graph identity is deferred to `needs_research`.
- Generic concrete types with unresolved/ambiguous type args, and types with no accessible
  parameterless construction path the adapter can instantiate — captured as `Unsupported`, flagged.
- Migration of `[SerializeReference]` renamed/moved types via `[MovedFrom]`/type fallback — deferred;
  a missing concrete type surfaces as a **conflict** (fail-loud), source untouched.
- `[SerializeReference]` on arrays/lists of managed refs — **in scope** for the element modeling
  (each element is a `ManagedReference`), but reference-shared elements follow the sharing rule above.

## Core deliverables
### Types added/changed (referencing §3)
- Add `ValueNode.ManagedReference(concreteType: TypeRef?, fields: Map<string, ValueNode>)`.
- Add `Plan` op `SetManagedReference(path, concreteType, fields)` (§5 op list).
- Reuse §3 `TypeRef` for concrete-type identity; reuse `Nested`-style field maps and all `ValueNode`
  kinds for the instance's fields. The recursion MODEL is reused, but `ValueNode.ManagedReference` IS a
  new `ValueWalk` container kind that every value pass must learn, enforced at compile time (a
  per-container `ValueWalk` arm at each call site) and by the descent-scan guard, the same as
  `UnityEventListeners` was in M8. Reused model, new container kind: not the drop-in "not a new type"
  the earlier draft of this note implied.

### Functions/behaviors (each a testable contract)
- **Canonical serialization** — a `ManagedReference` serializes deterministically: `concreteType`
  (assembly-qualified `TypeRef`) then `fields` in canonical (stable-sorted) key order; `null`
  reference serializes as a distinct canonical `null` token (never as an empty-fields instance).
  Byte-identical across repeated serialization of an equal model.
- **Model ↔ managed instance projection** — Core exposes a pure mapping between `ManagedReference`
  and `(managedReferenceFullTypename, per-field values)` so the adapter is a dumb applier; the
  `TypeRef ↔ managedReferenceFullTypename` conversion is total and reversible for resolvable types.
- **Diff** — a managed-reference field is changed when `concreteType` differs (incl. non-null↔null) OR
  any field in the (recursively compared) `fields` map differs. A **type change** invalidates the old
  field set: Diff emits a single `SetManagedReference` op carrying the full new type+fields (not a
  field-level patch), because the instance is replaced.
- **Materialize → Plan** — a desired managed ref differing from actual lowers to one
  `SetManagedReference(path, concreteType, fields)`; equal → no op (idempotent). `concreteType == null`
  lowers to a clear.
- **Reconcile → SourcePatch** — a managed-ref delta in the snapshot patches the exact authoring span:
  a field-only change rewrites the affected setter argument in place; a **type change** rewrites the
  concrete-type construction (and its whole field closure); null↔non-null adds/removes the assignment.
  Formatting-preserving (§5). Nested managed-ref changes patch the nested construction span.
- **Nested recursion** — projection, diff, and patch all recurse into `fields` that contain further
  `ManagedReference` values, at arbitrary depth.
- **Fail-loud / conflict** — a snapshot managed ref whose `managedReferenceFullTypename` resolves to no
  loadable type (`ManagedReferenceMissingType`) is surfaced as a conflict with object+field+location
  (§7); source is not patched and the reference is not nulled.

## Editor adapter deliverables
- **Read**: for each `[SerializeReference]` property (`SerializedProperty.propertyType ==
  ManagedReference`), read `managedReferenceValue` (or `managedReferenceFullTypename` when the object
  can't be constructed) plus the instance's child `SerializedProperty` fields; hand raw
  `(fullTypename, field values)` to Core. Detect null (`managedReferenceValue == null`) and missing
  type (`managedReferenceFullTypename` non-empty but unresolvable). Detect sharing/cycles via
  `managedReferenceId` (`rid`) seen ≥2× → mark for Core's `Unsupported` handling.
- **Write**: execute `SetManagedReference` by instantiating the concrete type
  (`Type.GetType`/domain type lookup from `TypeRef` → `Activator.CreateInstance` or Unity's managed-ref
  instantiation), assigning `prop.managedReferenceValue = instance`, then applying child field values
  through `SerializedObject` + `ApplyModifiedPropertiesWithoutUndo`. `concreteType == null` →
  `managedReferenceValue = null`. Reconcile-in-place; never wipe the component (§5).
- **Resolve** concrete type from `TypeRef.FullName` + `AssemblyHint`; asset/object-ref fields on the
  instance resolve via the existing M4/M5 boundary resolvers.
- Adapter carries **no** polymorphism logic beyond instantiate-and-fill — type choice, field set, and
  recursion order all come from Core's plan.

## Authoring API added
```csharp
// Field of interface/abstract type, set to a concrete builder-expressed instance:
enemy.Component<AiBrain>(c => c.SetRef(x => x.strategy, new Aggressive { range = 5f, target = player }));

// Null is authorable and round-trips:
enemy.Component<AiBrain>(c => c.SetRef(x => x.strategy, null));

// Concrete type carrying a nested [SerializeReference] field:
c.SetRef(x => x.strategy, new Composite {
    primary  = new Aggressive { range = 5f },     // nested managed ref
    fallback = new Flee { speed = 3f },
});
```
- `SetRef(pathExpr, instance)` targets a `[SerializeReference]` field; the concrete `new T { … }`
  lowers (Editor→Core) to `ManagedReference{ concreteType = TypeRef(T), fields = { … } }`.
- `null` lowers to `ManagedReference{ concreteType = null, fields = {} }`.
- Object/asset-referencing fields inside the instance reuse the M4/M5 authoring forms (handles /
  asset refs) and lower to `ObjectRef`/`AssetRef` inside `fields`.

## IdentityMap / sidecar changes
- No new entry kinds. A managed instance is **not** a scene object and gets **no** `LogicalId` /
  `GlobalObjectId` — it is owned data inside its host component's field.
- Asset/object refs inside the managed instance reuse existing `Entries[]` / `Assets[]` resolution
  (M4/M5), unchanged.
- The Unity YAML `references:` / `rid` registry is a serialization detail Unity owns; Core models the
  logical tree only and does not persist `rid`s (they are non-deterministic across edits).

## Core test plan (RED tests — behaviors)
- **Model ↔ managed ref**: build `ManagedReference{ concreteType=Aggressive, fields={range:5f} }`;
  project to `(fullTypename, fields)` and back → equal; `fullTypename` is assembly-qualified.
- **Round-trip non-null**: SceneModel with `SetRef(x => x.strategy, new Aggressive{range=5f})` →
  serialize → parse → equal model.
- **Null round-trip**: `SetRef(x => x.strategy, null)` serializes as canonical null and parses back to
  `concreteType == null`; distinct from an empty-fields instance.
- **Type-change round-trip**: expected `Aggressive`, actual snapshot `Flee` → Diff emits one
  `SetManagedReference` with the new type+fields (not a field patch); Reconcile rewrites the `new T{…}`
  construction and its field closure.
- **Field edit on the instance**: same concrete type, `range` 5→9 → Diff emits `SetManagedReference`;
  Reconcile rewrites only the affected field argument.
- **Nested managed ref**: `Composite{ primary=Aggressive, fallback=Flee }` round-trips; a change to
  `primary.range` patches the nested span; a change of `primary`'s concrete type recurses correctly.
- **Diff idempotence**: identical desired/actual managed refs → zero ops.
- **Missing type → conflict**: snapshot `managedReferenceFullTypename` unresolvable → surfaced
  conflict, no `SourcePatch`, reference not nulled.
- **Sharing/cycle → Unsupported**: a shared `rid` (or cycle) → `ValueNode.Unsupported`, flagged,
  byte-identical round-trip; not forked into duplicate trees.

## Unity confirmation checklist
1. Component `AiBrain` with `[SerializeReference] IStrategy strategy;`. Author
   `SetRef(x => x.strategy, new Aggressive{ range = 5f })`, Materialize. **Expected:** in the Inspector
   the field shows an `Aggressive` instance with `range = 5`.
2. In Unity, edit `range` to `9` → save. **Expected:** source's `range` argument becomes `9f`; concrete
   type unchanged.
3. In Unity, **switch the concrete type** (assign a `Flee` via the managed-reference type picker) and
   set its fields → save. **Expected:** source's `new Aggressive{…}` becomes `new Flee{…}` with the new
   fields; no stale `Aggressive` fields remain.
4. In Unity, set the field to **None** (null) → save. **Expected:** source becomes
   `SetRef(x => x.strategy, null)`.
5. Assign a concrete type carrying its own `[SerializeReference]` field (`Composite`) and edit the
   nested instance → save. **Expected:** source reflects the nested `new …{…}` and its edited values.
6. Re-run Materialize with no code change. **Expected:** no plan ops (idempotent).
7. (Negative) Rename/remove the concrete type's C# class, reopen → save. **Expected:** a located
   conflict is surfaced; source is not silently changed and the field is not nulled.

## Decomposition guidance

- **The cross-cutting invariant is the `ManagedReference` container kind, owned by `ValueWalk`.** It gets
  ONE owning task that lands FIRST, before any parse/emit/reconcile/adapter task depends on it. That task
  delivers: `ValueNode.ManagedReference(TypeRef? concreteType, FieldMap fields)` plus its `JsonDerivedType`
  registration; an arm in ALL FIVE `ValueWalk` primitives (`Map` `ValueWalk.cs:124`, `Any` `:205`, `Fold`
  `:289`, `Descend` `:366`, `Enumerate` `:415`) plus a slot-recursion helper analogous to the listener slot
  helper; the `Differ` arm (a concrete-type change or any nested field change emits ONE whole-node
  `SetManagedReference`, never field-level patches); and registration in
  `SceneBuilder.Core.Tests/ValueContainerDescentScanTests.cs` (a `ManagedReference` token scanner plus
  per-file allowlist, mirroring the `UnityEventListeners` treatment at that file's ~:355-448). This is the
  same container-kind job M8 did for `UnityEventListeners`: well-precedented, touches many files, low
  conceptual risk, but every value pass fails to compile (and the scan guard fails) until it handles the
  new kind. Do NOT restate the recursion rule as per-task prose; the `ValueWalk` arm plus the descent scan
  ARE the mechanism.

- **Object-initializer parse and emit are REUSED, not built.** The `new T { ... }` shape already
  round-trips for `Nested`: `ValueNodeParser.cs:77-78` dispatches `ObjectCreationExpressionSyntax` to
  `ParseObjectCreation` `:176-197` -> `ParseNested` `:207-223`, which recurses `Parse` on each
  `field = value` at `:218`; emit folds `Nested` recursively in `SourceExpr.ValueNodeLiteral` `:72-84`. The
  only new parse entry is a `SetRef`-only `ParseManagedReference` (a `ManagedReference`-flavored copy of
  `ParseNested`'s member loop, plus `null -> ManagedReference(concreteType: null, empty)`), invoked from a
  new `case "SetRef":` in `BuilderParser.cs` (mirror `ParseSetCall` `:513-539`, reuse the `x => x.field`
  selector at `:505-506`). Syntax alone cannot distinguish `ManagedReference` from `Nested` (both are
  `new T { ... }`); the `.SetRef` vs `.Set` CALL decides. Emit adds one `SourceExpr` `Fold` arm
  (`null -> "null"`, non-null -> `"new " + TypeRef.FullName + " { ... }"`). Do not plan a task to build
  object-initializer parsing from scratch; it exists.

- **Recognizer groundwork lands in BOTH copies.** `.SetRef(...)` must be allowlisted in
  `SceneBuilder.Grammar/FlatShapeRecognizer.cs` (`ProcessComponentSetCall` `:296-322`, with an arg-shape
  check mirroring `CheckSetCall` `:324`) AND in its analyzer twin
  `CodeScenes.Analyzers/FlatShapeAnalysis.cs`, kept in lockstep (`RecognizerAgreementTests` /
  `RecognizerCompletenessTests` fail otherwise). Until then a `.SetRef(...)` file fails the shape gate
  (SB1003). This is required groundwork and the analyzer twin is easy to miss; put both files in the owning
  task's TOUCHES.

- **Reconcile granularity (a note that removes a false requirement).** A field-only change AND a
  concrete-type change BOTH rewrite the WHOLE `new T{...}` value expression at the field's value span (the
  shipped `PatchComponentField` path, `ComponentPatchApplier.cs:155-183`, fed by the whole-value-span
  rewrite at `ComponentReconciler.cs:374-402`). There is no member-level sub-span patch today and M9 does
  not need one; do not plan finer-grained patching than the shipped whole-value-span rewrite. The
  "rewrites only the affected field argument" wording in the field-edit contracts above (Reconcile ->
  SourcePatch, and the field-edit test) means that whole value span re-rendered from the projected model,
  not a member-level sub-span edit inside the closure.

- **Primary risk, its own task with an EditMode test: references INSIDE a managed instance's fields.** The
  spec puts asset/object refs inside `fields` in scope (`new Aggressive { target = player }`), but
  `SceneBuilder.Core/Reconcile/NestedValueEmission.cs:229-236` (`IsRepresentable`) DELIBERATELY EXCLUDES
  `AssetRef`/`ObjectRef`/`Unsupported`/empty-list from nested initializers, and top-level object-ref
  lowering plus handle pre-rendering (`ComponentPatchApplier.cs:130-140`) operate at top-level field-key
  granularity. So a `ManagedReference` field holding an object/asset ref cannot reuse the `Nested`
  representability path as-is: this task must make object-ref/asset-ref resolution and handle pre-rendering
  DESCEND into `ManagedReference.fields`. This is the milestone's genuine depth and gets its own EditMode
  test (author `SetRef(x => x.strategy, new Aggressive { target = someHandle })`, Materialize, confirm the
  live reference resolves; edit the nested target in Unity, confirm it round-trips), separate from the
  plain-value round-trip tests.

- **Adapter task (Unity-boundary, EditMode-gated).** `SetManagedReference` execution in `PlanExecutor`
  (instantiate the concrete type -> `managedReferenceValue = instance` -> fill child fields;
  `concreteType == null` -> clear), following the `SetField`/`SetReference` precedent, with the EditMode
  coverage the Unity-facing rule requires.

Keep each task's TOUCHES complete (an owned-defect file goes in TOUCHES, or the task is split), and
enumerate the RED cases literally: the Core test plan above already does, and the
nested-ref-holding-an-object-ref case stays its own EditMode case, separate from the plain-value
round-trips.

## Dependencies
- **M3** (components + serialized fields; `ComponentData.Fields`, `ValueNode.Nested`, `TypeRef`).
- **M2** (Reconcile + Roslyn `SourcePatch` for statement/arg/closure edits).
- **M4 / M5** (asset/object refs that may appear inside a managed instance's fields).

## Risks/notes
- `managedReferenceFullTypename` format is `"<assemblyName> <namespace>.<Type>"`; map to/from `TypeRef`
  (`FullName` + `AssemblyHint`). Assembly renames/versions are the main drift risk — treat unresolved
  types as conflicts, never as null.
- `SerializedProperty.managedReferenceValue` requires Unity 2019.3+; `ManagedReferenceMissingType` /
  `managedReferenceId` APIs require 2021+ — the adapter (not Core) owns the version-gated calls (§2).
- A concrete-type change **replaces** the instance (old fields are gone); modeling type-change as a
  whole-node `SetManagedReference` (not field patches) prevents applying stale fields to a new type.
- Determinism: never persist Unity `rid`s (registry order is not stable); Core's canonical form is the
  logical tree only.
- Instantiation requires the adapter to construct the concrete type; types without a viable
  construction path are flagged `Unsupported` rather than materialized half-formed.
