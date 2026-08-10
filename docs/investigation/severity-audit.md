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

