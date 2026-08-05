# CodeScenes — work order

Read `CLAUDE.md` first; it is the operating contract and it wins over this file.
This file says only what to do next and in what order.

## Decision waiting for you

**`specs/35` D2 — the sorting-layer convergence regression — needs an approach decision and is the
one thing blocking a full spec-35 build.** Spec 33's C6 excluded `m_SortingLayer`; the excluded field
is then in neither the snapshot nor the type template, so the Differ emits a plan op on every build
forever, and the reconciler (which walks snapshot fields) can never prune the source line. Two ways
to close it, written up in the spec:
- **(a)** Excluded fields are one-way, made loud: report a source-authored excluded field through
  spec 33's located report channel. Smaller, reuses a channel that now exists.
- **(b)** Excluded fields are prunable: carry the adapter's excluded-path set into Core so the
  reconciler emits a removal and the line disappears. Probably what a user expects.

Nothing authors the field today, so nothing is broken right now.

## State of `main`

Tree clean. Gate `GATE PASS: Core + Unity EditMode green (passed=580 failed=0 skipped=0)`.

Shipped and live-verified: spec 33 in full (C5-C10, the located report channel, and the bidirectional
round-trip proof suite), plus M-UI RectTransform sync (spec 13) and spec 32's C1-C4.

Shipped, gate-verified only: spec 31, the hidden-serialized-field reader contract. Its headline claim
- component enable/disable syncing scene->code - still has no live confirmation.

## Order

### 1. Harness first — it pays for itself across everything after

**1b. Sweep existing pipeline prose from the codebase.** `verify.sh` now lints comments for task ids,
`research.md` citations, iteration numbers and pending-state prose — but only on lines the working
diff ADDS. Everything already in the tree is invisible to it. One sweep closes that.

**1d. Run `/tdd-learnings`.** Eight recurring entries remain active (13 were applied and archived to
`ledger-archive.md`). Most are evidence-discipline: RED confirmed for the wrong reason, stale-test
sweeps scoped too narrowly, a deliverable dropped with a prose rationale.

**1e. Review `docs/agent-friction.md`.** It populates itself from Unity editor logs via a `Stop` hook;
nobody needs to run anything. Promote recurring `PRODUCT` rows to spec items and set their `status` so
they move out of the open list. One row needs judgement now: a typed asset catalog emitting
`CS0542: 'Materials': member names cannot be the same as their enclosing type`. It came from a log
predating the collapse-rule fix, so **check whether it still reproduces** before acting.

### 2. Fix what is broken on `main` — `specs/35`

Four defects spec 33's build surfaced, each adversarially audited rather than taken as filed.

- **D1** — authoring a populated `List<GameObject>` of scene references writes N NULL slots.
  Confirmed by construction: `WriteProperty` has no `ObjectRef` case and no `default:` arm, so every
  element falls through the switch silently. This is C9's data loss reversed and nothing covers it.
- **D2** — BLOCKED on the decision at the top of this file. Excluded from a build until you answer.
- **D3** — the incremental snapshot cache has no invalidation site, so the manual Sync and Build menu
  commands can leave it serving nodes computed under a stale IdentityMap: a spurious dangling-reference
  warning and a suppressed field patch for one cycle.
- **D4** — sync value-patches inside an author's typed selector, which cannot take an `InstanceHandle`,
  so rewiring a field onto a prefab-instance root emits source that does not compile.

### 3. Features, in this order

- **`specs/09` M8 UnityEvents.** Unblocks the minigolf demo together with spec 13. The target model is
  already resolved in the spec: add `ComponentRef<T>` captured via `node.Ref<T>(ordinal)`. No model
  change — `ObjectRef` already carries a bare LogicalId and the IdentityMap already has
  `Kind=="Component"` entries.
- **`specs/08` M7 robustness.** Smallest remaining. Suppression and reload re-subscribe already
  shipped under M-Auto; what is left is `SyncCheckpoint` hash routing, external-edit reconcile on
  focus-regain, and a determinism audit. Highest data-loss protection per hour spent.
- **`specs/10` M9 SerializeReference.** Depends on `ValueWalk` being right: a `ManagedReference`'s
  fields may contain further `ManagedReference`s, nesting arbitrarily. If recursion is wrong anywhere,
  this is where it hurts most.
- **`specs/12` M11 animation.** NOT buildable as written — its only playback path is the legacy
  `UnityEngine.Animation` component, which Paul rejected. Needs the parked AnimatorController work
  promoted out of `needs_research` and a spec rewrite BEFORE any pipeline run.

- **`specs/34` M-Licensing.** Activation, seats and the 14-day trial, for the Gumroad build only.
  Not sequenced against the above — it gates shipping, not any other milestone. Two open items block
  a pipeline run: whether `ECDsa` P-256 verification works on the target editor across all three
  desktop platforms, and which machine identifier is actually stable. Both are spikes, not guesses.
  The backend half lives in the site repo (`CodeScenesSite/specs/01-licensing-backend.md`) and is
  already partly stood up.

`specs/00-foundation.md` stays in `specs/` as the living contract.

## Resuming an interrupted pipeline run

Two stops, opposite handling — conflating them wastes a full run:

- **Paused, killed, usage-limited, or died on an API error** — the plan was fine, work was
  interrupted. Resume by id; completed tasks replay from cache:
  ```
  Workflow({ scriptPath: "~/.claude/skills/tdd-pipeline/pipeline.workflow.js",
             resumeFromRunId: "<RUN_ID>",
             args: { spec: "<path>", noPush: true,
                     fastGateCommand: "GATE_SKIP_UNITY=1 ./verify.sh",
                     slowPathGlobs: ["com.codescenes/**", "unity-gate/**"] } })
  ```
  Resume REQUIRES re-passing `args` or it bails with "no input" — including the gate-scoping pair
  (see CLAUDE.md), which is not recorded in the run and is silently off if omitted.
- **Halted on plan validation** — the cached plan IS the problem, so resuming reproduces it. Hand-fix
  `.agent_handoffs/<feature>/tasks.md` (gitignored) and relaunch FRESH.

Editing feature code under a paused run is safe as long as the full gate passes afterward: a cached
GREEN verdict only lies if behavior broke, and the gate catches that. Editing the harness
(`verify.sh`, `pipeline.workflow.js`, `tdd-*.md`) under a running one is not — agents read their
prompt at spawn, and edits get swept into the next bucket commit by `git add -A`.

## Per milestone, all four steps

1. Build through the tdd-pipeline — one invocation per milestone, `noPush`, on `main`.
2. Quote the `GATE PASS:` line from `./verify.sh`.
3. Live-verify with `unity-live-verify`. Close the editor before any batchmode gate run and reopen
   after; there is one license seat.
4. Fix what live-verify finds, then move the spec to `specs/completed/` and update its README.

## Rules that are not optional

- **The gate is `./verify.sh` and the only verdict is its `GATE PASS:` line.** A wrapper's exit code is
  not the gate's exit code. Quote the line or you did not verify it.
- **Gate-green is not done.** Every Unity-facing milestone gets a live-editor pass via
  `unity-live-verify` before it is called complete. This has caught what the gate missed every time,
  including a defect that killed auto-sync entirely while 400+ tests stayed green.
- **A milestone does not absorb pre-existing bugs found next door.** File them as their own spec.
- **Name a cross-cutting invariant and give it ONE owning task** with ONE shared implementation every
  other task calls, plus the check that fails when a site bypasses it. `task-deconstruct` and
  `tdd-decomposition-validator` enforce this; do not restate an invariant as per-task guidance.
- **Only one Unity license seat.** A batchmode gate and a live editor cannot run at once.
- **Circuit breaker.** Bring Paul a kill/continue decision with evidence when a run passes ~4h, the
  seam pass re-opens a committed bucket, a task cycles more than 3 times, or two status checks have
  nothing substantive. A status update that proposes no action is not worth sending.

## Known and accepted

Recorded so they are not rediscovered as new:
- Native enum fields whose backing public member no reflective rule can name (`m_CastShadows`,
  `m_MotionVectors`, `m_AdditionalShaderChannelsFlag`, `m_SpriteTileMode`, `m_Interpolate`) round-trip
  as raw ints. Values converge; only the typed spelling is absent.
- A struct whose representable members return to default while an unrepresentable member still differs
  reports on every sync. Console noise, not file churn.
- `ComponentDefaultOmission.Index` probes via `FieldMap.TryGetValue`, a linear scan, where the
  blueprint specified an O(1) dictionary. Correctness unaffected.
- A `// CONFLICT:` marker writes a U+2014 em dash into the user's builder `.cs`
  (`SceneBuilderSync.Conflicts.cs:267`), violating the ASCII-in-plain-text rule.
- `docs/agent-friction.md` stamps `first_seen`/`last_seen` at extraction time, not from log timestamps.
