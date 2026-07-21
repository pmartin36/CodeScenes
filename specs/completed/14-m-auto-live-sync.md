# M-Auto — Seamless auto-sync (the invisible bidirectional loop, ON by default)

### Additions to the contract

**M-Auto is not a feature you turn on — it is the product turned on.** It adds no §3 value type, no
new Plan/Reconcile op, and no Core surface beyond a pure drift determination the adapter already gets
for free from `Materializer.Materialize` and `Reconciler.Reconcile`. Everything it adds is
Editor-adapter plumbing: the two triggers that make Build/Sync fire on their own, the suppression that
stops them echoing, and the incremental-identity path that makes per-edit sync cheap enough to run on
every change.

| Added | Shape (summary) | Owner |
|---|---|---|
| **Auto loop, ON by default** | scene→code and code→scene fire automatically, debounced, no button on the happy path | M-Auto |
| **Single persisted master toggle** | one on/off governing BOTH directions; persisted per-project in `EditorPrefs`; a testing/power-user affordance, default **ON** | M-Auto |
| **Scene→code trigger** | `ObjectChangeEvents.changesPublished` + `EditorSceneManager.sceneSaved`, registered under `[InitializeOnLoad]` (survives domain reload) | M-Auto |
| **Code→scene trigger** | the plugin's own `FileSystemWatcher` on `<ProjectRoot>/SceneBuilders/`, marshalled to the main thread via `EditorApplication.update` | M-Auto |
| **Write-seam echo suppression** | our own scene writes and our own source writes must not re-trigger the opposite direction — suppressed at the single write path every caller inherits, not per trigger | M-Auto |
| **Incremental identity** | debounce + a cached `GlobalObjectId` map invalidated by change events + a change-scoped snapshot; batch `GetGlobalObjectIdsSlow` where a walk is unavoidable | M-Auto |
| `DriftState { CodeAhead, SceneAhead }` (adapter helper) | derived from the existing empty-Plan / empty-Patch determination; no Core type | M-Auto |

The manual **Build** / **Sync** menu items (`CodeScenes/Build DemoScene (code -> scene)`,
`CodeScenes/Sync DemoScene (scene -> code)`) are **retained** — as debug + testing tools, not as the
product. They are how a developer disables auto and validates a future milestone step-by-step.

---

## Goal

Turn nothing on and stop touching buttons. Edit the scene and the builder `.cs` rewrites itself; edit
the `.cs` and the scene rebuilds in place. The user never presses Build, never presses Sync, never
opens a panel, never confirms a dialog (foundation §7 — "Sync writes directly and silently; there is
no confirmation dialog or preview-and-approve step, because the product is seamless-by-default"). Auto
is **ON out of the box**. A single master toggle lets a developer switch it **off** to drive sync by
hand — that toggle exists for testing and power-users, not because auto is optional.

## The gap (observed against the code, not theorized)

The Build (code→scene) and Sync (scene→code) paths exist and converge, but **nothing fires them
without a menu click.** There is no scene-edit trigger, no file watcher, no debounce, no
echo-suppression, and no incremental identity anywhere in `com.codescenes/` — verified: a repo-wide
grep for `ObjectChangeEvents`, `InitializeOnLoad`, `FileSystemWatcher`, `changesPublished`,
`sceneSaved`, and `GetGlobalObjectIdsSlow` returns **zero** matches. M-Auto builds that machinery. The
four blockers below are the real obstacles a naive "just call Sync on every event" would hit; each is
grounded in the code and each has a specified solution.

## Blockers — verified against the real code, and their solutions

### 1. The obvious code→scene trigger is dead by construction

`SceneBuilderPaths` (`com.codescenes/Editor/SceneBuilderPaths.cs`) documents and enforces that
builders live at `<ProjectRoot>/SceneBuilders/`, **outside** `Assets/` and `Packages/` — the only
roots Unity's asset refresh scans. Its own remarks state code→scene "is driven by the plugin's OWN
file watcher." Therefore `AssetPostprocessor.OnPostprocessAllAssets` — the trigger the naive design
reaches for — **can never fire for a builder edit**: Unity never imports a file it never scans. Using
it would silently no-op.

**Solution — the real triggers:**
- **Scene→code** = `ObjectChangeEvents.changesPublished` (per-edit, in-editor mutations) **+**
  `EditorSceneManager.sceneSaved` (catches edits a coarse change event misses), subscribed in an
  `[InitializeOnLoad]` static constructor so the subscription **re-registers after every domain
  reload**.
- **Code→scene** = the plugin's own `System.IO.FileSystemWatcher` rooted at
  `SceneBuilderPaths.BuildersDirectory`, filtered to `*.cs`. `FileSystemWatcher` raises on a
  background thread and Unity's APIs are main-thread-only, so the watcher only sets a dirty flag; an
  `EditorApplication.update` pump drains it on the main thread and runs the debounced Build.

`SceneBuilderPaths.WriteIfChanged` exists precisely to protect this loop: it writes only when the
bytes differ, so an identical-content Sync does not bump the source mtime and self-trigger the
watcher (see blocker 5).

### 2. "Empty patch ⇒ in sync" deadlocks on a spurious patch

`SceneBuilderSync.SyncResult` (`com.codescenes/Editor/SceneBuilderSync.cs`) documents a real
convergence hazard: a reconcile can **produce** a non-zero patch (`PatchEdits > 0`) whose **applied**
text is byte-identical to the source (`EditsApplied == 0`, `Changed == false`). The doc-comment names
it outright: "a non-zero value is a convergence defect even when the text happens to match." A drift
rule of "raw `Patch.Edits.Length > 0` ⇒ scene-ahead" would therefore latch **scene-ahead forever** on
such a defect; combined with the "apply neither on conflict" rule, the loop **permanently deadlocks**
and never syncs again.

**Solution — drift is measured in bytes-on-disk, not raw op count:**
- **Honest scene→code drift** = the same three-part gate `SceneBuilderSync.Run` already computes:
  `Patch.Edits == [] && ReconcileResult.Skipped == [] && ReconcileResult.Conflicts == []`
  **and** no real map/asset delta (`AddedEntries == [] && RemovedLogicalIds == []` and
  `AssetCacheMerge.ChangedCount == 0`). Fields verified present on `ReconcileResult`.
- **Honest code→scene drift** = `Plan.Ops == [] && Plan.Skipped == []`. Both fields verified present
  on `Plan`.
- **The spurious-patch guard (the anti-deadlock pin):** a patch whose **applied** text is
  byte-identical is treated as **no drift**. `WriteIfChanged` already computes exactly that
  distinction — `SyncResult.Changed` is "bytes actually changed on disk," and `EditsApplied` counts
  only edits whose applied text differed. The drift signal reads `Changed`, never raw `PatchEdits`. A
  `PatchEdits > 0` with `EditsApplied == 0` is surfaced **loudly** (a Console error naming the
  reconcile as a convergence defect) and the loop treats the pair as converged rather than latching on
  it — a real defect is escalated, never silently re-applied every cycle.

### 3. There is no incremental identity — the reader is a full-scene walk

`SceneSnapshotReader.Read` (`com.codescenes/Editor/SceneSnapshotReader.cs`) walks **every** root and
every child, calling `GlobalObjectId.GetGlobalObjectIdSlow(go)` **once per GameObject** (line 55), and
`SerializedFieldBridge.ReadComponent` constructs a fresh `SerializedObject` and iterates
`NextVisible` over **every component** (`SerializedFieldBridge.cs:45,107`). That is `O(scene)` per
call — affordable per button-press, **fatal per keystroke**. Auto fires this on every change; a
whole-scene `GetGlobalObjectIdSlow` sweep per transform drag is exactly the "drag → freeze → drag →
freeze" failure CLAUDE.md forbids.

**Solution — make per-edit work `O(changed)`, not `O(scene)`:**
- **Debounce** ~300–500 ms on `EditorApplication.update`: coalesce a burst of N edits into a single
  reconcile after the last one settles, so a drag emits one sync, not one per frame.
- **Cached `GlobalObjectId` map** keyed by instance id, **invalidated by the change event itself**:
  `ObjectChangeEvents.changesPublished` reports *which* objects changed (a `ChangeSet` of instance
  ids), so only those entries are re-derived; unchanged objects keep their cached id.
- **Change-scoped snapshot:** the reconcile still takes a `SceneSnapshot` (contract unchanged), but
  the adapter **assembles** it from the cached structure and re-reads `SerializedObject`/`NextVisible`
  **only for the components on changed objects**, rather than a cold full walk. The heavy
  per-component `NextVisible` pass runs for `O(changed)` components.
- **Batch id resolution where a walk is unavoidable** (first snapshot of a session; a structural
  reparent that moves a subtree): use `GlobalObjectId.GetGlobalObjectIdsSlow(Object[], GlobalObjectId[])`
  — the batch overload — instead of a per-object loop. *(This overload is the documented Unity batch
  API; the pipeline must confirm it against the installed 6000.5.3f1 editor before relying on it — it
  is not exercised by any current code path.)*
- **Latency budget:** the debounced cycle for a single-field edit completes well inside the debounce
  interval and touches `O(changed)` objects. Enforced by a counting seam (below): a single-field edit
  must not resolve a `GlobalObjectId` for every object in the scene.

### 4. A freshly-created object has no durable identity before the scene is saved

`SceneBuilderSync.Run` deliberately **does not save the scene** (its own comment: "No scene re-save
(created GameObjects' GlobalObjectIds already came from the snapshot)"). But a GameObject created live
in the editor has **no durable `GlobalObjectId` until the scene is saved** — before the save its
`GetGlobalObjectIdSlow` value is a session-local id that will not match after a reload, so persisting
it into the sidecar strands the new object on the next Build/Sync. The reconciler treats a node as a
create-candidate only when its `GlobalObjectId` is non-empty (`Reconciler.cs:659`), so the append
happens — but the id it records is not durable. Under **manual** Sync the user typically saved first;
under **auto** the trigger *is* the creation, before any save.

**Solution — save-on-sync for structural creates (a genuine design decision, stated explicitly):**
when the debounced scene→code cycle detects a structural create, the auto-sync executor calls
`EditorSceneManager.SaveScene` on the active scene **before** snapshotting, so the new object earns a
durable `GlobalObjectId`, then reconciles and persists that durable id. This is consistent with the
Build path, which already saves the scene (`SceneBuilderBuild.cs:115–116`). The alternative — defer
the sync until the user's next natural save — is **rejected**: it contradicts the seamless premise
(the code must update when the scene changes, not when the user remembers Ctrl+S). The save itself
runs inside write-seam suppression (blocker 5) so the resulting `sceneSaved` does not re-trigger.
Transform/component edits to already-durable objects do **not** force a save — only a structural
create does.

### 5. Our own writes must not echo back as a trigger

With both triggers live, each direction's write is the other direction's stimulus: a code→scene Build
mutates the scene → `ObjectChangeEvents` fires → scene→code Sync runs on our own mutation; a
scene→code Sync writes the `.cs` → the `FileSystemWatcher` fires → code→scene Build runs on our own
write. Left unguarded this is an infinite ping-pong.

**Solution — suppress at the write seam every caller inherits (per CLAUDE.md's inherit-by-default
rule), never a scope each trigger must remember to open:**
- **Scene write seam:** the code→scene apply path (the `ApplyModifiedProperties` / `SaveScene`
  chokepoint inside Build) raises a ref-counted scene-suppression flag for the duration of the write;
  the `ObjectChangeEvents` handler consults it and drops events attributable to our own mutation.
- **Source write seam:** `SceneBuilderPaths.WriteIfChanged` is the one function every source/sidecar
  write already routes through. It records the `(path, content-hash)` it just wrote into a suppression
  registry; the `FileSystemWatcher` handler compares the file's current hash against the registry and
  drops the echo of our own write. `WriteIfChanged`'s existing no-op-write elision already prevents an
  identical-content Sync from firing the watcher at all; the registry covers the real-write case.

This is the **`SuppressionScope`** that `specs/08-m7-robustness.md` specifies as a ref-counted,
exception-safe, time-bounded Editor guard. **It does not yet exist** — a repo-wide grep for
`SuppressionScope` returns zero matches. M-Auto therefore either lands it (as a Core-free adapter type
placed at the two write seams above) or is sequenced behind the M7 task that does; see Dependencies.
The design requirement is that suppression is a **property of the write path**, so a future caller
inherits it by default and cannot forget to open a scope.

## The conflict case — the one genuine open design question (needs user ratification)

When **both** sides changed since the last converged state — the builder `.cs` was edited *and* the
scene was edited — there is no authoritative direction. The old panel-based design parked this in a
"drift indicator" and applied neither. **With auto ON, there is no panel and no button to park it
behind.** This is the real open decision.

**Recommended resolution (RECOMMENDED, but OPEN — the user must ratify):**

1. **Field-level authority, not file-level.** Core's diff is already field-granular — it emits
   per-field `SetField` / `SetAssetRef` ops and per-argument source edits. A "both sides changed"
   verdict at file granularity is almost always false: the user moved an object in the scene *and*
   renamed a different object in code. Declare a **conflict only on TRUE overlap** — the *same* field
   of the *same* object changed on both sides — and **auto-resolve everything else** by applying each
   side's non-overlapping edits in its own direction. This collapses the conflict surface to the rare
   genuine case.

2. **Non-modal surfacing for the residue.** For a true overlapping conflict, do **not** open a modal
   or a panel (foundation §7 forbids a confirm/preview step). Surface it where the user already looks:
   a **Console error** (fail-loud, located — object/component/field, per §7), an inline
   **`// CONFLICT:` marker** written into the builder at the conflicting statement, and a scene-view
   overlay on the affected object. Nothing blocks; the loop keeps converging every non-conflicting
   field.

3. **Tie-break: SCENE wins, code intent preserved (RATIFIED by the user, 2026-07-16).** On the
   residual same-field overlap the **scene edit is authoritative** — the source is patched to the
   scene's value, so the loop converges on what the user just did in the editor. The prior **code**
   value is **not silently discarded** — it is captured into the emitted `// CONFLICT:` marker as the
   alternate so the author can restore it by editing code. Never last-write-wins; never a silent
   clobber (§7). (The builder remains the source of truth in general; this narrow same-field tie-break
   favors the live scene edit by explicit user decision — not code-wins, not mark-and-halt.)

Adopt **(1) + (2) + (3)** — the only shape consistent with "no buttons, no panel, no modal" while
still honoring "one authoritative direction, never a silent pick." **RATIFIED:** the tie-break in (3)
is **scene-wins** (user decision, 2026-07-16).

## In scope

- **Auto ON by default**, both directions, no button on the happy path, no confirmation dialog.
- **A single master toggle** — one boolean governing both directions — persisted per-project (below).
- **Scene→code trigger:** `ObjectChangeEvents.changesPublished` + `EditorSceneManager.sceneSaved` under
  `[InitializeOnLoad]`, surviving domain reload.
- **Code→scene trigger:** the plugin's own `FileSystemWatcher` on `<ProjectRoot>/SceneBuilders/`,
  main-thread-marshalled via `EditorApplication.update`.
- **Debounce** (~300–500 ms) coalescing a burst of edits into one cycle per direction.
- **Incremental identity:** cached `GlobalObjectId` map invalidated by change events; change-scoped
  snapshot assembly; batch `GetGlobalObjectIdsSlow` for unavoidable walks.
- **Byte-grounded drift** (blocker 2) and **write-seam echo suppression** (blocker 5).
- **Save-on-sync for structural creates** (blocker 4).
- **Field-level conflict authority + non-modal surfacing, scene-wins tie-break** (the conflict section, ratified).
- **Retained manual Build/Sync menu items** as debug/testing tools with auto off.
- **Play mode:** auto pauses in Play mode (edits don't persist) and resumes on return to edit mode.

## Out of scope

- **Continuous per-keystroke sync inside the `.cs` text editor.** M-Auto reacts to committed source
  writes (the file watcher) and in-editor scene edits (`ObjectChangeEvents`), not to unsaved keystrokes
  in an external IDE. (`needs_research/live-continuous-sync.md`.)
- **A dockable SceneBuilder EditorWindow / drift-indicator panel.** The product has no panel on the
  happy path; conflict surfacing is non-modal (Console + inline marker + scene overlay). The master
  toggle is a single menu-checkbox, not a window.
- **Automatic 3-way textual merge** of a true same-field overlap. It is surfaced (§conflict), not
  auto-merged.
- **Multi-scene / cross-scene auto.** Single active scene per v1.
- **Per-direction toggles.** One master switch only, by explicit decision.

## Master toggle — persistence mechanism (decided)

**Storage: `EditorPrefs`, keyed by project path, default ON.** Key
`SceneBuilder.Auto.Enabled::<projectRootHash>` where `<projectRootHash>` is a stable hash of
`SceneBuilderPaths.ProjectRoot`; a missing key reads as **`true`** so a fresh project is auto-on
without any setup.

Why `EditorPrefs` keyed by project path, and not the alternatives:
- **Per-project + survives restart.** `EditorPrefs` persists across editor restarts in Unity's
  per-user store (Windows registry / macOS plist / Linux config), and the project-path key scopes it
  to *this* project on *this* machine — exactly "persisted as an editor setting, per project."
- **Never under `Assets/`.** `EditorPrefs` lives entirely outside the project tree, so toggling it
  triggers **no domain reload** — the cardinal constraint. A settings `.cs`/`.asset` under `Assets/`
  is disqualified for that reason alone.
- **Not a file under `SceneBuilders/`.** That folder is watched by our own `FileSystemWatcher`; a
  settings file there would self-trigger the loop. Disqualified.
- **Not `ProjectSettings/`.** A file there is version-controlled and shared team-wide; a per-developer
  testing affordance to disable auto must **not** propagate to every teammate's checkout, and it
  should be per-machine, which `EditorPrefs` gives directly.

The toggle is exposed as a single checkable menu item (`CodeScenes/Auto` with a checkmark) reading
and writing this key. The `[InitializeOnLoad]` bootstrap reads it to decide whether to arm the
triggers; flipping it arms/disarms both directions atomically.

## Core deliverables

**No new §3/§5 types, no new op.** The drift determination is the existing Core, asserted as behavior:

- `Materializer.Materialize(desired, snapshot, map)` yields `Plan.Ops == [] && Plan.Skipped == []`
  **iff** code matches scene.
- `Reconciler.Reconcile(model, snapshot, map, anchors, …)` yields
  `Patch.Edits == [] && Skipped == [] && Conflicts == []` **iff** scene matches code.
- A pure `DriftState { bool CodeAhead, bool SceneAhead }` derived from those two — but it is an
  **adapter** helper; Core exposes no new surface. (If a Core-side pure helper is convenient it stays
  free of Unity types and side effects.)

Behaviors (headless-testable, Core fixtures):
- an in-sync model/snapshot pair ⇒ empty Plan **and** empty Patch;
- a code-only change ⇒ non-empty `Plan.Ops`, empty `Patch.Edits`;
- a scene-only change ⇒ empty `Plan.Ops`, non-empty `Patch.Edits`;
- a both-sides change ⇒ non-empty Plan **and** non-empty Patch (⇒ the adapter runs the field-level
  conflict resolution);
- **the spurious-patch invariant:** a reconcile whose emitted patch re-applies byte-identically is
  **not** drift — the drift signal is the applied-byte delta, not `Patch.Edits.Length`.

## Editor adapter deliverables (pipeline-built, EditMode-gated per §8)

All under `com.codescenes/Editor/` unless noted.

- **`SceneBuilderAutoSync` bootstrap** (`[InitializeOnLoad]`): reads the master toggle, and when ON
  subscribes `ObjectChangeEvents.changesPublished` + `EditorSceneManager.sceneSaved`, starts the
  `FileSystemWatcher`, and registers the `EditorApplication.update` pump. Re-runs on every domain
  reload (that is what `[InitializeOnLoad]` guarantees). Unsubscribes/disposes when the toggle is off.
- **Master toggle menu item** (`CodeScenes/Auto`, checked): reads/writes the `EditorPrefs` key above;
  arms or disarms both directions.
- **Debounce pump** on `EditorApplication.update`: per-direction settle timer (~300–500 ms).
- **Scene→code executor:** builds a **change-scoped snapshot** from the cached `GlobalObjectId` map
  (invalidated by the change event's instance-id set), reconciles, and applies via the existing
  `SceneBuilderSync.Run` seam (extended to accept a pre-assembled snapshot). Forces
  `EditorSceneManager.SaveScene` first **iff** the change set contains a structural create (blocker 4).
- **Code→scene executor:** on a watched `.cs` write, parses; on a clean parse runs Build in place
  (non-destructive, M1b); on a parse error does nothing and logs it located.
- **`GlobalObjectId` identity cache:** instance-id → `GlobalObjectId`, invalidated per change event;
  the batch `GetGlobalObjectIdsSlow` for cold/structural walks. Exposes a **counting seam** (a
  test-visible counter of id resolutions performed) so the O(changed) budget is assertable.
- **Write-seam suppression** (blocker 5): the ref-counted scene-suppression flag around the
  scene-write chokepoint, and the `(path, hash)` registry consulted inside/around
  `SceneBuilderPaths.WriteIfChanged`. Realized as the M7 `SuppressionScope` if that lands first.
- **`DriftState` computation** from dry-run Materialize + Reconcile, driving the direction choice and
  the field-level conflict path.
- **Conflict surfacing:** Console (located, §7) + inline `// CONFLICT:` marker in the builder +
  scene-view overlay. No modal, no panel.
- **Play-mode gate:** disarm on `EnteredPlayMode`, re-arm on `EnteredEditMode`.

## Authoring API added

**None.** M-Auto is editor behavior over existing Core; it introduces no builder-facing surface.

## IdentityMap / sidecar changes

**None beyond what Build/Sync already write.** Auto reuses the same `SceneBuilderBuild.Run` /
`SceneBuilderSync.Run` code paths and their existing sidecar writes. The save-on-sync-for-creates
policy (blocker 4) changes *when* a durable `GlobalObjectId` is captured, not the sidecar's shape.

## Core test plan (RED tests — headless, `SceneBuilder.Core.Tests`)

The Core guarantee is only the drift determination; the loop, triggers, debounce, suppression, and
incremental identity are Unity-boundary behavior validated by the EditMode checklist (they are
structurally invisible to POCO fixtures).

1. `Drift_InSync_EmptyPlanAndEmptyPatch` — matching model/snapshot ⇒ `Plan.Ops`/`Plan.Skipped` empty
   AND `Patch.Edits`/`Skipped`/`Conflicts` empty.
2. `Drift_CodeOnlyChange_NonEmptyPlan_EmptyPatch`.
3. `Drift_SceneOnlyChange_EmptyPlan_NonEmptyPatch`.
4. `Drift_BothChanged_NonEmptyPlanAndPatch` — the signal that routes to field-level conflict handling.
5. `Drift_SpuriousPatchAppliesByteIdentical_IsNotDrift` — a reconcile that yields `Patch.Edits > 0`
   whose applied text equals the source ⇒ the drift determination reports **converged**, pinning the
   blocker-2 anti-deadlock rule at the Core/adapter contract (applied-byte delta, not op count).
6. `Drift_UnresolvedSkippedField_DoesNotCountAsSceneAhead` — a `ReconcileResult.Skipped` entry alone
   (no real edit) does not, by itself, latch scene-ahead (honest-drift definition).

## Unity confirmation checklist → EditMode tests

These become EditMode tests in `unity-gate/Assets/GateTests/` (new `AutoSyncTests.cs`, style
`Trigger_Scenario_Expectation`), driving the real `ObjectChangeEvents` / `FileSystemWatcher` /
`GlobalObjectId` boundary against a live scene per CLAUDE.md. **What is testable vs. what needs a
human** is called out honestly.

**Testable — these are the actual gate:**
1. **Auto is ON with no setup.** A fresh project with no toggle key set: arm the bootstrap; a scene
   edit produces a source rewrite with **no** menu click. (Default-ON, `EditorPrefs` unset ⇒ true.)
2. **Debounce fires once for N edits.** Emit N rapid `changesPublished` on one object inside the
   settle window ⇒ **exactly one** reconcile/apply runs, not N. (Counts applies via a seam.)
3. **No reverse-direction ping-pong.** Auto-build mutates the scene; assert the scene-suppression flag
   drops the resulting `ObjectChangeEvents` and **no** scene→code sync runs. Auto-sync writes the
   `.cs`; assert the `(path, hash)` registry drops the watcher echo and **no** code→scene build runs.
   The loop settles — a bounded number of cycles, never unbounded.
4. **Watcher fires on an external write but NOT on a `WriteIfChanged` no-op.** An external edit to the
   builder triggers code→scene; a Sync that re-emits byte-identical source (via `WriteIfChanged`) does
   **not** fire the watcher. (Pins blocker 1's mtime protection.)
5. **Trigger survives a domain reload.** Force a recompile/domain reload with auto ON ⇒ the
   `[InitializeOnLoad]` bootstrap re-subscribes and a subsequent scene edit still syncs.
6. **Per-edit work is O(changed), not O(scene).** In a large scene, a single-field edit ⇒ the identity
   cache's resolution counter shows `GlobalObjectId` resolutions proportional to the change set, not
   to scene size. (The counting seam; this is the perf gate.)
7. **Spurious patch does not deadlock.** Force the convergence-defect case (patch produced, applies
   byte-identical) ⇒ the loop logs the defect loudly and stays converged; a following genuine edit
   still syncs. (The blocker-2 gate, at the live boundary.)
8. **Fresh object created live, then synced.** Create a GameObject in the editor (no manual save) ⇒
   auto-sync saves the scene, the object gains a durable `GlobalObjectId`, the source appends it, and a
   second unchanged sync is a **no-op** (durable id persisted, not re-created). (Blocker 4.)
9. **Both-sides field-disjoint change auto-resolves.** Edit field A of object X in the scene and field
   B of object Y in code ⇒ both apply in their own direction, **no** conflict raised (field-level
   authority).
10. **True same-field overlap → scene wins, non-modally.** Edit the *same* field of the *same* object
    on both sides ⇒ the source is patched to the **scene** value (scene-wins), the prior **code** value
    is preserved in an inline `// CONFLICT:` marker, and a located Console error is logged; **neither
    side silently clobbered**; no modal opens.
11. **Master toggle off ⇒ manual only.** Toggle auto off; a scene edit produces **no** sync; the
    retained `CodeScenes/Sync` and `CodeScenes/Build` menu items still work. Toggle back on ⇒ the
    key persists and (re-read after a simulated restart) auto is on again.
12. **Play mode pauses auto.** Enter Play mode ⇒ edits do not sync; exit ⇒ auto resumes.

**NOT testable in EditMode — stated plainly, needs a human check:**
- **"Feels instant" under a real human drag** — that a continuous transform drag never stutters or
  freezes cannot be asserted headlessly. The **O(changed)** assertion (#6) and the **debounce-once**
  assertion (#2) are the machine-checkable stand-ins and are the enforced gate; the subjective
  smoothness is a manual confirmation the user performs by dragging a value in a large scene.

## Dependencies

- **M1 / M1b** (`Materialize`, non-destructive in-place Build) — auto-build updates the scene in place,
  never wipes; this is what makes code→scene safe to fire automatically.
- **M2 / M2b / M3 / M4** — the `Reconcile` → `SourcePatch` machinery (`SceneBuilderSync.Run`), the
  structural create/append path (blocker 4), and asset-ref lowering the drift check depends on.
- **M7 robustness** (`specs/08-m7-robustness.md`) — **owns `SuppressionScope`** (ref-counted,
  exception-safe, time-bounded) and `[InitializeOnLoad]` re-subscription. `SuppressionScope` **does not
  exist yet** (grep-verified). M-Auto's write-seam suppression is that type; sequence M-Auto to land
  after (or jointly with) the M7 task that introduces it, so suppression is inherited at the write
  seam rather than reinvented.
- **Ordering:** **M-Auto is the driver, moved up ahead of M5.** It is the product thesis (seamless
  bidirectional sync); cross-object references (M5) and later milestones are validated *through* it (or
  with it toggled off), not before it.

## Risks/notes

- **Feedback loop is the headline risk.** Mitigated by write-seam suppression (blocker 5), debounce
  (blocker 3), and one-authoritative-direction-per-cycle. EditMode #3 pins no-ping-pong. Suppression
  must be exception-safe and time-bounded (M7): a scope that fails to exit would deafen the loop.
- **The `AssetPostprocessor` trap.** It is the obvious, documented Unity hook for "a file changed" and
  it is **dead** here — the builder is outside the scanned roots (blocker 1). Any future contributor
  will reach for it; the file-watcher path is mandatory, not a preference.
- **The spurious-patch deadlock is subtle and load-bearing.** Drift measured as `Patch.Edits.Length`
  looks correct and silently latches forever on a convergence defect (blocker 2). Drift MUST read the
  applied-byte delta (`WriteIfChanged` / `SyncResult.Changed`), and a real defect MUST be escalated,
  not swallowed. Core test #5 and EditMode #7 pin both sides.
- **Perf is the whole product, not a nicety.** The current reader is `O(scene)` per call
  (`GetGlobalObjectIdSlow` per GameObject, `NextVisible` per component). Auto that walks the scene per
  keystroke reproduces the "drag → freeze" failure CLAUDE.md calls fatal. The O(changed) budget
  (EditMode #6, the counting seam) is a hard gate, not advisory.
- **Save-on-sync-for-creates is a real behavioral decision.** Auto-sync writes to disk (saves the
  scene) on a structural create that manual Sync never did. It is consistent with Build (which already
  saves) and is the only way a live-created object earns a durable id before the sidecar records it —
  but it means creating an object auto-saves the scene. Flagged for the user to confirm.
- **The conflict tie-break is RATIFIED: scene-wins.** Field-level authority + non-modal surfacing,
  and on the residual same-field overlap the scene edit wins — source patched to the scene value, the
  prior code value preserved in the `// CONFLICT:` marker. User decision 2026-07-16 (not code-wins, not
  mark-and-halt).
- **`GetGlobalObjectIdsSlow` (batch) is assumed, not yet exercised.** No current code path uses it; the
  pipeline must confirm the overload against the installed 6000.5.3f1 editor before depending on it,
  and fall back to the per-object call (still correct, only slower) if it is unavailable.
- **No confirmation dialog, ever.** Foundation §7 was set to seamless-by-default explicitly to support
  this; do not reintroduce a confirm/preview step, a drift panel, or a per-direction toggle.
