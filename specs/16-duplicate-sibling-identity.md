# M-Dup — Duplicate sibling names must never silently destroy data

## Goal

Two GameObjects that share a name under the same parent must keep their identities across any edit.
Today they are distinguished **only by position**, so a pure reorder swaps them (destroying a real
component and recreating it on the wrong object) and deleting the first duplicate destroys the object
the user *kept*. Both are silent: the resulting scene is self-consistent, so nothing surfaces.

This milestone closes the hole at the class level — the ambiguous pair can no longer *exist* in a file
the tool wrote, and a hand-authored one is **refused, never guessed**.

## The defect (observed, not theorized)

`LogicalIdResolver.Synthesize` mints `{parent}/{name}/{siblingIndex}`, and `LogicalIdResolver.Resolve`
claims persisted ids from a `(parent, name)`-keyed queue in **document order**
(`LogicalIdResolver.cs:63-69`). Ids are therefore pinned to **slots, not objects**. Observed against
real Core (`SceneBuilder.Core.Tests/DuplicateSiblingNameTests.cs`):

- **Reorder** two same-named siblings ⇒ the Rigidbody-owning `Enemy` (really `goid-A`) comes back as
  `goid-B`. Its live Rigidbody `goid-A-rb` goes unconsumed ⇒ orphan ⇒ **DESTROYED**, and a new one is
  **CREATED** on the wrong object. A pure reorder destroys a real component.
- **Delete the FIRST duplicate** ⇒ the survivor (really `goid-B`) comes back as `goid-A`: the tool
  destroys the object the user kept and repurposes the one they deleted.

`IdentityRemapper`'s name tier (`IdentityRemapper.cs:134`, `remaining.Find(e => e.Name == ...)`) is
structurally blind for duplicates — it returns the FIRST match, so tier (b) degenerates into tier (c)
positional matching.

## The policy already exists — this milestone finishes enforcing it

§4 already states: *"If a synthesized id becomes ambiguous (e.g. two same-named siblings reordered),
Reconcile surfaces a **conflict** rather than guessing."* `ConflictDetector.DetectAmbiguousReorders`
implements exactly that — **but only for scene→code**. The **Build (code→scene)** path has no such
guard and silently guesses. This is an UNENFORCED EXISTING POLICY, not an open design question.

## In scope

- Detect the ambiguity at the ONE chokepoint both directions reach: `BuilderParser.Parse`.
- Sync (the only path that writes builder source) injects `.Id(...)` so the file never *contains* an
  ambiguous pair — both for newly-appended duplicates (prevention) and for a pre-existing
  hand-authored group (self-heal, while the positional mapping is still trustworthy).
- Build **refuses** (located conflict, §7) rather than guessing.
- Fuzzer: generate duplicate names, add a reorder op, and add **invariant 6 — identity preservation**.

## Out of scope

- **Auto-renaming the user's objects. REJECTED** — Unity explicitly permits duplicate sibling names;
  do not fight the engine's data model.
- Random-GUID ids. The builder file is rewritten by an LLM (see CLAUDE.md); an opaque GUID will not
  survive that. The minted id must be deterministic and semantic.
- Build *injecting* `.Id(...)` — Build never writes the builder file. Build only refuses; Sync heals.

## Core deliverables

### 1. Detection at the chokepoint — `BuilderParser` / `LogicalIdResolver`

- A node's id is **positional** when it has neither an authored handle nor an explicit `.Id(...)` —
  i.e. exactly when `LogicalIdResolver.Resolve` falls through to the claim-queue/`Synthesize` path.
- `ParseResult.Ambiguities : IReadOnlyList<Conflict>` — one `Conflict` per ambiguous sibling group:
  **≥2 siblings that share a `Name` and are BOTH positional**. `Kind = AmbiguousAnchor`,
  `Location` = the offending statement's anchor span, `Reason` names the objects and instructs the
  user to add `.Id("...")` (§7: fail loud, located).
- **Contract: detection is unconditional.** Every `BuilderParser.Parse` computes it; there is no
  opt-in flag a caller can forget. Parse does **not** throw — Sync must be able to parse an ambiguous
  file in order to heal it. The *policy* (refuse vs heal) belongs to each consumer.

### 2. Sync injects `.Id(...)` — the write path can no longer create the hazard

- `AppendStatement.ExplicitId : string?` — when set, the applier renders `.Id("<value>")` into the
  emitted statement and `NewLogicalId == ExplicitId`.
- `Reconciler`: when appending an object whose `Name` collides with an existing sibling under the same
  parent, mint an id **at that moment**.
- `IntroduceIdCall : SourceEdit` — injects `.Id("...")` into an EXISTING statement, to heal a
  pre-existing hand-authored ambiguous group. Reuses `Reconciler.Rekey` (the single re-key path) so
  the sidecar follows the id change and the GlobalObjectId is not stranded.
- **Healing is only legal while the mapping is still trustworthy** — i.e. when no reorder is pending
  among the group. Injecting then pins the current, correct mapping before a reorder can scramble it.

### 3. The minted id: deterministic and semantic

`{name}-{n}`, smallest `n ≥ 2` not already taken (checked against the Reconciler's `reserved` set of
every known LogicalId/handle). `Enemy`, `Enemy-2`, `Enemy-3`. Derived from the name, so an LLM
rewriting the file keeps it meaningful and stable; a GUID would not survive that.

**Why `.Id(...)` and not a better index:** `.Id(...)` lives **IN** the statement; a sibling index is
only **IMPLIED BY** the statement's position. Every edit that moves statements preserves the former
and destroys the latter. That is the whole structural argument.

**Only one positional member per group is needed.** With `{Enemy/0(positional), Enemy-2(explicit)}`
the `(parent,"Enemy")` claim queue holds exactly one entry, so the sole positional statement claims it
regardless of document position. So injection targets all members of a group **but the first**.

### 4. Build refuses

`SceneBuilderBuild.Run`: if `parse.Ambiguities` is non-empty, **throw a located error naming each
ambiguous group and telling the user to add `.Id("...")`** — *before* Materialize/Execute, so nothing
touches the scene. Never guess.

## Editor adapter deliverables

- `SceneBuilderBuild.Run` — the refusal above.
- `SceneBuilderSync` — surfaces injected-id edits like any other patch (no new user-facing surface).

## Authoring API

No new authoring surface. `.Id("...")` already exists (§4 priority 2); this milestone makes the tool
*write* it when it would otherwise create an ambiguity.

```csharp
scene.Add("Enemy").Transform(pos: (0f, 0f, 0f)).Component<UnityEngine.Rigidbody>();
scene.Add("Enemy").Id("Enemy-2").Transform(pos: (5f, 0f, 0f));  // <- injected by Sync
```

## IdentityMap / sidecar changes

None structurally. An injected `.Id(...)` re-keys the affected entries through the existing
`Reconciler.Rekey` path (the GlobalObjectId moves with the id; components follow their owner).

## Core test plan (RED)

1. `Reorder_TwoSameNamedSiblings_DoesNotSwapIdentityOrDestroyComponent` — parse+`Remap` a swapped pair
   against a sidecar; the Rigidbody's owner keeps `goid-A`, its Rigidbody keeps `goid-A-rb`, and no
   live component is orphaned. **(Observed RED: owner came back `goid-B`.)**
2. `DeleteFirstDuplicate_KeepsTheSurvivingObjectsIdentity` — survivor keeps `goid-B`; `goid-A` is the
   orphan. **(Observed RED: survivor came back `goid-A`.)**
3. `Parse_TwoPositionalSameNamedSiblings_ReportsAmbiguity` — `Ambiguities` non-empty, located, names
   both statements.
4. `Parse_SameNamedSiblingsDisambiguatedById_ReportsNoAmbiguity` — one `.Id(...)` is enough.
5. `Parse_SameNamedSiblingsWithHandles_ReportsNoAmbiguity` — handles are ids too.
6. `Parse_SameNameUnderDifferentParents_ReportsNoAmbiguity` — the group is per-parent.
7. `Reconcile_AppendDuplicateName_InjectsDeterministicSemanticId` — emits `.Id("Enemy-2")`, not a GUID.
8. `Reconcile_AppendDuplicateName_MintedIdAvoidsCollision` — `Enemy-2` taken ⇒ `Enemy-3`.
9. `Reconcile_PreExistingAmbiguousGroup_InjectsIdAndRekeysSidecar` — GlobalObjectId not stranded.
10. Round-trip: after injection, a reorder of the (now-disambiguated) pair preserves both identities.

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/`)

1. Build a scene with two same-named siblings authored with `.Id(...)`; reorder them in the Hierarchy;
   Sync. **Expected:** both keep their `GlobalObjectId`; no component destroyed/recreated.
2. Duplicate a GameObject in the Hierarchy (Unity names the copy identically under the same parent);
   Sync. **Expected:** the emitted statement carries an injected `.Id("<Name>-2")`; the file parses,
   compiles, converges.
3. Hand-author a builder with two positional same-named siblings and Build. **Expected:** Build
   REFUSES with a located error naming both and instructing `.Id("...")`; the scene is untouched.
4. **Fuzzer (`SyncFuzzTests`)** — the real gate:
   - **duplicate names:** ~1-in-4 `CreateRoot`/`CreateChild`/`RenameObject` reuse an EXISTING
     sibling's name instead of minting `Fuzz`+n (today a monotonic `nameCounter` makes collisions
     impossible by construction).
   - **reorder op:** a `ReorderSibling` op — `t.SetSiblingIndex(rng.Next(parent.childCount))`. Today
     there is no reorder op at all (`rng.Next(11)`; `SetSiblingIndex` never called; `Reparent` skips
     same-parent moves).
   - **INVARIANT 6 — identity preservation.** All five existing invariants are blind to this defect:
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
  this policy and must keep passing: `Parse` reports ambiguity but does not throw, so those fixtures
  still parse.
- `Reconciler.cs:200-204` suppresses **every** edit for an ambiguous sibling group, not just the
  reorder — a permanently unsyncable region that violates the product thesis. Deliverable 2's
  self-heal is what retires it, and the suppression is now reachable only in the one case an id
  cannot rescue (the group is ALREADY scrambled by a pending reorder, so there is nothing sound to
  pin — injecting would make a guess permanent). Every other ambiguous group is healed on the next
  sync, after which it is never ambiguous again and edits flow. Do not widen the suppression.
- `IdentityRemapper`'s name tier (`remaining.Find(e => e.Name == ...)`) is left as-is, deliberately.
  Refusing to match on ambiguity turns a silent *swap* into a silent *destroy* (unmatched ⇒
  `GlobalObjectId=""` ⇒ create + orphan ⇒ destroy) — strictly worse, and still silent, because
  `Remap` returns an `IdentityMap` and has no conflict channel to surface a refusal through. Nor is
  the tier where the observed damage occurs: in the reorder repro both siblings match at tier (a)
  (exact LogicalId equality) on ids the resolver had already pinned to the wrong slots, so tiers
  (b)/(c) never run. There is no correct answer without ids — which is the entire point — so the fix
  is prevention (deliverables 1/2/4), and `Remap` never sees an ambiguous group once the file cannot
  contain one.
