# SceneBuilder — session handoff (paused 2026-07-16 10:55 EDT, resume 12:15pm)

## RENAME DONE (2026-07-16) — CodeScenes / com.codescenes
- **Repo moved:** `/home/paul/Source/UnitySceneBuilder` → `/home/paul/Source/CodeScenesUnity` (`.git` traveled; work from the new path).
- **Package renamed:** `com.scenebuilder` → `com.codescenes` (folder, `package.json` name, `displayName` → CodeScenes; `unity-gate` manifest + packages-lock; Core→Plugins DLL-staging target in `SceneBuilder.Core.csproj`; verify.sh Unity-trigger regex).
- **Menu items:** `SceneBuilder/Build…` / `SceneBuilder/Sync…` → `CodeScenes/Build…` / `CodeScenes/Sync…` (attribute strings only).
- **Product prose** (README, CLAUDE.md, foundation spec headings/intros, verify.sh header) → CodeScenes.
- **KEPT (scene-building machinery):** `SceneBuilder.Core/Authoring/Editor` assemblies, namespaces, `SceneBuilder.Core.dll`, `SceneBuilder.sln`, `.csproj` names, class names (`SceneBuilderBuild/Sync/Paths`), all types. No namespace/asmdef/class-name changes.
- **Verified from the new path:** `GATE PASS: Core + Unity EditMode green (passed=147 failed=0 skipped=0)`, Core 382 tests. Commits `788d6d6` (rename) + `6ad1817` (restaged DLL).

## Repo state (all on `main`, tree clean, HEAD = 2fe50b6)
- **M-Builtin** merged — author primitives from code (`Builtin("Cube")`). Core 347 / Unity 133. Verified GATE PASS.
- **M16** committed — duplicate siblings disambiguate by injected `var` handle, NOT `.Id("Name-2")`. Core 370 / Unity 137. Verified GATE PASS. `InjectExplicitId`/`MintId`/`IntroduceIdCall` retired; `IdCollisionHealer`/`NodeAnchor`/`IntroduceHandle` shipped.
- **Spec cleanup** (c1aaaf7) — 10 specs realigned to shipped behavior; `completed/15` booby trap defused; foundation §7/§8 root cause fixed (gate is `./verify.sh`, not compile-only).
- **spec 16 revised** (d1f3ed9) — RECOVERED from transcript after scratchpad backup was lost. Durable backup: `/tmp/scenebuilder-spec16-revised.backup.md`.
- **spec 18** (7f50133) — nested value types, probe-grounded (reflection FullName, NOT SerializedProperty.type which drops namespaces/mangles generics).

## STATUS 2026-07-16 ~12:20 EDT
- **Both new specs DONE + committed to main:** spec 14 (M-Auto seamless, auto ON, persisted master toggle — 36704f0) and spec 19 (Sizer/Snapper — 3874438). CLAUDE.md M-Auto paragraph reconciled (in 36704f0). Spec design decisions all captured below + in memory.
- **spec 18 pipeline RE-LAUNCHED and RUNNING** (task w2y3xg3y2, run wf_ceb7e64c-611). When it completes: verify real GATE PASS (GATE_FORCE_UNITY=1 ./verify.sh, quote the line), it's already on main. If session-limit clips it, resume-from-journal.
- Open user calls (nothing blocks): M-Auto conflict case (recommend field-level authority + non-modal surfacing); Sizer/Snapper manual-override (recommend constraint-wins) and final names.

## (historical) spec 18 re-launch command if needed:
`Workflow({ scriptPath: "/home/paul/.claude/skills/tdd-pipeline/pipeline.workflow.js", args: { feature: "nested-value-types", tasksReady: true } })`
Plan APPROVED (`valid: true`), 2 buckets / 6 tasks, at `.agent_handoffs/nested-value-types/tasks.md`. Fixes the proven `new object { }` CS0117 emit bug. After it lands: verify real GATE PASS (never an exit code), it's already on main.

## Gate/pipeline rules (hard-won)
- Gate verdict = the `GATE PASS`/`GATE FAIL` line ONLY, never an exit code. Verify independently: `GATE_FORCE_UNITY=1 ./verify.sh` (single fg command, NO nested `&`).
- Pipelines serialize on Unity's single-instance lock — one at a time.
- Never touch the repo while a pipeline owns it. Restore the rebuilt `Plugins/SceneBuilder.Core.dll` before launching (`git restore`).
- `.agent_handoffs/` is gitignored — plans live only there; back up anything else OUTSIDE the session scratchpad (it dies on date roll — that's how the spec-16 backup was lost).
- Session-limit escalations cost ~0 tokens (API refuses); resume-from-journal recovers. Not a real failure.

## NEW WORK ordered — FINAL ORDER (user-confirmed 2026-07-16)
**1. spec 18 (running, finish+verify) → 2. M-Auto (spec 14) → 3. M5 cross-object-refs (spec 06) → 4. Sizer/Snapper (spec 19) → M6+.**
Specs 14 and 19 are WRITTEN + committed (36704f0, 3874438). They still need decompose→validate→build via the tdd-pipeline.
Order rationale: M5 before Sizer/Snapper because Snapper's optional `target:` override uses M5's cross-object two-pass resolution (the raycast default needs nothing from M5).

**CRITICAL for the M5 step:** spec 06 is UNBUILDABLE as written — repair it FIRST (task #6 brief). Its headline sample fails to compile 4 ways (no generic Add<T> → use Component<T>; Button.target doesn't exist → targetGraphic, Component-typed; passing a handle to .Set has NO overload → M5 must ADD Set<TValue>(Func<T,TValue>, NodeHandle); .Set(...,null) is CS0121-ambiguous → needs an unambiguous null form). Plus F5 component-targets-unauthorable, F6 delete-cascade emits CS0103 (fix inside DetectRemovals), F7 (HIGHEST LEVERAGE) Rekey never rewrites ObjectRef targets (fix inside Rekey), F8 no ConflictKind.DanglingReference. So the M5 step = repair spec 06 → decompose → validate → build.

Note: spec 19 introduces the `DrivenChannels` suppression machinery for the FIRST time (spec 13/RectTransform was never built); RectTransform later reuses 19's mechanism, not vice-versa.

Each new pipeline: decompose+validate to `valid:true` FIRST (this session's plans were rejected up to 5x before passing — a bad plan just halts the pipeline). Run on main directly (user: "we can always go back"). Verify real GATE PASS after.

### M-Auto (rewrite spec 14 — current shape is wrong)
User DECISIONS:
- **Single master "auto" toggle** (one on/off, both directions) — NOT per-direction.
- **Keep the menu items** (manual Build/Sync) as debug + testing tools.
- Toggle exists so the user can DISABLE auto and validate future specs step-by-step; they'll test both ways.
- **Default: auto ON** (confirmed by user — matches invisible-product north star). Toggle state is a **persisted per-project editor setting** (must live outside `Assets/`). User will turn it OFF for THIS project. Spec 14 rewrite + Sizer/Snapper spec (19) are being WRITTEN now (specs are cheap; only the tdd-PIPELINE was paused for usage).
- Seamless auto is still THE product/north star; the toggle is a testing affordance, NOT "auto is optional."
- **RECONCILE CLAUDE.md**: its M-Auto paragraph calls the toggle shape "wrong / must be seamless-by-default." Update that wording so the next agent isn't whipsawed — a toggle is now wanted, for testing.
- Audit already found the real blockers: old trigger `AssetPostprocessor.OnPostprocessAllAssets` CANNOT fire for a builder outside `Assets/` → needs the plugin's own FileSystemWatcher + `ObjectChangeEvents`; one spurious patch would DEADLOCK sync → needs a guard (treat byte-identical applied text as no-drift); no incremental identity → full-scene `GetGlobalObjectIdSlow` per change is fatal per keystroke, needs debounce + cached/incremental identity (batch `GetGlobalObjectIdsSlow` exists).

### Sizer/Snapper (NEW spec — editor-time-only spatial authoring components)
Purpose: LLMs don't understand space. These let the LLM express INTENT, tool computes geometry.
- **Sizer**: any mesh specifies desired size in WORLD-SPACE units, indifferent to incoming mesh size. Reads mesh bounds, computes local scale to hit target (account for parent lossyScale). (Sizer dimensioning: aspect-lock fit with per-axis override — my inference, spec decides.)
- **Snapper** (maybe name `SurfaceSnap` — does walls/ceilings too): snap a mesh to a surface, origin-agnostic (uses actual mesh bounds, not pivot). Pick one horizontal (left/right/none) AND one vertical (up/down/none), combine (e.g. bottom-left corner). Runs AFTER Sizer (needs final world size).
- User DECISIONS:
  - **Snap target = raycast scene geometry, with optional explicit target override.** Needs colliders or falls back to renderer bounds.
  - **Live editor constraint** (NOT bake-once): persists as editor-only component, re-evaluates when mesh/surface moves, driven scale/position SUPPRESSED from code (reuse spec 13 RectTransform driven-property model), STRIPPED from game builds.
- Built on M3 component machinery (shipped) + spec 13's driven/suppressed-property concept. Editor-only-component-stripped-at-build is an impl concern the spec handles (missing-script risk if it's an Editor-assembly MonoBehaviour on a scene object).
- Bidirectional design point the spec MUST resolve: if the user manually moves a snapped object in-editor, does Snapper re-snap (win) or does the manual move override? (Same conflict class; acute here since transform is derived.)

## Open user items
- M-Auto default state (off-for-now vs) — confirm.
- Whether the user wants ME to draft both specs (M-Auto rewrite + Sizer/Snapper) for the other agent, or just hand off order + decisions.
- M16 went straight to main (no branch) per user "just do m16 on main, we can always go back" — user may still want to eyeball it. Test plan at `/tmp/scenebuilder-test-plan.md` (item 14 covers dup siblings).
