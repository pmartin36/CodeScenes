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

**This is a correlation with a real confound, and it cannot currently be resolved.** The harness repo
has no history before this week, so a prompt edit inside that same 11-hour window cannot be ruled
out. Two investigations reached this boundary independently and both stopped at the same wall.
Recommendation D1 exists to break the tie before anyone invests in it.

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
| P1 | **Halt plan validation only on HIGH findings.** Record med/low to the ledger and proceed. | 1h | 57% of runs currently die here; 75% of findings are med/low. |
| P2 | **Severity-gate validator routes.** An `isNit` predicate already exists in the harness — use it. A green gate plus a prose complaint becomes an advisory, not a route. | 2h | 73% of green-gate routes were process findings. |
| P3 | **Add a no-change guard.** If an iteration writes no files, stop and escalate instead of re-running. | 1h | Kills the 9-iteration / 2h05m loop class outright. |
| P4 | **Cut `MAX_LOOPS` 3 → 2.** | 15m | Bounds the tail; 25% of tasks burn 59% of tokens. |
| P5 | **Don't re-run four agents for a one-line fix.** Route straight to the code-writer when a finding names one file and needs no test change. | 2h | Observed: full research→test→code→validate cycles spent on a single doc comment. |

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

### Diagnostic — run alongside, gates nothing

**D1. One-feature A/B to separate model from prompt.** The only way to resolve §1's confound.
Until it runs, do not invest in anything premised on the cause being one or the other.

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
