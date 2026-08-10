# Agent forensics: what the TDD pipeline agents actually did

Source: `.agent_handoffs/` (23 MB, 1799 files, 39 features). Everything below is quoted from those
artifacts. Nothing is inferred from how the pipeline "should" work.

---

## Finding 1 — The handoff is not a blueprint, it is the implementation written four times

### 1a. Raw sizes

Per-feature totals, bytes, and the per-task normalisation:

| feature | tasks.md | research total | history total | validator total | tasks | research/task |
|---|---|---|---|---|---|---|
| `m4-asset-references` (cheap, complete) | 19,907 | 68,580 | **0** | 17,201 | 10 | 6,858 |
| `m5-cross-object-references` | 21,343 | 81,409 | 93,908 | 26,699 | 11 | 7,401 |
| `excluded-field-one-way-report` | 22,979 | 95,708 | 24,064 | 24,364 | 4 | 23,927 |
| `uniform-value-descent` (complete, 13 tasks) | 53,032 | 257,850 | 124,821 | 81,500 | 13 | 19,835 |
| `reference-writes-and-cache-invalidation` | 39,296 | 227,687 | 332,967 | 69,381 | 10 | 22,769 |
| `m8-unityevents` (current, 3 of 10 tasks) | **61,699** | 97,584 | 65,844 | 28,903 | 3 | **32,528** |
| `_superseded-m8-plan-2026-08-06` (abandoned) | **98,762** | 118,502 | 92,068 | 36,677 | 4 | 29,625 |
| `_superseded-m8-plan2-2026-08-07` (abandoned) | 64,765 | 33,366 | **215,156** | 19,488 | 2 | 16,683 |

`m4-asset-references` shipped 10 tasks with **zero `history.md` files** — no task was ever re-routed.
Its research averaged 6.9 KB/task. `m8-unityevents` spends 32.5 KB of research per task, **4.7x**
`m4`, and has produced 3 completed tasks across four plan revisions.

The three abandoned m8 plans plus the live one hold **274 KB of `tasks.md` alone** (48,620 + 98,762 +
64,765 + 61,699), for a feature that is roughly 20% done. That is more planning text than the entire
handoff footprint of `m4-asset-references`, `m5-cross-object-references`, `m2b-structural-syncback`,
`m3-reconcile-components` and `m4`'s scope reviews combined.

### 1b. The same content is emitted at four stages

Take `m8-unityevents/b1-t1`. Its entire product is a C# class of 14 `const string` fields naming
Unity's serialized `PersistentCall` paths, plus a guard test. Trace one of the 14 strings,
`m_ObjectArgumentAssemblyTypeName`, through the handoff chain:

```
m8-unityevents/tasks.md:            3 occurrences
m8-unityevents/plan-review.md:      2
m8-unityevents/b1-t1/research.md:   4
m8-unityevents/b1-t1/code-writer.md:1
m8-unityevents/b1-t1/validator.md:  1
```

The full 14-token vocabulary is enumerated in `tasks.md`, again in `research.md`, again in
`code-writer.md`, again in `validator.md`. Four files carry 15-20 distinct `m_*` tokens each.

The restatement is not summary, it is duplication of the *deliverable itself*. `code-writer.md`:

> The 14 `public const string` members now carry the measured `UnityEngine.CoreModule.dll`
> spellings (`m_Target`, `m_TargetAssemblyTypeName`, `m_MethodName`, `m_Mode`, `m_Arguments`,
> `m_CallState`, `m_ObjectArgument`, `m_ObjectArgumentAssemblyTypeName`, `m_IntArgument`,
> `m_FloatArgument`, `m_StringArgument`, `m_BoolArgument`, `m_PersistentCalls`, `m_Calls`), exactly
> as declared in the stub (identifiers unchanged, no `m_` prefix).

`validator.md`, immediately after, re-enumerates the identical list to "check" it:

> - (a) 14 serialized spellings — PRESENT and complete, `UnityEventProjection.cs:15-38`
>   (`m_Target`, `m_TargetAssemblyTypeName`, `m_MethodName`, `m_Mode`, `m_Arguments`, `m_CallState`,
>   `m_ObjectArgument`, `m_ObjectArgumentAssemblyTypeName`, `m_IntArgument`, `m_FloatArgument`,
>   `m_StringArgument`, `m_BoolArgument`, `m_PersistentCalls`, `m_Calls`) plus `CallsRelativePath`.
>   Not a subset.

### 1c. `research.md` is not research, it is a code review of code that does not exist yet

`m8-unityevents/b1-t1/research.md` FILES_EDIT does not describe what to accomplish; it dictates
line-level edits, including which comment string to replace:

> - `SceneBuilder.Core.Tests/ModeArgBypassScanTests.cs` —
>   (1) `ScanATokens` (`:70-76`) becomes `PersistentCallPaths.All.Concat(ApiNameTokens)` [...]
>   (3) The `:78` comment "Seeded once and never edited again" is FALSE from this commit — replace it with
>   what is now true [...]
>   (4) Pin test `ScanA_Allowlist_ContainsExactlyTheThreeSeededPaths` (`:221-232`) is EDITED, not deleted,
>   to pin the single remaining path; rename to `ScanA_Allowlist_ContainsOnlyCoresProjection`.

24 KB of that, for a task whose diff is a constants table. The code-writer's only remaining job is
transcription, which is exactly what its own report admits:

> MAPS_TO_BLUEPRINT: Every member of the `research.md` INTERFACES block is present with the exact
> signature and semantics specified [...] DEVIATIONS: none.

Across `uniform-value-descent`, the `research.md` : `code-writer.md` byte ratio is 257,850 : 37,283
— **6.9 bytes of blueprint per byte of implementation report**. The thinking is being paid for in
the research stage and then paid for again, in smaller denominations, three more times.

---

## Finding 2 — The worst task: 9 iterations, 8 of which changed nothing

`m-nested-props/b7-t2` ("nested-override first-author SubKey bootstrap"). `history.md` is 176,246
bytes across 2,622 lines and archives iterations 1-8; the live files are **iteration 9**.

The code was written once, in iteration 1, at `2026-07-23T17:36:47Z`. Its `code-writer.md` reports
six files with real content:

> FILES:
>   - com.codescenes/Editor/NestedOverrideBootstrap.cs (new)
>   - com.codescenes/Editor/DesiredModelLoader.cs (edit: wired Bootstrap.Resolve before Rehydrate; added `Loaded.BootstrapConflicts`)
>   [...]

Every subsequent iteration opens the same way. Iteration 2:

> FILES: unchanged from iteration 1 (VERIFY-ONLY re-run per research.md iteration 2's LOUD NOTE — this
> task was already implemented and validated GREEN in iteration 1; nothing to re-author):

Iteration 5:

> FILES: unchanged from iterations 1-4 (VERIFY-ONLY re-run per research.md iteration 5's note — this task
> was already implemented and validated GREEN in iteration 4; nothing to re-author):

Iteration 9 (the live file, `2026-07-23T19:40:04Z`):

> FILES: unchanged from iterations 1-8 (VERIFY-ONLY re-run per research.md iteration 9's note — this task
> was already implemented and validated GREEN in iteration 8; nothing to re-author):

### Iteration-by-iteration

| iter | validator verdict | gate | what changed |
|---|---|---|---|
| 1 (17:36) | GREEN (not archived) | green | the entire implementation |
| 2 (18:15) | not archived | green | nothing |
| 3 (18:02) | not archived | green | nothing |
| 4 (18:10) | GREEN, `EXIT_CODE: 0` | green | nothing |
| 5 (18:30) | not archived | green | nothing |
| 6 (18:42) | GREEN, `GATE PASS: Core + Unity EditMode green (passed=358 failed=0 skipped=0)` | green | nothing |
| 7 (19:06) | GREEN, same 358/0/0 | green | nothing |
| 8 (19:29) | GREEN, same 358/0/0 | green | nothing |
| 9 (19:42) | GREEN, same 358/0/0 | green | nothing |

Elapsed wall time from iteration 1's code-writer to iteration 9's validator: **2 hours 5 minutes**.
Test counts are identical (358 passed / 0 failed / 0 skipped) at iterations 6, 7, 8 and 9. Every one
of those validators ran the full `./verify.sh` including the Unity EditMode layer, which the project
contract describes as taking "minutes".

### The cause is stated in the artifacts, repeatedly

`research.md` iteration 5:

> **b7-t2 IS ALREADY IMPLEMENTED AND GREEN — this is a verify-only re-run.** The session-start git
> snapshot (clean tree at d48fe77) was STALE.

Iteration 6, same file, louder:

> **b7-t2 IS ALREADY IMPLEMENTED AND GREEN — this is a verify-only re-run. Do NOT re-author anything.**
> [...] Downstream agents: do NOT re-author `NestedOverrideBootstrap.cs`, do NOT re-wire
> `DesiredModelLoader.Load`/`SceneBuilderBuild`/`SceneBuilderSync`, do NOT re-add the Core
> `Conflict`/`DiagnosticCodes` members, do NOT re-write the EditMode tests — they exist and are correct.

Iteration 8, louder still:

> **b7-t2 IS ALREADY FULLY IMPLEMENTED, WIRED, AND LAST-VALIDATED GREEN. This is a verify-confirmed
> re-run — do NOT re-author anything.** The session-start git snapshot (clean tree @ `d48fe77`) is STALE.

The research agent diagnosed the loop correctly on iteration 5 and had no way to stop it. It spent
four more full iterations shouting at the orchestrator in bold. **This is not converging and it is
not thrashing. It is an infinite loop that ran out of budget rather than terminating.**
`152,636 of the 176,246 bytes` of `history.md` (87%) document iterations that changed no code.

### Contrast: a task where the loop was doing real work

`m-ui-recttransform/b3-t5` took 8 iterations, and there the routes were earned. Iteration 4's
validator had a fully green gate and routed anyway:

> EXIT_CODE: 0 / GATE_PASSED: yes
> ```
> GATE PASS: Core + Unity EditMode green (passed=426 failed=0 skipped=0)
> ```
> BLOCKING:
> - `SourcePatchApplier.cs:642-659` [...] the branch removes the WHOLE configure-lambda argument
>   whenever the anchored invocation is a `SimpleLambdaExpressionSyntax { Body: ExpressionSyntax }`'s
>   body, without checking that the anchored call is the body's ONLY call. [...]
>   - case D `scene.Add("P", p => p.Add("L").Component<UnityEngine.MeshFilter>());`
>     remove `P/0/L/0/UnityEngine.MeshFilter#0` -> applied text `scene.Add("P");` [...] the
>     CHILD GAMEOBJECT `L` is deleted from the user's source.

That is a real data-loss bug, measured end-to-end against the binary the gate had just passed, found
by review and not by any test. **Green-gate-plus-reviewer-route is not inherently waste** — this one
instance justifies the mechanism. The problem is the ratio, quantified next.

Two footnotes from the same file. First, the pipeline's own audit caught a code-writer that produced
nothing on a routed fix:

> superseded 2026-07-30T02:26:00-04:00 by iteration 5, which implements research.md iteration 3's
> comprehensive one-predicate blueprint after scope/bucket-b3.md found this iteration's fix never
> actually landed a code change for validator.md iteration 4's routed CODE_BUG — only test-writer
> iteration 4's two RED regression cases did

Second, the validator itself wrote the correct structural lesson and it was not acted on:

> SIGNAL: second consecutive iteration on the same guard [...] Each pass fixed the one cell it was
> shown and tested exactly that cell.

---

## Finding 3 — Most objections are process, not correctness. This is the headline.

### 3a. The corpus-wide route counts

Across every `validator.md` plus every archived iteration in every `history.md`:

```
429  STATUS: GREEN
 25  STATUS: ROUTE_TEST
 16  STATUS: ROUTE_CODE
  5  STATUS: ROUTE_RESEARCH
```

46 routes in 475 validations (9.7%). ROUTE_TEST is the majority route at 54%, and reading the route
reasons, most ROUTE_TESTs are bookkeeping against the DELIVERABLE text rather than a wrong behaviour:

> ROUTE → test-writer    (TEST_ISSUE: an explicit DELIVERABLE clause is unasserted)
> ROUTE → test-writer    (TEST_ISSUE: two named DELIVERABLE test cases were never written)
> ROUTE → test-writer    (TEST_ISSUE: stale test — asserts behavior this task legitimately changed)
> ROUTE → test-writer    (TEST_ISSUE: stale test — this task's own change exposed it)
> ROUTE -> test-writer   (TEST_ISSUE: three stale/wrong test expectations)

Against genuine correctness routes:

> ROUTE → code-writer    (CODE_BUG)
> ROUTE → research    (FUNDAMENTAL: approach/plan cannot satisfy the contract; conflicts with existing code)
> ROUTE -> test-writer   (TEST_ISSUE: broken test setup — target never resolved, assertions vacuous)

### 3b. The scope-validator findings, which are where the volume is

130 `scope/*.md` files carry 92 `SEVERITY` findings:

```
 8  SEVERITY high
14  SEVERITY med
70  SEVERITY low   (76%)
```

Sampling 24 of them and classifying CORRECTNESS (a failing test, a real defect, wrong behaviour)
versus PROCESS (paperwork, prose, stale comments, undeclared touches, doc drift):

**CORRECTNESS (8 of 24):**
1. `SEVERITY high — TASK b1-t1 — ReconcilerInstances.BuildAddInstanceComponent writes NON-COMPILING` source
2. `SEVERITY high — TASK b2-t1 — for a rect node the Differ compares Scale against the RAW live` value
3. `SEVERITY high — TASK b3-t1 — a scene-reference LIST whose rendered handle tokens are of two` kinds
4. `SEVERITY high — TASK b3-t2 — the auto-sync 3-way merge never learned the five RectTransform` fields
5. `SEVERITY high — TASK b6-t1 — PlanExecutor.ApplyTransformField (:315-332) writes whole localPosition/` rotation
6. `SEVERITY high — TASK b6-t1 — Snapshot-side DrivenChannels derivation absent`
7. `SEVERITY med — TASK b2-t2 — Derived handle is NOT uniquified aga`inst existing handles
8. `SEVERITY med — TASK b4-t1 — The scoped-.On find-or-create MATCH`ing defect

**PROCESS (16 of 24):**
9. `SEVERITY low — TASK b1-t4 — b1-t4/test-writer.md has no terminal STATUS: line.` — an agent's own report file
10. `SEVERITY low — TASK b1-t4 — b1-t4/test-writer.md still has no terminal STATUS: line.` — same, re-reported
11. `SEVERITY low — TASK b2-t2 — b2-t2/test-writer.md still has no terminal STATUS: line.` — same class again
12. `SEVERITY low — TASK b3-t2 — b3-t2/test-writer.md still has no terminal STATUS: line.` — and again
13. `SEVERITY low — TASK b1-t3 — SceneBuilder.Core/Model/ValueWalk.cs:6-13: the class <summary>` contradicts its remarks — reported **4 times**
14. `SEVERITY low — TASK b2-t1 — stale comment at com.codescenes/Editor/SceneBuilderAutoSync.cs:617-620` — reported twice
15. `SEVERITY low — TASK b2-t1 — docs/open-defects.md is referenced by no other document in the` repo
16. `SEVERITY low — TASK b2-t1 — docs/open-defects.md:118 carries a stray STATUS: READY handoff` token
17. `SEVERITY low — TASK b1-t1 — undeclared tracked edit in the bucket's diff: docs/open-defects.md`
18. `SEVERITY low — TASK b3-t1 — three archived spec listings still print a signature the tree no longer` has
19. `SEVERITY low — TASK b3-t1 — Repo docs still say D2 is unbuilt, after it shipped in this feature.`
20. `SEVERITY low — TASK b4-t3 — HANDOFF.md:39-44 still reads "specs/36 ... Not yet started; no code`
21. `SEVERITY low — TASK b1-t2 — Stale code comment: InstanceOverrideExecutor.cs:231-235 still states`
22. `SEVERITY low — TASK b1-t2 — Stale/misleading comment: InstanceOverrideExecutor.cs:223-224 still`
23. `SEVERITY low — TASK b3-t4 — Stray untracked gate artifact unity-gate/unity-gate/results.xml`
24. `SEVERITY low — TASK b2-t1 — published API copy is out of step with the shipped surface: the XML` doc

**8 correctness : 16 process — 2 process objections for every 1 real defect.** All 8 `high` findings
in the whole corpus are correctness; all 70 `low` findings sampled are process or cosmetic.

### 3c. The single worst example: agents routing each other over their own paperwork

`reference-writes-and-cache-invalidation/scope/bucket-b1.md:170`:

> - SEVERITY low — TASK b1-t4 — `b1-t4/test-writer.md` has no terminal `STATUS:` line. Measured on the
>   current tree: `tail -1` -> ``c.Set("<path>", new[] { ... })`, never a typed selector.` (the last
>   line of the `## Contract` paragraph); `grep -c '^STATUS' -> 0`. HANDOFF-PROTOCOL general rule 2
>   requires `STATUS:` as the genuine last line [...] Audit-trail only; no observable
>   behavior changes. Already registered in tasks.md `## DEFECTS` with OWNER b1-t4 (test-writer step).
>   **ROUTE → test-writer**

The finding names itself "Audit-trail only; no observable behavior changes" and routes anyway. It was
then re-reported and re-routed at every subsequent scope pass. `scope/final.md:240`:

> - SEVERITY low — TASK b1-t4 — `b1-t4/test-writer.md` still has no terminal `STATUS:` line.
>   Re-verified: `grep -c '^STATUS'` -> `0` [...] Routed by `scope/bucket-b1.md` finding 2 and
>   re-reported by bucket-b2, bucket-b3 and final iterations 1-2; still unfixed. **Not fixable by a
>   code-writer** — the producing agent owns its own handoff [...] Audit-trail only. ROUTE → test-writer

Five separate scope-validator passes, each re-running the whole test suite, each re-litigating a
missing line in a markdown file that no product code reads, each routing a fix the reviewer itself
states is impossible to apply. This is the clearest instance in the corpus of agents generating work
for each other rather than shipping code.

---

## Finding 4 — Task sizing predicts rework, and the threshold is 7 declared files

Parsed every `### Task` block with a matching handoff directory across all 39 features: **342 tasks**.
For each, the DELIVERABLE word count, the number of files in `TOUCHES:`, and the highest `iteration:`
recorded in that task's `validator.md` or `history.md`.

### By TOUCHES file count

| TOUCHES files | n | mean iterations | % needing rework | worst |
|---|---|---|---|---|
| 1-2 | 157 | 1.22 | 11% | 8 |
| 3-4 | 119 | 1.41 | 24% | 5 |
| 5-6 | 47 | 1.51 | 26% | 9 |
| **7-9** | 11 | **2.91** | **64%** | 7 |
| **10+** | 8 | **2.62** | **75%** | 6 |

The curve is flat to 6 files and then breaks. **A task declaring 7 or more files needs a second
iteration two-thirds of the time; a task declaring 6 or fewer needs one about a fifth of the time.**
Mean iterations roughly doubles at the same boundary.

### By DELIVERABLE size

| DELIVERABLE words | n | mean iterations | % needing rework |
|---|---|---|---|
| 0-50 | 221 | 1.22 | 14% |
| 51-100 | 67 | 1.52 | 22% |
| 101-200 | 34 | 2.03 | 38% |
| 201-400 | 16 | 1.81 | 44% |
| 401+ | 4 | 3.50 | 100% |

Monotone. Rework probability triples between a 50-word deliverable and a 200-word one, and all four
tasks with a 400+ word DELIVERABLE required rework.

### The two features sit on opposite sides of both thresholds

`m4-asset-references` — 10 tasks, DELIVERABLE 26-66 words, TOUCHES 2-4 files, **every task one
iteration, zero `history.md` files in the whole feature.**

`m8-unityevents` — DELIVERABLE 162-589 words, TOUCHES up to 14 files. Its `b3-t1` carries 589
DELIVERABLE words and 14 TOUCHES entries; `_superseded-m8-plan2`'s `b1-t2` carried 453 words and 16
TOUCHES entries and took 6 iterations, the most of any task in either m8 plan.

`uniform-value-descent`'s `b1-t1` — the one this report quoted in Finding 1c — carries a **512-word
DELIVERABLE** and took 4 iterations, the joint-worst in an otherwise clean feature where 10 of 13
tasks landed first time.

The actionable number: **cap TOUCHES at 6 files and DELIVERABLE at ~100 words.** Above either, expect
to pay for a second full research → test → code → validate → gate cycle more often than not.

### Corollary: research.md context is 85-90% unused

Counting distinct file paths named in each stage's document:

| task | files named in research.md | files the code-writer actually touched |
|---|---|---|
| `uniform-value-descent/b1-t1` | 93 | 10 |
| `m8-unityevents/b1-t2` | 59 | 11 |
| `m8-unityevents/b1-t3` | 57 | 7 |
| `m8-unityevents/b1-t1` | 24 | 5 |
| `m4-asset-references/b1-t1` | 11 | 8 |

The cheap feature's research names 11 files and 8 get touched. The expensive one names 59 and 11 get
touched. And the blueprint is not even authoritative: `m8-unityevents/b1-t2`'s code-writer touched
`ComponentRef.cs`, `ComponentTypeNormalizer.cs`, `DiagnosticDescriptors.cs` and
`FlatShapeRecognizer.UnityEvents.cs`, none of which the 30 KB `research.md` names.

---

## Finding 5 — Plan churn: four plans, three abandoned, and the text that accumulated was defensive

M8 has four `tasks.md` files. They did not grow — they *shrank in scope while growing in preamble*:

| plan | bytes | tasks | preamble (before first `## Bucket`) | `## DEFECTS` tail | body |
|---|---|---|---|---|---|
| `_stale-m8-unityevents-pre36` | 48,620 | 11 | 2,450 | 0 | 46,170 |
| `_superseded-m8-plan-2026-08-06` | 98,762 | 15 | 1,824 | **19,003** | 77,935 |
| `_superseded-m8-plan2-2026-08-07` | 64,765 | 12 | 3,491 | 9,360 | 51,914 |
| `m8-unityevents` (live) | 61,699 | 10 | **4,823** | 9,279 | 47,597 |

Task count went **11 → 15 → 12 → 10**. The plan is being re-cut, not extended. What grew across
revisions is the preamble, which doubled, and what it contains is not deliverables. The live plan's
first section is titled:

> ## Already shipped — do NOT re-plan (commits `30a9613`, `3325907`)
> Spec 09's "ALREADY SHIPPED" section is accurate and its two follow-on paragraphs are now also satisfied in
> part. Build ON all of this; never schedule it:

Followed by 26 lines enumerating what previous, abandoned plans built, so the next planner does not
re-plan it. That is 4.8 KB of "don't do this again" at the top of a 61 KB plan.

### The defect register is now a repo document with dead owners

`docs/m8-measured-defects.md` (29,271 bytes, 356 lines) exists solely to survive plan regeneration:

> Extracted 2026-08-06 from `.agent_handoffs/m8-unityevents/tasks.md` before that plan was regenerated
> from scratch. Every entry below was MEASURED against real code or a real editor during M8's bucket-b1
> implementation run; none is a guess. Kept in the repo because `.agent_handoffs/` is gitignored and
> rediscovering these costs live editor runs.

And its reading notes concede the ownership has rotted:

> - Entries whose OWNER names a task id refer to the PRE-REGENERATION plan; those ids no longer exist.
>   Treat the measurement as true and the ownership as void.

`OWNER: unassigned` appears **11 times** in `docs/m8-measured-defects.md` and **8 times** in the live
`tasks.md` `## DEFECTS` section. Nineteen measured, real defects with no owner, carried between plan
revisions as prose.

### The plan reviews cost more than a whole cheap feature's planning

`plan-review.md` per m8 revision: 12,716 + 14,395 + 16,746 + 10,602 = **54,459 bytes**, and three of
the four verdicts rejected the plan:

> VERDICT: ISSUES - 1 high, 3 med, 5 low.        (plan 1)
> VERDICT: NOT VALID — 1 high, 5 medium, 7 low.  (plan 2)
> VERDICT: VALID (no high-severity gap). 5 medium, 3 low.  (plan 3, live)

`m4-asset-references`' entire plan review is 3,687 bytes.

### The git log confirms the churn is the visible output

The last six commits on `main` touching this milestone:

```
754d18d docs: remove the partial cost investigation
eaef15f docs: correct the regression-hunt premise
ce3e36d docs: preserve the partial cost investigation before the budget runs out
18f36a2 docs: append the second plan's measured defects before rerolling again
ed4ee1b specs(09): record the adapter serialized-path vocabulary, learned by getting it wrong twice
3325907 m8 b1 (reroll): Reconciler split, and the foundation deltas the rest consumes
```

One code commit, five meta-commits. The commit messages name the pathology without prompting:
"learned by getting it wrong twice", "before rerolling again", "before the budget runs out".

---

## THE SINGLE MOST DAMNING MEASUREMENT

**M8 has produced 1,293,094 bytes of agent handoff prose across four plans and delivered 4,196
insertions across its two code commits (`30a9613` 3,287, `3325907` 909) — roughly 20% of one feature. The completed feature `uniform-value-descent`
delivered a whole 13-task milestone on 763,002 bytes, and `m4-asset-references` delivered a whole
10-task milestone on 181,886 bytes.**

Normalised per unit of delivered feature, M8 is spending **8.5x** what `uniform-value-descent` spent
and **35x** what `m4-asset-references` spent. The cheap feature is not cheap because its work was
easy: it is cheap because every task declared 2-4 files and a 40-word deliverable, and consequently
**not one of its ten tasks was ever routed back** — the feature has no `history.md` files at all.

---

## RANKED WASTE

**1. Iterations that change no code — the largest single block of wasted spend.**
`m-nested-props/b7-t2`: 9 iterations, code written once in iteration 1, iterations 2-9 all report
"FILES: unchanged from iterations 1-N ... nothing to re-author". 152,636 of 176,246 history bytes
(87%) document no-ops, across 2h05m, each iteration re-running the full Unity gate. The research
agent diagnosed it on iteration 5 — "The session-start git snapshot (clean tree at d48fe77) was
STALE" — and could not stop it. Corpus-wide, 429 of 475 validations returned GREEN; every GREEN that
follows a GREEN with an identical test count is this failure mode.

**2. Process objections outnumbering correctness objections 2:1.** 92 scope findings: 8 high, 14 med,
**70 low (76%)**. Every `high` is correctness; the sampled `low` findings are stale comments, doc
drift, and paperwork. The worst: `b1-t4/test-writer.md` — an *agent's own report file* — was routed
back to the test-writer for a missing `STATUS:` line by five successive scope passes, each one
stating "Audit-trail only; no observable behavior changes" and "Not fixable by a code-writer", and
routing anyway.

**3. Four-fold restatement of the deliverable.** `tasks.md` → `research.md` → `code-writer.md` →
`validator.md` each enumerate the same 14 string constants in `m8-unityevents/b1-t1`. Across
`uniform-value-descent` the research:code-writer byte ratio is **6.9:1**. The code-writer's own report
concedes there was nothing left to decide: "MAPS_TO_BLUEPRINT: Every member of the `research.md`
INTERFACES block is present with the exact signature and semantics specified [...] DEVIATIONS: none."

**4. Oversized tasks.** Tasks with 7+ TOUCHES files rework 64-75% of the time versus 11-26% below
that line; 400+ word DELIVERABLEs rework 100% of the time (n=4). `m8-unityevents/b3-t1` is currently
planned at 589 DELIVERABLE words and 14 TOUCHES files.

**5. Plan regeneration.** Three abandoned plans, 54,459 bytes of plan review (3 of 4 rejected), a
29 KB out-of-band defect register created because `.agent_handoffs/` is gitignored, and 19 measured
defects whose `OWNER` fields point at task ids that no longer exist.

**6. Research context that is never used.** `uniform-value-descent/b1-t1`'s research names 93 file
paths; 10 were touched. And it is not even a reliable contract — `m8-unityevents/b1-t2`'s code-writer
edited four files the 30 KB blueprint never mentions.

---

## RECOMMENDATIONS

**1. Make the orchestrator re-read the working tree before each iteration, and hard-stop a task whose
diff is byte-identical to the previous iteration's.** (Effort: small, in `pipeline.workflow.js`.
Sequencing: first, it blocks the largest waste block.) Justified by `m-nested-props/b7-t2`, where the
research agent wrote "The session-start git snapshot (clean tree at `d48fe77`) is STALE" in four
consecutive iterations and the loop continued. A no-change iteration should terminate the task as
GREEN, not re-enter it. This is the one fix that pays for itself on the next run.

**2. Split the validator's output into BLOCKING and LEDGER, and permit routing only on BLOCKING.**
(Effort: small — a prompt change in `tdd-validator.md` and `tdd-scope-validator.md` plus a routing
guard. Sequencing: second.) Define BLOCKING as: a failing gate, or a defect demonstrated by a
measured reproduction. Everything else — stale comments, doc drift, handoff-file formatting,
unasserted deliverable clauses with real coverage — appends to a ledger and never routes. Justified
by the 70:8 low-to-high severity ratio and by the `STATUS:`-line route, which the reviewer itself
labelled "Audit-trail only; no observable behavior changes" before routing. Note the counter-case
this must preserve: `m-ui-recttransform/b3-t5` iteration 4 routed on a fully green gate and found a
real data-loss bug (`scene.Add("P", p => p.Add("L").Component<MeshFilter>())` losing the child
GameObject `L`) — that finding carried a measured end-to-end reproduction, so it passes the BLOCKING
test as defined.

**3. Forbid a scope pass from re-reporting a finding already recorded in the ledger.** (Effort:
trivial. Sequencing: with #2.) The `ValueWalk.cs` `<summary>` nit was reported four times; the
`STATUS:`-line finding five times; two `docs/open-defects.md` findings twice each. Each re-report
carried a full suite run.

**4. Cap tasks at 6 TOUCHES files and ~100 DELIVERABLE words in `task-deconstruct`, and make the
decomposition validator fail a plan that exceeds either.** (Effort: small. Sequencing: before the
next feature is deconstructed.) Justified directly by the 342-task measurement: rework goes 26% → 64%
at the 7-file boundary, and 14% → 38% between 50 and 200 deliverable words. `m4-asset-references`
sits entirely inside both caps and has zero rework in the corpus.

**5. Change `research.md` from a line-level edit script to a bounded contract: the file list, the
public signatures, and the named risks — with a hard size cap (target 8 KB, the `m4` average).**
(Effort: medium — rewrite `tdd-research.md`. Sequencing: after #1 and #2, which are cheaper.)
Justified by the 6.9:1 blueprint-to-implementation ratio, by research naming 93 files where 10 were
touched, and by blueprints that dictate which code comment to replace ("The `:78` comment 'Seeded
once and never edited again' is FALSE from this commit"). If research is going to be that specific it
should be writing the code; if it is not writing the code, it should stop pre-writing it.

**6. Make the defect ledger a first-class repo artifact with mandatory ownership, written by the
pipeline rather than salvaged by hand.** (Effort: medium. Sequencing: last.) Justified by
`docs/m8-measured-defects.md`, which exists because ".agent_handoffs/ is gitignored and rediscovering
these costs live editor runs", and which carries 11 `OWNER: unassigned` entries plus a note that all
task-id owners are "void". A defect with no owner is re-measured every plan regeneration; M8 has paid
that at least twice ("learned by getting it wrong twice").

---

## WHAT I COULD NOT ESTABLISH

- **Token or dollar cost.** Nothing in `.agent_handoffs/` records model, token counts, or spend. All
  cost figures here are document bytes and wall-clock timestamps, which are proxies. The 8.5x and 35x
  figures are byte ratios, not billing ratios.
- **Why the orchestrator re-entered `m-nested-props/b7-t2` nine times.** The artifacts state the git
  snapshot was stale; they do not show the orchestrator's decision. `pipeline.workflow.js` lives in
  `~/.claude/skills/tdd-pipeline/` and I did not read it — the loop-bound and the re-entry condition
  are there, not in the handoffs.
- **Which iterations 1, 2, 3 and 5 of `b7-t2` actually did.** Their `validator.md` entries are not in
  `history.md`; only iterations 4, 6, 7, 8 were archived. The claim that they were GREEN comes from
  the *next* iteration's research note, not from a preserved validator record.
- **Whether the three abandoned m8 plans were abandoned for a reason a smaller plan would also have
  hit.** The `_lessons/` directory and `docs/open-defects.md` were not read in depth. I have the plan
  reviews' verdicts but not the human decision to reroll.
- **Whether the process objections ever prevented a real bug.** I established that 76% of scope
  findings are low-severity and that at least one green-gate route (`b3-t5` iteration 4) caught real
  data loss. I did not trace any low-severity finding forward to see whether ignoring it would later
  have cost something.
- **Wall-clock cost of the Unity gate layer.** `CLAUDE.md` says "minutes"; the gate logs record test
  durations but not total editor-suite runtime, so the 2h05m for `b7-t2` could not be decomposed into
  gate time versus agent time.
