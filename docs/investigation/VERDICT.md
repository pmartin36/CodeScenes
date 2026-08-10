# Why features got 12x more expensive — verdict and plan

Compiled 2026-08-07 from four independent investigations. Each was run without knowledge of the
others' conclusions, so where they converge that is evidence; where they conflict, both positions
are shown below rather than averaged. Full reports: `codebase.md`, `pipeline-economics.md`,
`cost-trend.md`, `agent-forensics.md`, plus the derived dataset `pipeline-agent-tokens.csv`
(1,942 per-agent rows).

---

## 0. The headline number was wrong, including in this conversation

The harness's `tokens` telemetry is **the final context-window size, not tokens spent**. One agent
reported 74,987; its transcript shows **1,911,842 raw tokens over 40 turns**. Everything anyone has
quoted from `cost-log.md` — including my own "~21M over two days" — was built on that field or on
`tokensOutput`, which is output-only.

Re-derived from the agent transcripts (matching 1,942 of 1,945 records, including all 543 whose
telemetry was blank):

| | 88 runs (07-16 → 08-07) | the two days in question |
|---|---|---|
| raw tokens | **5.65B** | **904M** |
| fresh input | 381M | 51.2M |
| output | 28.4M | 5.07M |

**Cache-read is 93.8% of everything moved. Output is 0.56%.** Any plan aimed at making agents write
less prose is aimed at half a percent of the bill.

---

## 1. What changed, and when

**A step change on 07-29, not a gradient.** Output per agent dispatch was flat across thirteen days
and fifteen features (8.2k → 14.7k, no trend), then jumped to 19.6k–38.0k and stayed. Cache-read per
dispatch: 1.85M → 5.2M. Output per production line: median 647 → 1465.

**The mechanism, measured independently:** turns per agent invocation went **32 → 78**, and context
per turn roughly doubled. Cost per task went from 6.8M raw (7 tasks in 19 minutes, 07-16) to 85.9M
raw (2 tasks in 3.6 hours, 08-07) — **12.6x**.

**What coincides with that boundary is the model.** Every run through 07-29 00:47 records
`claude-opus-4-8`; every run from 07-29 11:27 records `claude-opus-5`. Nothing else in the repo
changed that morning.

**RESOLVED 2026-08-07 — the confound is broken, and the model is the dominant cause.**
(`confound-resolution.md`.) The prompts did not change:

- Every workflow-side prompt template for all seven roles first appears 07-16 and last appears
  08-06/07 — all straddle the boundary **unchanged**. The only new template in the corpus is a
  `GATE_SKIP_UNITY=1` validator variant from 08-04.
- The agent `.md` bodies are not stored in transcripts, so they were tested indirectly by turn-1
  prompt-prefix token accounting. There IS a step at the boundary: **+2,130 tokens, near-uniform
  across all seven roles** (2,076–2,145). That uniformity is the tell — seven different agent files
  with different tool sets cannot be hand-edited to the same delta. It is a shared harness-side
  prefix that shipped with the rollout, not an authored change. `CLAUDE.md` (both), skills, plugins
  and the Claude Code version are all excluded by direct check.
- Boundary confirmed from data: `claude-opus-4-8` last invocation 07-29T04:43Z, `claude-opus-5`
  first 07-29T15:17Z, nothing between, and **no old-model run afterwards**.

**The natural control settles it.** `tdd-test-writer` and `tdd-code-writer` run `claude-sonnet-5` and
did so on BOTH sides of the boundary. Across it:

| | turns per invocation | output tokens |
|---|---|---|
| roles whose model changed (opus) | **1.70–2.04x** | **2.0–3.1x** |
| roles whose model did not (sonnet) | 1.27–1.37x | 1.27–1.28x |

Daily means for the opus roles are flat at 29.1–36.0 for nine working days (SD under 2), then 62.4
on 07-30 and 74.7 on 07-31. That is the 32 → 78 series, and it moves for exactly the roles whose
model changed.

**Cost decomposition.** Turns per task 205.7 → 505.9 (**2.46x**) = 1.61x more agent invocations per
task (5.01 → 8.08 — i.e. retries) × 1.53x more turns per invocation. The retry multiplier is not
independent evidence: retries are ordered by `tdd-validator`, itself an opus role.

**Honest bounds, as stated by the investigation.** Crediting the whole sonnet-role rise (~1.3x) to
the shared prefix is an upper bound, since some of it is contamination — research blueprints from the
opus role are 2.1x longer — and some is opus-ordered retries. Of the ~2.0x opus rise, roughly three
quarters of the excess sits with the model swap. Still unsettled: the 07-28/07-30 agent `.md` bodies
are genuinely gone (they changed somewhere in 07-17→08-04, but the token accounting says not on
07-29), and task difficulty is not randomized across the boundary.

**What this changes.** The remediation is now aimed at the right thing. The expensive roles are the
deliberative ones — research, validator, scope-validator, decomposition-validator — and they are
also where the process-versus-correctness waste was measured. Two levers that did not exist before
this result: **bound what a deliberative role may spend** (Phase 1), and **choose the model per
role** rather than per pipeline, which the sonnet control shows is a real and measured dial.

**What it is NOT** — three hypotheses the data kills:

- **Codebase growth.** During the flat era the Core *doubled* (8,174 → 15,537 lines; 172 → 401
  EditMode tests) with per-dispatch cost unchanged. After 07-29 it grew 1.2x more while cost
  doubled.
- **Process accretion.** `verify.sh` 95 → 294 lines, the ledger 0 → 77 entries, five of seven scan
  tests — all dated 07-30 or later. It *sustains* the level; it cannot explain the break.
- **Spec size.** Cost vs spec size is r = −0.32 pooled; the expensive late features have the
  *smallest* specs. Agent dispatches predict cost at r = 0.95. **"Split big specs" is unsupported** —
  I was about to recommend it and the data says no.

---

## 2. Where the tokens actually go

| phase | share of raw |
|---|---|
| test-writer | 26.6% |
| code-writer | 25.1% |
| research | 22.0% |
| scope-validator | 10.8% |
| validator | 8.9% |
| intake | 3.2% |
| plan validation | 2.7% |

**Non-code-writing work is 74.5% of raw and 81.9% of fresh input.**

- One task cycle: mean 5.5 agent invocations, **18.6M raw** (median 10.5M, worst 240M). A clean
  four-agent task costs ~10M; one needing rework costs 43.8M.
- **25% of tasks consume 59.3% of all task-cycle tokens.**
- **50 of 88 runs (57%) died in plan validation having produced zero code.** M8 halted 16 times
  without converging. 75% of the 385 findings were medium/low — but the halt fires on
  `planIssues.length` regardless of severity. That is 27% of output on the two days.
- Instruction overhead is 10–21k tokens before an agent reads anything, 17–35k by turn 5, and it
  **grew 40–59% in three weeks**.
- **The gate is a red herring for tokens** (0.04%) though real for wall-clock (3–32%). It fires
  **5.6x per task because four different agents each run it**.

---

## 3. Where the investigations disagree

Shown rather than smoothed. All four were asked the same two questions.

**(a) Is the codebase the cause?** The trend and economics data say no — friction was equally present
when features were cheap. The two codebase audits say it is a real and serious *constant*: nine to
thirteen M8 defects have the stated cause "this file is in no task's `TOUCHES`", and there are
**18 catch-all dispatch sites over `ValueNode`, 16 silent, none caught at compile time** — one of
which (`SerializedFieldBridge.cs:429`) logs a warning and **drops the write**.
**Resolution: both are right.** Friction is a multiplier, not the trigger. It got expensive when each
dispatch started costing 2.4x more turns, because every extra turn is spent rediscovering an edit
surface the code does not expose.

**(b) Should we add an exhaustive visitor and delete the descent scans?** The first round said yes and
I repeated it to you. The third investigation **tested the premise and refuted it**: it reproduced
`CS8509` on a switch covering all cases of a hierarchy closed by a private constructor. C# still
warns. **Spec 36's rejection of exhaustive switching was empirically correct.** The remaining
options are a committed kind index plus fail-loud defaults, not a compiler guarantee.

**(c) Is green-gate reviewer routing waste?** Forensics found 73% of green-gate routes were process
findings, and one case where an agent's own report file was routed back five times for a missing
`STATUS:` line. But it kept the counter-example: one green-gate route found **real data loss** —
removing a chained component deleted a child GameObject from the user's source.
**Resolution: the ratio is the problem, not the practice.** Gate the routing by severity; keep the
review.

---

## 4. What is actually broken, ranked

1. **Agents take 2.4x more turns per dispatch since 07-29**, cause unresolved (model vs prompt).
2. **Plan validation halts on any finding, at any severity** — 57% of all runs, zero code produced.
3. **Rework concentrates**: a quarter of tasks burn 59% of task tokens, and the trigger is measurable
   (see 4) rather than random.
4. **Task sizing predicts thrashing.** Across 342 tasks: rework is 11–26% at 1–6 declared `TOUCHES`,
   and **64–75% at 7+**; mean iterations roughly double at the same boundary. By deliverable length:
   14% rework under 50 words, 38% at 101–200, **100% at 400+**. `m4-asset-references` sits inside
   both caps and has zero `history.md` files in the entire feature.
5. **A no-change loop with no stop.** `m-nested-props/b7-t2` ran **9 iterations having written code
   once**; iterations 2–9 each report "nothing to re-author". 87% of a 176KB history is no-ops, over
   2h05m, re-running the full Unity gate each time. The research agent diagnosed the cause at
   iteration 5 and had no way to halt.
6. **Context is re-injected without bound.** `history.md` reached 167KB, `tasks.md` 98KB, and both
   are re-read every cycle by every agent.
7. **The edit surface is not derivable from the code** — the constant multiplier from §3(a).
8. **A live bug**: the component-key rule `ordinalByType` is written six times and has already
   drifted. `ReconcilerInstances.Nested.cs:180` yields `.../BoxCollider#1` where canonical is `#0`.

---

## 5. The plan

Sequenced by certainty × cheapness. Everything in Phase 1 is independent of the unresolved model
question, and two items are outright bugs.

### Phase 1 — stop the bleeding (about a day, all harness-side)

| # | Change | Effort | Expected saving |
|---|---|---|---|
| P1 | **Add a bounded plan-repair loop (cap 2 passes).** THE REAL FIX for the 57%. There is no repair path today: the validator runs once and the run returns. **61% of HIGH findings (43/70) state a fix that is a `tasks.md` edit** — add an edge, add a TOUCHES entry, name the owner. The halt discards a repair the validator has already written out. | 4h | Directly addresses the 50 halted runs; most were repairable in place. |
| P2 | **Replace the severity gate with a CLASS filter.** Halt on: deterministic harness findings, unowned mandatory artifact, un-hoisted shared interface/invariant, measurably false claim in `tasks.md`, CLAUDE.md hard-fail consequences. On a 48-finding sample this halts 31 instead of 48 and waves through nothing consequential. Severity does not track class (TOUCHES-overlap appears 7 high / 9 med / 3 low). | 4h | Cuts halts ~35% without the risk in the note below. |
| P2b | **Attach ledgered findings to the owning task's DEFECTS/ASSUMPTIONS block, not to a ledger.** Research is required to attack assumptions adversarially; ledgers here were MEASURED to be read by nobody — `plan-review.md` MED-3 had to be re-recorded verbatim by a later agent noting "plan-review.md is not read by the task agents", and `docs/open-defects.md` is referenced by nothing while holding nine unassigned defects. | 2h | Stops findings being recorded into a void. |
| P3 | **Add a no-change guard.** If an iteration writes no files, stop and escalate instead of re-running. | 1h | Kills the 9-iteration / 2h05m loop class outright. |
| P4 | **Cut `MAX_LOOPS` 3 → 2.** | 15m | Bounds the tail; 25% of tasks burn 59% of tokens. |
| P5 | **Don't re-run four agents for a one-line fix.** Route straight to the code-writer when a finding names one file and needs no test change. | 2h | Observed: full research→test→code→validate cycles spent on a single doc comment. |


### CORRECTION — two Phase 1 items were withdrawn after audit (`severity-audit.md`)

**"Halt only on HIGH findings" was a NO-OP and its premise was false.** The harness does not halt on
any finding; it halts when the decomposition validator returns `valid:false`, and that agent's prompt
*already* says `valid=false if any high-severity gap` (`pipeline.workflow.js:401,404`). Across 81
plan-validation runs: all 50 that halted carried at least one HIGH, and the 31 that proceeded carried
147 findings of which **zero** were high. The change would have altered **0 of 50 halts**. My "75% of
findings are med/low" was a fact about the finding population, not about the halt trigger.

**"Severity-gate the validator's routing" was UNSAFE.** Severity is unreliable in the dangerous
direction: on a 48-finding sample, **15 of 32 MED/LOW (47%) would have caused a real failure** —
non-compiling emitted source, silent identity loss on reload, gate-red file-size breaches, a Plan op
with no executor arm. There are **24 cross-severity near-duplicate pairs**: the same defect rated LOW
in one run and HIGH in another.

**Gate colour is worthless as a routing signal.** Of 21 validator verdicts that routed rework with
`GATE_PASSED: yes`, only **3 were cosmetic**. The other 16 include two data-loss defects (auto-sync
silently deleting a component, and a whole child GameObject, from builder source), two uncaught-
exception parser crashes on ordinary keystrokes, and false published API copy shipping to `api.json`.
The population is green by construction, so greenness carries no information. This supersedes the
"73% of green-gate routes are process findings" figure in §3(c).

**`isNit` exists (`pipeline.workflow.js:690-696`) and is not fit for this.** It is only called on
`SCOPE_OUT.findings`; `VALIDATOR_OUT` has no severity field or findings array, so on a per-task
verdict it returns `false` for every input. Its keyword half misses **40% of HIGH findings** (28/70).
It has produced zero `flaggedNits` across 88 journals — it has never fired.

**Implementation warning.** The deterministic checks at `:350` (unknown dep), `:358` (cycle) and
`:394` (TOUCHES overlap) push plain strings with **no severity prefix**. A naive
`planIssues.filter(i => /high/i.test(i))` silently deletes the three checks with a perfect hit rate.
Any filter must apply to the validator's issues only.

### Phase 2 — cap the inputs (half a day, `task-deconstruct` + protocol)

| # | Change | Effort | Expected saving |
|---|---|---|---|
| P6 | **Cap `TOUCHES` at 6 and deliverables at ~100 words.** Split anything larger at plan time. | 2h | The sharpest measured effect available: 11–26% rework becomes 64–75% above the line. |
| P7 | **Split `tasks.md` into per-task fragments**; an agent reads its own task, not all of them. | 3h | `tasks.md` reached 98KB, re-read by every agent every cycle. |
| P8 | **Bound `history.md`** — keep the most recent attempt plus verdicts, not full archives. | 2h | 167KB worst case, re-injected per cycle. |
| P9 | **Delete accreted prompt sections.** Instruction overhead grew 40–59% in three weeks; the Provenance rule appears in 12% of research files, so it is not earning its cost. | 3h | 10–21k tokens per agent before any work. |

### Phase 3 — the codebase constant (2–3 days)

| # | Change | Effort | Expected saving |
|---|---|---|---|
| P10 | **Delete the 1000-line file-size budget.** It cost a full pipeline task today (the `Reconciler` split) and leaves `SourcePatchApplier.cs` at 941 with no escalation path. | 1h | Unblocks the next M8 bucket immediately. |
| P11 | **Count-free allowlists** on the two oldest guards; drop 37 hand-maintained exact-count entries. | 4h | Removes per-feature guard maintenance; test-only change. |
| P12 | **Commit a generated value-kind index** and feed it to `task-deconstruct` so `TOUCHES` is derived, not guessed. Add fail-loud defaults at the 16 silent dispatch sites. | 1.5d | Attacks the 9–13 "file in no TOUCHES" defect class, the constant multiplier. |
| P13 | **Give `ordinalByType` one owner and fix the `#1`/`#0` drift.** | 1d | A real shipped bug. |

### Phase 0 — APPLIED 2026-08-07

**The default model was set back to 4.8.** That is the single highest-leverage change in this
document and it is done. Per §1, the roles that switched to Opus 5 carried a 1.70–2.04x turn
increase and 2.0–3.1x output increase while the two Sonnet roles held at ~1.3x through the same
boundary; reverting the default targets that at its measured source.

**The next feature run is now a natural experiment, and it is worth instrumenting deliberately.**
Everything after 07-29 confounds "model" with "harness with no cost brakes". A run on 4.8 with the
harness unchanged separates them for the first time:

- If cost per task returns to roughly the 07-16 baseline (~6.8M raw, 7 tasks in 19 minutes), the
  model was carrying nearly all of it and Phases 1–3 are optimisation, not rescue.
- If it lands part-way, the gap is the harness amplifiers — no plan-repair path, `MAX_LOOPS` 3, no
  no-change guard, unbounded `history.md` re-injection — and THAT gap is the honest budget for the
  remaining work.

Measure it from the agent transcripts the same way `pipeline-agent-tokens.csv` was built (the harness
`tokens` field is a context-window size, not spend — see §0). One feature is enough.

**Two cautions.** Nothing measured today is a 4.8 baseline: this entire investigation, and both M8
implementation runs, ran on Opus 5. And the reversion is blunt where P0 was targeted — it returns the
deliberative roles to 4.8 along with everything else. If the trial shows quality regressions in the
review roles specifically, the per-role dial is still available: two of those routes on a green gate
caught real data loss (a component, and a whole child GameObject, silently deleted from builder
source), so some of that deliberation was earning its cost.

### Phase 0 — original analysis (superseded by the above, kept for the reasoning)

| # | Change | Effort | Expected saving |
|---|---|---|---|
| P0 | **Set the model per agent ROLE, not per pipeline.** The deliberative roles (research, validator, scope-validator, decomposition-validator) carry the 1.70–2.04x turn increase and 2.0–3.1x output increase; the two roles already on `claude-sonnet-5` held at 1.27–1.37x through the same boundary. Trial a cheaper model on the review roles first — they read and judge rather than author, and they are where the measured process-vs-correctness waste sits. | 30m to change, one feature to evaluate | Directly targets the ~2.46x turns-per-task rise at its measured source. |

**D1 (was: A/B to separate model from prompt) — DONE, not needed.** Resolved by reading the
transcripts instead of spending a feature. See §1.

---

## 6. What we could not establish

- **Model vs prompt change on 07-29.** Inseparable from existing data. D1 exists for this.
- **The "~1 hour per feature" baseline.** Batched `specs/completed/` moves destroyed per-milestone
  durations. The 07-16 datapoint (7 tasks in 19 minutes) is the closest anchor.
- **Whether M8's cost is harness regression or genuine difficulty.** M8 is the largest spec in the
  tree; the economics investigation states plainly it could not separate the two.
- **Whether any guard ever blocked a correct change that was then abandoned.** `.agent_handoffs/` is
  gitignored, so abandoned work leaves no trace.
- **Unity gate wall-clock as EditMode tests went 30 → 635.** No timestamps in the gate logs.

---

## 7. Method note

Two earlier conclusions in this investigation were wrong and were corrected by later work: that
`cost-log.md`'s numbers represented spend (they are output-only, and the harness `tokens` field is a
context-window size), and that an exhaustive `switch` was available in C# for this hierarchy (it is
not; `CS8509` was reproduced). Both errors came from asserting a measurement rather than taking one.
Treat any figure here without a stated source the same way.
