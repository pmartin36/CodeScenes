# CodeScenes — work order

Read `CLAUDE.md` first; it is the operating contract and it wins over this file.
This file says only what to do next and in what order.

## State of `main`

Tree clean. Gate `GATE PASS: Core + Unity EditMode green (passed=637 failed=0 skipped=0)`
(2026-08-06, `GATE_FORCE_UNITY=1`; the Core-only trigger skips layer 2 on a pure-Core change, and a
skip is not a Unity pass).

**Spec 36 (uniform value descent) SHIPPED and live-verified 2026-08-06** — moved to
`specs/completed/`. All five passes route container descent through `ValueWalk`, which gained
`Fold<T>` and `Descend<TContext>`. Two PERMANENT guards landed: `ValueContainerDescentScanTests`
(Roslyn token scan, fails when any site outside `ValueWalk.cs` re-derives a container switch) and
`GateTestMetaFileTests` (every `unity-gate` test `.cs` has its sibling `.meta`, so a test cannot
silently never run). Live log: `SceneBuilderTest/Logs/live-verify-spec36.log`.

Measured during that live pass, NOT caused by 36 and unowned: the demo scene reports a steady
9 plan ops on every build (its `FitSize`/`SurfaceSnap` move transforms after a build, so the next
build sees real drift; an isolated probe without spatial components went 11 ops then 0), and
`[CodeScenes] SurfaceSnap on 'New Game Object' has no Renderer/mesh bounds to snap` fires on every
DemoScene build against an object that is not in the builder. Neither is in `docs/open-defects.md`
yet; neither was investigated.

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

### 2. Features, in this order

- **`specs/09` M8 UnityEvents. NEXT — unblocked, 36 shipped.** Its `.agent_handoffs/m8-unityevents/tasks.md` is STALE —
  it carries hand-edits that assigned the descent migrations to M8's own b1-t1. Relaunch M8 FRESH after
  36 lands (the tree will have moved); do not resume that plan. The target model is
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
- **Halted on plan validation** — the cached plan IS the problem, so resuming reproduces it. What to
  change depends on WHICH halt this is, and the relaunch mode is the whole game:
  - **First halt:** fix the spec or `args`, relaunch with `{ spec: ... }`. Re-intake is right here.
  - **Second halt onward:** hand-fix `.agent_handoffs/<feature>/tasks.md` (gitignored) and relaunch
    with `tasksReady: true`, NOT with `{ spec }`:
    ```
    args: { feature: "<name>", tasksReady: true, noPush: true,
            fastGateCommand: "GATE_SKIP_UNITY=1 ./verify.sh",
            slowPathGlobs: ["com.codescenes/**", "unity-gate/**"],
            testPathGlobs: ["unity-gate/Assets/GateTests/**", "SceneBuilder.Core.Tests/**"] }
    ```
    Passing `{ spec }` re-runs intake, which DISCARDS your hand-fixed `tasks.md` and generates a new
    decomposition. You then review a different artifact every round: fixed issues stay fixed, but the
    fresh plan makes fresh omissions, so the finding count never falls. Measured on spec 36: seven
    rounds of spec-fix-and-regenerate never converged; switching to `tasksReady` dropped it to one
    high per round, each strictly downstream of the previous fix, at ~115k tokens instead of ~160k.
  - **Reproduce literals IN `tasks.md`.** Agents read the handoff protocol, `tasks.md`, sibling
    `research.md` and the `SOURCE:` spec. A deliverable saying "with the spec's signature" or "every
    file in the spec's floor" is a reference, not a contract. Paste exact signatures and enumerated
    filenames in.
  - **Two signs to stop editing and change something else:** the same class of finding returning in
    new clothes (you are writing rules as prose — an invariant needs an owning task, one shared
    mechanism, and a check that fails on bypass), or a requirement whose enforcement lives in the
    harness rather than the repo (agent behavior is in `~/.claude/agents/tdd-*.md`; re-express the
    guarantee as build order, a committed artifact, or a test).

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
