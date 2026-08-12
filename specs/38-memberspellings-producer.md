# Spec 38 — the adapter never produces MemberSpellings, so every non-Button UnityEvent listener drops

## Measured defect (do not re-derive)

A fresh, Inspector-added persistent UnityEvent listener on ANY serialized UnityEvent field EXCEPT
`Button.m_OnClick` is silently dropped on scene->code sync instead of being reconciled into source as
`.OnClick(...)` / `.OnEvent(x => x.field, ...)`. Measured live: on a host carrying a `UnityEvent
onPlain` and a `UnityEvent<float> onFloat`, adding a listener in-editor (void, static-float, or
dynamic, each targeting a public compiled method) and syncing produces `PatchEdits == 1` only for the
sibling `Button.m_OnClick`; the other three drop with a `UnsyncableListener` note reading "no public
C# spelling". The decisive variable is the FIELD KEY, not the listener mode, not the generic
`UnityEvent<T>` type, and not the `.OnEvent` form.

The mechanism, verified in code:

- The adapter's snapshot factory NEVER sets `MemberSpellings`. `SceneSnapshotReader.FromRoots`
  (`com.codescenes/Editor/SceneSnapshotReader.cs:98-104`, the `new SceneSnapshot { ... }` build site)
  populates `SchemaVersion`, `Roots`, `ComponentDefaults`, and `FieldExclusions`, and leaves
  `MemberSpellings` at its `SceneSnapshot` default of `Array.Empty<MemberSpelling>()`
  (`SceneBuilder.Core/Model/SceneSnapshot.cs:22`). This factory is documented as the one construction
  point every producer routes through (`SceneSnapshotReader.cs:61-65`), so the omission covers the
  cold read and the incremental assembles alike. There is NO producer anywhere in `com.codescenes/`:
  a grep for `MemberSpellings` assignments finds only the two Core-test snapshots that hand-feed it
  (`SceneBuilder.Core.Tests/UnityEventReconcileSourcePatchTests.cs:82`,
  `SceneBuilder.Core.Tests/FoundationDeltaTests.cs:111`) and the model default. The sibling
  `ComponentDefaults` IS populated at the same site, from the same per-type walk.
- The public-name machinery the producer needs already exists, unused by any snapshot path:
  `SerializedMemberMap.TryPublicMemberName(Type declaringType, string serializedName, out string
  member)` (`com.codescenes/Editor/SerializedMemberMap.cs:205`) resolves `m_OnValueChanged` ->
  `onValueChanged` and returns false when no compiling spelling exists.
- Core builds the lookup from an always-empty array. `Reconciler.Reconcile` builds
  `MemberSpellingIndex.Build(actual.MemberSpellings)` once per pass
  (`SceneBuilder.Core/Reconcile/Reconciler.cs:63`); with `MemberSpellings` empty, the index is
  `MemberSpellingIndex.Empty`.
- The listener reconciler drops on the empty index. For any `fieldKey != "m_OnClick"`, the spelling
  lookup fails and the code emits `Conflict.UnsyncableListener(..., UnresolvedEventMemberName)` then
  `continue`s with NO edit (`SceneBuilder.Core/Reconcile/ComponentReconciler.UnityEvents.cs:99-109`),
  and this runs BEFORE the ADD/append branch at `:144-148`. `m_OnClick` is the single hardcoded
  bypass (the `OnClickFieldKey` guard at `:100`, and the `OnClickPublicName` render arms at
  `:167,:172`). A listener field is keyed by its serialized `propertyPath`
  (`com.codescenes/Editor/SerializedFieldBridge.cs:134`), so a user field serializes as `onPlain` /
  `onFloat` and Button's as `m_OnClick`. The field key alone decides the drop.

Mocked-test blind spot (per the "passing tests are not working software" rule): a Core test CANNOT
catch this defect. `UnityEventReconcileSourcePatchTests` hand-feeds a `MemberSpelling[]` into its
snapshot (`ButtonSnapshot(fieldKey, memberSpellings, ...)` at `:65-83`; the `.OnEvent` matrix passes
`spellings` at `:351-354`), so the Core `.OnEvent` matrix passes green while the live adapter feeds
nothing. Worse, `Reconcile_OnEventFieldMissingMemberSpelling_ReportsUnresolvedEventMemberName_NoPatch`
(`:559-573`) passes `null` for `memberSpellings` on a NORMAL field (`m_OnValueChanged`) and pins the
drop as if it were correct, masking the missing producer. The only test that can see the defect runs
Unity, because the defect lives at the adapter boundary the Core POCO fixtures cannot reach.

## Goal

A persistent listener the author wires in the Inspector on ANY serialized UnityEvent field, targeting
a method that has a compiling public C# spelling, reconciles into source on sync as
`.OnEvent(x => x.<field>, ...)` (or `.OnClick(...)` for the Button field), exactly as `m_OnClick`
already does. The fix supplies the one missing piece: the adapter producer that fills
`SceneSnapshot.MemberSpellings`.

## In scope

- A producer in the adapter snapshot factory that emits one `MemberSpelling { Type, SerializedPath,
  PublicName }` for each serialized UnityEvent field of each component type in the snapshot, with
  `PublicName` derived through the existing `SerializedMemberMap.TryPublicMemberName`. Placed at the
  single `SceneSnapshot` construction factory (`SceneSnapshotReader.FromRoots`) beside the
  `ComponentDefaults` population, so every read path (cold and incremental) inherits it by construction
  rather than opting in per site.
- Correcting the mis-pinning Core test at
  `UnityEventReconcileSourcePatchTests.cs:559-573` so it asserts the drop only for a field whose
  serialized member genuinely has no public C# spelling, not for a normal field like
  `m_OnValueChanged`.

## Out of scope

- The intentional genuinely-no-public-spelling `UnsyncableListener` path. A serialized member whose
  backing is private with no compiling public property (so `TryPublicMemberName` returns false) still
  yields `UnsyncableListener`, and that remains correct. This spec fixes the MISSING producer; it does
  not remove the deliberate no-spelling report.
- Any Core reconcile change. The Core delta/append path
  (`ComponentReconciler.UnityEvents.cs:99-148`) already reconciles the listener correctly once the
  spelling index is non-empty; no Core code is touched except the corrected test.
- `Button.m_OnClick`. It already round-trips through its hardcoded bypass
  (`ComponentReconciler.UnityEvents.cs:100,:167,:172`) and is unchanged.

## Additions to the contract

None. The `MemberSpelling` record (`SceneBuilder.Core/Model/MemberSpelling.cs:11`), the
`SceneSnapshot.MemberSpellings` array (`SceneSnapshot.cs:22`), and the `MemberSpellingIndex` consumer
(`Reconciler.cs:63`) all already exist, delivered by M8 (spec 09) and the M8 foundation work under
spec 31/33. This spec supplies the adapter PRODUCER that was never written for them. Everything else
binds to `00-foundation.md` verbatim (§2 seam: Core is Unity-free and cannot reflect a public member
name, so the adapter must supply it).

## Editor adapter deliverables

> Built by the pipeline, gated by the Unity EditMode suite in `unity-gate/`. Not hand-wired.

- **The MemberSpellings producer.** For each serialized UnityEvent field encountered while reading a
  component, emit one `MemberSpelling { Type = the component `TypeRef`, SerializedPath = the field's
  serialized name, PublicName = the resolved public spelling }`. A UnityEvent field is the same one
  `SerializedFieldBridge` already recognizes via `UnityEventReader.IsUnityEventField`
  (`SerializedFieldBridge.cs:118`). `PublicName` is derived through
  `SerializedMemberMap.TryPublicMemberName(componentType, serializedName, out publicName)`
  (`SerializedMemberMap.cs:205`). The producer sets `SceneSnapshot.MemberSpellings` at the single
  factory `SceneSnapshotReader.FromRoots` (`SceneSnapshotReader.cs:98-104`), the same site that sets
  `ComponentDefaults`, so no read caller can obtain a snapshot that skipped it.
- **A field with no compiling public spelling emits no entry.** When `TryPublicMemberName` returns
  false, no `MemberSpelling` is emitted for that field. The index then has no key for it, and the
  reconciler's existing `UnsyncableListener` path fires, which is the correct outcome for a member
  that genuinely cannot be named in source.

## Decomposition guidance

This is one adapter producer plus tests: small. Do not over-split.

- **(a) The producer + tests.** Owns the producer in `SceneSnapshotReader.cs` (and any small helper it
  factors out) and its use of `SerializedMemberMap.TryPublicMemberName`. TOUCHES
  `com.codescenes/Editor/SceneSnapshotReader.cs`, the new/edited producer source, the EditMode test in
  `unity-gate/Assets/GateTests/`, the direct-adapter EditMode assertion, and the corrected Core test
  `SceneBuilder.Core.Tests/UnityEventReconcileSourcePatchTests.cs`. If the producer derives spellings
  by walking a per-type template the way `ComponentDefaultTemplate` does, that file is in TOUCHES too;
  if it collects during the live component read in `SerializedFieldBridge.CollectFields`, that file is
  in TOUCHES instead. Keep the producer and its single application point in ONE task: the invariant is
  "every snapshot carries the spellings for its UnityEvent fields", and splitting the derivation from
  the factory that stamps it reintroduces the opt-in hazard.

The primary RED test is EditMode, because a Core test structurally cannot see this defect (that IS the
defect). Enumerate the RED cases below literally so the symmetric set is not pruned.

## Core / adapter test plan (RED)

The PRIMARY RED test MUST be EditMode in `unity-gate/Assets/GateTests/`. A Core test cannot see the
defect, so a Core-only RED would pass while the live adapter still drops.

EditMode (`unity-gate/`), required per the Unity-facing coverage rule (adapter behavior at the
SerializedProperty boundary):

- **A non-`m_OnClick` UnityEvent listener reconciles into source.** Build a scene with a component
  carrying a non-`m_OnClick` UnityEvent field (a plain `UnityEvent onPlain` and/or a `UnityEvent<float>
  onFloat` on a fixture host). Add a persistent listener in-editor targeting a public compiled method,
  then Sync. *Expected:* (a) the builder source gains `.OnEvent(x => x.<field>, target, ...)` (or the
  `.OnClick`-shaped call for a plain Button add), and (b) `result.Notes` carries NO `UnsyncableListener`
  / `UnresolvedEventMemberName` for that field. Today the source gains nothing and the note fires; the
  test is RED until the producer runs.
- **The producer populates MemberSpellings directly.** A direct assertion that
  `SceneSnapshotReader.Read` over a scene containing a component with a UnityEvent field returns a
  `SceneSnapshot` whose `MemberSpellings` contains an entry for that field, with the correct component
  `Type`, the serialized `SerializedPath`, and the `PublicName` that `TryPublicMemberName` yields.
  Today `MemberSpellings` is empty; the test is RED.

Core (`SceneBuilder.Core.Tests`), a correction, not new coverage:

- **Correct the mis-pinning test.**
  `Reconcile_OnEventFieldMissingMemberSpelling_ReportsUnresolvedEventMemberName_NoPatch`
  (`UnityEventReconcileSourcePatchTests.cs:559-573`) currently pins the drop for a normal field
  (`m_OnValueChanged`) fed a `null` spelling array, which is exactly the live defect masquerading as
  correct behavior. Rewrite it to assert the `UnsyncableListener` drop ONLY for a serialized member
  that genuinely has no public spelling (the out-of-scope path), so it no longer certifies the bug.

## Unity confirmation checklist (EditMode, `unity-gate/`)

1. **A non-Button UnityEvent listener round-trips.** Author a host with a `UnityEvent onPlain` field,
   Build, add a persistent listener in the Inspector targeting a public compiled `void` method, Sync.
   *Expected:* source gains `.OnEvent(x => x.onPlain, <target>, m => m.<Method>())`; no
   `UnsyncableListener` note for `onPlain`; a second Sync is a no-op.
2. **A generic `UnityEvent<T>` field round-trips the same way.** Repeat with a `UnityEvent<float>
   onFloat` field wired dynamically to a matching `float` method. *Expected:* source gains
   `.OnEvent(x => x.onFloat, <target>, h => h.<Method>, dynamic: true)`; no note; converges.
3. **`Button.m_OnClick` still works and a genuinely-unspellable field still reports.** A scene mixing a
   Button `m_OnClick` listener and a listener on a UnityEvent field whose serialized member has no
   compiling public spelling: the Button reconciles, the unspellable field still surfaces
   `UnsyncableListener`, and neither regresses the other.

## Dependencies

- **M8 / spec 09 (UnityEvents)** — the `.OnClick` / `.OnEvent` authoring surface, `ValueNode.
  UnityEventListeners`, the reconciler's listener path, and the `m_OnClick` bypass this spec extends to
  every field.
- **Spec 31 / M8 foundation (the MemberSpelling reader contract)** — the `MemberSpelling` record,
  `SceneSnapshot.MemberSpellings`, and `MemberSpellingIndex` were added to carry the public member
  name to Core; this spec writes the producer they were built to receive.
- **M2 (Reconcile)** — the source-patch machinery that emits the `.OnEvent(...)` / `.OnClick(...)`
  call, unchanged here.

## Risks/notes

- **Place the producer at the factory, not per read site.** `SceneSnapshotReader.FromRoots` is the one
  `SceneSnapshot` construction point (`SceneSnapshotReader.cs:61-65`); the cold read and
  `ChangeScopedSnapshot`'s incremental assembles both route through it. Setting `MemberSpellings` there,
  beside `ComponentDefaults`, makes every snapshot carry the spellings by construction. A producer bolted
  onto only the cold path leaves the incremental sync (the common auto-sync case) still empty.
- **Do not remove the no-spelling report.** A member `TryPublicMemberName` cannot name still yields
  `UnsyncableListener`, and that is the intended fail-loud for a listener the author cannot express in
  source (§7). The corrected Core test must keep asserting it for that genuine case.
- **The corrected test is the tell.** Leaving `...MissingMemberSpelling...NoPatch` pinned against a
  normal field would let a future regression of the producer pass green again. Rewriting it against a
  genuinely-unspellable member is part of the fix, not an optional cleanup.
