# Spec 44 — instance-override build convergence and two-direction reporting

Two defects reproduced by the `excluded-field-one-way-report` probe over the current tree, filed in
`docs/open-defects.md`. They share one theme: the code->scene BUILD path silently declines a
prefab-INSTANCE override that the scene->code SYNC path would surface. One never converges (an op on
every build, forever); the other is dropped with no report while the sync direction reports the same
condition.

This is spec 35 D2 extended to the instance-override surface. D2 closed the same permanent
non-convergence trap for ORDINARY component fields (matched `SetField` loop and the create
`AddComponent` branch) and made the decline loud through the located report channel. Its fix does
not reach the instance-override emit surface, because the gate it introduced
(`SceneBuilder.Core/Diff/ExcludedFieldGate.cs`) is threaded only through `Differ.EmitComponentEdits`,
and `InstanceOverrideDiff.Emit` is a separate call from `Differ.WalkDesired:112` that receives no
gate. Read D2 (`specs/completed/35-reference-writes-and-cache-invalidation.md`, section "D2 — an
authored sorting layer never converges") before this spec; every decision here mirrors one it made.

## D1 — an authored instance override on an adapter-excluded serialized path never converges

**REPRODUCED (`FOUND-BY: excluded-field-one-way-report`).** An authored prefab-instance override on a
serialized path the adapter excludes emits an apply op on every build and never reaches a fixed
point, with no report. Same trap as D2, different authoring surface.

Confirmed by construction, both halves:
- **The snapshot never carries the override.** `com.codescenes/Editor/PrefabInstanceProbe.Overrides.cs:91`
  drops any `PropertyModification` whose `(target type, propertyPath)` pair
  `SerializedFieldExclusions.IsExcluded` rejects (`continue` before the record is built), so the
  excluded path is absent from `SnapshotNode.Overrides`.
- **The differ emits for any desired override with no snapshot counterpart.**
  `SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:88-100` emits a `SetInstanceOverride` whenever a
  desired `(Target, PropertyPath)` key is missing from the snapshot (or its value/reference differs).
  An excluded path is always missing, so the op is emitted every build. `InstanceOverrideDiff.Emit`
  receives no `ExcludedFieldGate` (`Differ.WalkDesired:112` passes only `ops, conflicts`), while the
  sibling `EmitEdits`/`EmitCreate` calls at `Differ.cs:105`/`:137` both receive `fieldGate`.

VERIFIED with a real prefab and a live `SpriteRenderer.m_Size` instance override: Unity holds the
modification (`GetPropertyModifications` returns it, True) but `snapshot.Overrides` carries it False
(dropped at `PrefabInstanceProbe.Overrides.cs:91`); `SetInstanceOverride(m_Size.x) build1=1 build2=1
conflicts=0`. The op is emitted every build, converges never, and no report is surfaced. The same
holds for `m_SortingLayer` on a renderer inside an instance (the exact field D2 covers for ordinary
components).

**The two sibling emit sites carry the same exposure.** `InstanceOverrideDiff.cs:138`
(`AddInstanceComponent`) carries the whole `desiredComponent.Component` (`ComponentData`) and `:208`
(`AddInstanceChild`) carries the whole `desiredAdded.Node` (a `GameObjectNode` with its components),
so an excluded field inside either payload flows through unfiltered. Neither passes through the gate.

**The backstop covers two of the three sites, not the third.** `SceneBuilder.Core/Diff/ExcludedFieldAudit.cs`
attributes an excluded field on `Plan.AddInstanceComponent` and `Plan.AddInstanceChild` (via
`CheckComponentFields`/`CheckNode`, `:40-45`), because those ops carry a `ComponentData`/`GameObjectNode`
it can resolve a `TypeRef` for. It cannot attribute a `SetInstanceOverride`: that op's `Target` names
a nested prefab member, not a model component, and the audit indexes only model components
(`ExcludedFieldAudit.cs:11-14` documents this as a known gap). So even if the gate were threaded, the
`SetInstanceOverride` surface would have no check proving it was.

## D2 — a stale instance override declined on BUILD is reported on SYNC but not on BUILD

**REPRODUCED by construction (`FOUND-BY: excluded-field-one-way-report`).** A stale prefab-instance
override detected on the code->scene build is suppressed with no report to the author, while the same
detection on the scene->code sync IS surfaced.

Confirmed by construction:
- **The build path builds the conflict, then the channel drops it.**
  `InstanceOverrideDiff.DetectStaleOverrides` (`SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:35-63`)
  excludes the stale key from `Set`/`Revert` emission and appends a `ConflictDetector.StaleOverride`
  conflict (`:58-61`). `ConflictDetector.StaleOverride` (`SceneBuilder.Core/Reconcile/ConflictDetector.cs:229-241`)
  is a plain object initializer that sets `Kind`, `LogicalId`, `Reason`, `Location`, and leaves
  `RecurrenceKey` null. The conflict is copied into `Plan.Conflicts` and routed by
  `SceneBuilderBuild.RunCore:301` to `ConflictSurfacing.SurfaceNotes`, which drops any note whose
  `RecurrenceKey` is null (`com.codescenes/Editor/ConflictSurfacing.cs:161-164`). So the build applies
  nothing for the override AND tells the author nothing.
- **The sync path surfaces it.** The sync direction routes the same conflict to
  `ReconcileResult.Conflicts` -> `ConflictSurfacing.SurfaceConflicts`
  (`com.codescenes/Editor/SceneBuilderSync.cs:231`), which logs every entry regardless of
  `RecurrenceKey`.

VERIFIED: `StaleOverride RecurrenceKey=<null>`; the op is suppressed on the plan and no note is
surfaced on the build.

**Why a naive fix spams.** Simply un-dropping keyless notes on the build path would log the standing
condition on every debounced auto-sync build, which fires on every keystroke. The `Notes`/`RecurrenceKey`
channel exists precisely to surface a standing condition once per editor session per key
(`ConflictSurfacing.cs:14-16`, `:155-177`). The fix is to give the declined-override report a stable
per-target recurrence key, not to bypass the dedup.

## Invariant #1 — excluded serialized fields are gated on EVERY emit surface

**Owner.** `SceneBuilder.Core/Diff/ExcludedFieldGate.cs` is the ONE decision point for "an authored
field the adapter excludes never becomes a plan op, and is reported instead." D2 established it for
component edits. This spec extends its reach; it does not add a second gate.

**Mechanism.** `InstanceOverrideDiff.Emit` is given the same `ExcludedFieldGate` instance the differ
already constructs at `Differ.cs:41` (one instance per diff, sharing the `conflicts` list and the
per-`(component, root)` dedup set). `Differ.WalkDesired:112` passes `fieldGate` into
`InstanceOverrideDiff.Emit` alongside `ops`/`conflicts`. Inside `Emit`:
- The `AddInstanceComponent` payload (`:138`) and the `AddInstanceChild` node's components (`:208`)
  are filtered through the gate's existing `Admit(ComponentData[])` before the op is built, exactly as
  the component-create branch does. An excluded field is stripped from the payload and reported once.
- The `SetInstanceOverride` emit (`:88-100`) is admitted through a new override-level entry point on
  the gate, keyed on the override's `OverrideTarget.ComponentType` (a type full name) and its
  `PropertyPath`. An excluded override is not emitted and is reported once.

**The type-name seam.** `IFieldExclusionPolicy.IsExcluded` takes a `TypeRef`
(`SceneBuilder.Core/Model/IFieldExclusionPolicy.cs:11`), but an `OverrideTarget` carries only
`ComponentType`, a bare full-name string (`SceneBuilder.Core/Model/OverrideTarget.cs:12`), no `TypeRef`.
The gate bridges this at ONE place: an exclusion check keyed by type full name (the policy already
reduces a `TypeRef` to `ownerType.FullName` before matching, `SerializedFieldExclusions.cs:121`). The
adapter policy resolves a full name the same way it resolves a `TypeRef`. This crossing is Invariant
#1's one design decision, the sibling of D2's "the exclusion set lives adapter-side and the differ
lives in Core, so the two halves meet somewhere."

**Check.** `ExcludedFieldAudit` is extended to attribute a `Plan.SetInstanceOverride` op. Because that
op's `Target` names a nested prefab member rather than a model component, the audit resolves the type
from the op's `Target.ComponentType` full name directly and reports
`{instanceLogicalId} > {ComponentType} > {propertyPath}` when the path is excluded, closing the gap its
own header documents (`ExcludedFieldAudit.cs:11-14`). The check drives all three instance-override emit
surfaces (`SetInstanceOverride`, `AddInstanceComponent`, `AddInstanceChild`), so no site can carry an
excluded field into a plan op without the audit failing. This is the rule AND the check, not a rule
stated once and trusted at three sites.

## Invariant #2 — a declined instance override is reported on BOTH directions, once per session

**Owner.** The report channel is the one D2 uses: a `Conflict` carrying a stable `RecurrenceKey`,
surfaced through `ConflictSurfacing.SurfaceNotes` (once per editor session per key) rather than the
per-pass `SurfaceConflicts` channel.

**Mechanism — the recurrence key.** Two report kinds gain a stable per-target key:
- The **excluded-override** report (Invariant #1's report side). Its identity is
  `(instanceLogicalId, OverrideTarget, PropertyPath)`. Mirror `Conflict.UnauthorableField`'s key shape
  (`unauthorable-field:{componentLogicalId}:{fieldKey}`, `Conflict.cs:208`), extended so the
  `OverrideTarget` is part of the key: two nested members sharing a `PropertyPath` must not dedup each
  other. Reusing `Conflict.UnauthorableField` is acceptable when the key it composes carries the
  target; a dedicated factory is acceptable too. Either way the located report names the instance, the
  override's component type and the property path, and states the line has no effect (D2's
  `Conflict.LineHasNoEffect` copy).
- The **stale-override** report. `ConflictDetector.StaleOverride` (`ConflictDetector.cs:229-241`) is
  given a stable `RecurrenceKey` of the form `stale-override:{instanceLogicalId}:{ComponentType}:{SubKey}:{propertyPath}`,
  derived from the `(LogicalId, OverrideTarget, PropertyPath)` it already receives. Nothing else about
  the factory changes.

**Mechanism — both directions.** With a stable key assigned, the build path stops dropping the report:
`SurfaceNotes` now surfaces it once per session (`ConflictSurfacing.cs:161-166` no longer hits the
null-key `continue`). The sync path routes the same keyed report through the once-per-session channel
too, so a debounced sync does not re-log it on every keystroke (closing the per-pass behaviour the
sync side has today at `SceneBuilderSync.cs:231`). The condition reads identically in both directions
because the key is composed in ONE factory, the same discipline `Conflict.UnsyncableListener` uses so
the read and write directions cannot spell one condition two ways (`Conflict.cs:217-227`).

**Severity.** A declined override is "a line the user wrote is not reaching their scene," the mirror of
D2's `UnauthorableField`, which `ConflictSurfacing.Severity` already surfaces at WARNING
(`ConflictSurfacing.cs:186-189`). `StaleOverride` and the excluded-override kind read at WARNING for
the same reason.

## Core deliverables

- `InstanceOverrideDiff.Emit` (`SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:17`) takes an
  `ExcludedFieldGate` parameter, threaded from `Differ.WalkDesired:112`. Its `SetInstanceOverride`,
  `AddInstanceComponent` and `AddInstanceChild` emits admit their fields/payload through the gate; an
  excluded field is dropped and reported once through the gate's existing report list.
- `ExcludedFieldGate` (`SceneBuilder.Core/Diff/ExcludedFieldGate.cs`) gains an override-level admit
  keyed on `(OverrideTarget.ComponentType full name, PropertyPath)`, and the excluded-override report
  carrying the stable key of Invariant #2.
- `IFieldExclusionPolicy` (`SceneBuilder.Core/Model/IFieldExclusionPolicy.cs`) gains a full-name-keyed
  exclusion check (or the gate composes a `TypeRef` whose `FullName` is the `OverrideTarget.ComponentType`),
  so the override surface reuses the one exclusion set.
- `ConflictDetector.StaleOverride` (`SceneBuilder.Core/Reconcile/ConflictDetector.cs:229`) sets a stable
  `RecurrenceKey`; no other field changes.
- `ExcludedFieldAudit` (`SceneBuilder.Core/Diff/ExcludedFieldAudit.cs`) attributes a
  `Plan.SetInstanceOverride` op against the exclusion policy via its `Target.ComponentType`, so the
  bypass check covers all three instance-override emit surfaces.
- The bypass check test: a snapshot-vs-desired diff over a prefab instance whose authored override
  targets an excluded path, driven through each of the three emit surfaces, asserts the plan carries no
  op for the excluded field and `ExcludedFieldAudit.EmittedExclusions` is empty (no site bypassed the
  gate).

## Editor adapter deliverables

The read path that feeds D1 is Unity-observable: `PrefabInstanceProbe.Overrides.cs:91` drops the
excluded modification, and only a live `PrefabUtility.GetPropertyModifications` over a real instance
exercises it. That boundary is where the non-convergence escapes, so it needs an EditMode test in
`unity-gate/Assets/GateTests/` that authors a real instance override on an excluded path and builds
twice against a live scene. The POCO fixtures cannot reproduce the "Unity holds it True, snapshot
carries it False" asymmetry.

The build-path surfacing of D2 (`SurfaceNotes` no longer dropping the keyed report) and the WARNING
severity are also adapter-observable and belong in the same EditMode coverage.

## Decomposition guidance

Hoist the two cross-cutting mechanisms; do not restate them per task.

- **Task A — gate every instance-override emit surface (Invariant #1, its own owning task).** Thread
  `ExcludedFieldGate` into `InstanceOverrideDiff.Emit`; add the override-level admit and the full-name
  exclusion seam; extend `ExcludedFieldAudit` to attribute `SetInstanceOverride`; write the bypass
  check driving all three surfaces. This is the ONE mechanism D1 needs and it owns the check.
  TOUCHES: `SceneBuilder.Core/Diff/InstanceOverrideDiff.cs`, `SceneBuilder.Core/Diff/Differ.cs`,
  `SceneBuilder.Core/Diff/ExcludedFieldGate.cs`, `SceneBuilder.Core/Diff/ExcludedFieldAudit.cs`,
  `SceneBuilder.Core/Model/IFieldExclusionPolicy.cs`,
  `com.codescenes/Editor/SerializedFieldExclusions.cs` (the policy's full-name entry point),
  `SceneBuilder.Core.Tests/**` (the bypass check and the headless convergence sim),
  `unity-gate/Assets/GateTests/**` (the live excluded-override convergence test).
- **Task B — report a declined override on both directions once per session (Invariant #2).** Assign
  the stable recurrence key on `StaleOverride` and the excluded-override report; route both directions
  through the keyed `SurfaceNotes` channel; add the WARNING severity. Depends on Task A (it reports the
  same declines Task A stops emitting). TOUCHES:
  `SceneBuilder.Core/Reconcile/ConflictDetector.cs`, `SceneBuilder.Core/Reconcile/Conflict.cs` (if the
  excluded-override report gets a dedicated factory), `com.codescenes/Editor/ConflictSurfacing.cs`
  (Severity), `com.codescenes/Editor/SceneBuilderSync.cs` (route the keyed report through the Notes
  channel), `SceneBuilder.Core.Tests/**` (the recurrence-key composition test),
  `unity-gate/Assets/GateTests/**` (the build-path surfacing test).

Keep each task's TOUCHES complete: a task that omits a file it edits mis-scopes the gate.

## Core and adapter test plan

RED tests, behavior not structure:

- **D1 convergence, all three surfaces (Core, headless).** A prefab instance whose authored override
  targets an excluded path (`m_Size` / `m_SortingLayer`), and an added component / added child carrying
  an excluded field. Diff against a snapshot the adapter would produce (excluded path absent). Assert
  no `SetInstanceOverride` / no excluded field in the `AddInstanceComponent` / `AddInstanceChild`
  payload, and `ExcludedFieldAudit.EmittedExclusions` empty.
- **D2 recurrence key (Core, headless).** A stale override detected on a diff. Assert the
  `StaleOverride` conflict carries a non-null, stable `RecurrenceKey` and that two diffs of the same
  standing condition compose the same key.
- **D1 convergence (EditMode, required).** Author a real `SpriteRenderer.m_Size` instance override on
  a prefab instance in a live scene. Build. Build again. Assert the second build emits zero ops for
  the override and a located WARNING was surfaced exactly once naming the instance, the component type
  and the path.
- **D2 build-path surfacing (EditMode, required).** Construct a stale override (the prefab asset's
  default drifted and the live value now equals the new default). Build. Assert a located WARNING is
  surfaced on the build and is NOT re-logged on a second build in the same session.

## Unity confirmation checklist

1. In a live scene with a prefab instance, override a serialized field the adapter excludes (e.g.
   `SpriteRenderer.m_Size` or a renderer's `m_SortingLayer`) via an authored `.Override(...).Set(...)`.
   Build twice. Expected: the second build reports zero ops for that override (a fixed point), and a
   located WARNING naming the instance, the component type and the path is logged once, telling the
   author the line has no effect.
2. Author an added component or added child under an instance carrying an excluded field. Build.
   Expected: the excluded field is dropped from the applied override, a located WARNING is surfaced,
   and a second build is a fixed point.
3. Create a stale override (change the prefab asset's default so the recorded base no longer matches
   and the live value equals the new default). Build. Expected: the override is declined AND a located
   WARNING is surfaced on the build (matching what a sync already reports); a second build in the same
   session does not re-log it.

## Dependencies

- Spec 35 D2 (`ExcludedFieldGate`, the located report channel, the once-per-session `Notes` dedup).
  This spec is D2 extended to the instance-override surface.
- M6/M10 prefab instances (`InstanceOverrideDiff`, `SetInstanceOverride`/`AddInstanceComponent`/
  `AddInstanceChild`, `PrefabInstanceProbe`, the structured `Overrides` snapshot shape).
- Spec 33's located report channel and section 7 fail-loud philosophy.

## Not in scope

- The unconditional scene re-save on every build (`SceneBuilderBuild.cs:241-242` marks dirty and saves
  regardless of `plan.Ops.Length`), filed separately in `docs/open-defects.md`. Suppressing an op does
  not stop the save; that is a distinct, larger question. D2 already ruled it out of scope and this
  spec inherits that ruling.
- `ReconcileResult.Skipped` logging once per skipped field on every sync (the low-severity per-pass
  spam entry in `docs/open-defects.md`). It is the same missing-dedup shape but a different channel
  (`Skipped`, not `Conflicts`); closing it needs its own recurrence-key decision on that channel.
- The `OpaqueOverrides` residue (`Differ.cs:119-127`) stays read-only and unmodelled; nothing here
  touches it.
