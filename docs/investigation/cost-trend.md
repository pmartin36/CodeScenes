# Cost per feature over time

Scope: when the TDD pipeline's cost per feature started climbing, and what coincided.
Written incrementally; the table is the primary artifact.

## Data sources and what each one can and cannot say

| Source | Covers | Reliability |
|---|---|---|
| `.agent_handoffs/<feature>/**/*.md` byte totals | all 35 features, 07-13 → 08-07 | PROXY. Bytes of agent-written handoff prose. The only signal that reaches back before 07-16. |
| `~/.claude/projects/.../subagents/workflows/wf_*/agent-*.jsonl` per-message `usage` | 07-16 15:29 → 08-07 04:10 (88 runs, 2179 agent transcripts) | MEASURED. Real `input`/`output`/`cache_creation`/`cache_read` token counts per agent. Nothing before 07-16 — the first nine features predate the workflow journals entirely. |
| `.agent_handoffs/_lessons/cost-log.md` | last 4 features only | MEASURED but output-tokens only, and only for runs that reached the end. |
| `git log --numstat` windowed to each feature's artifact mtimes | all features | PROXY. Lines are attributed by time window, so unrelated doc/spec commits inside the window are counted in "other" and excluded, but a hand-fix committed mid-feature is counted as feature work. |

`workflowProgress` with per-agent `tokens`/`durationMs` is **not** present in these journals; the
token numbers below come from the agent transcripts instead, which is strictly better data.

Token columns: `outTok` is output tokens summed over every agent in the feature. `totalTok` is
input + cache-creation + cache-read + output, i.e. total context volume pushed through the model.
Cache-read dominates `totalTok` (typically 88-93% of it) and is the cheapest component, so the two
columns answer different questions: `outTok` tracks work produced, `totalTok` tracks context weight.

## THE TABLE

Ordered by first artifact mtime. `tasks` is `### Task` headings in `tasks.md`; `agents` is
transcript count (every agent dispatch including retries); `prod`/`test` are `.cs` lines
added+deleted in the feature's git window.

| # | date | feature | tasks | runs | agents | outTok | totalTok | artifact KB | prod lines | test lines | outTok / prod line | agents / task |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 07-13 | scenebuilder-core-m0-m2 | 19 | — | — | — | — | 362 | 2131 | 1617 | — | — |
| 2 | 07-13 | m2b-structural-syncback | 15 | — | — | — | — | 313 | 1135 | 998 | — | — |
| 3 | 07-14 | m1b-nondestructive-materialize | 4 | — | — | — | — | 64 | 415 | 1342 | — | — |
| 4 | 07-14 | m2c-flags-syncback | 6 | — | — | — | — | 138 | 1188 | 1926 | — | — |
| 5 | 07-14 | m3-components-fields | 14 | — | — | — | — | 247 | 1461 | 2337 | — | — |
| 6 | 07-14 | m3-reconcile-components | 9 | — | — | — | — | 282 | 1926 | 2265 | — | — |
| 7 | 07-14 | m3-handleless-component-attach | 3 | — | — | — | — | 60 | 1299 | 572 | — | — |
| 8 | 07-14 | identity-remap | 3 | — | — | — | — | 50 | 347 | 237 | — | — |
| 9 | 07-14 | m4-asset-references | 10 | — | — | — | — | 177 | 1242 | 2382 | — | — |
| 10 | 07-15 | m-builtin-resources | 19 | — | — | — | — | 657 | 1226 | 4014 | — | — |
| 11 | 07-15 | duplicate-sibling-identity | 15 | — | — | — | — | 321 | 1663 | 4987 | — | — |
| 12 | 07-16 | nested-value-types | 6 | — | — | — | — | 141 | 102 | 993 | — | — |
| 13 | 07-16 | unqualified-type-names | 7 | 1 | 43 | 353k | 48.1M | 137 | 1063 | 1275 | 333 | 6.1 |
| 14 | 07-16 | headless-builder-validation | 10 | 1 | 56 | 647k | 103.4M | 252 | 2339 | 2821 | 277 | 5.6 |
| 15 | 07-16 | m-auto-live-sync | 10 | 1 | 62 | 912k | 159.5M | 282 | 1904 | 2626 | 479 | 6.2 |
| 16 | 07-17 | m5-cross-object-references | 11 | 1 | 85 | 994k | 184.0M | 317 | 1740 | 2794 | 572 | 7.7 |
| 17 | 07-17 | spatial-authoring-components | 13 | 3 | 83 | 1146k | 199.4M | 380 | 1158 | 2445 | 990 | 6.4 |
| 18 | 07-17 | project-subasset-refs | 9 | 1 | 40 | 420k | 55.5M | 162 | 569 | 2080 | 739 | 4.4 |
| 19 | 07-17 | m6-prefab-instances | 16 | 2 | 103 | 1046k | 176.6M | 392 | 1024 | 2201 | 1022 | 6.4 |
| 20 | 07-21 | fitsize-surfacesnap-transform-authority | 9 | 2 | 102 | 1083k | 195.4M | 406 | 2890 | 3459 | 375 | 11.3 |
| 21 | 07-21 | m10-prefab-overrides | 8 | 5 | 129 | 1745k | 282.5M | (interleaved) | ~2600 | ~2400 | ~670 | 16.1 |
| 22 | 07-22 | codescenes-analyzers | 13 | 1 | 69 | 795k | 115.1M | 306 | 2715 | 1785 | 293 | 5.3 |
| 23 | 07-22 | typed-prefab-facades | 23 | 5 | 115 | 1267k | 194.5M | 447 | 2308 | 2523 | 549 | 5.0 |
| 24 | 07-22 | m-nested-props | 19 | 2 | 170 | 1817k | 401.8M | 758 | 2728 | 3440 | 666 | 8.9 |
| 25 | 07-23 | typed-asset-catalogs | 12 | 2 | 87 | 1075k | 226.2M | 351 | 1662 | 2346 | 647 | 7.2 |
| 26 | 07-28 | leading-folder-collapse | 1 | 1 | 9 | 99k | 11.4M | 35 | 331 | 229 | 301 | 9.0 |
| 27 | 07-28 | multi-scene-builders | 5 | 2 | 44 | 530k | 100.2M | 201 | 570 | 479 | 930 | 8.8 |
| 28 | 07-28 | multiscene-discover-filescan-fix | 1 | 1 | 10 | 74k | 11.4M | 32 | 80 | 62 | 930 | 10.0 |
| 29 | 07-28 | live-verify-bug-fixes | 8 | 1 | 46 | 516k | 102.6M | 182 | 462 | 1302 | 1118 | 5.8 |
| 30 | 07-29 | **m-ui-recttransform** | 14 | 3 | **225** | **4448k** | **973.0M** | **1683** | 3242 | 8063 | **1372** | 16.1 |
| 31 | 07-30 | serialization-fidelity | 8 | 1 | 82 | 2427k | 628.2M | 743 | 2752 | 3509 | 882 | 10.2 |
| 32 | 08-04 | boundary-defects-and-roundtrip-proof | 6 | 5 | 133 | 2521k | 573.1M | 931 | 969 | 4196 | **2602** | 22.2 |
| 33 | 08-05 | reference-writes-and-cache-invalidation | 6 | 4 | 100 | 2027k | 551.6M | 850 | 1384 | 5587 | 1465 | 16.7 |
| 34 | 08-05 | excluded-field-one-way-report | 4 | 2 | 34 | 592k | 130.1M | 254 | 420 | 1411 | 1410 | 8.5 |
| 35 | 08-06 | uniform-value-descent | 13 | 14 | 108 | 1807k | 240.4M | 671 | 367 | 2587 | **4926** | 8.3 |
| 36 | 08-05→08-07 | **m8-unityevents** (still unfinished) | 48 across 4 plans | **21** | 133 | 3479k | 678.5M | 1131 | 1384* | 2843* | ≥2514 | 2.8 |

\* m8 is in flight; a large part of its production work is uncommitted at the time of measurement,
so its per-line figures are floors, not values.

Notes on specific rows:
- Rows 1-12 have no token data at all. The workflow journal starts 07-16 15:29, at `unqualified-type-names`.
- Row 21 (`m10-prefab-overrides`) has no `.agent_handoffs/` directory of its own; it ran interleaved
  with `fitsize-surfacesnap` on a branch, so its artifact bytes and git lines are estimates from
  commit subjects, and its token row is exact (name-attributed from journals).
- `runs` counts every `wf_*` workflow launch attributed to the feature, including plan-validation
  rounds that produced zero code. That distinction is what rows 35 and 36 are about.

## Compounding: what each shipped feature leaves behind for the next one

Snapshot at the last commit of each day. `verify.sh` and `CLAUDE.md` are the process surface every
agent must satisfy; the test counts are the suite every later task must keep green.

| date | Core `[Fact]` | Core test files | EditMode tests | EditMode files | verify.sh lines | CLAUDE.md lines | Core prod lines |
|---|---|---|---|---|---|---|---|
| 07-14 | 269 | 39 | 30 | 7 | 62 | 51 | 5611 |
| 07-15 | 333 | 45 | 69 | 13 | 95 | 100 | 6840 |
| 07-16 | 426 | 59 | 172 | 31 | 95 | 102 | 8174 |
| 07-17 | 521 | 68 | 243 | 40 | 95 | 102 | 9267 |
| 07-18 | 562 | 77 | 260 | 45 | 95 | 102 | 9832 |
| 07-21 | 635 | 82 | 276 | 46 | 95 | 102 | 11339 |
| 07-22 | 686 | 93 | 307 | 56 | 95 | 102 | 12645 |
| 07-23 | 753 | 99 | 333 | 65 | 95 | 102 | 14319 |
| 07-28 | 763 | 102 | 366 | 75 | 95 | 102 | 14510 |
| 07-29 | 855 | 112 | 401 | 83 | 95 | 102 | 15537 |
| 07-30 | 909 | 117 | 442 | 91 | **190** | 102 | 16117 |
| 07-31 | 954 | 121 | 488 | 96 | 190 | 106 | 16792 |
| 08-04 | 984 | 126 | 529 | 102 | **222** | **135** | 17027 |
| 08-05 | 1086 | 137 | 590 | 115 | 222 | 141 | 17738 |
| 08-06 | 1219 | 150 | 631 | 120 | **294** | 141 | 18519 |
| 08-07 | 1230 | 151 | 635 | 121 | 294 | 141 | 18668 |

Structural bypass-scan tests — tests that walk the whole codebase looking for call sites that skip a
chokepoint, so every future feature inherits the constraint — and their first commit:

| test | first shipped |
|---|---|
| `RecognizerAgreementTests.cs` | 07-22 (codescenes-analyzers) |
| `InstanceOverrideDropGuardTests.cs` | 07-28 (live-verify-bug-fixes) |
| `ObjectRefDescentScanTests.cs` | 08-05 (spec 35) |
| `AuthoringSurfaceScan.cs` | 08-05 (spec 35) |
| `ValueContainerDescentScanTests.cs` | 08-06 (uniform-value-descent) |
| `ListenerTargetBypassScanTests.cs` | 08-06 (m8) |
| `ModeArgBypassScanTests.cs` | 08-06 (m8) |

Five of the seven land in the last three days of the window. The lessons ledger is the same shape:
77 open entries and 13 archived ones, and **every** entry carries a `last-seen-date` of 07-30 or
later. The ledger did not exist before 07-30.

The compounding is real but its timing is wrong for the cost curve. Between the first measured
feature (07-16) and the inflection (07-29) the Core roughly doubled — 8174 → 15537 production lines,
426 → 855 Core facts, 172 → 401 EditMode tests — while `verify.sh` and `CLAUDE.md` did not change at
all, and per-agent cost was **flat** across that whole stretch (next section). After 07-29 the
codebase grew only a further 1.2x while cost per agent doubled. Codebase growth is therefore
measurable but demonstrably insufficient on its own.

## The step, isolated: cost per agent dispatch

`agents` is the count of agent transcripts. Output tokens per dispatch, per implementation run:

| date | feature | agents | outTok/agent | cacheRead/agent |
|---|---|---|---|---|
| 07-16 | unqualified-type-names | 43 | 8.2k | 0.99M |
| 07-16 | headless-builder-validation | 56 | 11.5k | 1.66M |
| 07-16 | m-auto-live-sync | 62 | 14.7k | 2.36M |
| 07-17 | m5-cross-object-references | 85 | 11.7k | 1.98M |
| 07-17 | spatial-authoring-components | 79 | 13.6k | 2.26M |
| 07-17 | project-subasset-refs | 40 | 10.5k | 1.22M |
| 07-17 | m6-prefab-instances | 101 | 9.8k | 1.54M |
| 07-21 | fitsize-surfacesnap | 100 | 10.1k | 1.73M |
| 07-21 | m10-prefab-overrides | 120 | 12.5k | 2.00M |
| 07-22 | codescenes-analyzers | 69 | 11.5k | 1.47M |
| 07-22 | typed-prefab-facades | 107 | 10.3k | 1.51M |
| 07-22 | m-nested-props | 168 | 10.4k | 2.16M |
| 07-23 | typed-asset-catalogs | 84 | 12.3k | 2.46M |
| 07-28 | multi-scene-builders | 29 | 10.4k | 2.04M |
| 07-28 | live-verify-bug-fixes | 46 | 11.2k | 2.04M |
| — | **boundary** | | | |
| 07-29 | m-ui-recttransform | 221 | 19.6k | 4.08M |
| 07-30 | serialization-fidelity | 82 | 29.5k | 7.33M |
| 08-04 | boundary-defects (r1) | 25 | 18.6k | 5.14M |
| 08-04 | boundary-defects (r2) | 53 | 22.0k | 4.60M |
| 08-04 | boundary-defects (r3) | 50 | 15.6k | 3.13M |
| 08-05 | reference-writes (r1) | 42 | 18.7k | 5.73M |
| 08-05 | reference-writes (r2) | 54 | 21.0k | 5.01M |
| 08-05 | excluded-field-one-way-report | 32 | 16.7k | 3.60M |
| 08-06 | uniform-value-descent | 76 | 14.9k | 2.40M |
| 08-06 | m8 (r1) | 27 | 29.7k | 5.89M |
| 08-07 | m8 (r2) | 39 | 20.1k | 4.16M |
| 08-07 | m8 (r3) | 24 | 38.0k | 10.64M |

Thirteen days of flat (8.2k → 11.2k, no trend), then a discontinuity. Mean output per dispatch is
11.4k before and 22.2k after; mean cache-read per dispatch is 1.85M before and 5.2M after.

Broken out by agent role, with `msgs` = assistant turns per dispatch:

| agent type | era A (pre 07-29) out/msgs | era B (07-29 on) out/msgs | out ratio |
|---|---|---|---|
| tdd-research | 18.0k / 35.3 | 38.8k / 74.2 | 2.2x |
| tdd-scope-validator | 10.2k / 42.4 | 32.6k / 85.6 | 3.2x |
| tdd-validator | 5.3k / 25.4 | 13.9k / 45.9 | 2.6x |
| tdd-decomposition-validator | 14.1k / 21.3 | 28.9k / 38.3 | 2.0x |
| tdd-test-writer | 15.4k / 50.9 | 19.8k / 70.2 | 1.3x |
| tdd-code-writer | 10.9k / 45.9 | 14.7k / 61.8 | 1.3x |

Every role got heavier, turns roughly doubled everywhere, and the read-only investigative roles
(research, scope-validator, validator) got heavier fastest. The writing roles moved least.

**The one thing that flips exactly at that boundary is the model.** Every workflow run through
07-29 00:47 records `claude-opus-4-8` as the orchestrator/primary model; every run from 07-29 11:27
records `claude-opus-5`. The Sonnet sub-model is unchanged across the boundary. Nothing else in the
repo changed on 07-29 except `specs/13`'s test-methodology section, committed at 11:17 — and that
commit is itself already signed `Co-Authored-By: Claude Opus 5`, so it post-dates the switch.

## Halted plan-validation rounds: the second, later cost

Runs of ≤4 agents are intake + decomposition-validator rounds that halted before any code was
written. They produce zero commits.

| era | halted-round output tokens | implementation output tokens | wasted |
|---|---|---|---|
| A (07-16 → 07-28, 18 features) | 810k | 14.0M | 5.5% |
| B (07-29 → 08-07, 7 features) | 1920k | 15.4M | 11.1% |

Concentrated almost entirely in the last two: `uniform-value-descent` 12 halted rounds / 31% wasted,
`m8-unityevents` 18 halted rounds / 28% wasted. Before 07-29 no feature exceeded 14%, and eight
features had zero. `cost-log.md` records the mechanism by hand: rounds 1-7 of spec 36 re-ran intake
each time, which discards the hand-fixed `tasks.md` and regenerates a differently-flawed plan, so the
finding count never fell. Switching to `tasksReady:true` dropped it to one high per round.

## Is cost superlinear in spec size?

No — cost is essentially **uncorrelated** with spec size. Pearson r against total output tokens:

| predictor | era A (n=15) | era B (n=6) | pooled (n=21) |
|---|---|---|---|
| spec requirement count | +0.25 | +0.64 | −0.32 |
| spec bytes | −0.01 | +0.71 | −0.17 |
| tasks planned | +0.56 | +0.67 | +0.18 |
| production lines changed | +0.65 | +0.82 | +0.47 |
| **agent dispatches** | **+0.96** | **+0.95** | **+0.87** |

The pooled correlation with spec size is *negative* because the expensive late features have the
smallest specs: spec 33 is 14KB with 4 deliverables and cost 2.5M output tokens; spec 28 is 58KB with
36 deliverables and cost 1.1M. Agents dispatched is a near-perfect predictor (r≈0.95 in both eras),
and agents-per-task shows no trend with plan size within an era (era A ranges 4.4 to 16.1 agents/task
with no relationship to the 5-to-23 task range). So the pipeline is linear in plan size, and there is
**no measured threshold beyond which splitting a spec pays**. Splitting specs is not the lever.

## Efficiency per unit of output

Output tokens per production line changed, in time order (era A then era B):

333, 277, 479, 572, 990, 739, 1022, 375, ~670, 293, 549, 666, 647, 301, 930, 930, 1118
→ **1372, 882, 2602, 1465, 1410, 4926, ≥2514**

Era A: median 647, mean 638, no trend across 13 days (the first three features average 363; the last
three average 993, but the intervening 12 oscillate between 277 and 1022 with no monotone drift).
Era B: median 1465, mean 2167. The ratio of medians is 2.3x; of means, 3.4x.

Artifact bytes per production line tells the same story with the coarser proxy: 0.11-0.41 KB/line
through 07-28 (excluding `nested-value-types`, which changed almost no production code), then 0.52,
0.27, 0.96, 0.61, 0.61, 1.83.

## INFLECTION POINT

**A step change, not a gradual climb, at `m-ui-recttransform`, whose implementation run started
07-29 11:56.** The boundary is tight: the last `claude-opus-4-8` run ended 07-29 00:47 and the first
`claude-opus-5` run began 07-29 11:27, so the switch happened inside an eleven-hour window that
contains no other repo change except `specs/13`'s test-methodology section at 11:17 — itself already
authored by Opus 5.

Three independent series break at exactly that run and nowhere else:
- output tokens per agent dispatch: 8.2-14.7k for thirteen days, then 14.9-38.0k;
- assistant turns per dispatch: roughly 2x for every one of the six agent roles;
- cache-read per dispatch: 0.99-2.46M, then 2.40-10.64M.

There is no candidate gradient. Within era A the codebase nearly doubled with per-agent cost flat;
within era B the codebase grew 1.2x with per-agent cost already doubled at the very first run.

A **second, smaller inflection** follows a week later, in the halted-plan dimension only: from
`uniform-value-descent` (08-05) onward, 28-31% of output tokens go to plan-validation rounds that
produce no code, against 0-14% for every earlier feature. That one is a harness-loop defect, and
`cost-log.md` already names its mechanism (intake relaunch discarding the hand-fixed `tasks.md`).

## VERDICT — candidate causes ranked by strength of evidence

1. **Model/agent-behavior change on 07-29 (strong, near-conclusive on timing; causality inferred).**
   Every measured intensity series steps at the same run, the model identifier in the transcripts
   flips at that run, and the effect is present in every agent role including roles whose prompts
   and inputs did not change. Effect size: ~1.95x output per dispatch, ~2.8x context per dispatch.
   Combined with a roughly 1.85x rise in dispatches per task (7.4 → 13.7), it accounts for a ~3.6x
   rise in tokens per task, which brackets the 2.3-3.4x observed rise in tokens per production line.
   What I could NOT establish: whether the agent prompts (`~/.claude/skills/tdd-pipeline/*`) changed
   in the same window. That repository is new and carries no history, so the model switch and any
   simultaneous prompt change are not separable from the data available. The claim I can defend is
   "the harness's per-agent behavior changed discontinuously on 07-29", not "Opus 5 specifically
   caused it".

2. **Plan-validation halt loops (strong, but late and narrow).** 18 halted rounds on m8, 12 on
   spec 36, ~1.9M output tokens across era B for zero commits. Measured directly, mechanism already
   documented. It explains the last two features' misery but not the 07-29 step, since it barely
   registers before 08-05.

3. **Process accretion (moderate, and it post-dates the step).** `verify.sh` 95 → 190 → 222 → 294
   lines, `CLAUDE.md` 102 → 141, a lessons ledger from zero to 77 open entries, five of the seven
   structural bypass-scan tests, and the "add decomposition constraint" spec sections all land on
   07-30 or later. Every one of them is *downstream* of the inflection. They plausibly sustain and
   deepen the higher cost level, and the scope-validator's 3.2x is the role most exposed to them, but
   nothing in the timeline lets accretion explain the step itself.

4. **Codebase friction (weak — actively refuted as a sufficient cause).** The Core doubled in size
   during era A with per-agent cost flat. Test suites grew 2.4x (Core facts) and 2.3x (EditMode)
   over the same stretch, again with flat cost. It contributes to the level, not the break.

5. **Feature/spec size (refuted).** Cost is uncorrelated with spec requirement count (r = +0.25 in
   era A) and negatively correlated pooled (−0.32), because the most expensive features have the
   smallest specs. Agents-per-task shows no relationship to plan size.

Not established, and worth saying plainly: I could not measure wall-clock cost of the `verify.sh`
Layer-2 Unity suite as it grew from 30 to 635 EditMode tests. `durationMs` is absent from these
journals and the gate logs carry no timestamps. Gate runtime is a real and plausibly large
contributor to elapsed time per feature that this data cannot see at all.

## RECOMMENDATIONS

1. **Kill the intake-relaunch loop in plan validation.** (Effort: hours. Sequence: first.)
   Measured: 30 halted rounds across the last two features, ~1.9M output tokens, zero commits;
   `cost-log.md` records that switching to `tasksReady:true` cut findings to one per round. Make
   `tasksReady:true` the default on any re-invocation that follows a halt, so a hand-fixed `tasks.md`
   is never discarded. This is the single largest recoverable waste and it is already diagnosed.

2. **Cap dispatches per task and force escalation instead of iteration.** (Effort: hours. Second.)
   Measured: agents/task rose from 7.4 (era A mean) to 13.7 (era B), and agent count is the near-
   perfect predictor of cost (r = 0.95). Cost per feature is `dispatches × intensity`; intensity is
   not under your control, dispatch count is. A hard per-task ceiling that routes to a human decision
   rather than another research→test→code→validate cycle converts the worst tail (22.2 agents/task on
   `boundary-defects`) into a five-minute answer.

3. **Shrink what the read-only roles read.** (Effort: days. Third.)
   Measured: `tdd-research` 18.0k → 38.8k output and 1.68M → 6.88M cache-read per dispatch;
   `tdd-scope-validator` 10.2k → 32.6k. These roles grew fastest and they only search and report.
   Give them a pre-built index or a scoped file list from the plan's `TOUCHES` instead of letting each
   dispatch re-explore an 18.7k-line Core plus 151 test files from scratch. This targets the largest
   single component of the step.

4. **Stop adding process surface without deleting some.** (Effort: ongoing. Fourth.)
   Measured: `verify.sh` tripled and `CLAUDE.md` grew 38% in the nine days after the inflection, and
   the ledger reached 77 open entries with 13 archived. Impose a budget: a new gate check or CLAUDE.md
   rule ships only when an existing one is retired or folded into it. Accretion is not the cause of
   the step, so this is a level-holding measure, not a fix — do not expect it to restore throughput on
   its own.

5. **Re-measure with a controlled A/B before concluding the model is the cause.** (Effort: one
   feature. Fifth, but before any large investment in 1-3 is scaled up.)
   The 07-29 boundary is a coincidence of two changes I cannot separate: model identifier and any
   unrecorded harness-prompt edit. Run one small, already-specified feature twice — same `tasks.md`,
   same gate, one run on each model — and compare output tokens per dispatch. That single experiment
   either confirms the ranking above or moves cause 1 down and cause 3 up, and it is far cheaper than
   guessing.
