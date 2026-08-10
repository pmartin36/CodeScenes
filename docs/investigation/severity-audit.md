# Severity audit: is "halt only on HIGH" safe?

Scope: stress-test of one proposed remediation change before it is built. Nothing here re-audits the
pipeline generally.

## Verdict

**UNSAFE AS PROPOSED, and the premise it rests on is factually wrong.**

Two independent reasons, both measured.

1. **The harness does not do what the proposal says it does.** It does not halt on ANY finding. It
   halts when the decomposition validator returns `valid: false`, and the validator's own prompt
   already instructs `valid=false if any high-severity gap`. Across 81 recorded plan-validation runs:
   50 returned `valid:false` and **every one of the 50 carried at least one HIGH finding**; 31
   returned `valid:true` while carrying 147 findings (94 low, 53 medium, **zero high**) and the run
   proceeded. The severity gate the proposal wants to add is already in place. Implementing it would
   change the outcome of **0 of the 50 halts**.

2. **Severity is not reliable in the dangerous direction.** On an independently judged 48-finding
   sample (16 high, 16 medium, 16 low): 3 of 16 HIGH were over-labeled (all still real defects, none
   consequence-free), and **15 of 32 MED/LOW (47%) would have caused a real failure if the run had
   proceeded**: non-compiling emitted source, silent identity loss, a gate-red file-size breach, an
   emitted plan op with no executor, a spec-mandated test nobody would write. Twenty-four
   cross-severity near-duplicate pairs exist in the corpus: the same defect text rated LOW or MEDIUM
   in one run and HIGH in another, in the same feature.

The recommended variant is in the final section: keep the halt, replace the human round-trip with a
bounded plan-repair loop, and filter by finding CLASS rather than by severity.

## Data and method

Source: `~/.claude/projects/-home-paul-Source-CodeScenesUnity/*/subagents/workflows/wf_*/journal.jsonl`,
88 workflow runs, 81 of which contain a `tdd-decomposition-validator` result (`{valid, issues[]}`).
507 plan findings total: 70 high, 198 medium (`medium` + `med`), 239 low.

The report's headline count of "385 findings" is close to the 360 findings that landed on halted runs;
the remainder are the harness's own deterministic `planIssues` (dependency cycles, unknown dependency
ids, TOUCHES overlaps) which are computed in-script and carry **no severity prefix at all**. That
matters for implementation and is flagged in the safe-variant section.

Per-task validator verdicts: 480 records recovered from `.agent_handoffs/*/*/validator.md` and
`history.md`. Scope-validator findings: 215 recovered from the journals.

## Question 1: how reliable are the labels?

Sample: 48 findings, judged independently against the actual consequence of ignoring each. The sample
is the first 16 in each severity stratum in journal order, spanning 12 features (typed-prefab-facades,
m10-prefab-overrides, m-nested-props, reference-writes-and-cache-invalidation,
boundary-defects-and-roundtrip-proof, excluded-field-one-way-report, m-ui-recttransform,
spatial-authoring-components, m8-unityevents, uniform-value-descent, codescenes-analyzers,
multi-scene-builders). Full finding texts are in the journals; the ids below are positional within
each stratum.

### HIGH: 13 of 16 correctly labeled, 3 over-labeled, 0 consequence-free

Correct (ignoring them causes a concrete failure):

| id | consequence if ignored |
| --- | --- |
| H1 | spec 25's mandated typed-emit form never built; no task touches the emitter |
| H2 | shipping editor passes a null facade catalog, so every typed form fails to resolve |
| H3 | the read path drops `PropertyModification.objectReference`, corrupting every reference-valued override |
| H4 | a spec requirement de-scoped on a citation that does not exist in the spec file |
| H5 | the round-trip task can be scheduled before the authoring/parse code exists; its assertions are vacuous |
| H6 | renderer and parser invent the same fluent syntax independently; divergence breaks round-trip silently |
| H7 | no revert op, so stale overrides persist in the scene against code-as-truth |
| H8 | child-GameObject overrides are modelled, executed and read with no authoring surface and no parse arm |
| H10 | EditMode compile assertions run before the generator that emits the types they assert |
| H11 | three tasks are concurrent writers to `BuilderParser.Instance.cs` with no edge; writes clobber |
| H12 | behavior #10 revert path and checklist #8 have neither a Core nor an EditMode owner |
| H14 | two unrouted gates: one throws `NotSupportedException` behind a green bucket, one silently patches a deleted list element to `NodeHandle.None` |
| H15 | the bypass check as specified allowlists the two sites it was meant to catch |

Over-labeled (real, but the blast radius is a missing verification rather than a broken behavior):

- H9. Prefab delete/move regen has no verifying deliverable. A coverage gap plus a CLAUDE.md rule
  breach, not a demonstrated break. MEDIUM.
- H13. One named Core test (`NestedInNestedPrefab_Override_RoundTripsBytes`) is unowned and will not
  be written. One missing test. MEDIUM.
- H16. The spec's mandatory Live tier is downgraded to "worth a live pass" prose. A dropped
  verification tier. MEDIUM.

**No HIGH in the sample was really LOW.** The high-side error is one severity step, and always in the
conservative direction.

### MEDIUM: 11 of 16 would have caused a real failure

| id | severity says | actual consequence |
| --- | --- | --- |
| M1 | medium | two tasks clobber `CodeScenes.Analyzers.csproj` in parallel |
| M2 | medium | the spec names "Test 12 and the compile assertion pin this" for cross-prefab type collision; no task pins it |
| M3 | medium | Unity-observable behavior with headless-only coverage, which CLAUDE.md forbids outright |
| M5 | medium | an unresolvable selector silently drops or guesses a durable identity key; the spec calls this out as "never a silent drop, never a guessed key" |
| M6 | medium | a Plan op is emitted with no executor arm, so remove-child does nothing |
| M9 | medium | stale-override detection needs a value no task produces, so behavior #9 cannot be built |
| M10 | medium | emitted child-edit syntax has no parse-back path, so the emit has no consumer |
| M12 | medium | spec-mandated project-wide `Prefabs.<X>` uniqueness has no owner or test |
| M13 | medium | a nested-prefab edit leaves composing parents' facades silently stale |
| M14 | medium | a nested source GUID never joins `Assets[]`, so an `AtPath` target fails to resolve after reload |
| M16 | medium | two measured write paths emit non-compiling source (CS0103), which CLAUDE.md rates a bug outright |

Correctly labeled MEDIUM: M4 (spec is internally contradictory, needs a human decision), M7
(file-size headroom risk, not a breach today), M8 (per-op unit not crisply owned but covered
end-to-end), M11 (a wrong "headless" label on an EditMode test), M15 (directory-granular TOUCHES
overlap the validator itself judges collision-free).

### LOW: 4 of 16 would have caused a real failure

- L4 and L13. `BuilderParser.cs` at 982/1000 and 975/1000 lines with a gate-enforced 1000-line
  budget. Ignoring either is a RED gate, not a style note. This class later materialized for real:
  `docs/m8-measured-defects.md` records `SourcePatchApplier.cs` at 941/1000 with 59 lines of headroom
  as a MEDIUM that stayed unrouted through two plans.
- L7. Nested-prefab source GUIDs joining the `Assets[]` cache is unowned; the same issue is M14 at
  MEDIUM in a different run. Consequence: unresolvable `AtPath` target after reload.
- L9. `AddedComponents` GlobalObjectId recording is unowned, so "the added component loses stable
  identity across reload". Identity is the product's core mechanism.

The other 12 lows are correctly labeled: fidelity notes, "consider an explicit assertion", authorized
de-scopes flagged for a human, and two entries the validator itself marks "no action needed".

### Confusion summary

| direction | count | rate |
| --- | --- | --- |
| HIGH that was really lower | 3 / 16 | 19% (all one step; none consequence-free) |
| MED/LOW that would have caused a real failure | 15 / 32 | 47% |

The dangerous direction is the common one. Under "halt only on HIGH", the 47% ships.


## Question 2: counter-examples

I searched for a medium or low finding that a later run proved consequential. There are many, from
three independent directions.

### A. The same defect rated LOW/MEDIUM in one run and HIGH in another

Token-overlap matching within a feature, across different workflow runs, opposite sides of the HIGH
boundary, overlap >= 0.40: **24 pairs**. A sample, with the divergent labels:

| feature | rated | rated | the defect |
| --- | --- | --- | --- |
| m8-unityevents | MED-1 | HIGH-1 | `ModeArgBypassScanTests` Scan A allowlist is inert: a mode table inside either adapter file is invisible to both scans |
| m8-unityevents | MEDIUM | HIGH | `SourceExpr.cs` throws `NotSupportedException` on the new `ValueNode` kind and the render path is in no task's TOUCHES |
| m8-unityevents | LOW | HIGH | the null-`Target` (dangling) listener has no owning read-back or render deliverable |
| m8-unityevents | MEDIUM | HIGH | the shipped `ObjectRefDescentScanTests` exact-count allowlist is unowned by any task |
| uniform-value-descent | LOW | HIGH | b1-t1 weakens D0's superset requirement so "found nothing beyond the floor" becomes legal |
| uniform-value-descent | LOW | HIGH | "every NEW EditMode file ships with its committed `.meta`" is per-task prose with no owning mechanism |
| uniform-value-descent | LOW | HIGH | `PinnedTestBaseline.json` is declared by five tasks, two of them in one bucket with no edge |

For the first row the escalation is triple-attested. It appears as `MED-1` in
`.agent_handoffs/m8-unityevents/plan-review.md`, as `HIGH-1` in a later plan-validation run, and as a
MEASURED register entry in `docs/m8-measured-defects.md`:

> SEVERITY med [...] `ModeArgBypassScanTests.cs` does not guard what `tasks.md:241-242` claims [...]
> a mode table written inside `UnityEventWriter.cs` or `UnityEventReader.cs` is invisible to BOTH
> scans and spec 09:219-220 [...] is unguarded exactly where the adapter listener code lands.
> [...] Same finding as `scope/bucket-b1.md` finding 1 and `plan-review.md` MED-1.

Under the proposal that finding is a ledger line on its first two appearances. It was consequential
enough that a third validator rated it HIGH and a measuring agent burned real editor time to
rediscover it.

### B. MEDIUMs that reached `docs/m8-measured-defects.md` as real, measured breakage

Every entry in that file is measured against real code or a real editor. The medium-rated entries
include:

- `SceneBuilder.Core/Reconcile/SourceExpr.cs` throwing `NotSupportedException` on a listener value,
  with the source-render path in no task's TOUCHES. Rated **med**.
- Every consumer of `ValueNode.Primitive.Value` throwing `InvalidCastException` once a Primitive has
  crossed a JSON boundary, demonstrated with a scratch console against the built DLL. Rated **med**,
  on the reasoning that it is not reachable in the shipped product *today*; the entry then names the
  natural b3-t1 test that makes it live.
- `SourcePatchApplier.cs` at 941/1000 lines with no split task. Rated **med**, with the note: *"Same
  finding as plan-review.md MED-3, which remains unrouted: plan-review.md is not read by the task
  agents, so this register is its channel."*

That last one is the direct evidence against the "record medium/low to a ledger" half of the
proposal. A ledger already exists for exactly these findings, and the measured outcome of writing to
it was that the finding **remained unrouted** and had to be re-recorded in a second document by a
later agent. Adding a second ledger nothing consumes does not make a waved-through finding safe.

### C. Ledgered findings that were never consumed

`docs/open-defects.md` carries nine `OWNER: unassigned` defects and was, at the time a scope validator
measured it, *referenced by no other document in the repo* (`grep -rn "open-defects" --include='*.md' .`
excluding `.agent_handoffs/` returned nothing). The same validator also found a stray `STATUS: READY`
terminator mid-file making the register look truncated. Both were still reproducing when re-checked
on a later pass and marked "CARRIED, re-verified as still reproducing [...] still unfixed."

## Question 3: a better discriminator than severity

I tested six candidate signals against the 507 plan findings. Rate = share of that severity carrying
the signal.

| candidate signal | HIGH | MEDIUM | LOW | separates? |
| --- | --- | --- | --- | --- |
| cites a repo file that exists | 86% | 70% | 55% | weak |
| cites `file.cs:NN` | 69% | 44% | 32% | weak |
| cites a spec line | 41% | 22% | 21% | weak |
| structural (`TOUCHES` / `DEPENDS_ON`) | 43% | 42% | 22% | no |
| says MEASURED / verified / grep | 16% | 4% | 3% | rare but clean |
| ownership-gap language ("no task owns", "unowned") | 37% | 16% | 7% | best single signal |
| finding text > 1200 chars | 60% | 2% | 0% | near-perfect, and an artifact |

No single lexical signal is a usable gate. Finding CLASS is, because the class determines the
consequence and the severity does not. The cross-tab that proves severity is not tracking class:

| class | high | med | low |
| --- | --- | --- | --- |
| TOUCHES overlap with no edge | 7 | 9 | 3 |
| missing dependency edge | 4 | 12 | 4 |
| undeclared TOUCHES | 4 | 11 | 2 |
| unowned spec requirement | 20 | 14 | 11 |
| un-hoisted invariant | 4 | 6 | 3 |
| missing EditMode coverage | 3 | 21 | 24 |
| file-size budget | 1 | 14 | 20 |
| under-tested detail / informational | 0 | 0 | 13 |

Every consequential class is smeared across all three severities. Only the bottom row is
severity-clean, and it is the row nobody cares about.

### The filter the data supports

Halt on a finding if it matches ANY of these, regardless of its stated severity:

- **D. Deterministic harness findings.** Dependency cycle, unknown dependency id, TOUCHES overlap
  without an edge. These are computed in-script, are always exactly right, and carry no severity
  string at all. See the implementation warning below.
- **O. Unowned mandatory artifact.** A spec requirement, a spec-named test, an adapter deliverable, a
  stale test the feature invalidates, or a fixture/file the plan provably edits, with no owning task
  DELIVERABLE and no owning TOUCHES entry. Lexical seeds: "no task owns", "has no owning
  (task|deliverable)", "owned by no task", "unowned", "is in no task's TOUCHES", "absent from disk".
- **S. Un-hoisted shared interface or invariant.** A shape, vocabulary, key format or rule used by two
  or more tasks with no owning task and no bypass check. This is the class CLAUDE.md already names:
  "name the owner, the shared mechanism, and the check that fails on bypass".
- **F. A measurably false claim in tasks.md.** The plan asserts a line number, a test name, a call
  site or a spec citation that does not exist. Downstream agents trust tasks.md, so a false claim is
  strictly worse than a missing one.
- **C. A stated consequence in the repo's own hard-fail set.** Non-compiling emitted source, a
  gate-red file-size breach, a silent drop or silent default, identity loss, or Unity-observable
  behavior with no EditMode coverage. All four are CLAUDE.md rules, not judgement calls.

Everything else goes to the ledger: fidelity notes, "consider an explicit assertion", soft deliverable
phrasing, authorized de-scopes flagged for a human, informational entries, and risks the validator
itself scores as not-a-breach-today.

### How the filter scores on the sample

| | halts today | halts under the class filter |
| --- | --- | --- |
| HIGH (16) | 16 | 16 |
| MEDIUM (16) | 16 | 11 |
| LOW (16) | 16 | 4 |
| total | 48 | 31 (65%) |

It catches 15 of the 15 MED/LOW findings I judged consequential and ledgers 17 of the 17 I judged
safe, with one arguable miss: M7 (file-size headroom, 59 lines, not a breach) is ledgered by the
filter and did later materialize. That is the correct call anyway, because "not a breach today" is a
register entry, and the register is where it in fact ended up.

Compared to today: same halt behavior on HIGH, 35% fewer halting findings overall, and zero
consequential findings waved through in the sample. Compared to the proposal: the proposal ledgers 15
consequential findings that this filter catches.

## Question 4: the `isNit` predicate

**It exists.** `~/.claude/skills/tdd-pipeline/pipeline.workflow.js:690-696`, verbatim:

```js
const NIT_SEVERITY = /^(low|nit|nits|minor|info|informational|trivial|cosmetic|style|advisory)$/
const BEHAVIORAL_DETAIL = /(\bfail|\bregress|\bcrash|\bthrow|\bexception|data loss|\bincorrect|\bwrong\b|\bbroken\b|\bbreaks\b|null ?ref|does not (work|run|compile|match)|gate red|red gate|\buncovered\b|\buntested\b|missing test|no test\b|not covered|\bunreachable\b|\bsilently\b|\bdiverge)/i
function isNit(f) {
  const sev = String((f && f.severity) || '').trim().toLowerCase()
  if (!NIT_SEVERITY.test(sev)) return false
  return !BEHAVIORAL_DETAIL.test(String((f && f.detail) || ''))
}
```

Three things about it matter for the proposal, and all three are measured.

**1. It cannot be applied to the per-task validator at all.** `isNit` is called in exactly one place,
`scopeValidate` at `:712-713`, over `SCOPE_OUT.findings`. The per-task validator emits `VALIDATOR_OUT`
(`:106-119`), whose fields are `status`, `gatePassed`, `gateExitCode`, `behavioralEvidence`,
`diagnosis`, `lesson`. **There is no severity field and no findings array.** Feed a validator verdict
to `isNit` and `sev` is `""`, `NIT_SEVERITY` fails, and it returns `false` for every input. The second
proposal ("stop the per-task validator routing rework when the gate is GREEN and the finding is
cosmetic") cannot be implemented on top of `isNit` without first adding a severity or class field to
`VALIDATOR_OUT` and teaching `tdd-validator.md` to populate it. That is new classification, not reuse.

**2. Its keyword half is not fit to gate a routing decision.** Measured against the 507 plan findings:
**28 of 70 HIGH findings (40%) contain no `BEHAVIORAL_DETAIL` keyword**, as do 121 of 198 MEDIUM. A
predicate that misses 40% of the findings everyone agrees are serious is not a consequence detector,
it is a vocabulary matcher. Applied to the 135 real low-severity scope findings in the journals it
returns `true` for 82. Most of those 82 are genuinely trivial and exactly what the code comment
describes (a handoff doc missing its terminal `STATUS:` line, a stray `STATUS: READY` token mid-file,
a stale code comment). But in the first 14, three are not:

- "Latent cross-bucket seam interaction [...] newly made reachable by this feature; found by code
  reading, **unexercised** by any test (gate green)". `unexercised` is not in the keyword list;
  `uncovered` and `untested` are.
- "Deliverable-named EditMode test `unity-gate/Assets/GateTests/InstanceAddReconcileTests.cs` [...] is
  **absent from disk**". A deliverable clause that did not ship, classified as a nit.
- "The surface scan enforcing the D4 'one mechanism' invariant is one-directional [...] a future
  authoring member declar[ing] [...]". A bypassable invariant guard, classified as a nit.

That is roughly a 20% false-negative rate on the class of findings that matter, driven entirely by
word choice.

**3. It has never fired in this repo.** Zero `flaggedNits` entries appear in any of the 88 workflow
journals. The 215 scope findings recorded all predate the predicate, so this is not evidence it is
broken; it is evidence it is **unproven in production** and must not be treated as a validated
component to build a routing gate on.

Verdict on the claim: the predicate exists, it is correctly scoped to a cheap-collection decision on
scope findings where a wrong call is recoverable, and it is **not** fit as-is for gating routing on
per-task validator verdicts.

## Question 4b: what the per-task routing proposal would actually wave through

The second proposal targets a measured population of 21 validator verdicts (of 480 total, 4.4%) that
routed rework while `GATE_PASSED: yes`. Deduplicating two double-recorded events gives 19 distinct
events. Judged individually:

Genuinely cosmetic (3):

- `m-ui-recttransform/b3-t3`: a stale comment block in `FlagApplyTests.cs` naming a deleted symbol.
  Two comment restatements, zero executable change.
- `serialization-fidelity/b3-t2`: a missing class-doc paragraph that was deliverable clause 4.
- `uniform-value-descent/b3-t4`: a handoff payload wording fix in `code-writer.md`, no production
  change (though it exists to stop b4-t2 consuming one of two divergent reason strings).

Real, and several severe (15):

| feature / task | what routing caught behind a green gate |
| --- | --- |
| m-ui-recttransform b3-t5 iter4 | the configure-lambda remove branch deletes the whole lambda argument, so a routine Inspector component-delete under auto-sync silently deletes a component the user kept, or a whole child GameObject, from the builder source |
| m-ui-recttransform b3-t5 iter3 | `IsChainedNonStatementCall` misclassifies a component call, so a `RemoveStatement` deletes the enclosing node statement and the GameObject with it |
| m8-unityevents b1-t3 iter2 | seven static-argument forms and two listener-target forms that the recognizer accepts and `BuilderParser.Parse` then kills with an uncaught exception; plus a convergence fix shipped as an opt-in, against CLAUDE.md's opt-in rule |
| m8-unityevents b1-t3 iter3 | `callState: UnityEventCallState.RuntimeOnl` (an ordinary typo under live sync) escapes the recognizer and kills `BuilderParser.Parse` with an uncaught `InvalidOperationException` |
| m8-unityevents b1-t2 iter1/iter2 | SB1003's published Title and `FlatShapeRecognizer.cs:278`'s message still tell the user a component closure accepts only `.Set(...)` after this task made `.OnClick(...)` legal. The Title ships to `api.json` and renders on the marketing site |
| serialization-fidelity b1-t1 | a test bent `Index.IsDefault` from "I have no default for this field" into "this field is at its default, omit it", a data-loss semantics on the emit path |
| m-ui-recttransform b3-t5 iter5 | the mandated EditMode file was never created; the fix's most damaging paths ship untested |
| m-builtin-resources b2-t2 | two spec-named deliverable tests do not exist; no test anywhere drives a resolved built-in ref through the Materializer |
| m5-cross-object-references b5-t2 | a BEHAVIORAL:yes deliverable with zero captured evidence, its only proof deferred to a task that had not run |
| serialization-fidelity b2-t1 | the C1 headline scenario is exercised by no test in the repo |
| excluded-field-one-way-report b1-t1 | the report contract proven at one site only, which the task's own ASSUMPTIONS ruled out |
| _superseded-m8 b1-t2 | `AssembleIncremental` invalidates GameObjects but not their components, so on the shipped auto-sync path a node re-resolves against a stale cached goid |
| _superseded-m8 b1-t2 iter3 | `MemberSpelling.cs:25` promises `StringComparer.Ordinal` over a dictionary built with the default `ValueTuple` comparer |
| _superseded-m8 b1-t1 | an over-asserting test forced a `LazyReadOnlyList<T>` workaround into production `ValueWalk.Fold` |
| reference-writes b2-t1 | a stale comment from this task's own commit, plus the `docs/open-defects.md` register being unreferenced and looking truncated |

Two data-loss defects and two parser crashes are in that list. Every one of them was found while the
gate was green, which is the whole reason the validator's review step exists. **"Gate is GREEN" is
worthless as a safety signal here by construction:** it is true for 100% of this population.

The only defensible version of the second proposal is: give `VALIDATOR_OUT` an explicit
`findingClass` field, and skip routing only for the classes that are provably non-executable
(`stale-comment`, `doc-only`, `handoff-payload`). That would have skipped 3 of 19 and routed 16. It
would not have been driven by the gate colour at all.

## Question 5: the safe version of this change

### The real cause of the 57%

The halt rate is not caused by over-strict severity handling. It is caused by there being **no repair
path**. `pipeline.workflow.js` calls the decomposition validator exactly once (`:400-403`), pushes its
issues into `planIssues` if `valid === false`, and at `:415` logs, records a lesson, and `return`s.
There is no loop back to the intake/deconstruct agent. A plan with one fixable gap costs the whole run.

And the gaps are overwhelmingly fixable in the plan text. Measured across the 70 HIGH findings:

- **70% (49/70) state an explicit fix** ("Fix:", "FIX in the plan:", "Remedy:", "Required:").
- **61% (43/70) state a fix that is a `tasks.md` edit**: add a dependency edge, add a file to TOUCHES,
  add a deliverable clause, name the owner, hoist a shared interface into its own task.

The halt throws away a repair the validator has already written. That is the highest-value change
available, and it does not require trusting a severity label.

### Recommended change, in order of confidence

**1. Add a bounded plan-repair loop (do this one first).** On `valid: false`, hand `planIssues` back to
the intake/deconstruct agent with an instruction to revise `tasks.md` and return, then re-validate. Cap
at 2 repair passes; halt on the third with the findings as today. Every constraint that makes this safe
already exists: the validator is independent and read-only, the DAG and TOUCHES checks are
deterministic and re-run on the revised plan, and nothing is committed at this stage. m8-unityevents
halted 18 times and uniform-value-descent 12 times; those are hand-repair cycles a bounded loop
absorbs.

**2. Replace the severity gate with the class filter (section on question 3).** Halt on D/O/S/F/C;
ledger the rest. This is what the proposal was reaching for, expressed in the variable that actually
predicts consequence. On the 48-finding sample it halts 31 instead of 48 and wave-throughs nothing
consequential.

**3. Attach ledgered findings to the owning task, not to a ledger.** The proposal's "record
medium/low to a ledger" half is already disproven in this repo. `plan-review.md` MED-3 was ledgered,
then re-recorded verbatim in `docs/m8-measured-defects.md` by a later agent with the note *"plan-review.md
is not read by the task agents, so this register is its channel"*, and `docs/open-defects.md` was found
to be referenced by nothing in the repo while holding nine unassigned defects. A finding a downstream
agent never reads is indistinguishable from a discarded one. The mechanism that works here is the one
the repo already uses when it works: put the finding in the owning task's DEFECTS/ASSUMPTIONS block,
which research is required to attack adversarially, and it arrives in front of the agent who can act
on it.

### Implementation warning, non-negotiable

The harness's own deterministic `planIssues` (`:350` unknown dependency, `:358` dependency cycle,
`:394` TOUCHES overlap) are pushed as **plain strings with no `high:`/`med:`/`low:` prefix**. Only the
validator's issues get a prefix, at `:404`. A naive "halt only on HIGH" implemented as
`planIssues.filter(i => /high/i.test(i))` silently drops all three deterministic checks. Those are the
checks with a perfect hit rate, and the script's own comment says so: *"a deterministic check has a
perfect hit rate where the prose did not"*. Whatever filter ships must be applied to the validator's
issues only, and the deterministic issues must halt unconditionally.

### What not to do

- Do not gate on the gate's colour. In the 19-event population where the second proposal applies, the
  gate is green in 100% of cases by construction, including two data-loss defects and two parser
  crashes.
- Do not reuse `isNit` for routing. It has no input to read on `VALIDATOR_OUT`, it misses 40% of HIGH
  findings on its keyword half, and it has never fired in production here.
- Do not treat "record it somewhere" as a mitigation. Two ledgers in this repo were measured to be
  read by nobody.
