# Spec 30 — Live-verify bug fixes (typed-override normalization, revert paths, façade compile-check, discovery cache)

> **Why this spec exists.** A live-editor validation pass (drive a running Unity editor through the
> Unity CLI, `unity-live-verify`) over the shipped session milestones (M10 root overrides, spec 24
> nested overrides, spec 25 façades, spec 27 child selectors, spec 28 asset catalogs, spec 29
> multi-scene) found six defects that the headless + batchmode gate PASSED. The gate is **structurally
> blind** to every one of them: its tests exercise the **string / serialized-path form** and the
> **happy path**, while the bugs live on the **typed authored form** and the **failure paths** (revert,
> a fold-orphan crash, a not-yet-reloaded new builder). Two of the six are silent data loss on the
> project's own north-star form — the compiler-safe `.Set((C c) => c.field, v)` typed selector. This
> spec turns the findings into fixable tasks, each with a **regression test that reproduces the LIVE
> failure**, not the string-form happy path the current tests use.
>
> **Meta-lesson (must shape the tests, not just the code).** Every fix below adds coverage the gate was
> missing *because of how the existing tests are written*:
> - `NestedRoundTripTests` / `PrefabInstanceReconcileTests` author overrides by editing the **scene**
>   (which reads back as the **string serialized-path** `Set<Light>("m_Intensity", …)` form) and then
>   rebuild — they **never hand-author the typed `Set((Light l) => l.intensity, …)` lambda and then
>   Sync**, so the typed→serialized normalization asymmetry (Bug 1) is never exercised.
> - The façade compile-check false-positive (Bug 5) is **actively suppressed** by the gate with
>   `LogAssert.ignoreFailingMessages` (`SyncIgnoringFacadeCompileNoise`), so the gate asserts *nothing*
>   about the red error every façade Sync logs.
> - The router tests call `SceneBuilderRouter.ResetForTests()` between cases, which **masks** the
>   missing production cache-invalidation (Bug 6).
>
> The fixes therefore add **typed-form coverage** and **failure-path coverage**, and **remove** the
> suppression. A PASS on the old tests is not a PASS on the real behavior.
>
> **This spec touches BOTH Core AND the Unity adapter**, so per CLAUDE.md it is **not complete without
> EditMode coverage** in `unity-gate/Assets/GateTests/` exercising the real editor boundary
> (`GameObject`/`SerializedObject`/`GlobalObjectId`/`PrefabUtility`/`AssetDatabase`). The headless Core
> tests operate on POCO fixtures and are structurally blind to exactly the typed→serialized lowering
> and the live prefab-revert semantics these bugs hide in.

---

## Scope ledger (Core vs adapter, per bug)

| Bug | One-line | Fix layer | Regression tests |
|-----|----------|-----------|------------------|
| 1 (CRITICAL) | Typed property-selector / short-type override silently lost on Sync (nested + M10) | **Adapter** (pre-diff normalization) | Core (key-match contract) **+** EditMode (live typed-form repro) |
| 2 | Drop+append on one anchor NREs (anchorless instance + override, add component) | **Core** (`SourcePatchApplier`) | Core (applier) |
| 3 | Override revert-to-default leaves stale `.Override(...)` in code | **Adapter** (downstream of Bug 1) + Core guard | EditMode (+ optional Core) |
| 4 | Scene-added component not reverted on code→scene rebuild | **Adapter** (`InstanceOverrideExecutor` revert) | EditMode |
| 5 | Every façade Sync logs a false "DOES NOT COMPILE" (CS0103 `Prefabs`) | **Adapter** (`BuilderCompileCheck` stub) + gate un-suppress | EditMode + adapter unit |
| 6 | New `SceneBuilders/*.cs` not discovered until a domain reload | **Adapter** (`SceneBuilderRouter` cache) | EditMode |

No new Core POCOs. No milestone-behavior changes — every task restores the behavior the shipped specs
already promise.

---

## Bug 1 (CRITICAL) — Typed property-selector / short component-type override silently lost on Sync

*Groups the spec-24 nested finding and the M10 short-type finding: ONE shared root cause, ONE fix.*

### Defect

Hand-authoring the compiler-safe **typed** form of a prefab-instance override Builds correctly but is
**silently dropped on the next Sync** (or dropped at Build for the short-type variant):

- **Nested (spec 24), critical data loss.** Author
  `.On(sel => sel.Turret.Barrel, b => b.Override(x => x.Set((UnityEngine.Light l) => l.intensity, 4f)))`.
  Build materializes `m_Intensity = 4` and a sidecar override correctly. A subsequent **Sync with NO
  scene change** reports `Changed = True`, **rewrites the builder to bare `scene.Instance(Prefabs.Tank);`**
  (drops the `.On`/`.Override`), and **nulls the sidecar override** — orphaned scene override, lost code.
- **M10 root, short type name.** Author `.Override(e => e.Set((Rigidbody x) => x.mass, 7f))` under
  `using UnityEngine;`. Build logs `Could not resolve component type 'Rigidbody'` and **drops the
  override** while the build still "succeeds". The FQN `UnityEngine.Rigidbody` is required today.

The **string serialized-path forms** — `Set<Light>("m_Intensity", …)` and the FQN type — round-trip
fine. This is anti-north-star: the compiler-safe typed form is the one that breaks.

### Root cause (confirmed)

The parse side emits **un-normalized authoring tokens**, and only the **code→scene execute** stage
normalizes them — the **scene→code reconcile** and **rehydrate** stages do not, so the parsed typed
override never matches its live/sidecar serialized counterpart.

- Parse stores the typed selector as a transient `member:` path and the verbatim lambda parameter type:
  `BuilderParser.Instance.cs:282-283` (`typeFullName = parameter.Type.ToString().Trim()` → `"Rigidbody"`;
  `propertyPath = "member:" + memberAccess.Name.Identifier.Text` → `"member:intensity"`).
- `AuthoredPathResolver` — the one pre-diff normalizer that "runs on the desired model BEFORE any Diff,
  in **both directions**" (`AuthoredPathResolver.cs:16`) and lowers `member:` → serialized path — only
  descends into `node.Components` and `node.Children` (`AuthoredPathResolver.cs:63-72`). It **never
  descends into `PrefabInstanceNode.Overrides` / `AddedComponents` / `RemovedComponents`.** This gap is
  explicitly documented: *"AuthoredPathResolver does not descend into
  PrefabInstanceNode.Overrides[]/AddedComponents[] … so a typed-selector override can still carry the
  transient 'member:<name>' form here"* (`InstanceOverrideExecutor.cs:231-234`).
- **code→scene only survives because the executor lowers at WRITE time:**
  `InstanceOverrideExecutor.ResolveProperty` resolves `member:<field>` → serialized path against the
  **live** `SerializedObject` (`InstanceOverrideExecutor.cs:236-251`); the component type is resolved at
  execute via `TypeOf` → `ComponentTypeResolver.Resolve(target.ComponentType)` (`InstanceOverrideExecutor.cs:218`).
  That `Resolve(string)` overload takes **no usings** (`ComponentTypeResolver.cs:176-197`), so an
  unqualified `"Rigidbody"` returns null → `SetInstanceOverride` no-ops (`InstanceOverrideExecutor.cs:33-37`)
  → **M10 short-type silent drop** at Build.
- **scene→code has NO execute stage.** `ReconcileOverrides` builds `modelByKey` on the **raw parsed**
  `(Target, PropertyPath)` — i.e. `member:intensity` + SubKey `(0,0)` + verbatim type
  (`ReconcilerInstances.cs:199-202`) — and compares it to the live snapshot's serialized `m_Intensity` +
  real SubKey + FullName. The keys never match, so the model override is treated as **model-only** and a
  `DropInstanceCall` / `DropScopedOnCall` is emitted (`ReconcilerInstances.cs:247-271`) — dropping the
  `.On`/`.Override` — while the unmatched snapshot override re-emits the **string** form. This is the
  observed data loss. (`ReconcileInstanceOverrides` is invoked unconditionally for every mapped instance
  root, `Reconciler.cs:459`, so the diff *does* run — it just mismatches.)
- **Rehydration misses too.** `InstanceOverrideRehydrator` matches sidecar records by
  `(Target.ComponentType, PropertyPath)` (`InstanceOverrideRehydrator.cs:78`). The sidecar persists the
  **resolved** `m_Mass` / FQN record, so a model override still carrying `member:mass` / `"Rigidbody"`
  **misses**, and `BaseValue` is never threaded — which then breaks stale detection (feeds Bug 3).

The single shared root cause: **the parsed instance-override target (property path AND component type)
is never normalized to the serialized/full-name identity that the live snapshot and the sidecar both
use, except by the code→scene executor. Every other consumer (reconcile diff, rehydrate) compares the
un-normalized token and fails to match.**

### Fix

**Normalize parsed instance-override targets once, in the single BOTH-directions pre-diff seam, so every
downstream consumer inherits it** (global rule: put the fix where every current and future caller gets
it by default, not behind an executor-only opt-in).

- Extend `AuthoredPathResolver` (`AuthoredPathResolver.cs`) to descend into every
  `PrefabInstanceNode.Overrides`, `AddedComponents`, and `RemovedComponents` (and their nested/scoped
  equivalents) and rewrite, on the desired model, **before the diff in both directions**:
  - **Property path:** `member:<field>` → the real serialized `propertyPath`, using a probe
    `SerializedObject` of the resolved component type — the exact `ResolvePath` logic already at
    `AuthoredPathResolver.cs:149-165` (member name, then `m_`-mangled fallback, fail-loud/located if
    neither resolves).
  - **Component type:** `Target.ComponentType` short → FullName via the **usings-aware** resolution the
    Build backstop already uses — `ComponentTypeResolver.Resolve(TypeRef, usings, out _)` /
    `UnityResolutionProvider.ResolveComponentType(type, usings)` (`UnityResolutionProvider.cs:28`), the
    builder's `using` set already parsed by the pipeline (the same mechanism spec 20 shipped for
    unqualified component types). Unresolved / ambiguous → a **located conflict**, never a silent drop.
- After this, the parsed typed override key equals the live-snapshot key AND the sidecar record key, so
  the reconcile diff matches (no drop/re-add), the rehydrator threads `BaseValue`, and `TypeOf` at
  execute resolves. `InstanceOverrideExecutor.ResolveProperty` / `TypeOf` become redundant safety nets
  rather than the only normalization point.
- Keep the executor's live-`SerializedObject` fallback for any override that reaches it un-normalized
  (defensive), but it must no longer be the *only* place normalization happens.

### Regression tests (state where the current test is blind)

- **Core (headless) — key-match contract, RED against current code.** Construct a
  `PrefabInstanceNode` whose override carries the **normalized** target (serialized `m_Intensity`,
  FullName type, resolved SubKey) and a `SnapshotNode` with the same serialized override, and assert
  `ReconcileInstanceOverrides` / `ReconcileOverrides` emits **neither a `DropInstanceCall` nor an
  append** (matched round-trip). Then assert that an override still carrying `member:intensity` /
  short-type **does** currently drop+re-add — pinning the mismatch. *Blind today:*
  `NestedRoundTripTests` and `PrefabInstanceReconcileTests` build their overrides by editing the scene,
  which reads back as the string `Set<Light>("m_Intensity", …)` form; they never hand-author the typed
  lambda and reconcile it. **(Note: the `member:` → `m_Intensity` lowering itself requires Unity, so the
  Core test pins the KEY-MATCH contract given a normalized model; the true typed→serialized repro is the
  EditMode test below.)**
- **EditMode (adapter) — the live repro, `unity-gate/Assets/GateTests/`.**
  1. *Nested typed override survives Sync.* Hand-write
     `.On(sel => sel.Turret.Barrel, b => b.Override(x => x.Set((UnityEngine.Light l) => l.intensity, 4f)))`,
     `SceneBuilderBuild.Run` (materializes `m_Intensity = 4` + sidecar override), then
     `SceneBuilderSync.Run` with **no scene change**. Assert `Changed == false`, the `.On(...)` block is
     **byte-preserved** in the source (not rewritten to bare `scene.Instance(Prefabs.Tank);`), and the
     sidecar `InstanceOverrideRecord` is intact (`SubKey`/`PropertyPath` present, not nulled).
  2. *M10 short type resolves.* Hand-write `.Override(e => e.Set((Rigidbody x) => x.mass, 7f))` with
     `using UnityEngine;`, `SceneBuilderBuild.Run`. Assert `m_Mass == 7` materialized as a genuine
     instance override, **no** `Could not resolve component type 'Rigidbody'` warning, and the override
     is not dropped. *Blind today:* no EditMode test hand-authors either typed form; they capture from
     the scene (string/FQN) instead.

---

## Bug 2 — Drop + append on the same anchor throws NullReferenceException

### Defect

An anchorless prefab instance (no `var` handle) that carries an override, when a component is added such
that the same Reconcile pass produces **both** a drop and a chained append on that instance's anchor,
crashes: `NullReferenceException` in the `DropInstanceCall` applier. Nothing syncs.

### Root cause (confirmed)

`ResolveInstanceChainedCallAppends` deliberately **folds** every override / add-component /
remove-component / scoped-`On` append on one anchor into a **single** `ReplaceNode`
(`SourcePatchApplier.Instances.cs:33-93`, hazard documented at lines 20-31): applying them as separate
appliers would have each applier's `ReplaceNode` retarget the chain to a fresh, untracked node,
orphaning the tracked node the next applier looks up via `GetCurrentNode` (which then returns null).

`ResolveDropInstanceCall` is **not** part of that fold — it registers an **independent** applier
(`SourcePatchApplier.Instances.cs:206-210`) that does its own `currentRoot.GetCurrentNode(target)` (line
208) and then `RemoveTrailingInvocation(currentRoot, current)` (line 209). When the chained-append fold
runs its `ReplaceNode` on the same anchor's chain (`AppendChainedCalls`, lines 117-124), it orphans the
Drop's tracked node → `GetCurrentNode(target)` returns null → the cast to
`(InvocationExpressionSyntax)null` flows into `RemoveTrailingInvocation`, which dereferences
`current.Expression` → **NRE at line 208-209**.

### Fix

Bring `DropInstanceCall` into the same single-`ReplaceNode`-per-anchor resolution as the chained appends
(the fold in `ResolveInstanceChainedCallAppends`), so a drop and any sibling append on one anchor apply
through one node rewrite and never orphan each other. (Fallback if a full fold is impractical: make the
Drop applier re-resolve from the anchor when `GetCurrentNode` returns null — but prefer the fold, which
matches the existing pattern and the global "fix where all callers inherit it" rule.)

### Regression test

**Core (headless)** — `SourcePatchApplier` test. Apply a single `SourcePatch` containing **both** a
`DropInstanceCall` (Kind `Override`) **and** an `AppendInstanceAddComponent` on the **same anchor** of an
**anchorless** instance statement (`scene.Instance(Prefabs.Tank).Override(...);` with no `var`). Assert
it applies **without throwing** and produces the expected source (override dropped, component appended).
*Blind today:* existing applier tests apply drop and append **separately** — never the drop+append combo
on one anchor that triggers the orphan.

---

## Bug 3 — Override revert-to-default leaves stale `.Override(...)` in code

### Defect

Reverting an instance override back to the prefab default on an **anchored** instance (right-click →
Revert, or dragging the value back) clears the live scene modification, but the ensuing Sync reports
`Scene already matches code — nothing to sync` (`SceneBuilderSync.cs:242-244`) and **leaves the stale
`.Override(...)` in the builder**. The code and scene now disagree.

### Root cause (confirmed at the code-path level; downstream of Bug 1)

The model-only revert-drop path **exists** — `ReconcileOverrides` emits a `DropInstanceCall` /
`DropScopedOnCall` for a model override absent from the snapshot (`ReconcilerInstances.cs:247-271`) — and
`ReconcileInstanceOverrides` runs for every mapped instance (`Reconciler.cs:459`). So a genuinely
snapshot-absent override *should* drop. It does not, because:

- The parsed model override key is **un-normalized** (Bug 1: `member:` path / short type), so it never
  matches the reverted instance's serialized snapshot key — the diff cannot recognize the model override
  as "the one that was reverted".
- `BaseValue` is never threaded onto the model override, because `InstanceOverrideRehydrator` matches on
  the same un-normalized `(ComponentType, PropertyPath)` and misses the sidecar's resolved record
  (`InstanceOverrideRehydrator.cs:78`). `InstanceOverrideDiff.DetectStaleOverrides` short-circuits on a
  null desired `BaseValue` (`InstanceOverrideDiff.cs:49-54`, invoked at `ReconcilerInstances.cs:169`), so
  a value-equals-base residual modification is mishandled → net **zero edits** → "already matches".

> **Flagged (could not fully isolate in static code):** whether the live symptom is driven purely by the
> Bug-1 key mismatch or *also* by a stale-key false-positive suppressing the drop could not be
> determined without a live editor run. The RED test below pins it; the fix covers both.

### Fix

- Primary: **covered by Bug 1's normalization** — once the model override key equals the snapshot/sidecar
  serialized key, a reverted override (absent from the normalized snapshot) reliably lands in the
  model-only branch and emits its `DropInstanceCall`.
- Guard: ensure a model override that is **absent** from the (normalized) snapshot **always** emits the
  drop and is **never** suppressed as "stale" — a stale conflict requires the key to be present on both
  sides with divergent `BaseValue`, not absent from the snapshot.

### Regression test

- **EditMode (adapter).** `SceneBuilderBuild.Run` an instance with an override; in the live scene
  `PrefabUtility.RevertPropertyOverride` it back to prefab default; `SceneBuilderSync.Run`. Assert
  `Changed == true`, the `.Override(...)` (or its `.On(...)` scoped form) is **removed** from source, and
  a second Sync is a genuine no-op. *Blind today:* EditMode override tests assert value-**change**
  reconcile, never revert-to-default → stale-drop.
- **Core (optional guard).** Reconcile a normalized model override against an **empty** snapshot →
  assert exactly one `DropInstanceCall` is produced and no stale conflict.

---

## Bug 4 — Scene-added component not reverted on code→scene rebuild

### Defect

A component added to an instance **in the scene** but absent from the desired code model is **not
removed** by a code→scene rebuild — it must revert, per M10 banner #6 (a model-absent override/added
component/added child is reverted). Live observation: on the same instance, the property override
reverted but the scene-added `Light` **persisted**.

### Root cause (confirmed at the code-path level)

The **diff emits the revert**: `InstanceOverrideDiff.EmitAddedComponents` produces a `RevertAddedComponent`
for a snapshot-only added component (`InstanceOverrideDiff.cs:147-161`), reached on code→scene via
`Differ.cs:99` (`InstanceOverrideDiff.Emit(instanceNode, entry.Node, …)` for every matched
`PrefabInstanceNode`). The failure is in the **executor**: `Apply(RevertAddedComponent)`
(`InstanceOverrideExecutor.cs:108-134`) resolves the component from
`result.ComponentsByLogicalId[op.ComponentLogicalId]` — but `op.ComponentLogicalId` is a **live-read
snapshot id**, which is **not** in that dictionary (it holds only plan-created ids for objects this pass
materialized). It then falls back to `StripOrdinal(op.ComponentLogicalId)` + `AddedComponentsOn(root,
owner).FirstOrDefault(FullName match)` (lines 124-127). The scene-added component is not matched by that
fallback, so `PrefabUtility.RevertAddedComponent` is never called and the component persists — while the
property-override revert (a different executor, `Apply(RevertInstanceOverride)`, lines 88-106, which
resolves via `GetComponent(type)` + `FindProperty`) succeeds, explaining the asymmetry.

> **Flagged:** the exact fallback miss (the snapshot-supplied `ComponentLogicalId` format vs
> `StripOrdinal`'s expectation) was not fully traced in static code — the snapshot added-component
> `LogicalId` assignment was not read. The RED test distinguishes read-miss vs executor-lookup-miss; the
> fix targets the executor's live-component resolution regardless.

### Fix

Make the code→scene added-component revert resolve the live component **robustly** — match the live
added component directly by the revert op's target sub-object + component **FullName** against
`PrefabInstanceProbe.AddedComponentsOn(root, owner)` (`PrefabInstanceProbe.Overrides.cs:308`), rather
than relying on `result.ComponentsByLogicalId` (which never contains a scene-only component). A component
present on the live instance as an added override and absent from the desired model must be reverted.

### Regression test

**EditMode (adapter).** `SceneBuilderBuild.Run` an instance; add a component to it in the live scene
(`GameObject.AddComponent` on the instance root → a real added-component override); run a code→scene
`SceneBuilderBuild.Run` from code that does **not** declare that component. Assert the component is
**removed** (`PrefabUtility.GetAddedComponents(root)` no longer lists it / `GetComponent<T>() == null`)
and the instance's `GlobalObjectId` is preserved. Cover the same-pass mixed case (a property override
that *is* declared stays; the undeclared added component reverts). *Blind today:* EditMode coverage
asserts property-revert on code→scene, never added-component-revert.

---

## Bug 5 — Every façade Sync logs a false "DOES NOT COMPILE" (CS0103 `Prefabs`)

### Defect

Every scene→code Sync that emits a typed façade builder (referencing `Prefabs.<X>`) logs a red
`[SceneBuilder] … emitted builder source DOES NOT COMPILE … CS0103: 'Prefabs' does not exist`
(`BuilderCompileCheck.Format`, `BuilderCompileCheck.cs:210-223`). The emission is **correct** — the
error is a false positive from the in-process compile-check. The gate hides it by setting
`LogAssert.ignoreFailingMessages` (`SyncIgnoringFacadeCompileNoise`,
`unity-gate/Assets/GateTests/InstanceAddReconcileTests.cs:90-100`; the same suppression appears in
`RoundTripSpatialSyncTests.cs:74-82`), which is itself the smell — the live user console shows an
alarming "bug in SceneBuilder's emission" on every façade Sync.

### Root cause (confirmed)

`BuilderCompileCheck.Check` compiles emitted source against the loaded editor assemblies
(`References()`, `BuilderCompileCheck.cs:81-128`) **plus a stub for the generated `Assets` catalog only**
(`StubTree()` → `AssetCatalogStubEmitter.Emit`, `BuilderCompileCheck.cs:150-168`, added at
`BuilderCompileCheck.cs:181-185`). The generated **`Prefabs` façade** type (spec 25's
`PrefabFacadeGenerator`, emitted only into the real project `.csproj` via the source generator, **never
present in the editor AppDomain**) has **no** stub, so `Prefabs.Tank` reads as CS0103 in the check even
though it compiles for real. The catalog got a stub (spec 28 fix); the façade never did.

### Fix

Give the compile-check the façade types the same way it already gets the catalog:

- Emit a compilable **`Prefabs` stub** from the on-disk façade manifest
  `SceneBuilders/Generated/Prefabs.sbfacade.json` — a `PrefabFacadeStubEmitter` analogous to
  `AssetCatalogStubEmitter`, cached and added to the compilation in `Check` exactly like `StubTree()`
  (`BuilderCompileCheck.cs:150-185`).
- **Remove** the `LogAssert.ignoreFailingMessages` suppression from the gate
  (`SyncIgnoringFacadeCompileNoise` in `InstanceAddReconcileTests.cs` and the equivalent in
  `RoundTripSpatialSyncTests.cs`), so a genuine emission-does-not-compile bug fails the gate again. Do
  **not** keep suppressing — the point of `BuilderCompileCheck` is to catch real emission bugs, and a
  blanket ignore defeats it.

### Regression test

- **Adapter unit** — `BuilderCompileCheck.Check` of a source that references `Prefabs.<X>` (and
  `Assets.<…>`), with a façade manifest present, returns **zero** diagnostics. Mirrors the existing
  `AssetCatalogCompileCheckTests` (which pins the catalog stub) for the façade.
- **EditMode (adapter)** — a scene→code Sync that emits a typed `Instance(Prefabs.X)` builder asserts
  **no** `DOES NOT COMPILE` / CS0103 error is logged, using `LogAssert.NoUnexpectedReceived()`
  **without** `ignoreFailingMessages`. *Blind today:* the gate sets `ignoreFailingMessages = true` for
  exactly these paths, so it asserts nothing about the false error.

---

## Bug 6 — New `SceneBuilders/*.cs` not discovered until a domain reload

### Defect

A newly created builder source file under `SceneBuilders/` is **not discovered** — and therefore does
not auto-sync — until a domain reload. The `FileSystemWatcher` Created event fires, but discovery
returns the stale cached route set and the new builder is invisible. (Workaround today:
`EditorUtility.RequestScriptReload()` then touch the file.)

### Root cause (confirmed)

`SceneBuilderRouter.Discover()` memoizes the route list in a per-domain static `_cache`
(`SceneBuilderRouter.cs:52`, populated at `:90`, returned early at `:63-66`). Both lookup directions read
`Discover()` (`TryRouteBuilderFile` `:118`, `TryRouteScene` `:134`), so both see the stale set. The
**only** cache invalidation is `ResetForTests()` (`SceneBuilderRouter.cs:166-169`), which is `internal`
and test-only — there is **no** production path that clears `_cache` when a builder file is
created/deleted. The static only drops on an AppDomain reload, which is why a reload "fixes" it. Spec 29
even called for this (`specs/29-multi-scene-builders.md` Risks: *"`Discover()` may be cached and
invalidated when the builder set changes (a create/delete in `SceneBuilders/`, which the watcher already
sees)"*) — the invalidation was never wired.

### Fix

Invalidate the route cache when the builder set changes, in the path every create/delete already flows
through:

- Add a public `SceneBuilderRouter.Invalidate()` (clears `_cache`; `ResetForTests` can delegate to it).
- Call it from the `FileSystemWatcher` Created / Deleted / Renamed handling for `SceneBuilders/*.cs` in
  `SceneBuilderAutoSync` (the watcher already watches the whole directory — see
  `specs/29-multi-scene-builders.md`, `SceneBuilderAutoSync.cs:146-150`). Placing the invalidation in the
  watcher event path means every current and future builder create/delete inherits it by default. The
  rescan is cheap (a handful of files), consistent with the sync-performance constraint.

### Regression test

**EditMode (adapter).** Call `Discover()` to populate the cache; **write a new builder `.cs` into the
real out-of-`Assets/` `SceneBuilders/` directory** (via `SceneBuilderPaths`, NOT under `Assets/` — a
builder under `Assets/` would be compiled and mask the real discovery path, per spec 29's discovery
note); invoke the create-invalidation (the watcher handler, or `Invalidate()` directly to model the
handler); assert `Discover()` now returns a `BuilderRoute` for the new builder **without a domain
reload**, and that a subsequent `TryRouteBuilderFile` for it succeeds. *Blind today:* router tests call
`ResetForTests()` between cases, which masks the missing production invalidation.

---

## Consolidated regression-test list (RED-first, per CLAUDE.md gate `./verify.sh`)

Each fix ships with a test that reproduces the **live** failure, not the string-form happy path.

| # | Test | Layer | Reproduces |
|---|------|-------|-----------|
| 1a | Reconcile matches a **normalized** typed override (serialized path + FullName + resolved SubKey); RED when key is `member:`/short | Core | Bug 1 key-match contract |
| 1b | Hand-author nested typed `.On(sel => …, b => b.Override(x => x.Set((Light l) => l.intensity, 4f)))`, Build, then Sync with no scene change → `.On` survives, sidecar override intact, `Changed == false` | EditMode | Bug 1 nested data loss |
| 1c | Hand-author `.Override(e => e.Set((Rigidbody x) => x.mass, 7f))` under `using UnityEngine;`, Build → `m_Mass` materializes, no "Could not resolve component type" warning, override not dropped | EditMode | Bug 1 M10 short-type drop |
| 2 | Apply one `SourcePatch` with a `DropInstanceCall` **and** an `AppendInstanceAddComponent` on the **same anchorless** anchor → no throw, expected source | Core | Bug 2 NRE |
| 3a | Build override, `RevertPropertyOverride` in scene, Sync → `.Override(...)` removed, second Sync no-op | EditMode | Bug 3 stale code |
| 3b | Reconcile normalized override vs empty snapshot → exactly one `DropInstanceCall`, no stale conflict | Core | Bug 3 guard |
| 4 | Build instance, add component in scene, code→scene Build from code lacking it → component reverted, `GlobalObjectId` preserved | EditMode | Bug 4 added-component revert |
| 5a | `BuilderCompileCheck.Check` of source referencing `Prefabs.<X>` (+ `Assets.<…>`) with manifest present → zero diagnostics | Adapter unit | Bug 5 false positive |
| 5b | Typed-façade scene→code Sync logs no `DOES NOT COMPILE`/CS0103, via `LogAssert.NoUnexpectedReceived()` **without** `ignoreFailingMessages` | EditMode | Bug 5 + un-suppress |
| 6 | Populate `Discover()` cache, write a new out-of-`Assets/` builder, invalidate via the watcher path → `Discover()`/`TryRouteBuilderFile` see it without a domain reload | EditMode | Bug 6 stale cache |

Un-suppress as part of Bug 5: delete `SyncIgnoringFacadeCompileNoise`
(`unity-gate/Assets/GateTests/InstanceAddReconcileTests.cs:90-100`) and the equivalent
`ignoreFailingMessages` block in `RoundTripSpatialSyncTests.cs:74-82`, replacing them with real
`LogAssert.NoUnexpectedReceived()` assertions once the façade stub lands.

## Dependencies

- **M10** (`specs/completed/11-m10-prefab-overrides.md`) — root override model, materialize-revert
  (banner #6), stale-override detection. Bugs 1, 3, 4 are restorations of its promised behavior.
- **Spec 24** (`specs/completed/24-nested-prefab-overrides.md`) — nested `.On(...)` scoped overrides;
  Bug 1's critical symptom and Bug 2's anchorless drop+append live here.
- **Spec 25** (`specs/completed/25-typed-prefab-facades.md`) — the `Prefabs` façade generator + manifest
  (`Prefabs.sbfacade.json`); Bug 5's missing compile-check stub.
- **Spec 28** (`specs/completed/28-typed-asset-catalogs.md`) — `AssetCatalogStubEmitter`/`StubTree` is the
  exact pattern Bug 5's façade stub mirrors.
- **Spec 29** (`specs/29-multi-scene-builders.md`) — `SceneBuilderRouter` discovery + cache and the
  `FileSystemWatcher`; Bug 6 wires the invalidation spec 29 flagged but left undone.
- **Spec 20** (`specs/completed/20-unqualified-type-names.md`) — the usings-aware
  `ComponentTypeResolver.Resolve(type, usings)` Bug 1's fix applies to instance-override targets.
