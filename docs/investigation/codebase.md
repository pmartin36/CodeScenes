# Is CodeScenes structurally hard to extend?

Audit date 2026-08-10. Working tree = M8 (`specs/09-m8-unityevents.md`) partially built, ~20% done.
All line/site numbers below are measured from the working tree at that point, not estimated.

## Size baseline

| Area | .cs files | lines |
|---|---|---|
| `SceneBuilder.Core/` | 144 | 19,179 |
| `SceneBuilder.Core.Tests/` | 159 | 40,219 |
| `com.codescenes/Editor/` (the "logic-light" adapter) | 53 | 11,851 |
| `com.codescenes/Runtime/` | 13 | 1,153 |
| `unity-gate/Assets/GateTests/` | 121 | 28,761 |
| `SceneBuilder.Grammar/` | 11 | 1,469 |
| `CodeScenes.Analyzers/` | 13 | 1,335 |

Test:production ratio for Core is 40,219 : 19,179 = **2.1:1**, and the Unity gate suite adds
another 28,761 lines. A feature change propagates into roughly two lines of test for every line of
production code it touches.

---

## 1. What adding one `ValueNode` kind actually requires

`SceneBuilder.Core/Model/ValueNode.cs` is a 13-case discriminated union (`Primitive`, `Enum`,
`Vec2`, `Vec3`, `Vec4`, `Quat`, `Color`, `Nested`, `List`, `Unsupported`, `AssetRef`, `ObjectRef`,
`UnityEventListeners`). Nothing in the repo enumerates where a kind must be handled. The sites have
to be re-derived by grep every time.

### Measured spread per kind (production only: Core + Grammar + Analyzers + com.codescenes + DocGen)

| Kind | token sites | files |
|---|---|---|
| Primitive | 60 | 17 |
| Unsupported | 52 | 23 |
| AssetRef | 49 | 18 |
| Nested | 39 | 11 |
| ObjectRef | 37 | 16 |
| List | 28 | 10 |
| Enum | 24 | 11 |
| UnityEventListeners (the in-flight M8 kind) | 20 | 5 |
| Vec3 | 12 | 9 |
| Vec2 / Quat | 8 | 7 |
| Vec4 | 6 | 5 |
| Color | 6 | 5 |

In tests the same kinds appear far more widely: `ObjectRef` 254 sites across 42 test files,
`AssetRef` 241/41, `Primitive` 395/61.

### The must-edit set for a new LEAF kind (traced, not estimated)

A leaf kind (no children) still has to be handled in all of these, and **none of them fails at
compile time** if you miss one:

1. `SceneBuilder.Core/Model/ValueNode.cs:26`: add the `[JsonDerivedType]` registration, or the
   node silently fails to survive a plan-JSON round trip.
2. `SceneBuilder.Core/Model/ValueNode.cs`: the record itself, plus custom `Equals`/`GetHashCode`
   if it holds a collection (see `List`, `UnityEventListeners`: both hand-roll `SequenceEqual` +
   a hand-rolled `HashCode` loop).
3. `SceneBuilder.Core/Parsing/ValueNodeParser.cs`: parse the C# literal back (5 catch-alls at
   `:100`, `:129`, `:204`, `:241`, `:265`, all producing `Unsupported` or `null`).
4. `SceneBuilder.Core/Reconcile/SourceExpr.cs:92-120`: render the C# literal. Catch-all at `:120`
   is `_ => throw new NotSupportedException`, a runtime throw.
5. `SceneBuilder.Core/Reconcile/ListValueEmission.cs:88-99`: `EmittedTypeToken`. Catch-all is
   `_ => null` at `:99`, silent.
6. `SceneBuilder.Core/Reconcile/NestedValueEmission.cs:231-235`: `IsRepresentable`. Catch-all is
   `_ => true` at `:235`, silent, and "representable" is the wrong default for a new kind.
7. `com.codescenes/Editor/SerializedFieldBridge.cs:153-203`: the READ dispatch, keyed on
   `SerializedPropertyType`; `default:` at `:202` returns `Unsupported`.
8. `com.codescenes/Editor/SerializedFieldBridge.cs:386-431`: the WRITE dispatch. `default:` at
   `:429` logs `Debug.LogWarning` and **drops the write**. A missed kind is a silent data loss at
   runtime, discoverable only by a live-editor round-trip test.
9. `com.codescenes/Editor/SerializedFieldBridge.cs:144-149`: `ContainsUnsupported`, `_ => false`.
10. `SceneBuilder.Core/Materialize/Materializer.cs:270-320`: plan-op emission; an `if/else if`
    chain over `AssetRef` / `ObjectRef` / reference-`List` / `Unsupported` with an implicit
    fall-through.
11. `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:754-778`: `AuthoredTextIsCurrent`, a
    hand-rolled PAIRED walk with `default: return true` at `:777`. A new kind defaults to
    "authored text is already current", i.e. never re-emitted.
12. Guard-test inventories (below): `ObjectRefDescentScanTests`, `ValueContainerDescentScanTests`,
    `ListenerTargetBypassScanTests`, `ModeArgBypassScanTests`: each requires a hand-written entry
    with an **exact occurrence count** for every new production site that names a guarded kind.

For a CONTAINER kind, add five more edits inside `SceneBuilder.Core/Model/ValueWalk.cs` (see §2).

### There is no committed index

Grep found no manifest, no `KindRegistry`, no exhaustiveness test enumerating the kinds. The only
artifacts approaching an index are the four scan-test `DeclaredInventory` arrays, and those are
inventories of *bypasses*, not of *required handling*: they tell you where a kind is mentioned
today, not where a new kind must be handled tomorrow. `SceneBuilder.Core/Model/ValueWalk.cs:16-38`
is a prose `<remarks>` list of which pass uses which primitive; it is documentation, not a check,
and it went stale once already (commit `442ba7b`, "correct the summary to match the four primitives
it ships").

---

## 2. Dispatch is closed, and every close is a silent one

Every dispatch over `ValueNode` kinds in production ends in a catch-all. C# would offer
compile-time exhaustiveness for a closed hierarchy, and the repo does not use it anywhere: there is
no visitor, no `abstract T Accept(...)`, and no `[JsonPolymorphic]`-adjacent exhaustive switch that
the compiler can check. Grep for `Accept(`/`IVisitor`/`Visit(` over `SceneBuilder.Core` returns
nothing on `ValueNode`.

### The catch-alls, named

| Site | catch-all | what a new kind silently gets |
|---|---|---|
| `SceneBuilder.Core/Model/ValueWalk.cs:142` (`MapNode`) | `default: return visited;` | treated as a leaf; children never mapped |
| `SceneBuilder.Core/Model/ValueWalk.cs:221` (`Any`) | `default: return false;` | predicate never reaches children |
| `SceneBuilder.Core/Model/ValueWalk.cs:312` (`Fold`) | `default: return leaf(node);` | folded as a leaf |
| `SceneBuilder.Core/Model/ValueWalk.cs` `Descend` (no default) | implicit | no descent at all |
| `SceneBuilder.Core/Model/ValueWalk.cs` `EnumerateAt` (no default) | implicit | yielded, children invisible |
| `SceneBuilder.Core/Reconcile/SourceExpr.cs:120` | `_ => throw` | **loud**, runtime |
| `SceneBuilder.Core/Reconcile/ListValueEmission.cs:99` | `_ => null` | silent |
| `SceneBuilder.Core/Reconcile/NestedValueEmission.cs:235` | `_ => true` | silent, wrong default |
| `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:777` | `default: return true` | silent, wrong default |
| `com.codescenes/Editor/SerializedFieldBridge.cs:148` | `_ => false` | silent |
| `com.codescenes/Editor/SerializedFieldBridge.cs:202` | `default:` → `Unsupported` | silent degradation |
| `com.codescenes/Editor/SerializedFieldBridge.cs:429` | `default:` → `LogWarning` + drop | **runtime data loss** |
| `SceneBuilder.Core/Model/UnityEventListener.cs:124` | `default: throw` | **loud**, runtime |
| `SceneBuilder.Core/Parsing/ValueNodeParser.cs:100,129,204,241,265` | `_ =>` `Unsupported`/`null`/`-1` | silent |

**Count: 18 catch-all dispatch sites over `ValueNode`, of which 2 fail loudly (both at runtime,
neither at compile time) and 16 accept an unhandled kind silently.** Zero fail at compile time.

### `ValueWalk` centralised the recursion but did not close it

Spec 36 (`specs/completed/36-uniform-value-descent.md`) hoisted container descent into
`ValueWalk`, a real improvement that the guard test enforces. But `ValueWalk` itself repeats the
identical container switch **five times** (`MapNode`, `Any`, `Fold`, `Descend`, `EnumerateAt`), and
adding `UnityEventListeners` as a container meant editing all five (visible in the current file at
lines 128-146, 205-221, 285-311, 372-395, 411-437). Because each ends in a leaf-defaulting
catch-all, a sixth container kind that is added to only four of the five compiles, passes the
descent guard (the guard only checks that a switch does not exist *outside* `ValueWalk.cs`), and
silently drops a subtree.

### Has the language-level option been declined, and is that recorded?

Yes, in two places, and the stated reason **checks out under measurement**:

- `specs/completed/36-uniform-value-descent.md:33-38`: "Removing the catch-all arms and letting the
  compiler flag the gap is NOT available. C# does not treat this hierarchy as closed: an `abstract
  record` base with `sealed record` cases, switched over with every case covered and no discard arm,
  still reports `CS8509`."
- `specs/completed/36-uniform-value-descent.md:355-357` explicitly puts "converting the codebase to
  a visitor interface" out of scope, on the grounds that "`Fold`'s per-container parameters give
  containers compile-time enforcement without rewriting every pass as an interface implementation."

I verified the CS8509 claim rather than taking it: a scratch project with `abstract record Node`,
a `private Node()` constructor closing the hierarchy, three `sealed record` cases and a switch
covering **all three** with no discard arm still fails with
`error CS8509: The switch expression does not handle all possible values of its input type`. The
spec is right. C# offers no closed-hierarchy exhaustiveness here.

Two things the record misses, though:

1. **No project sets `WarningsAsErrors`.** Grep over every `.csproj` finds `Nullable`, `LangVersion`
   and `NoWarn`, and no `TreatWarningsAsErrors`/`WarningsAsErrors` anywhere; there is no
   `Directory.Build.props`. CS8509 is a *warning* by default, so a partial switch compiles silently
   today. Promoting CS8509 to an error would not give closed-hierarchy checking, but it would at
   least force every switch-expression author to write the discard arm deliberately.
2. **The `Fold` argument generalises further than the spec applied it, and the gap is exactly where
   M8 got bitten.** A per-case delegate parameter DOES give compile-time enforcement: adding
   `UnityEventListeners` to `Fold` and `Descend` added a parameter, and every caller had to change
   or fail to compile. Measured caller counts:

   | primitive | per-container delegate params? | prod call sites | new container kind is caught at compile time? |
   |---|---|---|---|
   | `Fold` | yes | 1 | **yes** |
   | `Descend` | yes | 1 | **yes** |
   | `Map` | no (one generic `descend`) | 5 | no |
   | `Any` | no | 4 | no |
   | `Enumerate` | no | 5 | no |

   So 2 of the 5 recursion primitives are compile-checked and 3 are not, and the 3 that are not
   carry 14 of the 16 call sites.

---

## 3. The guard tests

Eight source-scanning / anti-drift guards, **2,146 lines of test code that police structure rather
than behavior**, plus ~130 lines of lint inside `verify.sh`.

| Guard | lines | what it checks | shape |
|---|---|---|---|
| `SceneBuilder.Core.Tests/ValueContainerDescentScanTests.cs` | 488 | every `ValueNode.List`/`ValueNode.Nested` token outside `ValueWalk.cs` must be declared | 22 sites, **exact counts** |
| `SceneBuilder.Core.Tests/RecognizerAgreementTests.cs` | 439 | `FlatShapeRecognizer.Analyze` and `BuilderParser.Parse` agree (accept/reject, and identical message+line+column) on a hand-written corpus | corpus |
| `SceneBuilder.Core.Tests/ModeArgBypassScanTests.cs` | 425 | no site outside `UnityEventProjection.cs` names the serialized persistent-call vocabulary; no adapter site names `ListenerArgMode`/`ListenerCallState` | count-free allowlist |
| `SceneBuilder.Core.Tests/ListenerTargetBypassScanTests.cs` | 306 | no site reads `m_Target`/`m_ObjectArgument`/... directly; no site composes the component-LogicalId `#` format by hand | count-free allowlist |
| `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs` | 253 | every `ValueNode.ObjectRef` token must be declared **+ a 1000-line-per-file budget** | 15 sites, **exact counts** |
| `SceneBuilder.Core.Tests/GateTestMetaFileTests.cs` | 94 | every `unity-gate` `.cs` has a sibling `.meta` | count-free |
| `SceneBuilder.Core.Tests/AuthoringSurfaceScan.cs` | 92 | no authoring API takes a banned handle type as a parameter | count-free |
| `SceneBuilder.Core.Tests/SourceExprLeafKindTests.cs` | 49 | a synthetic unknown `ValueNode` kind throws from `SourceExpr`, at root and below containers | behavioral |
| `verify.sh:81-128` | 48 | no pipeline-artifact prose in comments the working diff ADDS | diff-scoped lint |
| `verify.sh:130-158, 291` | 29 | the gate run leaves no residue | delta check |
| `verify.sh:202-272` | 71 | discount one exact Unity engine `[Error]` when it is a failure's sole cause | narrow exemption |

### What a new feature must DO to satisfy them

The two **exact-count** scans are the expensive ones. `ObjectRefDescentScanTests.cs:26-58` and
`ValueContainerDescentScanTests.cs:26-73` require, for every production site that so much as
*names* `ValueNode.ObjectRef` / `.List` / `.Nested`, a hand-written record of
`(relative path, enclosing member name, exact occurrence count, prose reason)`. Consequences a
feature author actually pays:

- Renaming a method moves its inventory entry. Extracting a helper splits one entry into two.
- Adding a single extra `ValueNode.Nested` mention inside an already-declared member fails the scan
  on the count, with no behavior change involved.
- Splitting a file (which the 1000-line budget forces, see below) moves entries between paths.
- The reasons are prose: `ValueContainerDescentScanTests.cs:63-64` carries a 40-word justification
  for **7** occurrences inside `NestedValueEmission.Complete`. That is a written argument a future
  editor has to re-argue.

The M8 register already records the maintenance cost as its own defect:
`docs/m8-measured-defects.md:103-106`, "four test files under `SceneBuilder.Core.Tests/` each carry
a private copy of the 'enumerate production .cs under SceneBuilder.Core + com.codescenes, skipping
obj/bin' walk". Confirmed: `ProductionFiles` is declared four separate times
(`ObjectRefDescentScanTests.cs:79`, `ValueContainerDescentScanTests.cs:83`,
`ListenerTargetBypassScanTests.cs:30`, `ModeArgBypassScanTests.cs:27`).

### Evidence of a guard blocking legitimate work

**The 1000-line file budget, bolted onto `ObjectRefDescentScanTests.cs:236-251`, cost M8 an entire
task and is about to cost it another.** Two independent records:

- Commit `3325907` (M8 bucket b1, reroll): "b1-t1 extracted `Reconciler.cs` (966 lines, **34 from
  the gate-enforced limit**) into `ReconcilerRemovals.cs` et al and made `ComponentReconciler`
  partial, so the two later tasks that add real logic there are not fighting over 34 lines. **A pure
  move: every existing test stayed green unmodified.**" A whole planned task, through research →
  test → code → validate → gate, produced zero behavior.
- `docs/m8-measured-defects.md:245-254`: "`SourcePatchApplier.cs` is 941 lines against the 1000-line
  budget enforced at `ObjectRefDescentScanTests.cs:242` — 59 lines of headroom. No split task exists
  for it... b4-t3 edits it and self-imposes 'at most a dispatch hook' but carries no ASSUMPTION or
  escalation path for the case where that hook plus its wiring exceeds 59 lines, which would land
  the file over the gate mid-task."

**A guard that does not guard what the plan says it guards.** `docs/m8-measured-defects.md:276-291`,
severity med: `ModeArgBypassScanTests`' Scan A allowlist names
`com.codescenes/Editor/UnityEventWriter.cs` and `UnityEventReader.cs`, "and
`ls com.codescenes/Editor/UnityEvent*` returns no matches — both adapter entries are reserved names
for files that do not exist yet." Three separate reviewers (`scope/bucket-b1.md` finding 1,
`plan-review.md` MED-1, the b1-t2 researcher at iteration 3) each rediscovered the same hole. The
guard was written to a plan, not to the tree.

### Evidence of a guard catching a real defect

`SourceExprLeafKindTests` + the `SourceExpr` throw did their job:
`docs/m8-measured-defects.md:56-66` records that the render path for `ValueNode.UnityEventListeners`
had **no owning task**, and that "b1-t1 supplies the `Fold` arm as a THROW naming the required
route, so the gap fails loud instead of rendering `Set("m_OnClick", ...)`". Fail-loud caught a real
routing gap that silent-catch-all would have shipped as corrupted user source.

`GateTestMetaFileTests` is the other clear win: without it a `unity-gate` test file with no `.meta`
never runs, and the suite stays green on a test that does not exist. Cheap (94 lines), count-free,
and no feature has to do anything to satisfy it beyond committing a file Unity requires anyway.

I found **no** record of `ValueContainerDescentScanTests`, `ObjectRefDescentScanTests` (the token
scan half) or `ListenerTargetBypassScanTests` catching a behavioral defect. Their recorded
appearances in `.agent_handoffs/` (70, 24 and 26 files respectively) are inventory-maintenance
traffic.

### Per-guard verdict

| Guard | verdict | why |
|---|---|---|
| `GateTestMetaFileTests` | **keep** | cheap, count-free, prevents a silently-non-running test |
| `SourceExprLeafKindTests` | **keep** | behavioral, caught a real gap, 49 lines |
| `RecognizerAgreementTests` | **keep** | it is the only thing pinning two grammar implementations together (§5) |
| `AuthoringSurfaceScan` | **keep** | 92 lines, count-free, guards the published API surface |
| `verify.sh` prose lint | **keep, finish it** | diff-scoped is correct; 676 pre-existing hits remain invisible (see below) |
| `ListenerTargetBypassScanTests` | **keep** | count-free, per-file; the low-friction shape |
| `ModeArgBypassScanTests` | **simplify** | count-free shape is right, but the allowlist names files that do not exist; point it at the tree, not the plan |
| `ValueContainerDescentScanTests` | **simplify** | convert 22 exact-count entries to a count-free per-file allowlist, same as the two later guards already do |
| `ObjectRefDescentScanTests` (token scan) | **simplify** | same: 15 exact-count entries → per-file allowlist |
| the 1000-line budget inside it | **delete or relocate** | it is not an ObjectRef concern, it has produced one pure-refactor task and one live risk, and it has caught no defect |

The repo already knows the better shape: `ListenerTargetBypassScanTests.cs:12-15` says it is
"modelled on `ValueContainerDescentScanTests`' UnityEventListeners allowlist pair" but explicitly
**count-free**. The two newer guards learned the lesson; the two older ones were never migrated.

### The prose lint's known blind spot, quantified

`HANDOFF.md` item 1b flags it; here is the number. `verify.sh:111-119` only inspects lines the
working diff ADDS, so pre-existing pipeline prose is invisible. Measured across the scanned roots:
**676 comment lines** already in the tree would fail the lint if it were unscoped
(`SceneBuilder.Core` 162, `SceneBuilder.Core.Tests` 236, `com.codescenes` 96,
`unity-gate/Assets` 182). `RecognizerAgreementTests.cs:13-18` is a live example: "Test #5 for
b1-t2... See `.agent_handoffs/codescenes-analyzers/b1-t2/research.md`", a pointer into a
gitignored directory, in a 439-line guard test.

---

## 4. Layering: the adapter is not logic-light

`specs/00-foundation.md:47-56` describes `SceneBuilder.Editor` as "thin, dumb", "**deliberately
logic-light**", owning "exactly four responsibilities": execute a Plan, read the scene into a
`SceneSnapshot`, capture edits via `ObjectChangeEvents`, resolve asset path/GUID and
`GlobalObjectId`.

Measured:

- **11,851 lines across 53 files** in `com.codescenes/Editor/`, which is **62% of the size of Core**
  (19,179 lines). Largest single file: `SceneBuilderAutoSync.cs` at 743 lines, for what the spec
  calls "trigger only".
- **16 of 53 adapter files (30%) name `ValueNode`, 117 mentions total.**
  `SerializedFieldBridge.cs` alone carries 51 mentions in 520 lines, an 11-arm `ValueNode` WRITE
  switch (`:386-431`) and a 12-arm READ switch (`:153-203`).
- **The two descent guards scan `com.codescenes/**` as production source and permanently declare 9
  adapter bypass sites**: `SerializedEnumNormalizer.NormalizeNode`,
  `SerializedFieldBridge.ContainsUnsupported` / `EnterNode` / `ReadList` / `ReadNested`,
  `AssetReferenceResolver.ReadObjectReference` / `ReadObjectReferenceValue`,
  `InstanceOverrideExecutor.WriteFieldValue`. The guards did not fail the seam; they ratified it.
- `com.codescenes/Editor/SerializedMemberMap.cs` (398), `SerializedFieldExclusions.cs` (187),
  `ComponentTypeNormalizer.cs` (163), `SerializedEnumNormalizer.cs` (174) and
  `ComponentDefaultTemplate.cs` (121) are **1,043 lines of naming/mapping/defaulting rules** that
  are not execute, read, capture or resolve. They need Unity reflection, which is a real reason to
  sit adapter-side, but they are policy, not plumbing.

### Does the seam force cross-layer changes?

Yes, and M8's own change set proves it. **20% of one feature is currently spread across 29 source
files in 8 projects**:

| project | files touched |
|---|---|
| `SceneBuilder.Core/` | 7 (6 modified, 1 new) |
| `SceneBuilder.Core.Tests/` | 11 (6 modified, 5 new) |
| `com.codescenes/Runtime/` | 5 (4 modified, 1 new) |
| `com.codescenes/Editor/` | 2 |
| `SceneBuilder.Grammar/` | 2 (1 modified, 1 new) |
| `CodeScenes.Analyzers/` | 2 |
| `SceneBuilder.Analyzers.Tests/` | 1 |
| `unity-gate/Assets/GateTests/` | 1 |

Because `com.codescenes/**` is a slow-gate glob (`CLAUDE.md`, `slowPathGlobs`), any task touching
the adapter pays the multi-minute Unity EditMode suite. A feature whose value kind must be handled
on BOTH sides of the seam therefore cannot be built as a sequence of fast-gated Core tasks.

The recurring phrase in `docs/m8-measured-defects.md` is the tell. Nine of its entries end with some
form of "**is in no M8 task's TOUCHES**" (`:38`, `:60`, `:82`, `:88`, `:96`, `:246`, `:274`, `:288`,
plus the `ChangeScopedSnapshot`/`SceneRefResolver` pair at `:78-86`). The edit surface of a feature
is not derivable from the spec; it is discovered by grep, mid-run, after the plan is frozen.

---

## 5. Duplication that costs, and what has already drifted

Five rules implemented more than once. **One has already drifted and produced a real wrong value.**

### 5.1 The ordinal-within-type component key: 6 hand-written copies, 3 verbatim

`docs/m8-measured-defects.md:295-302` measured it; I confirmed every site. The
`var ordinalByType = new Dictionary<string, int>(); ... ordinal = ordinalByType.TryGetValue(...)`
block appears at:

- `SceneBuilder.Core/Diff/Differ.cs:383`
- `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:810`
- `SceneBuilder.Core/Identity/IdentityRemapper.cs:218` and `:233`
- `SceneBuilder.Core/Parsing/BuilderParser.cs:681`
- `SceneBuilder.Core/Parsing/BuilderParser.Instance.cs:169`

**It has drifted.** `docs/m8-measured-defects.md:88-95`:
`SceneBuilder.Core/Reconcile/ReconcilerInstances.Nested.cs:180` composes
`$"{instanceLogicalId}/{component.Type.FullName}#{i}"` where `i` is the index in `node.Components`,
**not the ordinal-within-type every other site uses**. For `[Rigidbody, BoxCollider]` it yields
`.../BoxCollider#1` where the canonical id is `.../BoxCollider#0`. Wrong value, shipped, found by
accident during an unrelated M8 task.

### 5.2 The component-LogicalId `#` string format

An owner now exists: `SceneBuilder.Core/Identity/ComponentTargetResolution.cs:136-138`
(`ComposeLogicalId`) and `:161` (`TryParseLogicalId`), and `ListenerTargetBypassScanTests` Scan B
guards new sites. But `docs/m8-measured-defects.md:97-107` records **six shipped sites outside the
owner, none in any M8 task's TOUCHES**, and one is still live in the adapter:
`com.codescenes/Editor/InstanceOverrideExecutor.cs:219` hand-parses `afterSlash.LastIndexOf('#')`.
The format is also restated in prose in five more comments (`BuilderParser.cs:666`,
`BuilderParser.Instance.cs:136`, `ComponentPatchApplier.cs:367`, `SourceEdit.cs:147`,
`SceneHierarchyPath.cs:40`).

### 5.3 The builder verb table: 16 cases mirrored across two assemblies

`SceneBuilder.Grammar/FlatShapeRecognizer.cs` and `SceneBuilder.Core/Parsing/BuilderParser.cs`
carry the **same 16 `case` labels in the same order**:

| verb | FlatShapeRecognizer.cs | BuilderParser.cs |
|---|---|---|
| Transform / Tag / Layer / Active / Static / Id / Component / FitSize / SurfaceSnap / RectTransform | 168, 171, 174, 177, 180, 182, 185, 188, 191, 194 | 317, 320, 324, 328, 332, 336, 342, 345, 348, 351 |
| Set / OnClick / OnEvent | 309, 312, 315 | 484, 489, 492 |
| pos / rot / scale | 425, 426, 427 | 604, 607, 610 |

`BuilderParser.Recognition.cs:15` does delegate the *shape rejection* to the recognizer, and
`BuilderParser.cs:741-750` claims "the flat-shape grammar lives in exactly ONE place". That is true
of the reject/accept decision and false of the verb dispatch. `CodeScenes.Analyzers/UnityEventAnalysis.cs:30`
adds a third copy for the UnityEvent subset (`eventName != "OnClick" && eventName != "OnEvent"`).
Adding one authoring verb means three switches, a runtime API method, and a 439-line agreement
corpus. That is the M8 shape exactly.

The mitigation is real: `RecognizerAgreementTests` (439 lines) fails when the two drift. That is why
this one has **not** drifted. It is duplication paid for with a large test rather than removed.

### 5.4 The listener path vocabulary, five spellings inside one file

`"[" + i + "].Target"` / `.ArgValue` is composed independently at
`SceneBuilder.Core/Model/ValueWalk.cs:165, 298, 304, 421, 429`, inside the very file whose job is
to be the single owner of descent. Nothing derives one from another. `ed4ee1b`
("record the adapter serialized-path vocabulary, **learned by getting it wrong twice**") is the
commit-message record of what path-vocabulary drift costs here.

### 5.5 The scan tests' file-enumeration walk, four copies

`ProductionFiles` at `ObjectRefDescentScanTests.cs:79`, `ValueContainerDescentScanTests.cs:83`,
`ListenerTargetBypassScanTests.cs:30`, `ModeArgBypassScanTests.cs:27`, plus a fifth copy of
`EnclosingMember` in three of them. Recorded as a defect at `docs/m8-measured-defects.md:103-106`,
severity low, **owner: unassigned**.

---

## 6. Throughput, measured

The repo is 4 weeks old (first commit 2026-07-13, 296 commits, 106,187 lines of C#). The delivery
rate curve:

| window | commits/day (peak) | specs moved to `specs/completed/` |
|---|---|---|
| 2026-07-13 to 07-18 | 34, 22, 38, 22 | 13 |
| 2026-07-21 to 07-31 | 18, 23, 12, 27, 8, 11, 17 | 19 |
| 2026-08-02 to 08-10 | 1, 12, 15, 12, 5, 1 | 3 |

`ValueNode` grew from 10 kinds to 13 over the same period (`git show <c>:.../ValueNode.cs |
grep -c JsonDerivedType`: 10 on 2026-07-14, 11 same day, 12 on 07-17, 13 on 2026-08-06).

From `.agent_handoffs/_lessons/cost-log.md` (output tokens only, the log records no input side):

| feature | planned tasks | output tokens | escalations |
|---|---|---|---|
| `reference-writes-and-cache-invalidation` | 6 | 887,027 | 3 |
| `excluded-field-one-way-report` | 4 | 699,303 | 0 |
| `uniform-value-descent` (spec 36) | 13 + 13 | 138,360 + 1,318,317 | 7 + 0 |
| `m8-unityevents` (spec 09) | 12, then 11, then 10 | 3,336,666 across 10 log entries | 5 + 5 + 5 |

M8 has been **decomposed and started three times** (task counts 12, 11, 10 in three separate
cost-log entries) and has produced **two feature commits** (`30a9613`, `3325907`) for 3.34M output
tokens.

The recurring-failure ledger attributes almost none of this to the code.
`.agent_handoffs/_lessons/ledger.md` + `ledger-archive.md` classify 94 entries:
**63 `pipeline:agent-behavior`**, 11 `project:mechanizable`, 10 `pipeline:mechanizable`,
8 `project:spec-ambiguity`, 5 `project:scoped-guidance`. The harness is being tuned; the extension
surface is not.

---

## VERDICT

**Yes, but the flaw is narrower than "the codebase is bad".** The design is coherent and unusually
well documented. What is broken is that **the edit surface of a feature is not derivable from the
code, and the mechanisms added to compensate charge a per-edit tax without closing the gap.**

**Strongest evidence that it IS hard to work in:** `docs/m8-measured-defects.md` contains **nine
separate defects** whose stated cause is "this file is in no M8 task's TOUCHES" (`:38`, `:60`,
`:78-86`, `:82`, `:88`, `:96`, `:246`, `:274`, `:288`). Every one is a file a planner could not
know to include, discovered mid-run by an implementer. Add the 18 catch-all dispatch sites of which
16 silently accept an unknown kind, and one of them (`SerializedFieldBridge.cs:429`) silently drops
a write. Missing a site is invisible until a live-editor round trip.

**Strongest evidence that it is NOT:** the guard tests are not cargo cult. Spec 36's rejection of
switch exhaustiveness is empirically correct (I reproduced `CS8509` on a fully-covered switch over a
hierarchy closed by a private constructor), `SourceExpr`'s fail-loud arm caught a real unrouted
render path (`docs/m8-measured-defects.md:56-66`), and the two newest guards
(`ListenerTargetBypassScanTests`, `ModeArgBypassScanTests`) already dropped exact counts in favour
of count-free per-file allowlists. The repo diagnoses itself accurately and in writing. It just has
not been allowed to stop and act on its own findings: three of the five duplications in §5 are
recorded in `docs/m8-measured-defects.md` with **owner: unassigned**.

---

## RANKED PROBLEMS

1. **No committed index of a value kind's handling sites; 16 of 18 dispatch catch-alls are silent.**
   `SerializedFieldBridge.cs:429` (drop + warn), `NestedValueEmission.cs:235` (`_ => true`),
   `ComponentReconciler.cs:777` (`default: return true`), `ListValueEmission.cs:99` (`_ => null`),
   `ValueWalk.cs:142/221/312` and two implicit fallthroughs. Adding kind 14 means finding 12 sites
   by grep and being wrong silently.
2. **`ValueWalk`'s five recursions are only two-fifths compile-checked.** `Fold` (1 caller) and
   `Descend` (1 caller) break on a new container kind; `Map` (5), `Any` (4) and `Enumerate` (5)
   silently treat it as a leaf. 14 of 16 call sites are in the unchecked group.
3. **Exact-count scan inventories tax every edit.** 37 hand-maintained
   `(path, member, count, reason)` entries across `ValueContainerDescentScanTests.cs:26-73` (22) and
   `ObjectRefDescentScanTests.cs:26-58` (15). A rename, an extraction or one extra mention fails the
   gate with no behavior change.
4. **The 1000-line budget at `ObjectRefDescentScanTests.cs:236-251` has produced pure-refactor
   work and an unmanaged risk.** Commit `3325907` spent a full pipeline task splitting
   `Reconciler.cs` (966 lines, "34 from the gate-enforced limit", "a pure move: every existing test
   stayed green unmodified"). `docs/m8-measured-defects.md:245-254` records `SourcePatchApplier.cs`
   at 941 lines with 59 lines of headroom and an M8 task scheduled to edit it with no escalation
   path.
5. **The Core/adapter seam is a `ValueNode` seam, and it is a slow-gate boundary.** 16 of 53 adapter
   files name `ValueNode` (117 mentions); `SerializedFieldBridge.cs` holds two full kind switches;
   the descent guards permanently declare 9 adapter bypass sites. Any kind-level feature must cross
   the seam, and crossing it costs the multi-minute Unity EditMode suite on every task.
6. **`ordinalByType` is written six times and has already drifted.**
   `Differ.cs:383`, `ComponentReconciler.cs:810`, `IdentityRemapper.cs:218` and `:233`,
   `BuilderParser.cs:681`, `BuilderParser.Instance.cs:169`; the wrong seventh at
   `ReconcilerInstances.Nested.cs:180` produces `.../BoxCollider#1` for a canonical `#0`
   (`docs/m8-measured-defects.md:88-95`). Owner: unassigned.
7. **The builder verb table exists three times.** `FlatShapeRecognizer.cs` (16 cases),
   `BuilderParser.cs` (the same 16, same order), `UnityEventAnalysis.cs:30` (the UnityEvent subset).
   Held together by a 439-line corpus test rather than by shared code.
8. **`ModeArgBypassScanTests`' allowlist names two files that do not exist**
   (`com.codescenes/Editor/UnityEventWriter.cs`, `UnityEventReader.cs`), so it guards nothing at the
   exact boundary it was written for. Rediscovered independently three times
   (`docs/m8-measured-defects.md:276-291`).
9. **676 pipeline-artifact comment lines are grandfathered past the `verify.sh` lint**, including
   `.agent_handoffs/` pointers into a gitignored directory inside a 439-line guard test.
10. **Four copies of the scan tests' `ProductionFiles` walk** (`ObjectRefDescentScanTests.cs:79`,
    `ValueContainerDescentScanTests.cs:83`, `ListenerTargetBypassScanTests.cs:30`,
    `ModeArgBypassScanTests.cs:27`). Recorded, owner unassigned.

---

## RECOMMENDATIONS

Ordered. R1 must land before R2 and R3 are worth doing; R4 is independent and can run in parallel.

### R1. Make the kind-handling surface a committed, executable index

**(a) What.** Add `SceneBuilder.Core/Model/ValueNodeKinds.cs`: a static array of the 13 kind
`Type`s, plus a Core test that reflects over `ValueNode`'s nested `sealed record` types and fails
when the array and the hierarchy disagree. Then convert the silent catch-alls that have no correct
default into `throw new NotSupportedException($"...{node.GetType().Name}")`, following the shape
already proven at `SourceExpr.cs:120`: `SerializedFieldBridge.cs:429` (currently drops a write),
`NestedValueEmission.cs:235`, `ComponentReconciler.cs:777`, `ListValueEmission.cs:99`. Add a
per-kind coverage test that drives each of the 13 kinds through each converted dispatch and asserts
it does not throw, so kind 14 fails at `dotnet test` in seconds rather than in a live editor.
Separately set `<WarningsAsErrors>$(WarningsAsErrors);CS8509</WarningsAsErrors>` in a new
`Directory.Build.props` (verified: CS8509 does fire, and does become an error when promoted).

**(b) Files.** New `SceneBuilder.Core/Model/ValueNodeKinds.cs`, new
`SceneBuilder.Core.Tests/ValueNodeKindCoverageTests.cs`, new `Directory.Build.props`; edits to
`SerializedFieldBridge.cs`, `NestedValueEmission.cs`, `ComponentReconciler.cs`,
`ListValueEmission.cs`. Plus one EditMode test in `unity-gate/Assets/GateTests/` for the
`SerializedFieldBridge` change (adapter-facing, so `CLAUDE.md` requires it).

**(c) Effort.** 1 to 1.5 days, most of it in resolving whatever the coverage test turns red.

**(d) What it saves on M8.** `docs/m8-measured-defects.md:56-66` (the unrouted
`UnityEventListeners` render path with no owning TOUCHES) would have been a failing Core test at
plan time instead of a mid-run discovery. The same applies to the seven `SerializedFieldBridge`
write arms M8 still has to add.

**(e) Risk.** Medium. Converting a silent default to a throw can surface latent paths as new
runtime exceptions. Mitigate by landing the coverage test first and reading what it reports before
changing any default.

### R2. Collapse the two exact-count scan inventories to count-free per-file allowlists

**(a) What.** Rewrite `ValueContainerDescentScanTests` and `ObjectRefDescentScanTests`'s token scans
in the shape `ListenerTargetBypassScanTests.cs:12-15` already uses. Keep the per-file allowlist and
the "route this through X or declare it" message; drop the per-member exact counts. Extract the
duplicated `ProductionFiles` + `EnclosingMember` walk into one shared helper (problem 10, already
filed).

**(b) Files.** `SceneBuilder.Core.Tests/ValueContainerDescentScanTests.cs`,
`ObjectRefDescentScanTests.cs`, `ListenerTargetBypassScanTests.cs`, `ModeArgBypassScanTests.cs`; new
shared `SceneBuilder.Core.Tests/ProductionSourceScan.cs`.

**(c) Effort.** Half a day. It is test-only; `./verify.sh` proves it.

**(d) What it saves on M8.** Every M8 task that adds a `ValueNode.ObjectRef` or `.Nested` mention
currently has to add or amend an inventory entry with the right integer. M8 already touches
`ObjectRefDescentScanTests.cs` in the working tree for exactly this reason.

**(e) Risk.** Low, with one real cost: a count-free allowlist stops catching a *second* bypass added
to an already-permitted file. That is the trade the two newer guards already made deliberately.

### R3. Move the file-size budget out of the ObjectRef guard, and raise or retire it

**(a) What.** Delete `ObjectRefDescentScanTests.ProductionSource_StaysUnderFileSizeBudget`
(`:236-251`) from that fixture. Either drop the rule, or re-home it in its own fixture with a
threshold set from the actual distribution and a documented purpose. Right now it is an unrelated
concern living inside a kind guard, and the largest files are already at 941 and 966 lines.

**(b) Files.** `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs`, optionally a new
`SourceFileSizeTests.cs`.

**(c) Effort.** One hour.

**(d) What it saves on M8.** It removes the `SourcePatchApplier.cs` 59-line cliff that
`docs/m8-measured-defects.md:245-254` flags as an unmanaged mid-task gate failure, and it means the
next M8 bucket does not repeat b1-t1's pure-move task.

**(e) Risk.** Low, and the honest cost is that files drift larger. Mitigate by keeping the rule with
a threshold that reflects the tree (1,200) rather than one that forces refactors mid-feature.

### R4. Give the six `ordinalByType` copies one owner, and fix the drifted seventh

**(a) What.** Add `ComponentKeys.ComputeComponentKeys(IReadOnlyList<ComponentData>)` next to the
existing `ComponentTargetResolution.ComposeLogicalId` and route all six sites through it. Fix
`ReconcilerInstances.Nested.cs:180` to use ordinal-within-type. Extend
`ListenerTargetBypassScanTests` Scan B (already the `#`-format guard, already count-free) to fail on
a new hand-written `ordinalByType`. Route `InstanceOverrideExecutor.cs:219`'s hand parse through
`TryParseLogicalId`.

**(b) Files.** `SceneBuilder.Core/Diff/Differ.cs`, `Reconcile/ComponentReconciler.cs`,
`Identity/IdentityRemapper.cs`, `Parsing/BuilderParser.cs`, `Parsing/BuilderParser.Instance.cs`,
`Reconcile/ReconcilerInstances.Nested.cs`, `Identity/ComponentTargetResolution.cs`,
`com.codescenes/Editor/InstanceOverrideExecutor.cs`, `SceneBuilder.Core.Tests/ListenerTargetBypassScanTests.cs`,
plus an EditMode test for the adapter edit.

**(c) Effort.** 1 day, RED-first on the `#1`-vs-`#0` bug.

**(d) What it saves on M8.** M8's whole subject is component-targeted listeners keyed by exactly
this id. `docs/m8-measured-defects.md` already carries the wrong-ordinal bug and the six-site spread
as two separate unassigned entries; M8 will hit both.

**(e) Risk.** Medium. Changing a LogicalId that is currently wrong changes conflict-report keys and
possibly on-disk sidecar content. Needs a live-editor pass, not just the gate.

### R5. Sweep the 676 grandfathered pipeline-prose comments

**(a) What.** Run `verify.sh`'s `PROSE_AWK` unscoped over the tree, fix the 676 hits, then remove
the diff-scoping so the lint applies repo-wide. `HANDOFF.md` item 1b already asks for this.

**(b) Files.** ~50 files across `SceneBuilder.Core` (162 hits), `SceneBuilder.Core.Tests` (236),
`com.codescenes` (96), `unity-gate/Assets` (182); then `verify.sh:111-119`.

**(c) Effort.** Half a day, mechanical.

**(d) What it saves on M8.** Indirect but real: `RecognizerAgreementTests.cs:13-18` currently points
a reader at `.agent_handoffs/codescenes-analyzers/b1-t2/research.md`, which is gitignored and gone.
Every M8 research agent that reads that 439-line guard reads a dead pointer.

**(e) Risk.** Low, but it touches many files at once, so land it between features, never under a
running pipeline (`HANDOFF.md` warns about `git add -A` sweeping harness edits into a bucket
commit).

### R6. Derive the builder verb table from one declaration

**(a) What.** Put the 16 verbs in one table in `SceneBuilder.Grammar` (arity, argument shapes,
whether they open a component closure) and have `FlatShapeRecognizer.cs`,
`BuilderParser.cs` and `CodeScenes.Analyzers/UnityEventAnalysis.cs` read it. Keep
`RecognizerAgreementTests` as the behavioral proof, but stop using it as the only thing preventing
drift.

**(b) Files.** `SceneBuilder.Grammar/FlatShapeRecognizer.cs` (+ partials),
`SceneBuilder.Core/Parsing/BuilderParser.cs`, `CodeScenes.Analyzers/UnityEventAnalysis.cs`, and a
new verb-table file in `SceneBuilder.Grammar`.

**(c) Effort.** 2 to 3 days. This is the largest item and the least urgent.

**(d) What it saves on M8.** M8 adds `OnEvent`, and the working tree already shows it editing
`FlatShapeRecognizer.cs`, a new `FlatShapeRecognizer.UnityEvents.cs`, `BuilderParser.cs`, a new
`BuilderParser.UnityEvents.cs`, `UnityEventAnalysis.cs` and `RecognizerAgreementTests.cs`. Six files
for one verb.

**(e) Risk.** High relative to the others. The parser and the recognizer return different things
(a model vs violations) and unifying them is a real refactor. Do it after M8 ships, not during.

**Sequence.** R3 (one hour, unblocks the next M8 bucket) → R2 (half a day, test-only) →
R1 (the substantive one) → R4 → R5 between features → R6 after M8 ships.

---

## WHAT I COULD NOT ESTABLISH

- **That features "used to take ~1 hour".** I can measure commit density (34 to 38 commits/day in
  week 1 versus 1 to 15 in week 4) and specs moved to `specs/completed/` (13 in the first 6 days,
  3 in the last 9). I cannot separate wall-clock work time from calendar time, and the
  `specs/completed/` moves were batched, so no per-milestone duration is recoverable from git.
- **Total token cost per feature.** `.agent_handoffs/_lessons/cost-log.md` records `tokensOutput`
  only. The 7.9M / 12.8M figures in the brief are not reproducible from anything in the repo; I can
  only confirm 1.46M output for spec 36 and 3.34M output for spec 09.
- **Whether the guards ever blocked a *correct* change that was then abandoned.** `.agent_handoffs/`
  is gitignored, so only the surviving `docs/m8-measured-defects.md` extract and the commit messages
  are readable. Guard-vs-work friction may be systematically under-recorded.
- **I did not run `./verify.sh`.** Nothing in this audit changes code, and a Unity batchmode run
  takes the single license seat. Every number above comes from reading the tree, from git, or from
  the scratch CS8509 project (which I did build and did read the output of). No claim here rests on
  a gate result I did not observe.
- **Whether the 1000-line budget has ever prevented a genuinely unmaintainable file.** I found two
  records of it costing work and none of it saving any, but absence of a record is not absence of
  the effect.
- **The adapter responsibility split beyond `ValueNode` reach.** I measured how much `ValueNode`
  dispatch lives adapter-side (16 files, 117 mentions) and named 1,043 lines of
  naming/mapping/defaulting policy there. I did not classify all 53 adapter files against the
  foundation spec's four responsibilities; that would need a per-file judgement I could not ground
  in a measurement.
