---
feature: scenebuilder-core-m0-m2
agent: tdd-decomposition-validator
updated: 2026-07-13
iteration: 1
---

# Decomposition review: scenebuilder-core-m0-m2

VERDICT: VALID (no high-severity issues)

Reviewed tasks.md against SOURCE (task prompt) + specs/00-foundation.md, 01-m0-skeleton.md,
02-m1-hierarchy-transforms.md, 03-m2-syncback-transform.md, read independently in full.

## Coverage — every in-scope (Core) requirement maps to a deliverable
- M0 Core test plan (7 tests) → b0-t3 (Plan/CreateObject/unknown-op) + b0-t4 (IdentityMap). Types → b0-t3/b0-t4. Scaffold → b0-t1. CanonicalJson determinism substrate hoisted → b0-t2.
- M1 Core test plan (13 tests) → b1-t6 (parse/defaults/rotation/fail-loud), b1-t7 (LogicalId 3 priorities+persistence), b1-t5 (canonical serializer), b1-t8 (Diff 3 cases), b1-t9 (Materialize order+lowering). Types → b1-t1 (data model+Euler↔Quat), b1-t2 (SceneSnapshot), b1-t3 (ChangeSet/ChangeOp 10 variants), b1-t4 (M1 PlanOps).
- M2 Core test plan (12 tests) → b2-t3 (move/rename/reparent/reorder/GID-disambig/snapshot-authority/Euler-emit), b2-t4 (2 conflict branches), b2-t5 (formatting-preserving apply), b2-t2 (Anchors). Types → b2-t1 (SourceSpan/SourceEdit 3 variants/SourcePatch/Conflict/ReconcileResult). b2-t6 adds cross-milestone round-trip idempotence (additive, sound).

## Fidelity — details preserved
- CreateObject={op,logicalId,name} no extra fields; unknown-op fail-loud names token+location (§7).
- IdentityMap key order schemaVersion/scene/entries/assets; empty Assets → []; entry order preserved.
- SetField constrained to m_LocalPosition/m_LocalRotation/m_LocalScale only (M1).
- Rotation authored Euler, stored/serialized Quat; inverse (Quat→Euler) pinned in b1-t1 for M2 emission.
- Reconcile GID disambiguation (same GID+new name=rename≠delete+create; new GID=create; missing=delete) and snapshot-is-authority both owned as explicit tests. SourceEdit has exactly 3 variants, matching the §11 ledger (no delete-statement variant — consistent with M2 out-of-scope).

## Shared-interface hoisting — all correct
CanonicalJson(b0-t2)←b0-t3,b0-t4,b1-t4,b1-t5 (all DEPENDS_ON). Plan/PlanOp(b0-t3)←b1-t4. SceneModel(b1-t1)←every M1/M2 consumer. IdentityMap(b0-t4)←b1-t7,b1-t8. Parser/ParseResult(b1-t6)←b1-t7,b2-t2. SourceEdit types(b2-t1)←b2-t2,b2-t3,b2-t5. Euler↔Quat(b1-t1)←parser+reconcile. No interface re-invented per task.

## Dependency DAG / TOUCHES collisions
No cycles. Every file touched by >=2 tasks is dependency-linked: PlanJson.cs(b0-t3<b1-t4), BuilderParser.cs(b1-t6<b1-t7<b2-t2), ParseResult.cs(b1-t6<b2-t2), Reconciler.cs(b2-t3<b2-t4). No overlap without an edge.

## De-scoping — authorized, not silent
Editor adapter, unity/ project, PlanExecutor, SceneBuilderMenu, package.json/asmdef, snapshot reader, ObjectChangeEvents/sceneSaved id-capture are all excluded — explicitly authorized by the SOURCE line ("Scope: SceneBuilder.Core + SceneBuilder.Core.Tests only. Excludes SceneBuilder.Editor / unity/") and cited in the tasks.md notes block. Core is testable as pure functions over POCO fixtures (§8). Not a downgrade of any Core requirement.

## File-size budget
All TOUCHES files are new (repo has only specs/); no file at/over limit; no split task needed.

## Low-severity advisory (non-blocking)
- GATE_COMMAND is build+test only (no lint/analyzers). Acceptable for this project; a `dotnet format --verify-no-changes` or analyzer pass could be added.
- Data-only tasks (b1-t2, b1-t3, b2-t1) use "types compile and cover all N variants" deliverables — objectively checkable via the gate + inspection and exercised by downstream tests; acceptable, not vague.
- b1-t9 relies on transitive dependency to b1-t1 (via b1-t8/b1-t4) for SceneModel/PlanOp types; ordering is still correct.

STATUS: PASS
