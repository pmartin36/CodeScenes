# M-Dup — Duplicate sibling names must never silently destroy data

## Goal

Two GameObjects that share a name under the same parent must keep their identities across any edit.
A positional id distinguishes them **only by position**, so a pure reorder swaps them (destroying a
real component and recreating it on the wrong object) and deleting the first duplicate destroys the
object the user *kept*. Both are silent: the resulting scene is self-consistent, so nothing surfaces.

A second, independent shape of the same data loss: two statements that resolve to the **same
LogicalId**. `Reconciler.FlattenModel` (`Reconciler.cs:952-959`) does `modelByLogicalId[node.LogicalId]
= node`, so the later node silently overwrites the earlier one and a whole GameObject vanishes from
the model the reconcile reasons about.

This milestone closes both at the class level — neither hazard can *exist* in a file the tool wrote,
and a hand-authored one is **refused, never guessed**.

## The defects (observed, not theorized)

### Defect 1 — positional ids are pinned to slots, not objects

`LogicalIdResolver.Synthesize` mints `{parent}/{name}/{siblingIndex}`, and `LogicalIdResolver.Resolve`
claims persisted ids from a `(parent, name)`-keyed queue in **document order**
(`LogicalIdResolver.cs:63-69`). Observed against real Core (`SceneBuilder.Core.Tests/DuplicateSiblingNameTests.cs`):

- **Reorder** two same-named siblings ⇒ the Rigidbody-owning `Enemy` (really `goid-A`) comes back as
  `goid-B`. Its live Rigidbody `goid-A-rb` goes unconsumed ⇒ orphan ⇒ **DESTROYED**, and a new one is
  **CREATED** on the wrong object. A pure reorder destroys a real component.
- **Delete the FIRST duplicate** ⇒ the survivor (really `goid-B`) comes back as `goid-A`: the tool
  destroys the object the user kept and repurposes the one they deleted.

`IdentityRemapper`'s name tier (`IdentityRemapper.cs:134`, `remaining.Find(e => e.Name == ...)`) is
structurally blind for duplicates — it returns the FIRST match, so tier (b) degenerates into tier (c)
positional matching.

### Defect 2 — colliding LogicalIds are detected by nothing

`ConflictDetector.AmbiguousGroups` (`ConflictDetector.cs:43-66`) counts only **positional** sibling
groups. No check anywhere rejects two nodes resolving to the same LogicalId. Observed by running the
real `BuilderParser.Parse` — in every case below the source **compiles clean** and
`ParseResult.Ambiguities` is **empty**:

| Source | Resolved ids | Compiler |
| --- | --- | --- |
| `scene.Add("Enemy").Id("Enemy-2");` ×2 (copy-paste) | `Enemy-2`, `Enemy-2` | clean |
| `var enemy = scene.Add("A"); scene.Add("B").Id("enemy");` | `enemy`, `enemy` | clean |
| `p1.Add("X").Id("dup"); p2.Add("Y").Id("dup");` (different parents) | `dup`, `dup` | clean |
| `scene.Add("A", a => { var enemy = a.Add("Enemy"); }); scene.Add("B", b => { var enemy = b.Add("Enemy"); });` | `enemy`, `enemy` | clean (CS0128 is scope-local) |

Explicit ids are **global**, not per-parent, so a collision is not confined to a sibling group.
`.Id(...)` is a shipped public authoring surface (§4 priority 2), so a user can hand-write or paste a
collision today. `FlattenModel` then drops one of the colliding pair — the exact data-loss shape this
milestone exists to prevent.

## The policy already exists — this milestone finishes enforcing it

§4 already states: *"If a synthesized id becomes ambiguous (e.g. two same-named siblings reordered),
Reconcile surfaces a **conflict** rather than guessing."* `ConflictDetector.DetectAmbiguousReorders`
implements exactly that — **but only for scene→code**. The **Build (code→scene)** path has no such
guard and silently guesses. This is an UNENFORCED EXISTING POLICY, not an open design question.

## In scope

- Detect both hazards at the ONE chokepoint both directions reach: `BuilderParser.Parse`.
- Sync (the only path that writes builder source) gives a would-be-ambiguous statement a **`var`
  handle** so the file never *contains* an ambiguous pair — both for newly-appended duplicates
  (prevention) and for a pre-existing hand-authored group (self-heal, while the positional mapping is
  still trustworthy).
- Sync re-mints a colliding LogicalId so the model can never silently lose a node.
- Build **refuses** (located conflict, §7) rather than guessing.
- Fuzzer: generate duplicate names, add a reorder op, and add **invariant 6 — identity preservation**.

## Out of scope

- **Auto-renaming the user's objects. REJECTED** — Unity explicitly permits duplicate sibling names;
  do not fight the engine's data model.
- Random-GUID ids. The builder file is rewritten by an LLM (see CLAUDE.md); an opaque GUID will not
  survive that. The minted token must be deterministic and semantic.
- `GlobalObjectId` as an injected token. A goid is `""` until the scene is first saved
  (`specs/00-foundation.md:157`; `SceneBuilderSync.cs:231-232` deliberately does not re-save), so at
  the moment Sync must disambiguate a newly-created duplicate the id may not exist. It also inverts
  `00-foundation.md:164`: *"The map keeps GUIDs/GlobalObjectIds out of the clean C# source."*
- Build *writing* the builder file. Build never writes; Build only refuses. Sync heals.
- **Renaming an authored `var` to resolve a handle×handle collision.** Renaming a declarator requires
  rewriting every reference in its scope. Such a collision is report-only: Build refuses with a
  located error and the user renames the variable.

## Core deliverables

### 1. Detection at the chokepoint — `BuilderParser` / `LogicalIdResolver`

- A node's id is **positional** when it has neither an authored handle nor an explicit `.Id(...)` —
  i.e. exactly when `LogicalIdResolver.Resolve` falls through to the claim-queue/`Synthesize` path.
  `ConflictDetector.IsPositional` is the ONE definition; every consumer shares it.
- `ParseResult.Ambiguities : IReadOnlyList<Conflict>` — one `Conflict` per ambiguous sibling group:
  **≥2 siblings that share a `Name` and are BOTH positional**. `Kind = ConflictKind.AmbiguousAnchor`,
  `Location` = the offending statement's anchor span, `Reason` names the objects and instructs the
  user to give each a `var` handle or an explicit `.Id("...")` (§7: fail loud, located). The Reason
  MUST contain the literal substring `.Id(`.
- **Contract: detection is unconditional.** Every `BuilderParser.Parse` computes it; there is no
  opt-in flag a caller can forget. Parse does **not** throw — Sync must be able to parse an ambiguous
  file in order to heal it. The *policy* (refuse vs heal) belongs to each consumer.

### 2. Detection of colliding LogicalIds — same chokepoint, same contract

`Anchors`, `Handles` and `modelByLogicalId` are all LogicalId-keyed dictionaries: they *collapse* a
collision instead of revealing it. Detection therefore consumes an un-collapsed, document-ordered
list.

- `SceneBuilder.Core.Parsing.NodeAnchor` — new public record, one per parsed GameObject node:
  ```csharp
  public sealed record NodeAnchor
  {
      public string LogicalId { get; init; } = "";
      public string Name { get; init; } = "";
      public SourceSpan Span { get; init; }        // the node's Add(...) invocation span (== Anchors' value)
      public string? Handle { get; init; }         // authored `var` name, else null
      public SourceSpan? IdCallSpan { get; init; } // span of the `.Id("...")` invocation, else null
  }
  ```
- `ParseResult.NodeAnchors : IReadOnlyList<NodeAnchor>` — pre-order, document order, **never
  collapsed**: two nodes that resolved to the same LogicalId produce two entries. Populated by
  `BuilderParser` alongside `BuildAnchors`.
- `ConflictKind.DuplicateLogicalId` — new enum member.
- `ConflictDetector.DuplicateLogicalIdConflicts(IReadOnlyList<NodeAnchor> nodeAnchors)
  : IReadOnlyList<Conflict>` — groups by `LogicalId` (Ordinal), preserving first-occurrence order; one
  `Conflict` per id occurring ≥2 times:
  - `Kind = ConflictKind.DuplicateLogicalId`
  - `LogicalId` = the colliding id
  - `Location` = the **second** occurrence's `Span` (the first occurrence is the incumbent)
  - `Reason` names the id and the occurrence count and states that ids must be unique across the whole
    file — an explicit `.Id("...")` is global, not per-parent.
- `BuilderParser.ParseCore` sets
  `Ambiguities = DuplicateNameConflicts(model, anchors)` concatenated with
  `DuplicateLogicalIdConflicts(nodeAnchors)`, in that order. Unconditional, exactly as deliverable 1.
  Parse does not throw.

### 3. Sync heads a handle — the write path can no longer create the hazard

- `Reconciler.DetectAppends`: a create candidate heads its own handle when it has ≥1 create-candidate
  child, OR ≥1 representable component, **OR another node in its snapshot sibling array `nodes` shares
  its `Name`**. The third clause is what stops a duplicate *leaf* falling through to a positional id.
  `nodes` is the full snapshot sibling array, so this covers a duplicate against a mapped sibling as
  well as against another append.
- `Reconciler.InjectHandle(SnapshotNode)` (the duplicate-name pass's per-member action) calls
  `ResolveOwnerHandle(logicalId)` — the ONE handle-introduction path, shared with reparent and
  component-attach — which derives the name, adds it to `reserved`, and `Rekey`s the sidecar so the
  GlobalObjectId follows the id.
- `IntroduceHandle : SourceEdit` — new record, the carrier that makes a handle introduction reach the
  applier:
  ```csharp
  public sealed record IntroduceHandle : SourceEdit
  {
      // Inherited Anchor = LogicalId of the statement to rewrite into `var <Handle> = ...`
      // (its id BEFORE the rekey, which is what `anchors` is still keyed by).
      public string Handle { get; init; } = "";
  }
  ```
  `SourcePatchApplier.ResolveHandleIntroductions` gains
  `case IntroduceHandle h: Request(h.Anchor, h.Handle, true); break;` in its edit scan, and the main
  dispatch switch gains `case IntroduceHandle: break;` (the pre-pass does all the work; the `default`
  arm throws otherwise). Routing through that pre-pass is required, not incidental: it guarantees ONE
  `var` per anchor, so a node that is both a reparent target and a duplicate-name heal in the same sync
  does not get two declarations.
- `Reconciler.EnsureNoAmbiguousDuplicateNames` keeps its current shape: it runs over the SNAPSHOT
  (the post-sync truth), so ONE rule covers every way a duplicate arises — an append, a rename onto a
  sibling's name, and a hand-authored group.
- **Healing is only legal while the mapping is still trustworthy** — i.e. when no reorder is pending
  among the group. Injecting then pins the current, correct mapping before a reorder can scramble it.
- `Reconciler.IsPositionalInSource`'s append arm tests `append.Handle == null` only.
- Retired: `AppendStatement.ExplicitId`, `IntroduceIdCall`, `SourcePatchApplier.ResolveIntroduceIdCall`,
  `Reconciler.MintId`, and the `if (edit.ExplicitId != null) chain += ".Id(...)"` arm at
  `SourcePatchApplier.cs:757-759`. Sync never emits `.Id(...)`.

### 4. The minted handle: deterministic and semantic

`HandleNaming.Derive(node.Name, reserved)` — camelCase, invalid chars stripped, keyword/leading-digit
safe, then uniquified by numeric suffix from 2 (`HandleNaming.cs:109-129`). `reserved` already holds
every known LogicalId and handle, so the name is collision-checked against the whole file. Derived
from the object's name, so an LLM rewriting the file keeps it meaningful and stable; a GUID would not
survive that.

**The suffix only appears on a real collision.** `Derive` is verified to return:

| `node.Name` | `reserved` | result |
| --- | --- | --- |
| `Enemy` | `{}` | `enemy` |
| `Enemy` | `{Enemy/0}` | `enemy` |
| `Enemy` | `{Enemy/0, Enemy-2}` | `enemy` |
| `Enemy` | `{enemy}` | `enemy2` |
| `Enemy` | `{enemy, enemy2}` | `enemy3` |

A positional id (`Enemy/0`) and an explicit id (`Enemy-2`) do not collide with the identifier `enemy`,
so the common case emits `var enemy`, not `var enemy2`.

**Why a handle and not a positional index:** a `var` lives **IN** the statement; a sibling index is
only **IMPLIED BY** the statement's position. Every edit that moves statements preserves the former
and destroys the latter. That is the whole structural argument.

**Only one positional member per group is needed.** With `{Enemy/0 (positional), enemy (handled)}` the
`(parent,"Enemy")` claim queue holds exactly one entry, so the sole positional statement claims it
regardless of document position. So the pass targets all members of a group **but the first**.

### 5. Sync re-mints a colliding LogicalId

A collision is a property of the SOURCE alone — it needs no snapshot — so it is repaired before
reconcile, by a source-only pre-pass at the one path that writes source.

- `SceneBuilder.Core.Reconcile.IdCollisionHealer` — new **public** static class (the Unity adapter
  references Core as a DLL and cannot see internals):
  ```csharp
  public static class IdCollisionHealer
  {
      /// Returns healed source, or `source` unchanged when there is nothing to heal.
      public static string Heal(string source, ParseResult parse);
  }
  ```
- No-op when `parse.Ambiguities` contains no `ConflictKind.DuplicateLogicalId`.
- **The first occurrence in document order is the incumbent and is never touched.** It keeps the id
  and therefore keeps the sidecar's GlobalObjectId. Every later occurrence is re-minted. This is what
  makes the repair deterministic and non-guessing: the newcomer becomes a new identity, which is
  exactly what a pasted `Add` statement means.
- For each later occurrence, by its `NodeAnchor`:
  - `Handle != null` ⇒ **skip** (report-only; see Out of scope). The conflict still stands, so Build
    still refuses.
  - `Handle == null` ⇒ rewrite its statement into `var <h> = ...;` where
    `h = HandleNaming.Derive(nodeAnchor.Name, reserved)`; and when `IdCallSpan != null`, splice that
    `.Id(...)` invocation out of the chain (it is dead once a handle takes priority 1, and leaving the
    duplicate literal in the file lets the collision resurrect if the `var` is later removed). Reuse
    the established chained-call-removal shape (`SourcePatchApplier.ResolveRemoveFlagCall`) and
    handle-declaration shape (`SourcePatchApplier.BuildHandleDeclaration`).
  - `reserved` is seeded with every `NodeAnchor.LogicalId`, every authored `Handle`, and every name
    minted earlier in this pass.
- Formatting-preserving and deterministic.
- Re-minting cannot strand identity: the re-minted node has no sidecar entry, so `DetectRemovals`
  (which walks sidecar entries) never sees it and cannot emit a `RemoveStatement` for the user's
  pasted statement, and `DetectAppends` (which walks unmapped snapshot goids) never re-appends the
  mapped incumbent.

### 6. Build refuses

`SceneBuilderBuild.Run`: if `parse.Ambiguities` is non-empty, **throw a located `ParseException`
naming each conflict** — *before* Materialize/Execute, so nothing touches the scene. Never guess.
Because both detections report through `Ambiguities`, Build refuses on a colliding id for free.

`SceneBuilderBuild.FormatAmbiguities` renders a kind-neutral header
(`[SceneBuilder] Build REFUSED: {n} identity conflict(s) in {path}.`) followed by one
`{file}({line},{col}): {conflict.Reason}` line per conflict. The per-kind instruction lives in each
`Reason`, so the message stays correct as kinds are added.

## Editor adapter deliverables

- `SceneBuilderBuild.Run` — the refusal above.
- `SceneBuilderSync.Run` — after `DesiredModelLoader.Load` and **before** `Reconciler.Reconcile`:
  if `parse.Ambiguities` contains a `DuplicateLogicalId`, call `IdCollisionHealer.Heal(source, parse)`;
  when the result differs, write it via `SceneBuilderPaths.WriteIfChanged`, re-run
  `DesiredModelLoader.Load` on the healed source, and reconcile against THAT. The extra parse is paid
  only when a collision exists, so the steady-state sync cost is unchanged.
- Injected handles surface like any other patch (no new user-facing surface).

## Authoring API

No new authoring surface. A `var` handle (§4 priority 1) and `.Id("...")` (§4 priority 2) both already
exist; this milestone makes the tool *write* the handle when it would otherwise create an ambiguity,
and `.Id("...")` remains the manual escape hatch.

```csharp
scene.Add("Enemy").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
var enemy = scene.Add("Enemy").Transform(pos: (5f, 0f, 0f));  // <- handle introduced by Sync
```

## IdentityMap / sidecar changes

None structurally. An introduced handle re-keys the affected entries through the existing
`Reconciler.Rekey` path (the GlobalObjectId moves with the id; components follow their owner).

## Accepted risk — a handle is the token refactoring tools believe is meaningless

An IDE "rename symbol", or an LLM tidying `enemy` → `secondEnemy`, silently re-keys the LogicalId and
strands the sidecar entry. It compiles, so no gate catches it. This is accepted, and is a live
constraint rather than a hypothetical:

- Handles are ALREADY priority-1 ids, and `Reconciler.ResolveOwnerHandle` already auto-introduces them
  for component-attach and reparent. This extends an existing exposure; it does not create a class.
- `.Id("Enemy-2")` → `.Id("Enemy2")` does identical damage. An explicit id is protected only by
  convention, not by the language.
- A handle is unsafe against *renames*; an explicit id is unsafe against *copy-paste*. Copy-paste is
  the far more common way duplicates get created, and the compiler refuses a same-scope duplicate
  `var` (CS0128) at a chokepoint that runs in the user's IDE the instant they paste — the builder has
  its own `.csproj` outside `Assets/` (CLAUDE.md) — and again in `BuilderCompileCheck` on every sync
  that writes. CS0128 is **scope-local**, so it does not cover a collision across two closures; that
  is what deliverable 2's parse-time detection is for.

`IdentityRemapper`'s name tier is the recovery path for a renamed handle, not a guarantee.

## Core test plan (RED)

Existing tests in `SceneBuilder.Core.Tests/DuplicateSiblingNameTests.cs` that MUST be updated:

1. `Reconcile_AppendDuplicateName_InjectsDeterministicSemanticId` → assert
   `append.Handle == "enemy"` and `append.NewLogicalId == "enemy"` (was `ExplicitId == "Enemy-2"`).
   `reserved` is `{Enemy/0}`, so `Derive` yields `enemy` — **not** `enemy2`. The AddedEntry assertion
   becomes `LogicalId == "enemy" && GlobalObjectId == "goid-NEW"`.
2. `Reconcile_AppendDuplicateName_MintedIdAvoidsCollisionWithAuthoredId` → the fixture must change:
   `Enemy-2` being taken no longer forces a suffix, because an explicit id does not collide with an
   identifier. Make the incumbent an **authored handle**: model root `GameObjectNode { LogicalId =
   "enemy", Name = "Enemy" }` mapped to `goid-A`, plus an unmapped `goid-NEW` named `Enemy`. Assert
   `append.Handle == "enemy2"`.
3. `Reconcile_PreExistingAmbiguousGroup_InjectsIdAndRekeysSidecar` → assert
   `Assert.Single(result.Patch.Edits.OfType<IntroduceHandle>())` with `Anchor == "Enemy/1"` and
   `Handle == "enemy"`; AddedEntries contains `enemy`↔`goid-B`; RemovedLogicalIds contains `Enemy/1`.
4. `Reconcile_ThenApply_HealsAmbiguousSourceIntoAnUnambiguousFile` → assert the healed source contains
   `var enemy` (was `.Id("Enemy-2")`), that `BuilderParser.Parse(healed, prior).Ambiguities` is empty,
   that the healed model still has 2 roots, and that one root's LogicalId is `enemy`.
5. `Reorder_TwoSameNamedPositionalSiblings_SwapsIdentity_AndParseReportsTheAmbiguity`,
   `DeleteFirstDuplicate_DestroysTheKeptObject_AndParseReportedTheAmbiguity`,
   `Reorder_DisambiguatedSameNamedSiblings_PreservesIdentityAndComponent`,
   `Parse_SameNamedSiblingsWithHandles_ReportsNoAmbiguity`,
   `Parse_SameNameUnderDifferentParents_ReportsNoAmbiguity`,
   `Parse_MixedExplicitAndPositionalDuplicates_StillReportsTheTwoPositionalOnes` — must keep passing
   unchanged. They pin defect 1 and the §4 policy, and none of their fixtures collide on id.

New tests:

6. `FlattenModel_CollidingExplicitIds_SilentlyDropsANode` — **the RED that proves the data loss before
   the fix.** Parse two `scene.Add("Enemy").Id("Enemy-2");` statements, reconcile against a snapshot
   holding TWO Enemies (`goid-A` mapped to `Enemy-2`, `goid-B` unmapped), and assert the pre-fix
   behaviour is data loss: the model the reconcile sees carries ONE `Enemy-2` node although the source
   declares two. **(Observed: `Parse` yields ids `Enemy-2 | Enemy-2`, `Ambiguities` empty, source
   compiles clean.)**
7. `Parse_TwoStatementsWithTheSameExplicitId_ReportsDuplicateLogicalId` — `Ambiguities` holds exactly
   one `Conflict`, `Kind == ConflictKind.DuplicateLogicalId`, `LogicalId == "Enemy-2"`, `Location` is
   the SECOND statement's span, `Reason` names `Enemy-2`.
8. `Parse_HandleAndExplicitIdCollide_ReportsDuplicateLogicalId` — `var enemy = scene.Add("A");
   scene.Add("B").Id("enemy");` ⇒ one `DuplicateLogicalId` on `enemy`. The compiler cannot see this.
9. `Parse_SameExplicitIdUnderDifferentParents_ReportsDuplicateLogicalId` — explicit ids are global;
   two parents do not make `dup` unique.
10. `Parse_HandlesCollidingAcrossSiblingClosures_ReportsDuplicateLogicalId` — `var enemy` inside two
    different block-bodied closures ⇒ one `DuplicateLogicalId` on `enemy`. CS0128 does not fire here.
11. `Parse_UniqueIds_ReportsNoDuplicateLogicalId` — no false positives on any existing fixture shape.
12. `NodeAnchors_CollidingIds_AreNotCollapsed` — `ParseResult.NodeAnchors` has 2 entries for the
    colliding id while `ParseResult.Anchors` has 1. This is the property the detection depends on.
13. `Heal_CollidingExplicitIds_RemintsTheSecondOccurrenceOnly` — the healed source declares
    `var enemy` on the SECOND statement, the first is byte-identical to its input, the dead
    `.Id("Enemy-2")` is gone from the second, and a re-parse reports no `DuplicateLogicalId`.
14. `Heal_CollidingHandles_IsAReportOnlyNoOp` — two colliding authored handles ⇒ `Heal` returns the
    source unchanged and the conflict survives a re-parse (so Build still refuses).
15. `Heal_NoCollision_ReturnsSourceUnchanged` — reference-equal / byte-identical.
16. `Heal_RemintedHandleAvoidsEveryIdAndHandleInTheFile` — a file already containing `var enemy` forces
    the re-mint to `enemy2`.
17. `Heal_ThenReconcile_DoesNotRemoveOrDuplicateTheIncumbent` — after healing, reconcile against a
    snapshot holding only the incumbent emits no `RemoveStatement` for the re-minted statement and no
    `AppendStatement` for the incumbent.
18. Round-trip: after a handle is introduced, a reorder of the (now-disambiguated) pair preserves both
    identities.

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/Assets/GateTests/`)

Existing tests in `unity-gate/Assets/GateTests/DuplicateSiblingNameTests.cs` that MUST be updated:

1. `Sync_DuplicateNamedSiblingCreatedInScene_InjectsDeterministicSemanticId` (line 130) — the emitted
   source assertion becomes `StringAssert.Contains("var enemy", emitted)` (was
   `StringAssert.Contains(".Id(\"Enemy-2\")", emitted)`). The builder is `scene.Add("Enemy");`, so
   `reserved` is `{Enemy/0}` and `Derive` yields **`enemy`**, not `enemy2`. The
   `Assert.IsEmpty(BuilderParser.Parse(emitted).Ambiguities)` assertion stands unchanged.
2. `Build_HandAuthoredDuplicateSiblings_RefusesAndLeavesSceneUntouched` — stands unchanged;
   `StringAssert.Contains(".Id(", error.Message)` is satisfied by the `AmbiguousAnchor` Reason.
3. `Build_DuplicateSiblingsWithExplicitIds_BuildsBothObjects` and
   `Reorder_DuplicateNamedSiblings_PreservesObjectAndComponentIdentity` — stand unchanged.

New EditMode tests:

4. Duplicate a GameObject in the Hierarchy (Unity names the copy identically under the same parent);
   Sync. **Expected:** the emitted statement declares a `var` handle; the file parses, **compiles**
   (`BuilderCompileCheck.Check` returns no errors), and converges (a second Sync is a no-op:
   `SyncResult.PatchEdits == 0`).
5. Hand-author a builder with two `scene.Add("Enemy").Id("Enemy-2");` statements and Build.
   **Expected:** Build REFUSES with a located error naming `Enemy-2`; the scene is untouched (0 root
   GameObjects).
6. Hand-author the same colliding pair, Build the incumbent's scene state, then make a scene edit and
   Sync. **Expected:** the file is re-minted (`var enemy` on the SECOND statement only), it compiles,
   a subsequent Build no longer refuses, and the incumbent's `GlobalObjectId`/`EntityId` survives —
   the re-mint must not destroy and recreate the object the sidecar already tracked.
7. Give a synced, handle-headed duplicate a Rigidbody, reorder the pair, Sync, Build. **Expected:**
   both keep their identity; the Rigidbody is neither destroyed nor moved to the other object.
8. **Fuzzer (`SyncFuzzTests`)** — the real gate:
   - **duplicate names:** ~1-in-4 `CreateRoot`/`CreateChild`/`RenameObject` reuse an EXISTING
     sibling's name instead of minting `Fuzz`+n.
   - **reorder op:** a `ReorderSibling` op — `t.SetSiblingIndex(rng.Next(parent.childCount))`.
   - **INVARIANT 6 — identity preservation.** The five original invariants are blind to this defect:
     the emitted source still parses, compiles and converges, because it faithfully describes the
     *wrong-but-self-consistent* scene. Snapshot every live GameObject's/Component's identity
     (`GetEntityId()` — `GetInstanceID()` is a compile error in 6000.5.3f1) before each step; assert
     no pre-existing object/component was destroyed-and-recreated by an operation that shouldn't have
     (e.g. a pure reorder). **This is the invariant that names the real defect.**

## Dependencies

M1 (LogicalId derivation), M2/M2b (Reconcile + SourcePatch, `AppendStatement`, `Rekey`), M4 (component
identity).

## Risks/notes

- `ReconcileConflictTests` and `ConflictDetector.DetectAmbiguousReorders` cover the scene→code half of
  the §4 policy and must keep passing: `Parse` reports conflicts but does not throw, so those fixtures
  still parse.
- `Reconciler.cs:200-204` suppresses **every** edit for an ambiguous sibling group, not just the
  reorder — a permanently unsyncable region that violates the product thesis. Deliverable 3's
  self-heal is what retires it, and the suppression is reachable only in the one case a handle cannot
  rescue (the group is ALREADY scrambled by a pending reorder, so there is nothing sound to pin —
  injecting would make a guess permanent). Every other ambiguous group is healed on the next sync,
  after which it is never ambiguous again and edits flow. Do not widen the suppression.
- `IdentityRemapper`'s name tier (`remaining.Find(e => e.Name == ...)`) is left as-is, deliberately.
  Refusing to match on ambiguity turns a silent *swap* into a silent *destroy* (unmatched ⇒
  `GlobalObjectId=""` ⇒ create + orphan ⇒ destroy) — strictly worse, and still silent, because
  `Remap` returns an `IdentityMap` and has no conflict channel to surface a refusal through. Nor is
  the tier where the observed damage occurs: in the reorder repro both siblings match at tier (a)
  (exact LogicalId equality) on ids the resolver had already pinned to the wrong slots, so tiers
  (b)/(c) never run. There is no correct answer without ids — which is the entire point — so the fix
  is prevention (deliverables 1/2/3/5/6), and `Remap` never sees an ambiguous group once the file
  cannot contain one.
- A re-minted handle changes what a *code-side* reader calls the object, but the incumbent keeps the
  id, so no sidecar entry is stranded by `IdCollisionHealer`. The stranding risk that remains is a
  human/LLM renaming a `var` — see Accepted risk.
