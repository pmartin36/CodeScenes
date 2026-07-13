# Feature: scenebuilder-core-m0-m2
SOURCE: Build the SceneBuilder Core (Unity-free .NET) through milestones M0, M1, M2. Authoritative specs: specs/00-foundation.md (the contract), specs/01-m0-skeleton.md (M0), specs/02-m1-hierarchy-transforms.md (M1), specs/03-m2-syncback-transform.md (M2). Scope: SceneBuilder.Core + SceneBuilder.Core.Tests only. Excludes SceneBuilder.Editor / unity/ (Unity-dependent). Unity-only inputs (GlobalObjectId, live-scene reads) are supplied to Core as POCO SceneSnapshot fixtures; Core is tested as pure functions over POCOs (§8). No mocks of UnityEngine.
GATE_COMMAND: export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln

# Notes on scope decisions (authorized by the task prompt, not unilateral):
# - The Unity 6 project, SceneBuilder.Editor asmdef, PlanExecutor, SceneBuilderMenu, and the
#   Editor-side fluent builder are OUT OF SCOPE per the task ("EXCLUDE the SceneBuilder.Editor Unity
#   adapter and the unity/ project entirely"). Core behaviors that need Unity-only inputs are tested
#   over POCO fixtures instead.
# - The SceneBuilder.Core/ package.json + SceneBuilder.Core.asmdef (Unity embedded-package files) are
#   OUT OF SCOPE now per the task ("the SceneBuilder.Core/ folder later also becomes a Unity package
#   but that is OUT OF SCOPE now"). Only the dotnet consumer (csproj/sln/tests) is built.
# - The M1/M2 Roslyn parser parses a hand-written ISceneDefinition builder .cs file. Since the
#   Editor-side fluent authoring API is out of scope, parser tests use builder-file fixtures matching
#   the §6/M1 authoring surface (scene.Add / .Transform / .Tag / .Layer / .Active / .Static / .Id /
#   closures) as plain source text.

## Bucket b0: M0 — Skeleton & harness (Core: Plan, IdentityMap, deterministic JSON)
INTEGRATION SEAMS: SceneBuilder.sln ties Core + Tests for `dotnet test` (the CI gate). CanonicalJson (b0-t2) is the shared deterministic-JSON substrate consumed by PlanJson (b0-t3) and IdentityMapJson (b0-t4); both must produce fixed key order, invariant culture, and `\n` newlines through it. Plan/PlanOp/CreateObject and IdentityMap POCOs are the on-disk contract every later milestone extends (Plan grows PlanOps in M1; IdentityMap gains real GlobalObjectIds in M1).

### Task b0-t1: Solution + Core/Tests project scaffold
DESCRIPTION: Create SceneBuilder.Core (netstandard2.1 class library; PackageReference Microsoft.CodeAnalysis.CSharp and System.Text.Json) and SceneBuilder.Core.Tests (net8.0 xUnit, ProjectReference → Core), tied by SceneBuilder.sln at repo root. Create the Model/, Plan/, Identity/, Serialization/ source folders per the M0 on-disk layout. Do NOT create the unity/ project, Editor asmdef, package.json, or Core asmdef (out of scope).
DELIVERABLE: `export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln` runs from repo root and exits 0 (0 tests is acceptable at this point); `dotnet build SceneBuilder.sln` succeeds; SceneBuilder.sln, SceneBuilder.Core/SceneBuilder.Core.csproj, SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj all exist.
DEPENDS_ON: none
TOUCHES: [SceneBuilder.sln, SceneBuilder.Core/SceneBuilder.Core.csproj, SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj, .gitignore]
BEHAVIORAL: no
TEST_RECOMMENDATION: skip
TEST_RATIONALE: Pure scaffolding; the deliverable IS that `dotnet test` runs green. No behavior to assert yet.
ASSUMPTIONS: [Microsoft.CodeAnalysis.CSharp is compatible referenced from a netstandard2.1 library and consumable by a net8.0 test project; System.Text.Json is available on netstandard2.1 via the package.]

### Task b0-t2: CanonicalJson deterministic-serialization helper
DESCRIPTION: A shared helper (JsonSerializerOptions/writer utilities) that guarantees deterministic output: fixed property key order (declaration order via [JsonPropertyOrder] or a custom writer), CultureInfo.InvariantCulture number formatting, and `\n` newlines (normalize any `\r\n`). This is the single substrate both PlanJson and IdentityMapJson use — put determinism here so no serializer re-implements it (§8 determinism).
DELIVERABLE: A CanonicalJson type in SceneBuilder.Core/Serialization exposing serialize/deserialize entry points that emit invariant-culture, `\n`-newline JSON with stable key order; compiles and is referenced by later serializers.
DEPENDS_ON: [b0-t1]
TOUCHES: [SceneBuilder.Core/Serialization/CanonicalJson.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Determinism (byte-identical, invariant culture, `\n` newlines) is a load-bearing contract; a focused test guards drift even though the milestone tests exercise it transitively.
ASSUMPTIONS: [System.Text.Json can produce fixed key order via [JsonPropertyOrder]/property declaration order; if not, a custom Utf8JsonWriter path is used.]

### Task b0-t3: Plan + PlanOp + CreateObject POCOs and PlanJson
DESCRIPTION: Define `Plan { SchemaVersion:int; ScenePath:string; Ops:PlanOp[] }`, abstract `PlanOp`, concrete `CreateObject:PlanOp { LogicalId:string; Name:string }` (M0's only op; remaining §5 ops reserved for M1). Implement PlanJson.Serialize/Deserialize over CanonicalJson with an `op` discriminator; deserializing an unknown `op` throws a fail-loud, located error naming the offending op token (§7).
DELIVERABLE: Tests `Plan_WithSingleCreateObject_RoundTripsThroughJson`, `Plan_Serialize_IsByteIdenticalAcrossCalls`, `CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields`, `PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation` pass under the gate.
DEPENDS_ON: [b0-t2]
TOUCHES: [SceneBuilder.Core/Plan/Plan.cs, SceneBuilder.Core/Plan/PlanOp.cs, SceneBuilder.Core/Plan/CreateObject.cs, SceneBuilder.Core/Serialization/PlanJson.cs, SceneBuilder.Core.Tests/PlanJsonTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Round-trip equality, cross-call byte-identity, exact-field shape, and located unknown-op failure are explicit spec contracts (M0 Core test plan).
ASSUMPTIONS: [Polymorphic PlanOp (de)serialization keyed on an `op` string discriminator; CreateObject serializes exactly {op, logicalId, name} with no transform/component fields.]

### Task b0-t4: IdentityMap POCOs and IdentityMapJson
DESCRIPTION: Define `IdentityMap { SchemaVersion:int; Scene:string; Entries:IdentityMapEntry[]; Assets:AssetEntry[] }`, `IdentityMapEntry { LogicalId; GlobalObjectId; Kind:"GameObject"|"Component"; ComponentType?; ParentLogicalId? }`, `AssetEntry { Guid; LastKnownPath; TypeHint }`. Implement IdentityMapJson.Serialize/Deserialize over CanonicalJson producing the §4 file shape with top-level keys `schemaVersion, scene, entries, assets` in that exact order; empty Assets serialize as `[]`; Entries order preserved.
DELIVERABLE: Tests `IdentityMap_RoundTripsThroughJson_PreservingEntryOrder`, `IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder`, `IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray` pass under the gate.
DEPENDS_ON: [b0-t2]
TOUCHES: [SceneBuilder.Core/Identity/IdentityMap.cs, SceneBuilder.Core/Identity/IdentityMapEntry.cs, SceneBuilder.Core/Identity/AssetEntry.cs, SceneBuilder.Core/Serialization/IdentityMapJson.cs, SceneBuilder.Core.Tests/IdentityMapJsonTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Exact key order, entry-order preservation, and empty-array shape are explicit M0 contracts against the §4 sidecar format.
ASSUMPTIONS: [ParentLogicalId null serializes as JSON null; GlobalObjectId is "" (empty string) in M0.]

## Bucket b1: M1 — Flat hierarchy + transforms, one-way (parse → SceneModel → Diff → Materialize → Plan)
INTEGRATION SEAMS: The §3 data model (b1-t1) is the shared spine consumed by SceneSnapshot (b1-t2), ChangeSet (b1-t3), the canonical serializer (b1-t5), the parser (b1-t6/t7), Diff (b1-t8), and Materialize (b1-t9). Diff keys SceneModel↔SceneSnapshot via the IdentityMap (b0-t4) on LogicalId↔GlobalObjectId. Materialize lowers ChangeSet into extended Plan ops (b1-t4, extending b0-t3) with legal ordering. The parser persists synthesized LogicalIds into the IdentityMap so they stay stable. Euler↔quaternion conversion (b1-t1) is pinned here so M2 write-back can emit Euler deterministically.

### Task b1-t1: §3 data model POCOs + Vec3/Quat + Euler↔Quat conversion
DESCRIPTION: Add `SceneModel { SchemaVersion; Roots:GameObjectNode[] }`, `GameObjectNode { LogicalId; Name; Tag; Layer; Active; IsStatic; Transform; Components; Children }`, `TransformData { Kind; Position:Vec3; Rotation:Quat; Scale:Vec3 }`, value structs `Vec3`, `Quat` per §3, with contract defaults (Tag="Untagged", Layer=0, Active=true, IsStatic=false, identity transform). Include a deterministic Euler-degrees→Quaternion (and inverse for M2) conversion used by parser and serializer. Components stays typed but empty until M3.
DELIVERABLE: Types compile; a unit test pins Euler(0,90,0)→Quat equals the 90° yaw quaternion (and round-trips back to Euler) within tolerance; default-construction test confirms the §3 defaults.
DEPENDS_ON: [b0-t1]
TOUCHES: [SceneBuilder.Core/Model/SceneModel.cs, SceneBuilder.Core/Model/GameObjectNode.cs, SceneBuilder.Core/Model/TransformData.cs, SceneBuilder.Core/Model/Vec3.cs, SceneBuilder.Core/Model/Quat.cs, SceneBuilder.Core/Model/Rotation.cs, SceneBuilder.Core.Tests/RotationConversionTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: The Euler↔quaternion conversion is load-bearing for both M1 parse and M2 Euler emission; a numerical regression here silently corrupts round-trips.
ASSUMPTIONS: [Euler application order matches Unity's ZXY intrinsic convention; tests assert the specific quaternion for a 90° yaw so the convention is pinned.]

### Task b1-t2: SceneSnapshot POCO (actual-state mirror + GlobalObjectId)
DESCRIPTION: Add `SceneSnapshot` mirroring SceneModel's shape where each object/component carries its `GlobalObjectId` (string). For M1 only the structural fields M1 owns are populated (identity+GlobalObjectId, name/tag/layer/active/static, transform, parent, sibling order); Components stay empty. This is the POCO the (out-of-scope) adapter would supply; tests construct it directly as a fixture.
DELIVERABLE: SceneSnapshot type compiles and can represent an empty scene, a matching scene, and a drifted scene as test fixtures consumed by Diff (b1-t8).
DEPENDS_ON: [b1-t1]
TOUCHES: [SceneBuilder.Core/Model/SceneSnapshot.cs, SceneBuilder.Core/Model/SnapshotNode.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: skip
TEST_RATIONALE: Plain data container; exercised behaviorally through Diff/Reconcile tests rather than on its own.
ASSUMPTIONS: [SnapshotNode carries GlobalObjectId plus parent GlobalObjectId (or parent ref) and sibling index so Diff can key on GlobalObjectId and reconstruct hierarchy.]

### Task b1-t3: ChangeSet / ChangeOp POCOs
DESCRIPTION: Add `ChangeSet` (ordered `ChangeOp[]`) and `ChangeOp` variants: `AddNode, RemoveNode, Reparent, Reorder, SetName, SetTag, SetLayer, SetActive, SetStatic, SetTransform` (§ M1 additions). Pure data; produced by Diff (b1-t8), consumed by Materialize (b1-t9).
DELIVERABLE: ChangeSet/ChangeOp types compile and cover all ten variants.
DEPENDS_ON: [b1-t1]
TOUCHES: [SceneBuilder.Core/Diff/ChangeSet.cs, SceneBuilder.Core/Diff/ChangeOp.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: skip
TEST_RATIONALE: Type definitions only; behavior asserted through Diff and Materialize tests.
ASSUMPTIONS: [Each ChangeOp identifies its target node by LogicalId so Materialize can key against the IdentityMap.]

### Task b1-t4: Extend Plan with M1 PlanOps + PlanJson coverage
DESCRIPTION: Add the §5 PlanOps M1 implements (`DestroyObject, SetParent, ReorderChild, SetName, SetTag, SetLayer, SetActive, SetStatic, SetField`) as PlanOp subclasses; `SetField` in M1 is constrained to transform paths `m_LocalPosition`(Vec3)/`m_LocalRotation`(Quat)/`m_LocalScale`(Vec3). Extend PlanJson's `op` discriminator to (de)serialize every new op deterministically via CanonicalJson.
DELIVERABLE: All new PlanOps round-trip through PlanJson (serialize→deserialize→equal) under the gate; a test confirms each op's `op` discriminator value and field shape.
DEPENDS_ON: [b0-t2, b0-t3, b1-t1]
TOUCHES: [SceneBuilder.Core/Plan/PlanOps.cs, SceneBuilder.Core/Serialization/PlanJson.cs, SceneBuilder.Core.Tests/PlanOpsJsonTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: New ops extend the M0 discriminated-union JSON contract; a round-trip test guards the discriminator registry and constrained SetField value shapes.
ASSUMPTIONS: [SetField carries a path string + a typed value (Vec3/Quat); polymorphic value serialization follows the same discriminator pattern as PlanOp.]

### Task b1-t5: Canonical SceneModel serializer
DESCRIPTION: Deterministic, byte-identical canonical text serializer for a SceneModel (§2) — used by tests and as the equality basis. Emits the rotation as a quaternion (§3 note). Built on CanonicalJson determinism guarantees.
DELIVERABLE: Test `CanonicalSerializer_SameModel_IsByteIdenticalAcrossCalls` passes; serializing a model with an authored rotation emits the quaternion form.
DEPENDS_ON: [b0-t2, b1-t1]
TOUCHES: [SceneBuilder.Core/Serialization/SceneModelSerializer.cs, SceneBuilder.Core.Tests/SceneModelSerializerTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Byte-identical determinism is an explicit M1 contract and the equality basis other tests rely on.
ASSUMPTIONS: [Canonical form serializes children/roots in declared order and floats via invariant culture; quaternion (not euler) is the canonical rotation form.]

### Task b1-t6: Roslyn parser core — builder file → SceneModel structure
DESCRIPTION: Roslyn-parse an ISceneDefinition builder .cs file (per §6) into a `ParseResult { Model }` (Anchors added in M2). Cover per GameObjectNode: Name/Tag/Layer/Active/IsStatic, TransformData (pos/rot/scale, Kind="Transform", Euler→Quat via b1-t1), ordered Children (handle-reuse AND closure forms), ordered Roots, and contract defaults for bare `Add`. Fail loud/located (§6/§7) when the Build body contains unsupported interleaved control flow (e.g. a `for` loop generating `Add` calls) — the error names the source location.
DELIVERABLE: Tests `Parse_RootWithOrderedChildren_ProducesMatchingSceneModel`, `Parse_Transform_StoresEulerAuthoredRotationAsQuaternion`, `Parse_BareAdd_AppliesContractDefaults`, `Parse_InterleavedControlFlow_FailsLoudWithLocation` pass under the gate.
DEPENDS_ON: [b1-t1]
TOUCHES: [SceneBuilder.Core/Parsing/BuilderParser.cs, SceneBuilder.Core/Parsing/ParseResult.cs, SceneBuilder.Core/Parsing/ParseException.cs, SceneBuilder.Core.Tests/BuilderParserTests.cs, SceneBuilder.Core.Tests/Fixtures/]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Parsing is the core M1 logic with structural, default-application, conversion, and fail-loud branches — all explicit contracts.
ASSUMPTIONS: [Parser recognizes the exact M1 authoring surface (scene.Add/handle.Add/.Transform/.Tag/.Layer/.Active/.Static/.Id/closure); builder-file fixtures are plain source text since the Editor fluent API is out of scope.]

### Task b1-t7: LogicalId derivation (3 priorities) + persistence to IdentityMap
DESCRIPTION: Derive LogicalId per §4 priority order: (1) explicit handle name (`var player = scene.Add(...)` → "player", independent of Name), (2) explicit `.Id("...")`, (3) synthesized from `parent/name/siblingIndex` and PERSISTED into the IdentityMap so it stays stable across positional edits (re-parse reads it back from the map, not re-derived). Integrate into the parser from b1-t6.
DELIVERABLE: Tests `LogicalId_FromHandleName_Priority1`, `LogicalId_FromExplicitIdCall_Priority2`, `LogicalId_Synthesized_IsPersistedAndStableAcrossSiblingInsertion_Priority3` pass under the gate.
DEPENDS_ON: [b1-t6, b0-t4]
TOUCHES: [SceneBuilder.Core/Parsing/BuilderParser.cs, SceneBuilder.Core/Parsing/LogicalIdResolver.cs, SceneBuilder.Core.Tests/LogicalIdTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Three-priority derivation plus persistence-driven stability across sibling insertion is the identity linchpin (§4); regressions break all sync.
ASSUMPTIONS: [Synthesized-id stability requires the parser to consult an existing IdentityMap; the parse signature accepts an optional existing map and writes synthesized ids back into it.]

### Task b1-t8: Diff(SceneModel, SceneSnapshot, IdentityMap) → ChangeSet
DESCRIPTION: Implement `Diff(desired:SceneModel, actual:SceneSnapshot, IdentityMap) → ChangeSet` keyed LogicalId↔GlobalObjectId via the map (§5 step 3). Against an empty snapshot → only creates; against an equal snapshot → empty ChangeSet (idempotent); against a drifted snapshot (moved Root, renamed child, matched by GlobalObjectId) → in-place `SetTransform`+`SetName` on existing objects and NO RemoveNode+AddNode pair (reconcile-into-existing, §5).
DELIVERABLE: Tests `Diff_ModelVsEmptySnapshot_ProducesOnlyCreates`, `Diff_ModelVsEqualSnapshot_ProducesEmptyChangeSet`, `Diff_ModelVsDriftedSnapshot_ProducesInPlaceEdits_NeverRemoveThenAdd` pass under the gate.
DEPENDS_ON: [b1-t1, b1-t2, b1-t3, b0-t4]
TOUCHES: [SceneBuilder.Core/Diff/Differ.cs, SceneBuilder.Core.Tests/DifferTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: The differ's in-place-vs-recreate decision is the correctness heart of reconcile-into-existing (§5) and directly tested.
ASSUMPTIONS: [Matching uses IdentityMap LogicalId→GlobalObjectId then GlobalObjectId→snapshot node; an unmatched desired node yields AddNode, an unmatched actual node yields RemoveNode.]

### Task b1-t9: Materialize(SceneModel, SceneSnapshot, IdentityMap) → Plan
DESCRIPTION: Lower a ChangeSet (via Diff) into an ordered `Plan` of PlanOps (§5 step 4): parents created before children, `SetParent`/`ReorderChild` after creation, never referencing a not-yet-created object; `SetTransform` lowers to constrained `SetField` on `m_LocalPosition`/`m_LocalRotation`/`m_LocalScale` only.
DELIVERABLE: Tests `Materialize_OrdersParentsBeforeChildren_AndParentingAfterCreation`, `Materialize_LowersTransform_ToConstrainedSetFieldPaths` pass under the gate.
DEPENDS_ON: [b1-t3, b1-t4, b1-t8]
TOUCHES: [SceneBuilder.Core/Materialize/Materializer.cs, SceneBuilder.Core.Tests/MaterializerTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Legal topological ordering and the constrained transform lowering are explicit M1 contracts; a mis-order produces an unexecutable plan.
ASSUMPTIONS: [Materialize internally calls Diff; ordering is a topological sort over parent→child edges from the desired model.]

## Bucket b2: M2 — Sync-back for transform/name/parent (Reconcile → SourcePatch, THE MOAT PROOF)
INTEGRATION SEAMS: Reconcile (b2-t3/t4) diffs expected SceneModel vs actual SceneSnapshot keyed on GlobalObjectId (reusing b1-t8's Diff machinery) and lowers to SourcePatch/SourceEdit (b2-t1), locating targets via ParseResult.Anchors (b2-t2, extending the b1-t6/t7 parser). The Roslyn patch applier (b2-t5) applies SourceEdits to the builder file preserving trivia. The cross-milestone seam (b2-t6) chains parse (b1-t6/t7) → Materialize (b1-t9) → Reconcile (b2-t3) → apply (b2-t5) → re-parse → Materialize to prove round-trip idempotence. SceneSnapshot fixtures stand in for the out-of-scope Unity scene reader.

### Task b2-t1: SourcePatch / SourceEdit / SourceSpan / Conflict / ReconcileResult POCOs
DESCRIPTION: Add `SourceSpan { Start:int; Length:int }`; `SourceEdit` variants `PatchArgument(anchor, argName, newExpr)`, `MoveStatement(anchor, newParentAnchor)`, `ReorderStatement(anchor, newSiblingIndex)`; `SourcePatch` (ordered `SourceEdit[]` over one file); `Conflict { LogicalId?; GlobalObjectId?; Kind; Reason; Location:SourceSpan? }`; `ReconcileResult { Patch:SourcePatch; Conflicts:Conflict[] }`.
DELIVERABLE: Types compile and cover all three SourceEdit variants plus Conflict/ReconcileResult per the M2 additions ledger.
DEPENDS_ON: [b1-t1]
TOUCHES: [SceneBuilder.Core/Reconcile/SourceSpan.cs, SceneBuilder.Core/Reconcile/SourceEdit.cs, SceneBuilder.Core/Reconcile/SourcePatch.cs, SceneBuilder.Core/Reconcile/Conflict.cs, SceneBuilder.Core/Reconcile/ReconcileResult.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: skip
TEST_RATIONALE: Type definitions only; behavior asserted through Reconcile and patch-apply tests.
ASSUMPTIONS: [PatchArgument.newExpr is a source-expression string; anchors reference LogicalId whose span comes from ParseResult.Anchors.]

### Task b2-t2: ParseResult.Anchors — LogicalId → SourceSpan
DESCRIPTION: Extend the b1-t6/t7 parser to also populate `ParseResult { Model; Anchors:IReadOnlyDictionary<string,SourceSpan> }`, mapping each LogicalId to the invocation/statement span (§4 "Source anchor"). M1 consumers keep reading `.Model`.
DELIVERABLE: Test `Parse_ReturnsAnchors_MappingLogicalIdToInvocationSpan` passes — each node's LogicalId maps to the text span of its `Add(...)` invocation/statement.
DEPENDS_ON: [b1-t6, b1-t7, b2-t1]
TOUCHES: [SceneBuilder.Core/Parsing/BuilderParser.cs, SceneBuilder.Core/Parsing/ParseResult.cs, SceneBuilder.Core.Tests/AnchorTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Anchors are what make every SourceEdit target the exact call; a wrong span corrupts formatting-preserving patches.
ASSUMPTIONS: [The anchor span is the builder statement/invocation for that node; argument-level spans (e.g. the name arg, the pos arg) are derived by the reconciler from the anchor + Roslyn re-parse.]

### Task b2-t3: Reconcile — patch generation for move/rename/reparent/reorder
DESCRIPTION: Implement `Reconcile(expected:SceneModel, actual:SceneSnapshot, IdentityMap) → ReconcileResult` diffing on GlobalObjectId and lowering to SourcePatch: move→`PatchArgument` on `.Transform(pos:…)` (rotation emitted as Euler degrees while diffing quaternions); rename→`PatchArgument` on the `Add("…")` name arg with LogicalId unchanged; reparent→`MoveStatement`; reorder→`ReorderStatement`. Disambiguate rename vs delete+create via GlobalObjectId (same id + new name = rename, NOT Remove+Add; new id = create; missing id = delete). The full snapshot is the authority — a narrower event batch never bounds the patch set (§5).
DELIVERABLE: Tests `Reconcile_Move_ProducesTransformArgumentPatch`, `Reconcile_Rename_ProducesNameArgumentPatch_LogicalIdUnchanged`, `Reconcile_Reparent_ProducesMoveStatement`, `Reconcile_Reorder_ProducesReorderStatement`, `Reconcile_RenamedSameGlobalObjectId_IsRename_NotDeleteThenCreate`, `Reconcile_NewGlobalObjectId_IsCreate_MissingGlobalObjectId_IsDelete`, `Reconcile_EventBatchNarrowerThanSnapshot_PatchesAllSnapshotEdits`, `Reconcile_MovePatch_EmitsEulerRotation_WhileDiffingQuaternion` pass under the gate.
DEPENDS_ON: [b2-t1, b2-t2, b1-t8]
TOUCHES: [SceneBuilder.Core/Reconcile/Reconciler.cs, SceneBuilder.Core.Tests/ReconcileTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: This is the moat: GlobalObjectId-keyed disambiguation, Euler emission, and snapshot-authority are all explicit, individually-tested contracts.
ASSUMPTIONS: [Reconcile reuses b1-t8 Diff keyed on GlobalObjectId; the "event batch" input is modeled as a subset hint the reconciler ignores for diff scope — tests pass the full snapshot regardless.]

### Task b2-t4: Reconcile — conflict surfacing when unlocalizable
DESCRIPTION: Surface `Conflict`s instead of guessing: (a) two same-named siblings with synthesized LogicalIds reordered so the positional anchor is ambiguous → a Conflict naming both candidates + location, with NO SourceEdit for that node; (b) an edit to an object whose builder statement can't be found (no source anchor, e.g. created only in Unity) → a located Conflict rather than a malformed patch (fail loud, located §7).
DELIVERABLE: Tests `Reconcile_AmbiguousSynthesizedSiblings_SurfacesConflict_NoPatchForNode`, `Reconcile_EditWithNoSourceAnchor_SurfacesLocatedConflict` pass under the gate.
DEPENDS_ON: [b2-t3]
TOUCHES: [SceneBuilder.Core/Reconcile/Reconciler.cs, SceneBuilder.Core/Reconcile/ConflictDetector.cs, SceneBuilder.Core.Tests/ReconcileConflictTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: "Surfaced, never flattened" (§5/§7) is a core philosophy; the two conflict branches are explicit contracts and must not degrade into silent/incorrect patches.
ASSUMPTIONS: [Ambiguity is detected when >1 anchor candidate matches a synthesized-LogicalId node after reorder; no-anchor is detected when a diffed GlobalObjectId has no LogicalId with an entry in ParseResult.Anchors.]

### Task b2-t5: Roslyn SourcePatch applier (formatting-preserving)
DESCRIPTION: Apply a SourcePatch's SourceEdits to the builder .cs text via Roslyn syntax-node replacement using original trivia (NOT string splicing), so only the targeted span changes — indentation, comments, blank lines, and unrelated statements stay byte-for-byte identical. Cover all three SourceEdit kinds.
DELIVERABLE: Test `SourcePatch_Apply_PreservesUnrelatedFormattingAndComments` passes — a byte-diff shows only the targeted span changed.
DEPENDS_ON: [b2-t1, b2-t2]
TOUCHES: [SceneBuilder.Core/Reconcile/SourcePatchApplier.cs, SceneBuilder.Core.Tests/SourcePatchApplyTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: Formatting preservation via trivia-preserving node replacement is explicitly guarded by a byte-diff test; string splicing would silently regress it.
ASSUMPTIONS: [PatchArgument replaces an argument syntax node; MoveStatement/ReorderStatement relocate a statement node while carrying its leading/trailing trivia.]

### Task b2-t6: Cross-milestone seam — parse→materialize→reconcile round-trip idempotence
DESCRIPTION: End-to-end seam test proving the moat closes: starting from a builder file + a drifted SceneSnapshot, Reconcile produces a SourcePatch, the applier writes it back, re-parsing the patched source yields a SceneModel that, Materialized against the (now-matching) snapshot, produces a no-op Plan (empty ops). Confirms applying a reconcile patch then re-parsing yields a no-op Materialize plan.
DELIVERABLE: A test (e.g. `RoundTrip_ApplyReconcilePatch_ThenReparse_YieldsNoOpMaterializePlan`) passes under the gate, exercising parse→reconcile→apply→re-parse→materialize with an empty resulting Plan.
DEPENDS_ON: [b2-t3, b2-t4, b2-t5, b1-t9, b1-t7]
TOUCHES: [SceneBuilder.Core.Tests/RoundTripSeamTests.cs]
BEHAVIORAL: no
TEST_RECOMMENDATION: write
TEST_RATIONALE: This is the explicit final cross-milestone seam the task requires; idempotence is the definitive proof the two directions agree.
ASSUMPTIONS: [After applying the patch, the snapshot the re-parsed model is Materialized against is the same drifted snapshot that drove the reconcile, so a correct patch yields zero ChangeOps.]

STATUS: READY
