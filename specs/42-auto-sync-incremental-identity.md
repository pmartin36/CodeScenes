# Spec 42 — Auto-sync incremental identity: O(changed), not O(scene)

Two reproduced performance defects, both filed in `docs/open-defects.md`, sharing one root cause:
auto-sync's promised O(changed) per-cycle identity cost is not delivered. Every debounced keystroke
pays O(scene) — a whole-scene `GlobalObjectId` batch plus a full `SerializedObject` read of every
component, then a second uncached slow resolve per assigned scene-reference field. CLAUDE.md rates a
full-scene walk per keystroke fatal, not a tradeoff. Auto-sync is functionally correct today; this
is purely scalability, and it is the high-priority item because the product IS continuous sync.

Both defects were derived by construction from the call chain, and one has a measured
resolution-count reading; neither is timed. This spec closes them together because they are the same
failure — an identity resolve that escapes the counted, cached seam — reached by two paths.

## Build order

Both defects land in one milestone; Task A (the counted seam and its invariant check) is the
shared mechanism Task B's ref-resolver threading depends on. See Decomposition guidance.

## Defect 1 — every cycle cold-assembles the whole scene

The incremental read path exists and is used, and then a cold re-assemble at the cycle tail throws
its result away, so the O(changed) cache never survives a single cycle.

Confirmed by construction:

- The three cycle bodies read the live scene INCREMENTALLY at their head:
  `SceneBuilderAutoSync.ExecuteSceneToCode:589-591` and `ExecuteBothChanged:775-777` call
  `assembler.AssembleIncremental(scene, ids, sceneRef)` when `ids.Count > 0`. That is the O(changed)
  path working as designed.
- Every cycle body then calls `CaptureBaseline(scene)` at its tail: `ExecuteSceneToCode:598`,
  `ExecuteCodeToScene:679`, `ExecuteBothChanged:795`.
- `CaptureBaseline` (`SceneBuilderAutoSync.cs:697-726`) fetches the SAME per-builder assembler via
  `GetAssembler` (`:104-111`, one cached `ChangeScopedSnapshot` per builder in `_snapshotAssemblers`
  `:102`) and calls `assembler.AssembleCold(scene, sceneRef)` unconditionally at `:725`.
- `AssembleCold` (`com.codescenes/Editor/ChangeScopedSnapshot.cs:32-55`) does `Ids.Clear()` (`:34`),
  `Ids.WarmBatch(CollectAllObjects(scene))` (`:35` — one whole-scene `GetGlobalObjectIdsSlow` batch
  over every GameObject and non-Transform component), a full `SceneSnapshotReader.ReadNode` per root
  (`:41`, `:49` — a full `SerializedObject` read of every component), then replaces `_nodeByGoEntityId`
  wholesale (`:52`) and re-stamps `_cacheGeneration` (`:53`).

So the incremental read at the head of cycle N+1 is served by a cache that the cold `CaptureBaseline`
of cycle N rebuilt from scratch, and cycle N already paid the full-scene walk to build it. The
per-keystroke cost is O(scene) regardless of change-set size. The incremental cache buys nothing
while `CaptureBaseline` cold-reads every cycle.

This is not fixable inside the assembler alone. The baseline is a whole-scene converged snapshot read
under the POST-sync sidecar, and no change-set is threaded into `CaptureBaseline` today. Reusing the
already-computed incremental snapshot as the baseline is a contract change to `_baselines` and to the
`CaptureBaseline` signature, not a localized edit.

## Defect 2 — the scene-reference resolver bypasses the counted cache

Node identity resolves through the counted, cached seam; reference-field resolution resolves the
target's `GlobalObjectId` uncached on every read, and the perf gate cannot see it.

Confirmed by construction:

- `ChangeScopedSnapshot` reads each node with TWO distinct resolvers:
  `SceneSnapshotReader.ReadNode(go, Ids.Resolve, sceneRef.Resolve, sceneRef.ResolveListener)`
  (`ChangeScopedSnapshot.cs:41`, cold) and the same argument shape at `:119` (incremental,
  `ReadNodeShallow`). The second argument, `Ids.Resolve`, is the counted `GlobalObjectIdCache`
  (`com.codescenes/Editor/GlobalObjectIdCache.cs`) used for NODE identity. The third argument,
  `sceneRef.Resolve`, resolves reference FIELDS and is a different object.
- `sceneRef.Resolve` is `ObjectReferenceResolver.BuildFromIndex`'s returned closure
  (`com.codescenes/Editor/ObjectReferenceResolver.cs:183-196`). It calls
  `GlobalObjectId.GetGlobalObjectIdSlow(go).ToString()` (`:193`) on every invocation, cache-free.
- That closure is invoked from `AssetReferenceResolver.ReadObjectReferenceValue`
  (`com.codescenes/Editor/AssetReferenceResolver.cs:473,483` — `var id = resolveSceneRef(obj)`), once
  per non-asset object reference, on both the cold and incremental read paths.
- `SceneRefResolver.ForMap` (`com.codescenes/Editor/SceneRefResolver.cs:41-48`) builds that closure
  with no cache, and also builds `BuildListenerResolver`, whose closure resolves listener targets with
  another uncached lambda `o => GlobalObjectId.GetGlobalObjectIdSlow(o).ToString()`
  (`ObjectReferenceResolver.cs:240`).
- `GlobalObjectIdCache.ResolutionCount` (`GlobalObjectIdCache.cs:19,28-40`) counts only misses on
  `Ids.Resolve` / `Ids.WarmBatch`. The ref and listener resolvers never touch `Ids`, so their slow
  resolves are neither cached nor counted.

Measured: with N=6 objects each holding an assigned scene reference, `Ids.ResolutionCount` reads 13
with AND without the 6 reference fields (identical — the counted cache is blind to reference reads),
and 6 direct resolver invocations leave `Ids.ResolutionCount == 0`. So a scene of N reference-holding
objects costs N uncached slow resolves per cold assemble on top of the counted node-identity ones,
and the repo's perf gate
(`unity-gate/Assets/GateTests/AutoIdentityTests.cs:76-105`,
`Identity_SingleFieldEdit_ResolutionCountProportionalToChangeSet`, asserting `Ids.ResolutionCount == 1`)
is structurally blind to every one of them.

The intent already exists and is unwired. `ObjectReferenceResolver.StampListenerReference`
(`ObjectReferenceResolver.cs:207-219`) REQUIRES its `resolveGlobalObjectId` parameter precisely "so a
caller on the per-keystroke sync path passes a cache rather than silently paying
`GetGlobalObjectIdSlow`" (`:203-206`) — but the production callers on the sync path pass an uncached
lambda anyway.

## The one owning mechanism: a single counted identity seam threaded to BOTH consumers

**Owner.** `GlobalObjectIdCache` — the `Ids` instance on the per-builder `ChangeScopedSnapshot` — is
the one seam through which every `GlobalObjectId` slow resolve in an assemble passes. It already owns
node identity, keyed on `EntityId`, with `ResolutionCount` as the counting seam and `Invalidate` /
`WarmBatch` / generation degradation as its lifetime rules.

**Mechanism.** The scene-reference resolver and the listener-target resolver resolve their target's
`GlobalObjectId` through that same `Ids.Resolve`, not through a bare `GetGlobalObjectIdSlow`. The
expensive slow call is what is shared and cached; the cheap `goid -> LogicalId` dictionary lookup
(`IdentityNodeIndex.GlobalObjectIdToLogicalId`) stays where it is. Concretely, the assemble binds the
resolver's slow-resolve to `Ids` at the point the assembler already holds both — `SceneRefResolver`
and `ChangeScopedSnapshot.Ids` meet in `AssembleCold`/`AssembleIncremental`, and the resolver closures
must be given the cache rather than closing over `GetGlobalObjectIdSlow`. Node identity, reference
fields and listener targets then share one counted, cached, invalidatable seam.

**Invalidation policy for a moved / reparented / renamed target.** The policy is inherited from the two
mechanisms `Ids` already carries; the fix adds no third invalidation site, and the spec must prove
that is sufficient:

- The cached unit is the target's `GlobalObjectId` string, keyed on its `EntityId`. It is stable for a
  live object's lifetime. A target whose transform is reparented, or that is renamed, is itself in the
  change set for that cycle, so `AssembleIncremental` already invalidates its `Ids` entry
  (`ChangeScopedSnapshot.cs:70-93`) and the next read re-resolves it.
- The `goid -> LogicalId` projection is a function of the IdentityMap, whose generation keys the whole
  node cache (`SceneRefResolver.Generation` / `_cacheGeneration`, `ChangeScopedSnapshot.cs:53,65-68`,
  shipped by spec 35 D3). A rename or reparent that changes a LogicalId changes the sidecar, bumps the
  generation, and degrades the next assemble to a single cold read — never a stale reference.
- A referrer NOT in the change set keeps its cached node and its cached `ObjectRef`. That is correct
  exactly when neither of the two rules above fired: same generation, target's `EntityId` cache entry
  untouched. The spec REQUIRES a byte-equality test that moves/renames a reference target and asserts
  an incremental assemble equals a fresh cold read, to prove no case changes a referrer's resolved
  value without tripping one of the two rules. If such a case is found, the resolver's cache entry for
  the referrer must be invalidated on it, and that becomes the task's named decision — not a silent
  gap.

## The assembler-lifecycle contract: reuse the incremental snapshot, do not cold re-assemble

A converged auto-sync cycle must not perform an unconditional full cold walk at its tail. The contract:

- **What survives a cycle:** the assembler's `_nodeByGoEntityId` node cache and its warmed `Ids`. These
  are already per-builder and already survive between cycles; today `CaptureBaseline` discards both by
  cold-reading.
- **The baseline is the cycle's own live snapshot.** `AssembleIncremental` returns a WHOLE-scene
  `SceneSnapshot` (it walks every root, reusing cached nodes for the unchanged ones), not a partial
  one. For a scene->code cycle the live scene is unchanged by the cycle body (the sync writes source
  and the sidecar, not the scene), so the snapshot already computed at the cycle head IS the converged
  whole-scene baseline. `CaptureBaseline` reuses it rather than re-reading cold.
- **What invalidates reuse:** a generation change between the head read and the baseline capture (the
  sync rewrote the sidecar with a new mapping). On a converged, zero-mapping cycle the generation is
  stable and reuse is valid; on a cycle that genuinely mapped a new object the generation bumps once
  and the assembler degrades to a single cold read that cycle, then returns to incremental. O(scene) is
  paid only when the scene's identity actually changed, never on a keystroke that changed a value.
- **Both directions named.** `CaptureBaseline` takes the just-applied cycle's change set — the changed
  `EntityId`s for scene->code, the ids the plan materialized for code->scene — and assembles
  incrementally against it, falling to a cold read only on a generation change or a cold session (no
  prior cache). The code->scene tail (`ExecuteCodeToScene:679`) modifies the scene via the build, so
  its baseline read is bounded by what the build changed, not by scene size; the plan already knows
  those ids. `CaptureBaseline`'s current cold call at `ChangeScopedSnapshot.cs`-via-`:725` is replaced
  by an incremental one under this contract.

## The invariant and the check that fails on bypass

**Invariant.** An auto-sync cycle's identity-resolution cost is proportional to the change set, not to
scene size — across the WHOLE cycle, head read through baseline capture, and across node identity,
reference fields and listener targets alike.

**Owner / mechanism** as above: `GlobalObjectIdCache` is the single counted seam, and every slow
resolve (node, reference, listener) routes through it.

**Check that fails on bypass.** `GlobalObjectIdCache.ResolutionCount`, extended to count the reference
and listener resolves BECAUSE they now flow through `Ids.Resolve`, is asserted proportional to the
change set over a FULL converged cycle (head incremental read AND baseline capture) in a scene of N
reference-holding objects with a single changed field. Today this trips twice over: the uncached ref
resolves add N uncounted-until-routed resolves, and the tail `AssembleCold` does `Ids.Clear()` then a
whole-scene `WarmBatch` (N resolves) — either inflates the count. After the fix the count stays O(1).
This is the mechanized guard: the bypass is not a rule stated in prose but a value the same counting
seam reports, so an uncounted slow resolve or a per-cycle full-scene walk cannot pass. The existing
`Identity_SingleFieldEdit_ResolutionCountProportionalToChangeSet` is extended (reference fields added
to its scene, and the assertion carried across a baseline capture) rather than duplicated, so no future
edit can reintroduce either defect without failing it.

## In scope

- Bind the scene-reference resolver (`ObjectReferenceResolver.BuildFromIndex`) and the listener-target
  resolver (`BuildListenerResolver`) to the assembler's `GlobalObjectIdCache`, so reference and listener
  identity resolves are cached and counted through the one seam. Extend `ResolutionCount` accounting to
  cover them.
- Change the `CaptureBaseline` / `_baselines` contract so a converged cycle reuses the cycle's
  incremental snapshot (or an incremental re-assemble under the just-applied change set) instead of an
  unconditional `AssembleCold`, cold-reading only on a generation change or a cold session.
- Extend the perf gate so `ResolutionCount` proportionality holds across a full cycle and over
  reference-holding objects; add the target-move byte-equality proof for the invalidation policy.

## Out of scope

- Debounce, settle timing, and the `ObjectChangeEvents` routing (`SceneBuilderAutoSync` pump) are
  unchanged; this spec is about per-cycle identity cost, not when a cycle fires.
- The unconditional scene re-save on a zero-op build (`SceneBuilderBuild.cs:241-242`, a separate
  `docs/open-defects.md` entry) is a different per-cycle cost and is NOT folded in here.
- The `ReconcileResult.Skipped` per-field warning on every sync (a separate `docs/open-defects.md`
  entry) is unrelated console noise, not identity cost.
- No new Core value type and no change to snapshot content or byte layout: the invariant is that the
  cheaper path produces the byte-identical snapshot the cold path does today, proven by the existing
  and extended byte-equality tests.

## Core and adapter deliverables

- `ObjectReferenceResolver.BuildFromIndex` (`ObjectReferenceResolver.cs:183`) and
  `BuildListenerResolver` (`:237`) resolve their target `GlobalObjectId` through an injected
  `GlobalObjectIdCache` rather than a bare `GetGlobalObjectIdSlow` (`:193`, `:240`). `SceneRefResolver`
  (`SceneRefResolver.cs`) carries or is handed that cache at assemble time so node identity and
  reference/listener resolution share one counted seam; the generation semantics (`:30`, `ForMap:41`)
  are preserved.
- `GlobalObjectIdCache` counts the newly routed resolves (they pass through `Resolve`, so
  `ResolutionCount` covers them without a new counter) and the invalidation rules
  (`Invalidate`/`WarmBatch`) continue to key the shared seam.
- `SceneBuilderAutoSync.CaptureBaseline` (`SceneBuilderAutoSync.cs:697`) takes the just-applied change
  set and assembles the baseline incrementally, cold only on a generation change or a cold session;
  `ExecuteSceneToCode`, `ExecuteCodeToScene` and `ExecuteBothChanged` pass their change set through.
- The extended perf gate and the target-move byte-equality test (adapter/EditMode — this is Unity
  boundary behavior; the headless Core suite is structurally blind to real `GlobalObjectId`).

## Decomposition guidance

Name the shared seam once; do not restate the invariant per task.

- **Task A — the single counted seam and its invariant check.** Thread the assembler's
  `GlobalObjectIdCache` into `ObjectReferenceResolver.BuildFromIndex` and `BuildListenerResolver` so
  reference and listener resolves are cached and counted through `Ids.Resolve`. Owns the invariant
  check: extend `AutoIdentityTests.Identity_SingleFieldEdit_ResolutionCountProportionalToChangeSet` so
  its scene holds N reference-holding objects and the assertion holds across the resolver reads, and add
  the target-move byte-equality test. TOUCHES: `com.codescenes/Editor/ObjectReferenceResolver.cs`,
  `com.codescenes/Editor/SceneRefResolver.cs`, `com.codescenes/Editor/GlobalObjectIdCache.cs` (only if
  the counting seam needs a member), `com.codescenes/Editor/ChangeScopedSnapshot.cs` (the bind point),
  `unity-gate/Assets/GateTests/AutoIdentityTests.cs`.
- **Task B — the baseline-reuse lifecycle contract.** Change `CaptureBaseline` / `_baselines` so a
  converged cycle reuses the incremental snapshot (or re-assembles incrementally under the change set)
  instead of `AssembleCold`, cold-reading only on generation change or cold session. Depends on Task A:
  the full-cycle `ResolutionCount` assertion is only meaningful once reference resolves are counted, and
  the invariant check is extended to span the baseline capture. TOUCHES:
  `com.codescenes/Editor/SceneBuilderAutoSync.cs`, `com.codescenes/Editor/ChangeScopedSnapshot.cs` (if
  an incremental-under-known-ids baseline path is added), `unity-gate/Assets/GateTests/**`.

Keep each task's TOUCHES complete: a task that omits a file it edits mis-scopes the gate. Both tasks
touch `com.codescenes/`, so both take the full Unity gate.

## Unity confirmation checklist

These become EditMode tests in `unity-gate/Assets/GateTests/`, exercising a real scene with real
`GameObject` / `SerializedProperty` / `GlobalObjectId`.

1. In a scene of N GameObjects each holding an assigned scene-reference field, edit one field and drive
   one auto-sync cycle. Expected: `GlobalObjectIdCache.ResolutionCount` over the whole cycle (head read
   plus baseline capture) is proportional to the change set, not to N — with the reference resolves now
   counted through the seam. Raising N does not raise the per-cycle count.
2. Drive a converged single-field-edit cycle and assert the cycle tail does NOT cold re-assemble the
   whole scene: no `Ids.Clear()` + whole-scene `WarmBatch`, no full-scene `ReadNode`. The observable is
   the same `ResolutionCount` staying O(1) across the baseline capture, since a cold tail would reset
   and re-resolve N.
3. Move / reparent / rename a reference target, then assemble incrementally. Expected: byte-equal to a
   fresh cold read under the same map (the incremental cache never serves a stale resolved reference),
   and no spurious dangling-reference report.
4. Over a run of several consecutive value-only edits (no object created, no mapping added), the
   generation stays stable and every cycle after the first is incremental — the O(scene) cold read is
   paid only on a cycle that genuinely changes scene identity.

## Dependencies

- Spec 35 D3 (shipped): `SceneRefResolver.Generation` / `ChangeScopedSnapshot._cacheGeneration`
  generation keying — the invalidation policy here is built on it and must not regress it. Its tests
  (`AutoIdentityTests.Identity_IncrementalAfterIdentityMapChange_ByteEqualsColdRead:164`,
  `Identity_EqualContentMapAcrossAssembles_StaysIncremental:222`) stay green.
- M5 references (`ObjectRef`, `AssetRef`, `SceneRefResolver`, `ObjectReferenceResolver`).
- The auto-sync pump and the `_baselines` conflict-aware cycle (`SceneBuilderAutoSync`, b6-t1).

## Risks and notes

- The `CaptureBaseline` reuse must not change the baseline's CONTENT. The baseline feeds the
  conflict-aware three-way merge (`ExecuteBothChanged` / `RunConflictAware`), and an ObjectRef resolved
  under a different generation than the cold read would would shift field attribution. The generation
  key is exactly the guard: reuse only when the generation matches, else re-resolve incrementally under
  the new generation. The byte-equality tests are the proof this holds.
- The code->scene direction has no head incremental read to reuse; its baseline is bounded instead by
  the plan's materialized ids. If that id set is not readily threaded, the fallback is a cold read on
  the code->scene tail only, which is coarser (a whole-file save, not a per-drag keystroke) but still a
  cost to name, not to leave unmeasured. The task owns deciding whether to thread the plan ids or accept
  the coarser tail, and the invariant test must state which path it exercises.
- Neither defect is timed; the guard is the resolution count, not a wall-clock threshold. That is
  deliberate — a count is deterministic in the gate where a timing is flaky — but it means the spec
  proves O(changed) structurally, not a millisecond budget. A wall-clock confirmation belongs to a live
  editor pass, not the hard gate.
