# Why a feature costs millions of tokens — investigation, 2026-08-07

Status: **PARTIAL.** Four investigations were launched; two completed, two died on the session
limit. Their findings are below verbatim in substance. The two that died were the pipeline design
review and the historical regression hunt — the two that would have answered "when did this change
and what changed" — so the biggest question is still open. Resume there.

Context for whoever picks this up: spec 36 (`uniform-value-descent`) cost ~7.9M tokens and shipped.
`specs/09-m8-unityevents.md` has cost ~12.8M and is ~20% done. Combined ~21M over two days. The
owner reports features previously completing in about an hour.

---

## What the two completed investigations agree on

Both, independently, landed on the same root cause, and it is **not** the things that look
expensive.

**Not the bottleneck:**
- **The gate.** Median full gate incl. Unity EditMode: **69 s** (n=74, from `gate-output.log`
  mtimes). Core-only median 23 s. ~5.8 gate verdicts per task = **~7 minutes of gate wall-clock per
  task**. Against millions of tokens this is noise.
- **File size.** Of 226 production files, **4 exceed 800 lines and none exceeds 1000**. It has
  blocked exactly one file (`SourcePatchApplier.cs`, 941).
- **Deliverable length.** m8's average DELIVERABLE is 2,378 bytes vs uvd's 1,273, but uvd's own
  longest deliverable (4,373 bytes) ran clean in one cycle. Text length is not the predictor.

**The actual cost drivers, ranked:**

### 1. `ValueNode` is an open union dispatched by ~14 catch-all switches across 5 layers and 2 assemblies, with no committed map of where those switches are
- A new value kind is a **16-18 file cross-layer registration**. Measured spread: `Unsupported` 23
  files / 52 sites, `AssetRef` 18/49, `Primitive` 17/60, `ObjectRef` 16/37.
- **13 of the 23 entries in `docs/m8-measured-defects.md` are "the file that must change is in no
  task's declared TOUCHES".** The dominant M8 failure mode is not writing code wrong. It is not
  knowing which files a change reaches.
- `SourceExpr.LeafLiteral` still ends in `_ => throw new NotSupportedException(...)` and has **no
  `ObjectRef` arm at all**, which is why `ComponentReconciler.RenderFieldValue` pre-substitutes
  every `ObjectRef` with `Unsupported`. That gap had to be found by hand.
- Spec 36 fixed *container* descent (3 kinds) and explicitly ruled out the visitor
  (`specs/completed/36:356-358`). **It bought a Roslyn token scan where a compiler error was
  available.**

### 2. The validator routes on code-review opinion against a green gate
- **8 of 12 validator legs read routed with `EXIT_CODE: 0` and `GATE PASS` quoted in the same
  document.** Three of those routes concerned comment/title/doc-comment prose with explicitly zero
  behavioural effect.
- One full four-agent cycle (research → test → code → validate) was spent editing **one XML doc
  comment** that listed 2 of 4 enum kinds. Another on a comment claiming `StringComparer.Ordinal`
  where the default tuple comparer was used — the validator itself wrote "BLOCKING on process, not
  on behavior".
- **A route re-runs all four agents even for a one-line text fix.** test-writer iteration 6 exists
  purely to say "no test needed for a doc comment".

### 3. Handoff context grows without bound and is re-injected every cycle
- `history.md` archives every prior agent's full markdown. Worst: **167,817 bytes** on
  `_superseded-m8-plan2/b1-t2`. `tasks.md` reached 98,762.
- Context floor before an agent opens a single source file: **~30k tokens** typical, **~58k tokens**
  late-cycle. Times ~20 invocations = **~1.1M tokens of input on one task**, just re-reading
  handoffs.
- The blueprint is **re-emitted rather than referenced** at four stages per iteration — research
  FILES_EDIT, code-writer FILES, validator deliverable check, then history archives all of it.
- Corroborating partial from the regression agent before it died: *"per-run agent counts went DOWN
  while tokens went UP"* — i.e. cost growth is per-agent context, not more agents. **Worth
  confirming first thing next session.**

### 4. Whack-a-mole convergence
Both m8 tasks show the identical shape: validator names two instances of a class, code-writer fixes
exactly those two, next validator finds the third. Three rounds on `m8/b1-t2` were all "a grammar
message string not updated", each round finding one more site. Both validators diagnosed this
themselves and one proposed the right fix: a mechanical scan so the class is closed by the build
rather than by a validator probe.

### 5. Guards that cost more than they caught
- 12 source-scanning guards; the five named total **1,566 lines**, re-parsing 206 production files
  across 6 full-tree scans.
- `ModeArgBypassScanTests`' allowlist was **seeded with two files that did not exist**, while its
  token list matches the exact spellings (`m_Mode`, `m_Arguments`) the adapter's mandatory
  `FindPropertyRelative("m_Mode")` must write. It **cost at least two M8 cycles and caught zero
  defects**.
- Spec 36's pin apparatus — `PinnedTestBaselineTests.cs` + `.json` + completeness test, **359
  lines** — was created in `2a72f72` and deleted in `6902f31`, inside one feature, to prove a
  property `git diff --stat` answers for free.
- Exact-count inventories (`ObjectRefDescentScanTests`, `ValueContainerDescentScanTests`) match
  two-sided, so **any refactor inside an already-blessed member fails the gate**.
- Four files re-implement `RepoRoot([CallerFilePath])`; `EnclosingMember` and `ProductionFiles` are
  copy-pasted four times each. Already recorded in the defects register; never fixed.

### 6. Plan churn
Four plan directories for one spec, 2,859 lines of `tasks.md` for a 352-line spec, three discarded.
Plan-review finding counts went **up** across rerolls (13 → 32). Roughly **2.3M tokens produced no
code**.

---

## Headline measurement

Spec 36: **~7.9M tokens produced 367 changed lines of production code** (~21,000 tokens per line).
The full diff was 28 files, +2769/−111.

Baseline that worked: `uniform-value-descent`'s successful run had **11 of 13 tasks at exactly one
cycle per stage, zero escalations**. m8's comparable runs produced 2 GREEN tasks each. The
difference is not the codebase — same repo, same gate, same week.

---

## Recommendations from the completed investigations

Ordered by impact per effort. R1 and R3 attack the real cost; R2 removes a tax the wrong response
introduced.

**R1. Give `ValueNode` an exhaustive visitor; delete two scan tests.**
Add `Accept<T>(IValueNodeVisitor<T>)` to `ValueNode.cs` (12 one-liners), convert the ~14 catch-all
switches, then delete `ValueContainerDescentScanTests.cs` (488 lines) and
`ObjectRefDescentScanTests`' inventory. ~14 files, one day, compile-error-driven. Makes the
remaining 80% of M8 a build error instead of a hand-found defect.

**R2. Replace every exact-count inventory with a file-level permission list.**
Drop the `Count` field and two-sided comparison; delete the pinned exact-set assertions
(`ModeArgBypassScanTests:228-234`, `ListValueEmissionTests:526`). ~2 hours, zero production change.
Eliminates all four guard-file edits in M8's in-flight diff, and unblocks
`UnityEventWriter.cs`/`UnityEventReader.cs`, which today **cannot be allowlisted without deleting a
test**.

**R3. Commit a generated value-kind registration map.**
A tool emitting `docs/value-kind-sites.md`: per `ValueNode` kind, every production file and member
naming it, from the Roslyn walk the guards already implement. Feed it to `task-deconstruct` so
`TOUCHES` is *derived, not guessed*. ~150 lines. Attacks the 13-of-23 defect class directly, and
that is re-derivation rather than execution — the single highest-leverage change against the 12.8M
figure.

**R4. Split `SourcePatchApplier.cs` (941/1000) before touching it, not during.** One hour.

**R5. Stop regenerating plans; converge the one you have** via `tasksReady: true`. Already written
in `HANDOFF.md:88-101` and not followed.

**From the forensics, additionally:**
- **Cap what a validator may route on.** A green gate plus a prose complaint should be an advisory
  nit, not a route. Three of the routes examined were doc-comment text.
- **Do not re-run four agents for a one-line fix.** Route directly to the code-writer when the
  finding names a single file and no test change.
- **Bound `history.md`.** It reached 167KB and is re-read every cycle.

---

## Not established (the two dead investigations)

- **When the regression started, and whether a specific harness commit caused it.** `~/.claude` is
  a git repo; the harness's own evolution is reconstructable. Unanswered.
- **A quantified per-phase token breakdown** (intake / research / test / code / validate / scope).
- **Whether cost is superlinear in spec size**, which would argue for capping spec size rather than
  tuning the harness.
- The one partial signal: agent counts fell while tokens rose. Confirm before acting on it.
