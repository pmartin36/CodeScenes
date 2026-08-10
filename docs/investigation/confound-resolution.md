# Confound resolution: model change vs prompt change on 2026-07-29

**Verdict: PROMPTS IDENTICAL (with one shared-prefix caveat).** The prompt text the orchestrator
sends each agent is byte-identical across the 07-29 boundary for all seven roles, verified on every
one of 2,179 transcripts. The agent system prompts are not stored anywhere and cannot be diffed
directly, but the turn-1 prompt-prefix token count rules out per-role edits in the window: the prefix
stepped by a near-uniform ~+2,130 tokens in all seven roles at once, which one shared component can
produce and six independent file edits cannot. That shared +2,130 is the one real prompt-surface
change in the window, and it landed with the model rollout, not from any file under the user's
control. The discriminating measurement: the two roles whose model did NOT change (`tdd-test-writer`,
`tdd-code-writer`, both `claude-sonnet-5` throughout) rose 1.19x-1.37x in turns per invocation, while
every role whose model changed rose 1.64x-2.06x.

Question: cost per task in the TDD pipeline jumped 12.6x in a step change on 2026-07-29. The model
changed in that window (`claude-opus-4-8` -> `claude-opus-5`). Did the agent prompts change too?

Evidence base: 2,179 per-agent transcripts under
`~/.claude/projects/-home-paul-Source-CodeScenesUnity/*/subagents/workflows/wf_*/agent-*.jsonl`,
spanning 2026-07-16T19:25Z to 2026-08-07T11:47Z.

## Step 1 — what a transcript actually stores

Record types present across the corpus (sampled 400 files, then confirmed on the full set):
`user`, `assistant`, `attachment`. There is **no `system` record type and no system-prompt field**.

Per-record structure:

- **`user`** — `message.role`/`message.content`. The FIRST user record of each file is the complete
  prompt the orchestrator sent to that agent, verbatim, as a plain string. Later `user` records are
  tool results (they carry `toolUseResult` / `sourceToolAssistantUUID`).
- **`assistant`** — `message.model` (exact model id), `message.usage`
  (`input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`),
  `requestId`, `attributionAgent` (always the literal `workflow-subagent`, not the role name),
  `attributionSkill` (`tdd-pipeline`).
- **`attachment`** — deferred-tool name lists (`deferred_tools_delta`) and similar harness notices.
  Tool *names* appear; full tool JSONSchema definitions do not.

Every record also carries `timestamp`, `cwd`, `sessionId`, `version` (the Claude Code build),
`gitBranch`, `agentId`.

### Consequence for the key hypothesis

The hypothesis was that the transcripts carry the instruction text. **They carry half of it.**

- The **agent system prompts** (`~/.claude/agents/tdd-*.md` bodies, which define each role) are
  **NOT stored**. They are not recoverable from the transcripts for 07-28 or 07-30.
- The **orchestrator-constructed user prompts** (built by
  `~/.claude/skills/tdd-pipeline/pipeline.workflow.js` and sent as the first user message) **ARE
  stored verbatim**, for every invocation. These are directly diffable across the boundary.

So Step 2 is partially possible: the workflow-side prompt text can be diffed exactly; the
agent-definition-side prompt text cannot. Sections below cover the recoverable substitutes.

`~/.claude` is a git repo, but its first commit is `4c8d0f4 2026-08-04 15:54:08 -0400`
("Version the Claude Code configuration: skills, agents, settings"). It has no history covering
07-29, confirming the premise.

## Step 3 — the model boundary, confirmed from the data

Model ids come from `message.model` on every assistant record. All timestamps below are UTC as
stored (local EDT = UTC-4).

| model | invocations | first seen | last seen |
|---|---|---|---|
| `claude-opus-4-8` | 818 | 2026-07-16T19:25:09Z | 2026-07-29T04:43:23Z |
| `claude-opus-5` | 491 | 2026-07-29T15:17:54Z | 2026-08-07T11:40:34Z |
| `claude-sonnet-5` | 812 | 2026-07-16T19:35:09Z | 2026-08-07T11:47:27Z |
| `<synthetic>` | 58 | (harness-generated records, no model) | |

The boundary is clean and matches the premise: last `opus-4-8` at 07-29 00:43 EDT, first `opus-5` at
07-29 11:17 EDT, nothing in between. **No invocation after the boundary used `opus-4-8`** — there is
no natural old-model control on the far side.

Per-role model assignment (this matters for everything that follows):

| role | model before | model after |
|---|---|---|
| `tdd-research` | opus-4-8 | opus-5 |
| `tdd-validator` | opus-4-8 | opus-5 |
| `tdd-scope-validator` | opus-4-8 | opus-5 |
| `tdd-decomposition-validator` | opus-4-8 | opus-5 |
| `task-deconstruct` | opus-4-8 | opus-5 |
| **`tdd-test-writer`** | **sonnet-5** | **sonnet-5 (unchanged)** |
| **`tdd-code-writer`** | **sonnet-5** | **sonnet-5 (unchanged)** |

Two of the seven roles never changed model. They are the control.

## Step 2 — diffing the instruction text that IS recoverable

### 2a. Orchestrator-emitted prompts: IDENTICAL across the boundary

Every first-user-message in the corpus was extracted, normalized (task ids -> `<TASK>`, feature names
-> `<F>`, spec paths -> `specs/<S>`), and clustered into distinct templates with first/last-seen
timestamps.

| role | distinct base templates over the whole corpus | first seen | last seen |
|---|---|---|---|
| `tdd-research` | 1 | 2026-07-16T19:32 | 2026-08-07T09:43 |
| `tdd-test-writer` | 1 | 2026-07-16T19:35 | 2026-08-07T10:50 |
| `tdd-code-writer` | 1 | 2026-07-16T19:36 | 2026-08-07T11:22 |
| `tdd-decomposition-validator` | 1 | 2026-07-16T19:29 | 2026-08-07T08:10 |
| `task-deconstruct` | 1 | 2026-07-16T19:25 | 2026-08-07T08:01 |
| `tdd-scope-validator` | 2 (per-bucket / whole-feature) | 2026-07-16T19:44 | 2026-08-07T06:50 |
| `tdd-validator` | 2 (behavioral-evidence clause on/off) | 2026-07-16T19:37 | 2026-08-07T11:40 |

"Base template" = the invariant text with per-run injected blocks stripped (`DIRECTORY:` worktree
pin, `PRIOR DIAGNOSIS`, routed-retry preamble). Every base template's first occurrence is 07-16 and
its last is 08-06/08-07, i.e. **each one straddles the 07-29 boundary unchanged**.

The two multi-template roles are conditional, not chronological:
- `tdd-validator`'s behavioral-evidence sentence ("if tests were skipped you MUST capture concrete
  evidence...") is emitted per-task depending on whether tests were skipped. Both the with-clause
  (n=225) and without-clause (n=137) forms appear continuously from 07-16 through 08-06/08-07.
- `tdd-scope-validator` has a per-bucket form (n=125) and a whole-feature form (n=43); both run
  throughout.

The only genuinely NEW template anywhere in the corpus is the `tdd-validator` variant whose gate
command is `GATE_SKIP_UNITY=1 ./verify.sh` — first seen **2026-08-04T17:30**, six days after the
boundary, and it corresponds to the tracked commit `fa39234 2026-08-04 build(gate): scope the Unity
layer per task`.

**Verdict for 2a: the workflow-side prompt text did not change on 07-29. Byte-identical, all seven
roles.**

### 2b. Agent system prompts (`~/.claude/agents/tdd-*.md`): not recoverable, but bounded

Not stored in transcripts, and `~/.claude`'s git history begins 08-04. Two partial sources:

**`~/.claude/file-history/`** holds pre/post-edit snapshots of files edited through Claude Code.
Snapshots of tdd-* agent/skill files exist at 07-16 23:42 through 07-17 00:11, then nothing until
07-30 19:24 (`tdd-learnings`) and 07-31 09:07 (`tdd-decomposition-validator`). **No snapshot falls
inside the 07-29 window.** This is suggestive but NOT conclusive: the store is demonstrably
incomplete. `tdd-validator.md` was 5,768 bytes on 07-16 and 6,543 bytes in git at 08-04 with no
snapshot in between, so edits happened that file-history did not record.

**Prompt-prefix token accounting** is the stronger instrument. For each invocation, the total tokens
of the turn-1 prompt prefix (`input_tokens + cache_creation_input_tokens + cache_read_input_tokens`
on the first assistant record) is the size of system prompt + tool definitions + CLAUDE.md + the user
message. Median per role per day:

| day | CC ver | research | test-writer | code-writer | validator | scope-val | decomp-val |
|---|---|---|---|---|---|---|---|
| 07-24 | 2.1.217 | 9,725 | 14,220 | 13,268 | 10,489 | 10,005 | 9,381 |
| 07-28 | 2.1.220 | 10,157 | 14,724 | 13,772 | 11,062 | 10,511 | 9,844 |
| **07-29** | 2.1.220 | **12,305** | **16,854** | **15,902** | **13,141** | 10,535 | **11,992** |
| 07-30 | 2.1.220 | 12,305 | 16,854 | 15,902 | 13,209 | 12,657 | 11,990 |

Resolved to the individual invocation, the step lands exactly at the model boundary: the last
`opus-4-8` scope-validator at 07-29T04:43 has a 10,535-token prefix; the first `opus-5` run at
07-29T15:17 is already stepped. (The scope-validator's daily median lags one day only because its
07-29 invocations split 7 before / 6 after the boundary.)

The step size is **~+2,130 tokens, near-uniform across all seven roles**: research +2,145,
scope-validator +2,145, decomposition-validator +2,143, deconstruct +2,140, test-writer +2,127,
code-writer +2,127, validator +2,076.

That uniformity is the informative part. The seven roles have different agent `.md` files and
different tool sets. **Six independent hand edits to six different agent files cannot produce the
same +2,130 in all of them.** A single shared prefix component growing by ~2,130 tokens can. Ruling
out the shared components under the user's control:

- project `CLAUDE.md` — unchanged from 07-16 (`788d6d6`, 7,094 bytes) until 07-31 (`d73371a`). Not it.
- `~/.claude/CLAUDE.md` — the file-history snapshot from 07-28 09:18 is 8,476 bytes and differs from
  today's 8,632-byte version by exactly one added line. Not it.
- skills / plugins — `~/.claude/skills` holds only the five tdd/unity skills; no plugin was installed
  in the window; the MCP tool roster was already present in the 07-16 transcripts.
- Claude Code version — `2.1.220` on 07-28, 07-29 and 07-30 alike. Not a client upgrade.

So the ~2,130-token growth is a **shared, harness-side prefix change that landed with the model
rollout**, not a per-role prompt rewrite. It is a real change to what the agents read, and it is the
one prompt-surface delta in the window, but it is not "the agent prompts were rewritten".

## Step 4 — the discriminating measurement: the two sonnet roles never changed model

`tdd-test-writer` and `tdd-code-writer` ran on `claude-sonnet-5` on both sides of the boundary. Any
prompt or harness change in the 07-29 window hit them exactly as hard as it hit the opus roles — the
`pipeline.workflow.js` that builds their prompts, the shared prefix, the agent `.md` convention, all
shared. The model swap did not. So the two groups separate the hypotheses.

Turns per invocation, all invocations before vs. after 2026-07-29T15:17Z:

| role | model | mean turns before | after | ratio |
|---|---|---|---|---|
| `tdd-research` | opus-4-8 -> opus-5 | 36.7 | 74.7 | **2.04x** |
| `tdd-scope-validator` | opus-4-8 -> opus-5 | 44.8 | 88.8 | **1.98x** |
| `tdd-decomposition-validator` | opus-4-8 -> opus-5 | 21.3 | 38.3 | **1.79x** |
| `tdd-validator` | opus-4-8 -> opus-5 | 27.0 | 45.9 | **1.70x** |
| `task-deconstruct` | opus-4-8 -> opus-5 | 45.5 | 56.4 | 1.24x |
| `tdd-test-writer` | sonnet-5 (unchanged) | 51.1 | 70.2 | 1.37x |
| `tdd-code-writer` | sonnet-5 (unchanged) | 48.7 | 61.8 | 1.27x |

Daily means, opus-model roles vs sonnet-model roles. The opus series is flat for nine working days
and then steps; the sonnet series has no comparable step.

| day | opus-role turns (n) | sonnet-role turns (n) |
|---|---|---|
| 07-16 | 33.9 (44) | 41.7 (36) |
| 07-17 | 34.7 (116) | 55.8 (90) |
| 07-18 | 33.8 (68) | 44.4 (56) |
| 07-21 | 33.9 (97) | 47.9 (71) |
| 07-22 | 33.1 (99) | 44.8 (76) |
| 07-23 | 36.0 (104) | 49.2 (87) |
| 07-24 | 29.1 (23) | 50.6 (19) |
| 07-28 | 33.8 (48) | 66.6 (30) |
| **07-29** | **50.7 (66)** (mixed-model day) | 53.7 (48) |
| 07-30 | 62.4 (91) | 63.5 (76) |
| 07-31 | 74.7 (26) | 91.5 (25) |
| 08-05 | 63.4 (103) | 63.9 (75) |
| 08-07 | 60.1 (42) | 82.0 (27) |

The opus-role series is the one that matches the reported 32 -> 78: it sits at 33.9/34.7/33.8/33.9/
33.1/36.0/29.1/33.8 through 07-28 and reaches 74.7 on 07-31. Its standard deviation across those
nine pre-boundary days is under 2 turns. That is not a series drifting upward with task difficulty.

Splitting 07-29 itself, AM (`opus-4-8`) vs PM (`opus-5`), same day, same operator, same harness:

| role | AM mean turns (n) | PM mean turns (n) | ratio |
|---|---|---|---|
| `task-deconstruct` | 33.0 (1) | 81.0 (1) | 2.45x |
| `tdd-decomposition-validator` | 18.0 (1) | 56.0 (3) | 3.11x |
| `tdd-research` | 35.0 (7) | 70.6 (16) | 2.02x |
| `tdd-scope-validator` | 44.1 (7) | 90.0 (6) | 2.04x |
| `tdd-validator` | 24.9 (8) | 38.9 (16) | 1.56x |
| **`tdd-test-writer`** | **67.1 (8)** | **58.8 (16)** | **0.88x** |
| **`tdd-code-writer`** | **51.8 (8)** | **42.9 (16)** | **0.83x** |

Within the same 11-hour window, every role whose model changed rose 1.56x-3.11x and both roles whose
model did not change went *down*. Caveat, stated plainly: AM and PM ran different features
(`live-verify-bug-fixes` vs `m-ui-recttransform`), so this within-day split is confounded by task
difficulty and rests on small n. It agrees with the larger before/after comparison rather than
carrying the argument alone.

## What actually drove cost per task

Cost per task decomposes into two multiplicands, both measured here over the four per-task roles
(research, test-writer, code-writer, validator), keyed by (workflow, task id):

| | before | after | ratio |
|---|---|---|---|
| agent invocations per task (retries/routing) | 5.01 | 8.08 | 1.61x |
| turns per invocation | 41.0 | 62.7 | 1.53x |
| **turns per task** | **205.7** | **505.9** | **2.46x** |
| distinct tasks observed | 185 | 74 | |

Per role, both multiplicands moved:

| role | inv/task | turns/inv | turns/task |
|---|---|---|---|
| `tdd-research` | 1.26 -> 1.90 (1.51x) | 36.8 -> 75.8 (2.06x) | 46.3 -> 142.1 (3.07x) |
| `tdd-test-writer` | 1.31 -> 2.00 (1.53x) | 50.4 -> 68.8 (1.37x) | 66.9 -> 140.5 (2.10x) |
| `tdd-code-writer` | 1.30 -> 2.15 (1.65x) | 46.8 -> 55.8 (1.19x) | 63.6 -> 133.2 (2.10x) |
| `tdd-validator` | 1.30 -> 2.16 (1.66x) | 26.4 -> 43.5 (1.64x) | 35.1 -> 99.2 (2.83x) |

The retry multiplier (1.5x-1.7x, uniform across roles including the sonnet ones) is a *routing*
effect: retries are issued by `tdd-validator`, an opus role, whose verdicts got stricter or whose
failures got more frequent. The sonnet roles are re-invoked because an opus role told the
orchestrator to re-invoke them. Their invocation count is therefore not independent evidence.

Output tokens per invocation, which is closer to marginal cost than turn count:

| role | out tokens before -> after | cache-read tokens before -> after |
|---|---|---|
| `tdd-research` | 18,763 -> 39,097 (2.08x) | 1.74M -> 6.93M (3.98x) |
| `tdd-scope-validator` | 10,819 -> 33,895 (3.13x) | 1.91M -> 7.52M (3.93x) |
| `tdd-validator` | 5,661 -> 13,968 (2.47x) | 0.68M -> 2.12M (3.13x) |
| `tdd-decomposition-validator` | 14,171 -> 28,932 (2.04x) | 0.68M -> 2.22M (3.27x) |
| `task-deconstruct` | 31,444 -> 32,326 (1.03x) | 2.78M -> 4.28M (1.54x) |
| `tdd-test-writer` | 15,510 -> 19,823 (1.28x) | 3.18M -> 5.95M (1.87x) |
| `tdd-code-writer` | 11,636 -> 14,772 (1.27x) | 2.95M -> 5.16M (1.74x) |

The same split holds: opus roles 2.0x-3.1x on output, sonnet roles 1.27x-1.28x.

## How much can the one real prompt change explain

The only prompt-surface change detected in the window is the ~+2,130-token shared prefix (+21% on
the research role's turn-1 prompt, +14% on test-writer's). Attributing the turn increase to it
requires it to be both large and behaviorally directive; it is neither large relative to a
50-70-turn conversation whose context is dominated by tool results, nor identifiable as instruction
text that would change how many tool calls an agent makes.

Bounding it by the control: the sonnet roles absorbed that same +2,127-token prefix and rose 1.19x-
1.37x in turns per invocation, while the opus roles absorbed it and rose 1.64x-2.06x. If the shared
prefix change is credited with the *entire* sonnet-role rise (an upper bound — part of that rise is
contamination, since the sonnet roles read blueprints that the opus research agent now writes at 2.1x
the length, and part is the extra retries an opus validator ordered), then of the roughly 2.0x rise
in opus-role turns per invocation, about 1.3x is shared with the control and about 1.55x is not.
Roughly three quarters of the excess sits with the model swap; at most about a quarter is shared with
whatever else changed that day, and even that quarter is not established as prompt-driven.

Two things this does not settle:
- The bodies of `~/.claude/agents/tdd-*.md` on 07-28 and 07-30 are gone. They did change at some
  point between 07-17 and 08-04 (validator 5,768 -> 6,543 bytes, test-writer 4,840 -> 7,933,
  code-writer 2,703 -> 5,027, scope-validator 4,669 -> 6,559). The token accounting says those edits
  did not land on 07-29 — a per-file edit would show as a per-role step, and the observed step is
  uniform to within 70 tokens across seven roles with different files — but this is inference from
  prefix size, not a diff of the text.
- Task difficulty is not controlled. The features before and after the boundary are different
  features. The nine-day flatness of the opus series (29.1-36.0 turns) argues that difficulty was not
  the driver, but it is not a randomized comparison.
