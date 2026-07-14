# M-Auto — Automatic sync (auto-build / auto-sync toggles + drift indicator)

Makes the Build (code→scene) and Sync (scene→code) buttons **optional** by driving them automatically —
safely, behind toggles, with guards that surface conflicts instead of clobbering. Binds to
`00-foundation.md`; this is almost entirely Editor-adapter work (reusing existing Core Materialize/
Reconcile), so it is pipeline-built and **compile-gated** per §8. It does NOT introduce continuous
per-keystroke merge — that stays parked (`needs_research/live-continuous-sync.md`); M-Auto reacts to
**saves / imports / editor edits**, one authoritative direction at a time.

### Additions to the contract
- **`SceneBuilder` EditorWindow panel** — a dockable panel hosting: manual **Build** / **Sync** buttons
  (unchanged), two independent **Auto-build (code→scene)** and **Auto-sync (scene→code)** toggles, and a
  **drift indicator** (in-sync / code-ahead / scene-ahead / **conflict** states).
- Reuses M7's **`SuppressionScope`** (no new type) to stop an auto-apply in one direction from
  triggering the other. No new §3 value types. Core exposes only a pure **dry-run drift check** —
  `Materializer.Materialize` producing an empty `Plan` == "code matches scene"; `Reconciler.Reconcile`
  producing an empty `SourcePatch` == "scene matches code" — so the adapter computes drift from existing
  Core, no new Core surface required (a thin `DriftState { CodeAhead, SceneAhead }` helper may live in
  the adapter).

## Goal
Turn on Auto and stop touching buttons: saving the builder `.cs` rebuilds the scene in place; editing
the scene rewrites the `.cs`; a drift light shows when they differ. When BOTH sides changed since the
last sync, nothing is auto-applied — the light goes **conflict** and the user resolves explicitly. Never
last-write-wins, never a silent clobber, never a feedback loop.

## In scope
- **Auto-build on save (code→scene)**, opt-in toggle: detect a change to the builder `.cs`
  (`AssetPostprocessor.OnPostprocessAllAssets` after recompile, main-thread) → **debounce** → parse;
  on a **clean parse** run Materialize **in place** (M1b, non-destructive); on a parse error do nothing
  and show it. **Guard:** if the scene has un-synced edits (a dry-run Reconcile would produce edits),
  REFUSE the auto-build and go **conflict** — do not overwrite the user's scene edits.
- **Auto-sync on scene edit (scene→code)**, opt-in toggle: subscribe to
  `ObjectChangeEvents.changesPublished` + `EditorSceneManager.sceneSaved` (via `[InitializeOnLoad]`,
  survives domain reload — M7) as **triggers** → debounce → full-snapshot Reconcile → apply the
  `SourcePatch` to the `.cs`. **Guard:** suppress self-triggered events (M7 `SuppressionScope`) so an
  auto-build's own scene mutations don't bounce back.
- **One authoritative direction per cycle (the anti-clobber core):** before auto-applying, compute
  `DriftState` from dry-run Materialize + Reconcile. If only code-ahead → auto-build (if enabled); only
  scene-ahead → auto-sync (if enabled); **both ahead → apply neither, set the indicator to conflict**,
  and offer the manual Build/Sync (with preview) to resolve.
- **Drift indicator:** recomputed on each trigger (not by continuous polling) — in-sync / code-ahead /
  scene-ahead / conflict; clicking it opens the manual reconcile.
- **Debounce/settle** (~300–500 ms after the last change) so mid-edit/in-progress states don't fire.
- **Play mode:** auto is disabled in Play mode (edits don't persist); resumes on return to edit mode.
- Manual Build/Sync remain fully available; Auto is layered on top, each direction independently
  toggleable and **off by default**.

## Out of scope
- **Continuous per-keystroke sync** inside the `.cs` editor — M-Auto reacts to saves/imports, not
  keystrokes (`needs_research/live-continuous-sync.md`).
- **Automatic 3-way merge** when both sides changed — surfaced as a conflict for explicit resolution,
  never auto-merged (also live-sync research).
- Cross-scene / multi-scene auto (single active scene only, per v1).

## Core deliverables
- No new §3/§5 types. A pure, side-effect-free **drift determination** the adapter can call cheaply:
  reuse `Materializer.Materialize(desired, snapshot, map)` (empty `Plan.Ops` ⇒ code==scene) and
  `Reconciler.Reconcile(model, snapshot, map, anchors)` (empty `Patch.Edits` ⇒ scene==code). Behaviors
  (testable): an in-sync model/snapshot pair yields empty Plan AND empty Patch; a code-only change yields
  non-empty Plan, empty Patch; a scene-only change yields empty Plan, non-empty Patch; a both-sides
  change yields non-empty Plan AND non-empty Patch (⇒ adapter renders conflict).

## Editor adapter deliverables (pipeline-built, compile-gated per §8)
- The **`SceneBuilder` EditorWindow** panel (toggles + drift light + buttons), toggle state persisted in
  `EditorPrefs`.
- **File-change trigger** for auto-build: `AssetPostprocessor` (or a main-thread-marshalled
  `FileSystemWatcher`) → debounce via `EditorApplication.update`.
- **Scene-edit trigger** for auto-sync: `ObjectChangeEvents.changesPublished` + `sceneSaved`, registered
  in an `[InitializeOnLoad]` static ctor (re-registers after domain reload).
- **`DriftState` computation** (dry-run Materialize + Reconcile) → drives the indicator and the
  one-direction-per-cycle guard.
- **Feedback-loop suppression** using M7's `SuppressionScope` around every auto-apply.
- Debounce, clean-parse gate, play-mode gate, conflict surfacing (log + indicator), all reusing existing
  Materialize/Reconcile/Conflict plumbing.

## Authoring API added
None — this is editor UX over existing Core.

## IdentityMap / sidecar changes
None beyond what Build/Sync already write (Auto uses the same code paths).

## Core test plan (RED tests — headless)
- `Drift_InSync_EmptyPlanAndEmptyPatch`
- `Drift_CodeOnlyChange_NonEmptyPlan_EmptyPatch`
- `Drift_SceneOnlyChange_EmptyPlan_NonEmptyPatch`
- `Drift_BothChanged_NonEmptyPlanAndPatch` (⇒ conflict signal to the adapter)
- (adapter-side auto-trigger / debounce / suppression behavior is validated by the Unity checklist, not
  headless — the Core guarantee is only the dry-run drift determination)

## Unity confirmation checklist
1. Open the **SceneBuilder** panel; both Auto toggles **off** → Build/Sync work exactly as before.
2. Enable **Auto-build**; edit `DemoScene.cs` (move something) and **save** → the scene updates in place
   (existing objects keep identity, no wipe), no button pressed; drift light returns to in-sync.
3. Enable **Auto-sync**; move an object in the editor → after the debounce, `DemoScene.cs` rewrites
   itself, no button pressed.
4. Both on; edit one side → the other follows; there is **no** ping-pong / infinite rebuild loop.
5. **Conflict:** with both on, edit the `.cs` (unsaved-in-scene) AND move an object, then save/settle →
   the drift light goes **conflict**, **neither** side is auto-overwritten, and the manual buttons let
   you resolve with a preview.
6. Enter Play mode → auto pauses; exit → auto resumes.
7. Trigger a domain reload (recompile) with Auto on → the triggers re-register; auto still works.

## Dependencies
- **M1b** — non-destructive, reconcile-into-existing Build (so auto-build updates in place, never wipes).
- **M7** — self-event suppression (`SuppressionScope`), domain-reload survival, external-edit
  reconciliation (auto-both-directions is exactly the feedback-loop scenario M7 hardens).
- **M2** (Reconcile), **M1** (Materialize), plus `ObjectChangeEvents` as the scene-edit trigger.

## Risks/notes
- **Feedback loop** is the headline risk — mitigated by (a) `SuppressionScope` around auto-applies,
  (b) debounce, and (c) one-authoritative-direction-per-cycle. Test 4 pins no-ping-pong.
- **Both-changed conflict UX** must be obvious (the indicator + refusal), never a silent pick.
- **Drift cost:** compute drift only on triggers (post-debounce), not by continuous polling, to keep
  large scenes responsive.
- Default **off**: Auto is opt-in; the reliable explicit-reconcile default (v1) remains for anyone who
  wants it. Full always-on continuous sync stays out (live-sync research), by design.
