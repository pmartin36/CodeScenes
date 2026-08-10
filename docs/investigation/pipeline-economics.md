# Pipeline economics: where the tokens actually go

Investigation of the tdd-pipeline's token spend across 88 recorded workflow runs, 2026-07-16 to
2026-08-07.

## Dataset and its biases

Primary source: `~/.claude/projects/-home-paul-Source-CodeScenesUnity/<session>/workflows/wf_*.json`.
Each file carries a `workflowProgress` array with one `workflow_agent` record per agent invocation,
holding `label`, `phaseTitle`, `attempt`, `tokens`, `toolCalls`, `durationMs`, plus prompt and result
previews. This is a richer source than `journal.jsonl` (which carries only `started`/`result` pairs
and no telemetry) and than `cost-log.md`.

- 88 run files, 1945 agent invocation records.
- 1402 records carry `tokens`; **543 do not**. All 543 are `state: done` with `toolCalls` and
  `durationMs` also absent — they are real agent invocations from older runs whose telemetry the
  harness did not persist. 17 runs are affected (all of 07-16 through 07-24, plus three on 08-05 and
  `wf_e330eb7a-6e1` on 08-07 where 25 of 26 records are blank).
- **Every total below is therefore a floor, not a ceiling.** Where a run has partial telemetry it is
  labelled, and the aggregate tables are computed over full-telemetry runs only.
- `tokens` is the harness's per-agent total. It is not decomposed into input / output / cache-read,
  so it cannot be converted to dollars here, and cache-read tokens (cheap) are pooled with fresh
  input (expensive). Ratios between phases are sound; an absolute dollar figure is not derivable
  from this dataset.

Sum of recorded tokens across all 88 runs: **104,253,041**. Per-agent tokens sum exactly to each
run's `totalTokens`, so the two views are consistent.

### Tying out the "~21M in two days"

| date | recorded tokens |
|---|---|
| 2026-08-05 | 21,385,275 |
| 2026-08-06 | 9,092,670 |
| 2026-08-07 | 9,148,354 |

08-06 plus 08-07 is 18.24M recorded, and 08-07 contains `wf_e330eb7a-6e1` whose telemetry is 96%
missing (25 of 26 agent records blank). The true two-day figure is above 18.24M and ~21M is
consistent. Those two days produced `uniform-value-descent` complete (13 tasks, `wf_077d3c59-ae4`)
and `m8-unityevents` partial (5 tasks across `wf_834b442b-655` and `wf_0a90acf8-14d`, still
unfinished — the working tree still holds its uncommitted files).

## Q1. Per-phase token breakdown

> The tables in this section use the harness's own `tokens` field. The next section shows that field
> is the **final context size**, not tokens spent, and restates Q1 on real transcript usage. Read the
> proportions here; take the absolute numbers from "Q1, restated on real tokens".

### Aggregate over the 70 full-telemetry runs (54,329,432 tokens)

| phase / label | invocations | tokens | share | avg per invocation |
|---|---:|---:|---:|---:|
| research | 101 | 10,651,653 | 19.6% | 105,461 |
| test-writer | 108 | 9,906,322 | 18.2% | 91,725 |
| code-writer | 109 | 8,394,006 | 15.5% | 77,009 |
| validator | 116 | 6,536,718 | 12.0% | 56,351 |
| intake | 69 | 6,182,258 | 11.4% | 89,597 |
| validate-plan | 64 | 5,352,039 | 9.9% | 83,625 |
| scope-validator | 60 | 5,218,083 | 9.6% | 86,968 |
| commit | 40 | 902,102 | 1.7% | 22,552 |
| lessons | 15 | 851,640 | 1.6% | 56,776 |
| full-gate | 12 | 334,611 | 0.6% | 27,884 |

**Only 15.5% of spend is the agent that writes production code.** Research alone costs more than
code-writing (1.27x). Research + test + validate + scope + intake + validate-plan — everything that
is *not* writing the product — is 80.7%.

### `wf_077d3c59-ae4` — the working baseline (13 tasks, COMPLETE, 5,881,854 tokens, 6.66h, 76 agents)

| label | n | tokens | share | avg | tool calls | wall hrs |
|---|---:|---:|---:|---:|---:|---:|
| research | 15 | 1,812,802 | 30.8% | 120,853 | 542 | 3.18 |
| test | 15 | 1,137,481 | 19.3% | 75,832 | 362 | 0.86 |
| validate | 16 | 964,890 | 16.4% | 60,305 | 361 | 0.86 |
| scope | 7 | 871,034 | 14.8% | 124,433 | 303 | 0.91 |
| code | 12 | 676,425 | 11.5% | 56,368 | 240 | 0.44 |
| full-gate | 4 | 112,900 | 1.9% | 28,225 | 12 | 0.14 |
| commit | 4 | 103,367 | 1.8% | 25,841 | 16 | 0.08 |
| validate-plan | 1 | 91,433 | 1.6% | 91,433 | 19 | 0.11 |
| lessons | 1 | 67,675 | 1.2% | 67,675 | 14 | 0.03 |
| intake | 1 | 43,847 | 0.7% | 43,847 | 2 | 0.05 |

Research is 2.7x the cost of code on the run that worked. Note research also dominates wall clock:
3.18 of 6.66 hours (48%).

### `wf_834b442b-655` — m8-unityevents, 2 tasks, 3,541,461 tokens, 3.59h, 39 agents

| label | n | tokens | share | avg |
|---|---:|---:|---:|---:|
| research | 7 | 790,777 | 22.3% | 112,968 |
| validate | 9 | 771,429 | 21.8% | 85,714 |
| code | 9 | 613,106 | 17.3% | 68,122 |
| test | 7 | 537,463 | 15.2% | 76,780 |
| scope | 3 | 455,246 | 12.9% | 151,748 |
| intake | 1 | 163,146 | 4.6% | 163,146 |
| validate-plan | 1 | 100,816 | 2.8% | 100,816 |
| lessons | 1 | 80,286 | 2.3% | 80,286 |
| full-gate | 1 | 29,192 | 0.8% | 29,192 |

7 research, 7 test, 9 code, 9 validate invocations for **2 tasks**.

### `wf_0a90acf8-14d` — m8-unityevents continued, 3 tasks, 3,477,901 tokens, 3.79h, 24 agents

| label | n | tokens | share | avg |
|---|---:|---:|---:|---:|
| code | 7 | 1,273,802 | 36.6% | 181,971 |
| validate | 7 | 650,218 | 18.7% | 92,888 |
| test | 4 | 627,485 | 18.0% | 156,871 |
| research | 3 | 578,406 | 16.6% | 192,802 |
| intake | 1 | 159,216 | 4.6% | 159,216 |
| validate-plan | 1 | 121,781 | 3.5% | 121,781 |
| lessons | 1 | 66,993 | 1.9% | 66,993 |

Per-invocation cost here is 2-3x the baseline run: a single code-writer averages 182k tokens versus
56k in `wf_077d3c59-ae4`, and a single research averages 193k versus 121k.

### `wf_e330eb7a-6e1`

26 agent records, 25 with no telemetry. Only the `lessons` agent (60,994) is measurable. **Cannot be
analysed for phase breakdown** — reported here rather than guessed at.

## Correction: what the harness's `tokens` field actually measures

Cross-checking one agent (`research:b1-t1` of `wf_077d3c59-ae4`, harness `tokens` = 74,987) against
its own transcript `agent-a00021c3c480dd458.jsonl`:

| measure | value |
|---|---:|
| harness `tokens` | 74,987 |
| final-turn context (input + cache_creation + cache_read on the last API call) | 74,985 |
| turns (API calls) | 40 |
| cache_read_input_tokens summed over the session | 1,727,032 |
| cache_creation_input_tokens | 172,953 |
| output_tokens | 11,777 |
| **raw tokens actually sent/billed** | **1,911,842** |

**The harness's `tokens` is the final context window size, not tokens spent.** It undercounts real
consumption by 25x on that agent. The "~21M over two days" figure is this metric (08-06 plus 08-07
sums to 18.24M by it, and the missing-telemetry run puts it near 21M).

The transcripts do carry real usage, and **1942 of 1945 agent records have a matching transcript**,
including all 543 whose telemetry was blank. Everything from here uses the transcripts. This also
removes the coverage caveat above: the real-token dataset is essentially complete.

### Real consumption, all 88 runs, 1942 agents

| | tokens |
|---|---:|
| raw tokens sent (input + cache-create + cache-read + output) | 5,651,552,458 |
| fresh input (input + cache-create; billed at full/write rate) | 381,000,437 |
| cache-read input (billed at ~10%) | 5,242,152,088 |
| output | 28,399,933 |

Cache-read is **92.8%** of all tokens moved. Output is **0.5%**. This is the shape of the whole
problem in one line: the pipeline is not generating much; it is re-sending large contexts, turn
after turn, across hundreds of long agent sessions.

The two days in question (08-06 + 08-07, 234 agent invocations): **903,719,336 raw**, 51,212,577
fresh input, 5,068,389 output.

### Q1, restated on real tokens — all 88 runs

| phase | invocations | raw tokens | % raw | fresh input | % fresh | output | % out | turns | turns/agent | avg final ctx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| test-writer | 351 | 1,501,119,419 | 26.6% | 73,935,830 | 19.6% | 5,650,420 | 20.0% | 20,035 | 57 | 60,402 |
| code-writer | 355 | 1,416,604,280 | 25.1% | 68,280,395 | 18.1% | 4,401,048 | 15.6% | 19,101 | 54 | 52,659 |
| research | 301 | 1,236,834,323 | 22.0% | 88,045,861 | 23.3% | 8,085,434 | 28.7% | 15,581 | 52 | 73,576 |
| scope-validator | 159 | 605,889,962 | 10.8% | 43,518,588 | 11.5% | 2,741,398 | 9.7% | 9,145 | 58 | 69,687 |
| validator | 362 | 503,070,461 | 8.9% | 58,098,198 | 15.4% | 3,166,940 | 11.2% | 12,374 | 34 | 38,218 |
| intake | 86 | 181,519,081 | 3.2% | 20,195,418 | 5.3% | 2,025,479 | 7.2% | 2,492 | 29 | 71,886 |
| validate-plan | 81 | 151,836,863 | 2.7% | 16,986,911 | 4.5% | 1,902,153 | 6.7% | 2,596 | 32 | 66,659 |
| commit | 98 | 20,415,783 | 0.4% | 4,022,233 | 1.1% | 135,012 | 0.5% | 997 | 10 | 14,905 |
| lessons | 28 | 14,087,805 | 0.3% | 3,814,646 | 1.0% | 97,156 | 0.3% | 326 | 12 | 46,833 |
| full-gate | 16 | 2,068,794 | 0.0% | 651,304 | 0.2% | 10,096 | 0.0% | 79 | 5 | 26,040 |

Every agent in the pipeline runs an average of **34 to 58 turns**. At an average context of 50-75k
that is where the 5.65 billion goes: `turns x context`, not "one big prompt".

Non-code-writing work (research + test + scope + validate + intake + validate-plan + lessons) is
**74.5% of raw tokens and 81.9% of fresh input**.

## Q2. What one task cycle costs

251 task-cycles are measurable across the 88 runs (a task-cycle = every `research|test|code|validate`
invocation carrying the same `bN-tM` id inside one run).

| measure | value |
|---|---:|
| agent invocations per task, mean | 5.5 |
| agent invocations per task, median | 4 |
| agent invocations per task, worst | 36 (`wf_483effae-ccb` b2-t2 counted 26 in the 4 core labels alone) |
| raw tokens per task, mean | 18,556,288 |
| raw tokens per task, median | 10,524,160 |
| raw tokens per task, p90 | 36,265,623 |
| raw tokens per task, worst | 240,539,576 |
| fresh input per task, mean | 1,148,845 |
| fresh input per task, median | 826,250 |

A **clean** task (one research, one test, one code, one validate — 188 of 251, 75%) costs a mean
10,081,389 raw tokens / median 7,132,715. A task that needed more than four invocations (63 of 251,
25%) costs a mean 43,846,464 — **4.3x**.

**Those 25% of tasks consume 59.3% of all task-cycle tokens.**

The floor is therefore about 7M raw tokens (~600-800k fresh input) for the cheapest possible unit of
work: four agents, each reading the protocol, the plan, the spec and the tree from cold.

### Worst task cycles

| run | task | invocations | raw tokens | turns | shape |
|---|---|---:|---:|---:|---|
| wf_483effae-ccb | b2-t2 | 26 | 240,539,576 | 2,256 | research 5, test 7, code 7, validate 7 |
| wf_0a90acf8-14d | b1-t3 | 9 | 171,274,900 | 1,146 | research 1, test 2, code 3, validate 3 |
| wf_834b442b-655 | b1-t2 | 20 | 111,779,753 | 1,378 | research 4, test 4, code 6, validate 6 |
| wf_baf8d11f-543 | b1-t2 | 17 | 102,032,525 | 1,249 | research 5, test 4, code 4, validate 4 |
| wf_210a5d51-5b5 | b3-t1 | 15 | 99,783,404 | 1,083 | research 3, test 4, code 4, validate 4 |

## Q3. Rework multiplier, and what the rework is FOR

### The multipliers (all 88 runs, 251 task-cycles)

| phase | tasks that ran it | total calls | multiplier | % of tasks needing >1 |
|---|---:|---:|---:|---:|
| research | 228 | 301 | 1.32 | 17% |
| test-writer | 250 | 351 | 1.40 | 23% |
| code-writer | 247 | 355 | 1.44 | 24% |
| validator | 250 | 362 | 1.45 | 25% |
| scope-validator | 124 scopes | 158 | 1.27 | 18% |

Validate-cycles histogram across 250 tasks: `{1: 188, 2: 37, 3: 14, 4: 5, 5: 2, 6: 2, 7: 1, 9: 1}`.

`cost-log.md` agrees on the shape where it overlaps (14 recorded features): validate 1.73, code 1.67,
test 1.45, research 1.33, scope 1.46. Its multipliers run higher than the transcript-derived ones
because **it only records runs that reached the Lessons phase** — halted and killed runs are absent,
and those are disproportionately the cheap ones. Its `tokensOutput` (6,379,673 across 14 records) is
output only; measured output across all 88 runs is 28,399,933, so `cost-log.md` sees roughly 22% of
even the output-token picture.

### CORRECTNESS vs PROCESS: every readable validator route

44 distinct non-GREEN validator verdicts are recoverable from `history.md` + `validator.md` on disk
(the handoff files are overwritten per iteration, so this is every route that survived; verdicts from
overwritten iterations without a `history.md` entry are lost).

| category | count | share |
|---|---:|---:|
| CORRECTNESS — gate red, or a real product defect found on a green gate | 29 | 66% |
| PROCESS — green gate, no behavior change required | 12 | 27% |
| ENVIRONMENT — gate red on a host defect no agent could fix (`RLIMIT_NICE`/`setpriority`) | 2 | 5% |
| unreadable | 1 | 2% |

That headline is the wrong cut, though. **Split by gate state:**

| gate at the time of the route | count | CORRECTNESS | PROCESS |
|---|---:|---:|---:|
| RED (gate actually failed) | 27 | 25 | 0 |
| GREEN (gate passed; the route is reviewer opinion) | 15 | 4 | 11 |

- When the gate is red, the loop is doing real work: 25 of 27 red-gate routes are genuine failing
  tests or absent implementations. Only 2 are environment noise.
- **When the gate is green, 11 of 15 routes (73%) are process findings** — and each one costs a full
  research → test → code → validate cycle plus a full `./verify.sh`.

Green-gate PROCESS routes, itemised:

| task | what the full cycle was spent on |
|---|---|
| `m-ui-recttransform/b3-t3` | a stale comment block. Validator's own words: "Two comment restatements, present tense, no deleted-symbol names, **zero executable change**." |
| `m8-unityevents/b1-t2` (x3, consecutive) | the same rule stated in three places. Iteration 1 fixed `FlatShapeRecognizer`'s two messages; iteration 2 fixed `DiagnosticDescriptors.SB1003`'s Title; iteration 3 fixed `FlatShapeRecognizer.cs:278`. "Fix is two text edits (`:278` and the `:258-259` comment) with no test churn." Three full cycles, each behind the multi-minute Unity gate, for message strings. |
| `uniform-value-descent/b3-t4` | "deliverable clause 6 unmet — handoff payload, **no production change**" |
| `reference-writes-and-cache-invalidation/b2-t1` | "comment states a fact the code no longer contains" plus defect-register bookkeeping |
| `serialization-fidelity/b3-t2` | "BLOCKING simplification/compliance finding", all deliverables met, gate green |
| `excluded-field-one-way-report/b1-t1` | one missing assertion; "No production code change is needed; code-writer should not be routed" |
| `m-builtin-resources/b2-t2` | two named DELIVERABLE test cases never written; code complete |
| `serialization-fidelity/b2-t1` | deliverable-mandated EditMode coverage missing; code proven correct by an independent probe |
| `m-ui-recttransform/b3-t5` | mandated EditMode file never created |
| `m5-cross-object-references/b5-t2` | test-writer SKIPPED on a `BEHAVIORAL:yes` / `TEST_RECOMMENDATION: write` task |
| `_superseded-m8.../b1-t1` | an over-asserting test forced a production workaround; gate green throughout |

Four of those eleven (`m-builtin b2-t2`, `serialization b2-t1`, `m-ui b3-t5`, `m5 b5-t2`) are the
same failure: **the test-writer skipped, and the validator caught it one full cycle later.** The
test-writer's prompt says "Your default is to SKIP", and the validator's job includes checking the
deliverable is pinned. Those two instructions are in direct conflict, and the pipeline pays a whole
research→test→code→validate cycle every time the conflict resolves the wrong way.

## Q4. Context re-derivation

### What every agent must ingest before it does any work

| artifact | bytes | ~tokens | who reads it |
|---|---:|---:|---|
| global `~/.claude/CLAUDE.md` | 8,632 | ~2,200 | every agent |
| project `CLAUDE.md` | 9,695 | ~2,400 | every agent |
| agent definition (`tdd-*.md`) | 5,922-10,062 | ~1,500-2,500 | its own agent |
| `HANDOFF-PROTOCOL.md` | 12,117 | ~3,000 | every pipeline agent, read exactly once (measured 0.99-1.10 reads/invocation) |
| `.agent_handoffs/<feature>/tasks.md` | mean 33,930, max 102,462 | ~8,500 (max 25,600) | research 2.29x, validator 2.12x, validate-plan 2.19x per invocation |
| `research.md` | mean 11,531 | ~2,900 | test 1.04x, code 1.03x, validate 1.07x, research 2.32x |
| `history.md` | mean 51,646, **max 241,342** | ~12,900 (max 60,300) | read on every re-entry (research 0.62x, test 0.85x, code 0.92x per invocation) |
| `tasks.md` for the two features in question | 53,032 (uniform-value-descent), 61,699 (m8-unityevents) | ~13,300 / ~15,400 | |

Measured context at fixed turn checkpoints (median across all runs):

| agent | turn 1 | turn 5 | turn 10 | final |
|---|---:|---:|---:|---:|
| research | 9,720 | 26,456 | 37,538 | 93,154 |
| test-writer | 14,220 | 24,696 | 36,717 | 80,510 |
| code-writer | 13,332 | 20,423 | 36,542 | 63,594 |
| validator | 10,557 | 16,875 | 29,645 | 48,532 |
| scope-validator | 10,024 | 24,419 | 31,699 | 79,250 |
| intake | 21,144 | 35,275 | 48,153 | 105,593 |
| validate-plan | 13,112 | 32,347 | 46,834 | 77,386 |

Turn 1 is the system prompt plus tool schemas plus the agent definition plus both CLAUDE.md files:
**10-21k tokens before the agent has read anything about the task.** By turn 5 (protocol + plan +
blueprint loaded) it is 17-35k. That is the fixed re-derivation toll, and it is paid **1,837 times**
across the dataset.

Rough multiplication: 1,837 pipeline invocations x ~25k tokens of harness/protocol/plan boilerplate
= **~46M tokens of fresh input spent re-establishing the same context**, against 377.5M fresh input
total (12%) and 28.2M output. Because that boilerplate is then re-sent on every subsequent turn as
cache-read, its contribution to the 5.65B raw figure is far larger: 1,837 invocations x ~25k x a
median 34-58 turns is on the order of **1.5-2.5B raw tokens, 27-44% of everything moved**, entirely
re-reading text that did not change.

### Do downstream agents USE the blueprint, or restate it?

Measured by the set of **product** files (excluding `.agent_handoffs/` and `~/.claude/`) each agent
opened with `Read`, per task:

| run | outcome | files research read | files code-writer read | overlap with research |
|---|---|---:|---:|---:|
| `wf_077d3c59-ae4` (13 tasks, COMPLETE) | worked | 5.6 | 1.9 | 62% |
| `wf_834b442b-655` (m8, 2 tasks) | stalled | 10.0 | 12.5 | 35% |
| `wf_0a90acf8-14d` (m8, 3 tasks) | stalled | 10.0 | 23.0 | 42% |

On the run that worked, the code-writer opened **1.9** product files: it trusted the blueprint and
went straight to the edit. On the two runs that burned 7M tokens and produced 20% of a feature, the
code-writer opened **12.5 and 23** product files — more than research itself — and only ~40% of them
were files research had already read. **The blueprint stops being load-bearing exactly when the task
gets hard, and the code-writer re-does the research from scratch inside a Sonnet agent with no
blueprint discipline.**

The harness knows this is a risk and has responded by adding text, not structure. `tdd-research.md`
now requires a `## Provenance` section listing every inherited claim and whether it was re-verified.
Measured: **40 of 325 `research.md` files (12%) contain a Provenance section.** The instruction is
88% unfollowed.

The same pattern shows in the read counts: `tdd-research.md` tells research to read every prior
`research.md` for the feature ("a map, not an authority") and then to re-verify every load-bearing
fact by reading the code itself. Measured, research reads 2.32 `research.md` files **and** opens
5.6-10 product files per invocation. It pays for the map and then pays for the territory anyway.

## Q5. The plan-validation loop

**50 of the 88 runs (57%) ended at plan validation with zero code written.** Five more died in
intake. Only 32 runs ever reached a code-writer.

Halts by feature, in chronological order of `planIssues` count:

| feature | halts | issue count per attempt |
|---|---:|---|
| m8-unityevents | 16 | 12, 10, 9, 8, 24, 8, 9, 7, 10, 10, 8, 13, 7, 7, 9, 11 |
| uniform-value-descent | 10 | 6, 10, 7, 7, 4, 7, 6, 8, 9, 6 |
| typed-prefab-facades | 4 | 6, 3, 3, 4 |
| m10-prefab-overrides | 4 | 6, 4, 3, 5 |
| (feature unrecoverable) | 4 | 7, 8, 12, 13 |
| 8 others | 1-2 each | |

**The loop does not converge.** m8-unityevents went through sixteen recorded rounds and the finding
count on the last round (11) is higher than on the first (12 → ... → 11), with a 24-finding spike in
the middle. uniform-value-descent ran ten rounds, low of 4, ending at 6. Hand-patching a plan
produces a fresh crop of findings every time; it does not walk the count down.

### Are the findings real?

Both, and the split is measurable. Across 385 findings in 50 halts:

| severity | count | share |
|---|---:|---:|
| high | 70 | 18% |
| medium | 145 | 38% |
| low | 145 | 38% |
| deterministic in-script (TOUCHES overlap, DAG) | 25 | 6% |

**75% of every finding that halted a run was medium or low severity.**

The findings themselves are specific and mostly true. Sampled from `wf_84027a03-1e3` (13 findings,
1 high):

- LOW-3: *"the `## File-size budget` header records `ComponentReconciler.cs` at 819 lines; it is 822
  today (b1-t2 landed after the measurement). Same drift class as iteration 2's LOW-5."*
- LOW-4: *"b4-t3(g)'s sentence is garbled by the inserted `.OnEvent` clause"*
- MED-3: *"the `SourcePatchApplier.cs` headroom clause is filed under the wrong task"*

These are real observations about a **planning document**. They are also unbounded: any 60KB plan
re-read by a fresh Opus agent yields another dozen. `wf_aa821a80-f2b` produced 24 findings, 11 of
which were the deterministic TOUCHES-overlap check firing eleven times on the *same* two directory
entries (`SceneBuilder.Core.Tests/`, `unity-gate/Assets/GateTests/`) declared by six different tasks.

The structural fault is in `pipeline.workflow.js:404` and `:415`:

```js
if (decomp && !decomp.valid) for (const i of decomp.issues) planIssues.push(`[plan] ${i.severity}: ${i.detail}`)
...
if (planIssues.length) { /* halt the entire run */ }
```

The agent's contract is `valid=false if any high-severity gap`, but once `valid` is false **every**
issue is pushed, and the halt fires on `planIssues.length` — so a plan with one high and twelve lows
halts on all thirteen, and the human is expected to resolve all thirteen before the run can start.
Every recorded halt had at least one high, so `valid` was never once true in this dataset.

### What it cost

| | halted runs (50) |
|---|---:|
| raw tokens | 201,333,395 (3.6% of all) |
| fresh input | 22,862,346 (6.0%) |
| **output** | **2,555,502 (9.0%)** |
| mean raw per halted run | 4,026,667 |

On the two days in question, 26 of the 50 halts fired: **90,479,130 raw tokens (10.0% of the two-day
total), 11,622,973 fresh input (23%), 1,359,240 output (27%) — spent on runs that wrote no code.**

That is the honest scale. In raw terms the plan loop is not the biggest line; in *output* and in
human wall-clock (26 launch-and-halt cycles across two days, roughly 10-15 minutes each) it is the
most visible failure, and it is what stopped the two days from producing anything.

## Q6. Gate cost — is it real?

`verify.sh` invocations across all runs: **2,443** (in 70 runs; the other 18 never reached a gate).
Of these, 1,332 are launches rather than log greps.

| | value |
|---|---:|
| foreground gate runs taking >=30s | 400 |
| their duration | median 0.7 min, mean 0.8 min, **max 1.3 min** |
| backgrounded launches (per `CLAUDE.md`'s own recommended pattern) | 708 |
| `full-gate` agent label, share of all raw tokens | 0.04% |

Gate-blocked wall clock as a share of run duration:

| run | duration | gate launches | gate-blocked bash | share |
|---|---:|---:|---:|---:|
| wf_077d3c59-ae4 (13 tasks, complete) | 6.66h | 73 | 0.40h | 6% |
| wf_834b442b-655 | 3.59h | 48 | 0.22h | 6% |
| wf_0a90acf8-14d | 3.79h | 19 | 0.11h | 3% |
| wf_52e0ba7b-a03 | 7.94h | 80 | 1.48h | 19% |
| wf_483effae-ccb | 2.76h | 66 | 0.87h | 32% |

**Verdict on the gate: mostly a red herring for tokens, real for wall clock on Unity-touching work.**
It is 0.04% of token spend. It is 3-6% of wall clock on Core-heavy features and up to 32% on the
adapter-heavy ones. The baseline run fired the gate **73 times for 13 tasks — 5.6 gate runs per
task**, because every agent in the loop (code-writer 146 times, validator 123, commit 65, test 23)
runs it independently rather than the orchestrator running it once per iteration and handing the
output down. That duplication is the fixable part; the gate itself is not the problem.

## The trend: cost per task over time

Raw tokens per completed task, by run (runs with >=2 measurable tasks):

| period | tasks | raw tokens / task | fresh input / task |
|---|---:|---:|---:|
| 2026-07-16 to 07-23 | 148 | 12,528,028 | 1,118,440 |
| 2026-07-24 to 07-31 | 47 | 31,982,393 | 1,823,081 |
| 2026-08-01 to 08-07 | 53 | 38,015,667 | 1,830,420 |

The oldest run in the dataset, `wf_7e09016d-c27` (2026-07-16, `unqualified-type-names`), completed 7
tasks in **19 minutes** at **6,840,115 raw / 733,210 fresh per task**. `wf_834b442b-655` (2026-08-07,
m8) completed 2 tasks in 3.6 hours at **85,892,742 raw / 4,318,695 fresh per task** — a **12.6x**
regression in raw tokens per task and **5.9x** in fresh input. The "~1 hour per feature" memory is
consistent with the July 16 record.

Two mechanisms are separately measurable.

**(a) The agents' fixed instruction overhead grew.** Median turn-1 context (system + tools + agent
definition + both CLAUDE.md files, before any file read):

| date | research | test-writer | code-writer | validator | scope |
|---|---:|---:|---:|---:|---:|
| 2026-07-16 | 9,337 | 13,831 | 12,885 | 9,306 | 9,165 |
| 2026-07-23 | 9,649 | 14,216 | 13,264 | 10,554 | 10,004 |
| 2026-07-30 | 12,305 | 16,854 | 15,902 | 13,141 | 12,657 |
| 2026-08-07 | 14,838 | 19,612 | 18,018 | 14,840 | 14,483 |

+59% for research, +42% test-writer, +40% code-writer, in three weeks. That is the harness accreting
prose — every observed failure answered with another paragraph in a `tdd-*.md`.

**(b) The agents' sessions got much longer.** Median turns and median raw tokens per agent:

| date | agents | median turns | median final ctx | median raw / agent |
|---|---:|---:|---:|---:|
| 2026-07-17 | 191 | 35 | 50,153 | 1,411,738 |
| 2026-07-23 | 243 | 32 | 37,811 | 1,383,078 |
| 2026-07-30 | 122 | 45 | 68,403 | 2,394,901 |
| 2026-07-31 | 71 | 78 | - | 5,925,758 |
| 2026-08-05 | 196 | 59 | 85,618 | 3,481,606 |
| 2026-08-07 | 79 | 62 | 70,409 | 3,780,569 |

Turns roughly doubled and context roughly doubled, so raw tokens per agent went up ~2.7x. Combined
with more cycles per task, that is the whole 12x.

## VERDICT

**Yes, the pipeline is structurally flawed.** Not in its control flow — the orchestration in
`pipeline.workflow.js` is deterministic, resumable, and its hard-gate rule (GREEN requires a real
exit 0) is sound and demonstrably prevented false greens. The flaw is economic, and it has three
parts:

1. **The unit of work is far too small for the fixed cost of a unit of work.** A task-cycle spawns
   four fresh agents with no shared memory. Each one re-establishes 10-21k tokens of instructions
   plus a 12KB protocol, a 53-62KB plan, and a blueprint, then runs 34-58 turns re-sending all of it.
   The cheapest possible task costs ~7M raw tokens before anything goes wrong.
2. **Every failure has been answered by adding text to a prompt, and text is not free.** Instruction
   overhead grew 40-59% in three weeks, and the additions are largely unfollowed (the Provenance
   requirement: 40 of 325 files, 12%). A rule that costs 5k tokens x 45 turns x 1,837 invocations and
   is obeyed 12% of the time is a net loss.
3. **Two review layers are unbounded by severity.** Plan validation halts a whole run on findings
   that are 75% medium/low, and never converges (16 rounds on m8). The task validator routes a full
   four-agent cycle for green-gate process findings 73% of the time it speaks on a green gate.

The gate is not the problem (0.04% of tokens). The model is not obviously the problem. The
*instructions and the review layers* are the problem.

## QUANTIFIED COST TABLE — where the two days went

Both metrics shown: the harness's `tokens` field (what "21M" refers to) and real transcript tokens.

| bucket | agents | harness "tokens" | share | raw tokens | share | fresh input | confidence |
|---|---:|---:|---:|---:|---:|---:|---|
| **HALTED plan-validation runs (zero code)** | 61 | 4,759,381 | 26.1% | 90,479,130 | 10.0% | 11,622,973 | HIGH — direct transcript sum over 26 runs whose `result.planIssues` is non-empty |
| research | 31 | 3,309,178 | 18.1% | 189,839,710 | 21.0% | 10,108,789 | HIGH |
| code-writer | 34 | 2,563,333 | 14.1% | 252,842,062 | 28.0% | 7,430,862 | HIGH |
| validator | 41 | 2,522,138 | 13.8% | 107,719,205 | 11.9% | 7,855,820 | HIGH |
| test-writer | 33 | 2,378,193 | 13.0% | 166,119,236 | 18.4% | 7,359,583 | HIGH |
| scope-validator | 10 | 1,326,280 | 7.3% | 68,895,546 | 7.6% | 2,960,720 | HIGH |
| intake | 5 | 409,877 | 2.2% | 9,849,394 | 1.1% | 1,195,213 | HIGH |
| validate-plan (in runs that proceeded) | 5 | 405,669 | 2.2% | 13,040,536 | 1.4% | 1,389,119 | HIGH |
| lessons | 5 | 321,516 | 1.8% | 3,727,572 | 0.4% | 965,416 | HIGH |
| full-gate | 5 | 142,092 | 0.8% | 724,804 | 0.1% | 216,917 | HIGH |
| commit | 4 | 103,367 | 0.6% | 482,141 | 0.1% | 107,165 | HIGH |
| **TOTAL (08-06 + 08-07)** | **234** | **18,241,024** | | **903,719,336** | | **51,212,577** | |

Cross-cutting attributions (these overlap the rows above, they do not add to them):

| cut | value | confidence |
|---|---:|---|
| tokens that are cache-read replays of unchanged context | 847,438,370 of 903,719,336 (93.8%) | HIGH — direct `cache_read_input_tokens` |
| output tokens (the only thing that produced artifacts) | 5,068,389 (0.56%) | HIGH |
| spent on runs that produced **zero code** | 90,479,130 raw / 1,359,240 output (27% of output) | HIGH |
| spent re-establishing fixed harness/protocol/plan context | ~46M fresh input across the full dataset; on the order of 1.5-2.5B raw (27-44%) | MEDIUM — turn-checkpoint measurement x invocation count, not a per-token attribution |
| spent on tasks that needed more than one pass (whole dataset) | 59.3% of all task-cycle tokens for 25% of tasks | HIGH |
| accreted instruction growth since 07-16 (~5.5k tokens/agent) | ~5.5k x median 45 turns x 1,837 invocations = ~450M raw across the dataset (8%) | MEDIUM — assumes the growth is pure addition, which the turn-1 series supports but does not prove |

## RANKED CAUSES

1. **Long agent sessions replaying large contexts.** 93.8% of tokens are cache-read. Median turns per
   agent doubled (32-37 in mid-July → 59-78 in August) and median context doubled, producing a 2.7x
   per-agent cost rise. Evidence: per-day turn/context table above.
2. **Rework concentration.** 25% of task-cycles (63 of 251) consume 59.3% of task-cycle tokens, at a
   mean 43.8M raw vs 10.1M for a clean task. Validate-cycle histogram: `{1:188, 2:37, 3:14, 4:5, 5:2,
   6:2, 7:1, 9:1}`. The worst single task, `wf_483effae-ccb` b2-t2, ran 26 core-label invocations and
   240,539,576 raw tokens.
3. **The plan-validation halt loop.** 50 of 88 runs (57%) died there, m8-unityevents 16 times without
   converging. 75% of the 385 findings were medium/low but the halt fires on `planIssues.length`
   regardless. Cost on the two days: 10% of raw, 27% of output, plus ~26 human relaunch cycles.
4. **Process findings routed as if they were defects.** Of 15 validator routes issued on a *green*
   gate, 11 (73%) required no behavior change: stale comments, missing assertions, uncreated files,
   handoff payload. `m8-unityevents/b1-t2` burned three consecutive full cycles — each behind a full
   `./verify.sh` — on message-string wording. `m-ui-recttransform/b3-t3`: "zero executable change."
5. **Instruction accretion.** Turn-1 context up 40-59% in three weeks, and the additions are largely
   unfollowed (Provenance section present in 12% of `research.md`). Compounding: every added token is
   re-sent on every one of the session's 34-58 turns.
6. **The blueprint stops being load-bearing when the task is hard.** On the run that worked, the
   code-writer read 1.9 product files; on the two m8 runs it read 12.5 and 23 — more than research —
   with only 35-42% overlap. Research (22% of raw, 28.7% of output, more than the code-writer) is
   being paid for twice on exactly the tasks that cost the most.
7. **The gate is a red herring for tokens** (0.04%) but is duplicated 5.6x per task: code-writer ran
   `verify.sh` 146 times, validator 123, commit 65, test-writer 23. Real wall-clock cost 3-32% of a
   run.

## RECOMMENDATIONS

Sequenced. Each names the file, the effort, the saving tied to a measured number, and the risk.

### 1. Stop halting the run on non-high plan findings; carry them instead

**Change:** `~/.claude/skills/tdd-pipeline/pipeline.workflow.js:404` and `:415`. Push only
`high`-severity agent issues (and the deterministic DAG/cycle checks) into `planIssues`. Medium and
low findings go into a per-task carried note (`openRoute`/`carryNote` already exist and already do
exactly this job) so the owning task's research agent sees them at the moment it can act. Add a
`MAX_PLAN_ROUNDS = 2` counter that force-proceeds on the third attempt with everything carried.

**Effort:** small — ~20 lines, and `tdd-pipeline-edit`'s stub harness covers the control flow.

**Saving:** 75% of the 385 halt-causing findings were medium/low. On 08-06 + 08-07 the halted runs
were 90,479,130 raw / 11,622,973 fresh / 1,359,240 output (27% of the two days' output) and ~26
human relaunch cycles. Recovering even the medium/low-only halts is most of that.

**Risk:** a medium coverage gap reaches a task and gets caught one cycle later instead of before the
run. Bounded: it becomes a normal ROUTE, not a re-plan. This is the single highest-value change and
must go first because nothing else matters if 57% of runs never start.

### 2. Severity-gate the validator's routes the way scope findings are already gated

**Change:** `pipeline.workflow.js` — `isNit()` / `NIT_SEVERITY` / `BEHAVIORAL_DETAIL` (lines 690-696)
already exist for scope findings but the **validator's** `ROUTE_*` has no equivalent. Add: when
`v.gatePassed === true` and the diagnosis matches no `BEHAVIORAL_DETAIL` pattern, record it as a
`flaggedNit` and let the task go GREEN rather than re-entering the writer chain. Extend
`VALIDATOR_OUT` with a required `severity` field so the validator has to commit to a claim.

**Effort:** small — the predicate is written; it needs applying at line 653-663.

**Saving:** 11 of 15 green-gate routes (73%) were process-only. Each cost a full four-agent cycle;
mean cost of a >4-invocation task is 43,846,464 raw against 10,081,389 clean. `m8-unityevents/b1-t2`
alone spent three such cycles.

**Risk:** the 4 green-gate routes that *were* real defects (the stale-goid cache invalidation, the
two guard-2 shapes) would be demoted to nits. Mitigated by `BEHAVIORAL_DETAIL`, which matches all
four of their diagnoses ("data loss", "deletes", "silently", "stale"). Verify against those four
before shipping.

### 3. DELETE the accreted prompt sections, don't add more

**Change:** `~/.claude/agents/tdd-research.md`, `tdd-test-writer.md`, `tdd-code-writer.md`. Remove:
the `## Provenance` requirement (obeyed in 40 of 325 files, 12%); the "read any `research.md` already
written for THIS feature" instruction (research still opens 5.6-10 product files anyway, so the map
is not replacing the territory); the duplicated "read `history.md` BOUNDED" paragraphs in all three
agents (keep one, in the protocol); the two verbatim copies of the 200-character forbidden-comment
regex (keep one, in `HANDOFF-PROTOCOL.md`).

**Effort:** small — deletion only, but it needs the discipline to *not* replace it with new text.

**Saving:** turn-1 context grew 5,501 tokens for research, 5,781 for test-writer, 5,133 for
code-writer since 07-16. At the median 45 turns per agent and 1,837 invocations, ~5.5k of removed
prompt is on the order of **450M raw tokens (8% of the dataset)**. Restoring 07-16 prompt sizes
without other changes would not restore 07-16 costs, but it is the cheapest 8% available.

**Risk:** the failures those paragraphs were written for recur. Two of them measurably recur *anyway*
(re-proposing refuted approaches; half-applied fixes), which is the argument for deleting them: they
are not working. Route the two that matter into a mechanism instead — see #5.

### 4. Cut `MAX_LOOPS` from 3 to 2 and add a per-task token ceiling

**Change:** `pipeline.workflow.js:17` (`MAX_LOOPS`) and a new per-task budget check in
`implementTask`'s loop alongside the existing `BUDGET_RESERVE` check at line 592 — escalate a task
that has consumed more than N raw tokens rather than letting it run to the loop cap.

**Effort:** small.

**Saving:** the validate-cycle histogram is `{1:188, 2:37, 3:14, 4:5, 5:2, 6:2, 7:1, 9:1}`. Tasks
reaching a third validate almost never converge cheaply: the top five task-cycles by cost
(240M/171M/112M/102M/100M raw) all ran 3+ code cycles. Capping at 2 stops ~25 tasks roughly one
cycle earlier; at the measured 43.8M mean for rework tasks that is on the order of 200-400M raw
across a comparable period.

**Risk:** more `ESCALATED` tasks needing human judgement. That is the correct trade — the escalation
messages already carry the validator's diagnosis (`verdictNote`), and a human reading one diagnosis
is cheaper than a third automated cycle.

### 5. Stop re-reading `tasks.md`; split the plan into per-task fragments

**Change:** `~/.claude/skills/task-deconstruct/SKILL.md` and `HANDOFF-PROTOCOL.md` — emit
`.agent_handoffs/<feature>/tasks.md` as a short index (ids, titles, deps, TOUCHES) plus
`.agent_handoffs/<feature>/<task-id>/task.md` per task. Point every agent at its own fragment and the
index, never the whole plan.

**Effort:** medium — touches the deconstructor, the protocol, and every agent's "Inputs" list.

**Saving:** `tasks.md` is 53,032 bytes (uniform-value-descent) and 61,699 (m8), ~13-15k tokens, and
is read 2.29x per research invocation, 2.12x per validator, 2.19x per validate-plan. Second-order and
larger: it is also the 60KB document the decomposition validator generates 7.7 findings against per
round. A plan that no single agent reads whole is a plan that cannot accumulate 13 low-severity
drift findings about its own prose.

**Risk:** cross-task seams become less visible to research, which currently discovers them by reading
the whole plan. Keep the bucket `seams` array in the index — it is short and it is the part that
matters.

### 6. Run the gate once per loop iteration, in the orchestrator

**Change:** `pipeline.workflow.js` — add a gate step between `runCode` and `runValidate` that runs
`taskGate.command` itself and writes `gate-output.log`; change the validator prompt (line 506) from
"Run the EXACT gate command" to "read `gate-output.log`, which was produced by this iteration's run
of `<cmd>`; its exit code was N". Same for the code-writer and test-writer prompts.

**Effort:** medium — the prompts are load-bearing for the hard-gate rule, so the exit code must be
threaded as data the validator cannot fake.

**Saving:** 2,443 `verify.sh` invocations across 70 runs, 5.6 per task in the baseline run
(code-writer 146, validator 123, commit 65, test-writer 23). Wall-clock 3-32% of a run. Token saving
is small (full-gate is 0.04%); this one buys wall clock and removes the `GATE PASS`-line
misreporting class that `CLAUDE.md` documents having fooled three agents.

**Risk:** the hard gate is the one thing in this pipeline that provably works. Getting this wrong
re-opens false greens. Do it last, and keep the validator's `gatePassed` assertion checked against
the orchestrator's own recorded exit code.

## WHAT I COULD NOT ESTABLISH

- **Dollar cost.** The transcripts separate `input_tokens`, `cache_creation_input_tokens`,
  `cache_read_input_tokens` and `output_tokens`, and `model` is recorded per agent, so a price could
  be computed — but no pricing table is in the repo and I did not want to introduce guessed rates
  into a document of measurements. The inputs are all present in
  `docs/investigation/pipeline-agent-tokens.csv` if you want it.
- **How much of the rework was avoidable.** I can prove 25% of tasks took 59.3% of task-cycle tokens
  and that 73% of green-gate routes were process findings, but I cannot say what fraction of the
  red-gate cycles a better blueprint would have prevented. That needs a counterfactual, not a log.
- **Whether m8-unityevents is intrinsically harder.** It is the largest spec in the tree
  (`specs/09-m8-unityevents.md`, 27,900 bytes vs 11-14KB for the others). Its 16 plan halts and 86M
  raw tokens per task are therefore a confound: some of that is a hard feature, some is harness
  regression. The 07-16 → 08-07 per-task trend (12.6x) is measured across *different* features and
  cannot fully separate the two.
- **The "one feature per hour" baseline.** The oldest run in the dataset (2026-07-16,
  `wf_7e09016d-c27`, 7 tasks in 19 minutes) is consistent with it, but there is no telemetry from
  before 2026-07-16, so the claim rests on a single data point.
- **`wf_e330eb7a-6e1`'s phase breakdown from harness telemetry** (25 of 26 records blank). Its real
  token usage *is* recoverable from transcripts and is included in all transcript-based totals.
- **Why per-agent turn counts doubled in late July.** The correlation with instruction growth is
  strong but I did not isolate a cause; `~/.claude`'s git history was excluded from this
  investigation by instruction, and it is the only record of when each paragraph landed.
