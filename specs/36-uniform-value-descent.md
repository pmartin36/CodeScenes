# Spec 36: Uniform value descent

## Goal
Every pass that traverses a `ValueNode`'s container structure routes through `ValueWalk`. Adding a new
`ValueNode` kind then means editing `ValueWalk.cs`, plus a compile error at each call site that must
decide how the new kind renders. No pass can silently skip a kind.

## Current state (measured 2026-08-05)

`SceneBuilder.Core/Model/ValueWalk.cs` documents itself as THE recursion over a value's container
structure, and states that every transforming pass routes descent through `Map` "instead of
re-deriving `switch (node) { Nested => ..., List => ... }` for itself", so that "none of them can ship
having silently skipped a container kind". It offers `Map` (node in, node out), `Any` (predicate) and
`Enumerate` (pre-order with paths). `ObjectRefValues`, `NestedValueEmission`, `ListValueEmission`,
`ComponentReconciler` and `SerializedEnumNormalizer` route through it.

Five passes do not:

| Pass | Shape | Catch-all |
| --- | --- | --- |
| `SceneBuilder.Core/Lowering/AssetRefLowering.cs:72-85` | transform | `default: return node` (`:82`) |
| `SceneBuilder.Core/Validation/PlanningValidator.cs:151-188` (`WalkAssetValue`) | transform, side-effecting | `default:` (`:186`) |
| `com.codescenes/Editor/BuiltinRefValidator.cs:136-158` | transform, adapter | second copy of the `AssetRefLowering` walk |
| `com.codescenes/Editor/SerializedFieldBridge.cs` | transform, adapter | container cases inline |
| `SceneBuilder.Core/Reconcile/SourceExpr.cs:60-110` | renderer, returns `string` | `_ => throw new NotSupportedException` (`:109`) |

`AssetRefLowering.LowerNode` is a strict subset of `Map`: visit one kind, recurse into `List` and
`Nested`, treat everything else as a leaf. The first four are mechanical migrations.

`SourceExpr.ValueNodeLiteral` is not. It returns a `string`, and `Map` is node-in/node-out, so there is
no shape in `ValueWalk` for it to route through. It is the one pass that needs a new primitive rather
than a migration.

Removing the catch-all arms and letting the compiler flag the gap is NOT available. C# does not treat
this hierarchy as closed: an `abstract record` base with `sealed record` cases, switched over with every
case covered and no discard arm, still reports `CS8509` ("does not handle all possible values ... the
pattern '_' is not covered"). A discard arm is therefore mandatory, which is the silent catch-all this
spec removes. Enforcement comes from routing plus a scan test, not from switch exhaustiveness.

## Cross-cutting invariant

**Container descent exists in exactly one place.** `ValueWalk` owns it; every other pass calls in. This
has ONE owning deliverable (D1, which also owns the guard in D5) and every migration below calls that
one implementation. Do not restate the rule as per-task guidance.

**Behavior preservation is the second invariant, and it has one MECHANICAL check, not a reminder.**
Every migration in D2-D4 is behavior-identical by construction. The proof for each is that the migrated
pass's EXISTING tests stay green **unmodified**: a migration that needs a test edited has changed
behavior and is wrong. That proof is established by diff, which imposes three requirements:

- Characterization and descent tests go in **NEW files**, named `<Pass>DescentTests.cs`. No task may add
  to, or otherwise edit, a test file that already exists for a pass it migrates. Once a task edits such a
  file, "unmodified" can no longer be established, and a migration that quietly relaxes one existing
  assertion would pass its own gate. Each task names its concrete new file rather than declaring a test
  DIRECTORY, so that two tasks writing tests in the same directory have provably disjoint file sets and
  need no ordering edge between them.
- The pinned file list and its baseline are produced FIRST, by D0, not derived at check time. A list
  produced after the migrations cannot constrain them: a migration would be free to edit a file it
  judged out of scope, only for the list to later enumerate it and fail the gate after that bucket had
  already committed. D0 enumerates the files and captures the baseline; D5 consumes both.
- A characterization test must be shown to pass BEFORE the migration it pins. This applies to ALL FIVE
  migrations without exception, including the two adapter ones whose evidence is an EditMode run rather
  than a Core run: an EditMode characterization test written after its migration is exactly as worthless
  as a Core one. The evidence contract is the same in every case and is named once here so no deliverable
  restates it differently: the task's test-writer handoff carries `RED_CONFIRMED: no` plus the
  pre-migration run output showing the new test GREEN against unmodified production code, as substituted
  evidence. Every migration's deliverable states it. A test written after the migration to match the new
  code is otherwise indistinguishable from one that pins actual behavior, which is the entire point of
  writing it first.
- That contract is enforced STRUCTURALLY, by build order and by the gate, not by anyone's diligence and
  not by a handoff field. **Every characterization test is written and committed in D1c, a bucket that
  lands BEFORE any migration and whose diff touches ZERO production files.** The gate runs green on that
  commit. That commit is therefore proof, checkable after the fact by anyone with `git`, that those tests
  passed against unmodified production code: no production file changed in it, so there was nothing else
  for them to pass against.

  This replaces the earlier framing, which named the pipeline's task validator as the enforcer. That was a
  category error: the validator's behavior lives in the harness, not in this repo, so no deliverable here
  can change what it checks, and a spec that depends on it has no mechanism at all. Build order is
  something this spec CAN determine. A handoff field remains useful for the writer; it is not the check.

## Build order

1. **D0** Pin the baseline. First, because every later task is constrained by it. D1 depends on it too:
   the baseline is "pre-feature" only if nothing else has landed when it is captured.
2. **D1** `ValueWalk.Fold<T>`, after D0. Nothing else depends on the renderer path until this exists.
   `ValueWalk.cs`'s post-D1 content hash IS recorded into D0's pin data, but D1 does not write it: a
   single appender at the end of D1c owns every feature-created pin entry. Three tasks appending to one
   data file with no edges between them is a lost-append hazard whose failure mode is silent, and a
   dropped append leaves a file unpinned with no error. The protective property is unchanged, because
   every migration depends on that appender.
3. **D1c** Characterization tests for all five passes, in ONE bucket that commits before any migration.
   Its diff touches zero production files, and the gate is green on that commit. That is the whole
   mechanism behind "written before the migration it pins"; see the cross-cutting invariant.
4. **D2** Core transform passes (`AssetRefLowering`, `PlanningValidator`).
5. **D3** `SourceExpr` onto `Fold` (needs D1).
6. **D4** Adapter passes (`BuiltinRefValidator`, `SerializedFieldBridge`), with EditMode proof.
7. **D5** The guard, and only then the retirement of D0's pin. It fails until D2-D4 have landed, and its
   retirement step runs strictly AFTER the Unity confirmation checklist above, not merely in a later
   bucket. The checklist is the one piece of work directed to reuse the pinned `BuiltinRefGateHarness.cs`
   family, so it is exactly the case the pin polices; retiring the pin concurrently with it would remove
   the check while its most likely violation is being written. "Final act" means last in the feature, not
   last in its own bucket.

D2, D3 and D4 are independent of each other once D0 and D1 exist.

## D0 — Pin the baseline

Produces the two shared inputs every migration is judged against, before any migration is written.

Deliverables: the ENUMERATED list of pre-existing test files covering all five migrated passes,
committed into the repo as data rather than left to later judgement.

The list is built by SEARCHING THE TREE **and** must be a SUPERSET of the floor named here. These are
cumulative requirements, not alternatives: the search exists because the floor is incomplete, and the
floor exists because the pinning check is self-referential (it verifies only the files its own list
names, so a short list yields a green gate and a weaker proof for exactly the passes this feature
migrates). The floor is:

`SceneBuilder.Core.Tests/` — `AssetRefLoweringTests.cs`, `AssetRefDiffTests.cs`,
`AssetRefMaterializeTests.cs`, `AssetRefPlanTests.cs`, `AssetRefReconcileTests.cs`, `AssetRefValueTests.cs`,
`PlanningValidatorTests.cs`, `SourceExprTests.cs`.
`unity-gate/Assets/GateTests/` — the `RoundTripBuiltinRef*Tests.cs` family, `BuiltinRefGateHarness.cs`,
`HiddenSerializedFieldSyncTests.cs`, `SerializedFieldNativeEnumReadTests.cs`, `RoundTripNestedValueTests.cs`.

Omitting any of them requires a written justification recorded with the list. Do not read "derived by
searching the tree" as a prohibition on reading this floor.

Alongside the list, D0 records the pre-feature BASELINE: the base commit SHA plus a per-file content
hash, written into the repo as `SceneBuilder.Core.Tests/PinnedTestBaseline.json`, with the check itself
as `SceneBuilder.Core.Tests/PinnedTestBaselineTests.cs`. Both are named concretely here because a task's
declared file set drives gate scoping and write serialisation, and a placeholder like
`<pinned-baseline data file>` defeats both. A hash recorded later, after migrations have committed, would pin the
post-migration content and prove nothing.

D0 also ships the pinning check itself, as an xunit test in the existing Core suite (the sibling
`ObjectRefDescentScanTests` is the pattern). It compares each enumerated file's current hash against the
recorded baseline and fails naming any file that differs. It does NOT edit `verify.sh`: the gate script
is this feature's ground truth and is not modified by the run it judges.

D0 ships a SECOND check, for the same reason and in the same form: `SceneBuilder.Core.Tests/GateTestMetaFileTests.cs`
asserts that every `*.cs` under `unity-gate/Assets/GateTests/` has a sibling `.meta`, walking to the repo
root via `[CallerFilePath]` exactly as `ObjectRefDescentScanTests.cs:54-69` does. All 115 gate test files
satisfy this today and nothing asserts it; `verify.sh` has no such check and the only `.meta` assertions
in the tree (`AnalyzerToolkitInjectionTests.cs:214-216`) assert the opposite property about DLLs.

`GateTestMetaFileTests` ASSERTS ONLY. It never creates a missing `.meta`. Measured today the tree is
already clean (115 `.cs` and 115 `.cs.meta` under `unity-gate/Assets/GateTests/`), so the branch does not
fire; if it ever does, D0 REPORTS it rather than fixing it, because writing under `unity-gate/**` would
put a slow-glob write inside a fast-gated Core task and would not be serialised against the later tasks
that add files to that same directory.

This check is a MECHANISM and it is deliberately not a rule repeated per task. A missing `.meta` means
Unity never imports the file, so the test never runs, the gate is green, and the pre-migration evidence
run is green too, because a test that never runs also passes. Every failure mode here looks like success,
which is precisely why prose cannot discharge it. It lives in the Core suite so it runs in Layer 1 and
therefore also protects fast-gated tasks that never reach the editor suite. Unlike the pinning check it
is NOT feature-scoped: it stays permanently, since the hazard is permanent.

**Lifetime: feature-scoped.** The check exists to prove THIS refactor changed no existing test, and that
claim is only meaningful against this feature's baseline. Left permanent it would fail `./verify.sh` on
every future legitimate edit to those files with no re-baseline path. D5 retires it, as its final act,
after running it green.

## D1 — `ValueWalk.Fold<T>`

A fold for passes that consume a value into something other than a value. One delegate per container
kind plus a leaf delegate:

```csharp
public static T Fold<T>(
    ValueNode node,
    Func<ValueNode.List, IReadOnlyList<T>, T> list,
    Func<ValueNode.Nested, IReadOnlyList<KeyValuePair<string, T>>, T> nested,
    Func<ValueNode, T> leaf);
```

Children are folded before their parent, so `list` and `nested` receive already-folded results and
decide only how to combine them.

The per-container-kind parameter list is deliberate and is the enforcement mechanism for containers: a
new container kind adds a parameter, so **every** `Fold` call site fails to compile until its author
states how that kind is rendered. Transforms do not need this because `Map` rebuilds a container
structurally and a new kind is handled for all callers by editing `ValueWalk.cs`; a renderer has no
such default, because each container needs its own syntax.

Deliverables: `Fold` exists with that contract; folding is CHILDREN-BEFORE-PARENT (not merely
depth-first, which a pre-order fold also satisfies, and which would hand a container's delegate unfolded
children) and preserves list order and nested member order; a test folds a value nested at least three
deep and asserts both the result and the order in which the delegates ran.

D1 also owns **sufficiency for all five passes**, and it is a checkable deliverable rather than a claim:
D1 records a pass-to-primitive mapping naming, for each of the five, which primitive it will route
through and what context it needs. The mapping SHIPS as one line per pass in `ValueWalk`'s doc comment,
so it is reviewable as code rather than as a handoff assertion, and the two `SerializedFieldBridge.WriteProperty`
answers are exactly what D1's own acceptance tests below assert. Two affordances are known to be missing today and D1 supplies
whatever covers them, because a later task blocks without them:

- **List-item index.** `ValueWalk.Map` calls `descend(list, context, null)` (`ValueWalk.cs:88`), so a
  list item's key is always null and its index is never supplied. `SerializedFieldBridge` derives each
  child `SerializedProperty` from its parent via `GetArrayElementAtIndex(i)` (`:343-372`) and cannot
  work without the index.
- **Parent-before-children order with context.** The same site resolves a child property off its parent,
  so it needs the parent visited first and the parent's resolved context handed down. `Fold` is
  children-first by construction and supplies no context.

Both affordances are driven by `SerializedFieldBridge.WriteProperty`, and the mapping fails review if it
leaves either question unanswered for that site. `BuiltinRefValidator` is NOT assumed to need them:
`ValueWalk.Enumerate` already yields `parent + "[" + i + "]"` and `parent + "." + memberKey`
(`ValueWalk.cs:151-182`), which is the same path `BuiltinRefValidator.ValidateField` builds
(`BuiltinRefValidator.cs:136-158`). D1 tests that site against `Enumerate` before concluding a new
affordance is required, so the primitive is not widened on an assumption.

Whatever D1 ships is proved by acceptance tests, not by the mapping asserting sufficiency: descending a
two-item `List` yields item indices 0 and 1, and a context derived on a parent reaches each of its
children. Without those, the affordance's only proof would be an adapter test one bucket later, after D1
has committed.

D2-D4 do NOT edit `ValueWalk.cs`. They are independent of each other, so concurrent edits there would not
be serialised by any dependency edge. A migration that finds the shipped affordance insufficient escalates
by re-opening D1 rather than extending the primitive locally.

That is enforced, not requested. Neither existing check can see a violation: D0's pin covers pre-existing
TEST files only, and D5's token scan exempts `ValueWalk.cs` by name, so a locally widened or clobbered
primitive is invisible to both. **D1's final act therefore records `ValueWalk.cs`'s post-D1 content hash
into D0's pin data**, putting the file under the same check every migration bucket already runs. A D2-D4
task that edits it fails the gate, naming the file, at its own task rather than after its bucket has
committed. D5 retires this entry with the rest of the pin, since the constraint is feature-scoped: after
the feature, editing `ValueWalk.cs` is ordinary work.

## D2 — Core transform passes route through `Map`

`AssetRefLowering.LowerNode` and `PlanningValidator.WalkAssetValue` are deleted and replaced by calls to
the `ValueWalk` primitive each one's shape requires, supplying only that pass's own per-node decision.
The requirement is that the hand-rolled container switch is GONE and descent belongs to `ValueWalk`, not
that a specific primitive is used: `AssetRefLowering` transforms a value and `Map` fits it directly,
while `WalkAssetValue` is side-effecting and builds no value, so `Any`, `Enumerate` or `Fold` may suit it
better. No `switch` over `ValueNode.List` / `ValueNode.Nested` remains in either file.

Proof: every existing test of both passes stays green unmodified, plus a new test per pass, in a NEW
file, that a value reachable ONLY through container descent is processed (for `AssetRefLowering`, an
`AssetRef` with an empty `Guid` nested inside a `List` inside a `Nested` is lowered to a real `Guid`).

## D3 — `SourceExpr` renders through `Fold`

`SourceExpr.ValueNodeLiteral` is rebuilt on `Fold<string>`. Its `List` and `Nested` arms become the
corresponding `Fold` delegates; the remaining per-kind literal rendering becomes the leaf delegate.

The `NotSupportedException` survives, narrowed: it fires for an unrecognized **leaf** kind only.
Container kinds can no longer reach it, because a container is structural in `Fold` and a new container
kind breaks the call site at compile time instead.

Proof: existing source-rendering tests stay green unmodified; a test asserts the rendered output for a
value containing both a `List` and a `Nested` is byte-identical to the pre-migration rendering.

## D4 — Adapter passes route through `ValueWalk`

`BuiltinRefValidator` (whose walk is a second copy of `AssetRefLowering`'s) and `SerializedFieldBridge`
route their container descent through `Map` or `Fold` as their shape requires.

Because both are under `com.codescenes/`, each carries EditMode coverage in `unity-gate/Assets/GateTests/`
proving behavior is IDENTICAL after the migration against a live editor: `BuiltinRefValidator` still
validates the builtin references it validates today, and `SerializedFieldBridge` still reads the same
fields to the same values from a real scene. Existing EditMode tests for both stay green unmodified.

D4 owns `SerializedFieldBridge`'s SECOND descent as well, and owns the decision about it, because D4 owns
the file. `ContainsUnsupported` (`SerializedFieldBridge.cs:139-144`) recurses over `ValueNode.List` items
and DELIBERATELY does not descend `Nested`. Routing it through `ValueWalk.Any` would make it start
recursing into `Nested`, which is a behavior change and therefore forbidden by this spec. The expected
resolution is to leave it as it stands and give it a D5 inventory entry recording that reason. D4 makes
that call and writes the reason; D5 runs after D4 has committed and cannot make a scope decision about a
file it does not own.

The reason has a NAMED LOCATION so D5 consumes it verbatim rather than re-deriving its own wording: D4
writes it as a code comment on `ContainsUnsupported` itself, extending the explanatory block already at
`SerializedFieldBridge.cs:134-138`, and echoes the exact inventory-entry string in its code-writer
handoff. "Left somewhere consumable" is not a location.

## D5 — The guard

A source-scanning test MODELLED ON the shipped `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs`
(a new file following that file's pattern, not an addition to it): no file under `SceneBuilder.Core/**` or `com.codescenes/**` may reference a container kind except
`ValueWalk.cs` itself and entries in a declared inventory, each carrying a written reason. A (file,
member) pair absent from the inventory allows ZERO matches.

**The match is a Roslyn syntax-only scan for a `ValueNode.List` or `ValueNode.Nested` qualified-identifier
TOKEN in ANY position** — the approach `ObjectRefDescentScanTests.RawObjectRefTokens` already uses
(`SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs:104-130`). It is NOT a set of textual prefixes
such as `case ValueNode.List` / `is ValueNode.Nested`. Those four miss switch-EXPRESSION arms, which is
how much of the real descent in this tree is written and how the pass D3 migrates is written today:
`SourceExpr.cs:98-99,101` (`ValueNode.List { Items.Count: 0 } =>`, `ValueNode.List list =>`,
`ValueNode.Nested nested =>`), `SerializedFieldBridge.cs:142`, `ListValueEmission.cs:95,97`,
`NestedValueEmission.cs:234`. A prefix-matching guard would report a materially smaller inventory than
reality and would let a reintroduced hand-rolled descent through unseen, which voids the deliverable
this feature's enforcement rests on.

The test names the offending file and the pattern it matched, and states the remedy (route through
`ValueWalk`, or declare with a reason).

**D5 edits no production code.** Its only disposition for a scan match it did not expect is a declared
inventory entry carrying a written reason. "Route it through `ValueWalk`" is not available to D5: the
passes already routed are out of scope here, and a production edit from the scan task would also bypass
the per-task gate scoping, since a `com.codescenes/**` touch would be undeclared. A match that genuinely
warrants migration is reported, not fixed.

The inventory is derived by SCANNING THE TREE, not from this spec's site list or the decomposition's:
at least one legitimate site sits outside both (`SceneBuilder.Core/Materialize/Materializer.cs:296,317`,
`is ValueNode.List list && IsReferenceList(list)`, which these patterns match and which needs an entry
with a written reason). Expect others.

D5 also CONSUMES D0's pinning check rather than deriving it: it runs that check green over D0's
enumerated list and baseline, then retires it (removing the check and its baseline data) as its final
act, per the feature-scoped lifetime stated in D0. Because the check and its data are deleted, the claim
that it ever ran green is unverifiable afterwards unless it is captured: D5 records that green run's gate
output in its validator handoff BEFORE removing anything. Retiring an unrecorded check proves nothing.

## Test plan

- `Fold` over a three-deep value: result correct, delegates run depth-first, list and member order preserved.
- `Fold` over each leaf kind reaches the leaf delegate exactly once.
- `AssetRefLowering`: an empty-`Guid` `AssetRef` inside `List` inside `Nested` lowers to a real `Guid`.
- `PlanningValidator`: a diagnostic raised for a value reachable only through container descent.
- `SourceExpr`: rendering of a `List`+`Nested` value is byte-identical to pre-migration output.
- Every pre-existing test of all five passes passes UNMODIFIED, proved by D0's pinning check against
  D0's recorded baseline rather than by inspection.
- The D5 scan matches a switch-EXPRESSION arm (`ValueNode.List list =>`), not only `case`/`is` forms.
- Each characterization test is recorded GREEN against unmodified production code BEFORE its migration.
- The D5 scan fails when a hand-rolled container switch is reintroduced, and names the file.

## Unity confirmation checklist

Captured AFTER every migration in D2, D3 and D4 has landed. Each item exercises a migrated Core path
(asset resolution is D2's `AssetRefLowering`, field-to-source is D3's `SourceExpr`, plan ops are D2's
`PlanningValidator`), so evidence captured alongside those migrations rather than after them proves
nothing about the migrated code. This is the same wrong-time failure the pre-migration evidence contract
guards from the other direction.

1. Open a scene using builtin resources and project asset references, Materialize. **Expected:** assets
   and builtins resolve exactly as before; console clean.
2. Edit a field in the Inspector, sync. **Expected:** the field reaches source; no new warnings.
3. Re-run Materialize with no code change. **Expected:** zero plan ops.

Scene setup and console capture reuse the existing gate harness family. `BuiltinRefGateHarness.cs` is in
D0's pinned set, so any harness EXTENSION goes in a NEW file; a change that genuinely requires editing a
pinned harness escalates rather than lands, because breaking the pin destroys the feature's only
behavior-preservation proof.

Every NEW EditMode file created anywhere in this feature ships with its committed `.meta`. This is not
restated as a per-task obligation: D0's `GateTestMetaFileTests` is the check, it covers every file under
`unity-gate/Assets/GateTests/` whoever creates it, and it runs before any migration lands.

## Not in scope

- Any behavior change. This spec is behavior-preserving throughout; a migration that alters output is a defect.
- New `ValueNode` kinds. Spec 09 adds its own and depends on this landing first.
- Converting the codebase to a visitor interface. `Fold`'s per-container parameters give containers
  compile-time enforcement without rewriting every pass as an interface implementation.
- `ObjectRefValues`, `NestedValueEmission`, `ListValueEmission`, `ComponentReconciler`,
  `SerializedEnumNormalizer`: already routed, unchanged here beyond the D5 inventory.

## Dependencies

None. Blocks `specs/09-m8-unityevents.md`, whose new `UnityEventListeners` container kind is what made
the gap load-bearing.
