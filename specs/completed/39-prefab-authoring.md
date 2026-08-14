# Spec 39: flat prefab authoring (define and save a prefab asset from code)

Author a reusable prefab ASSET from a code builder and keep it in bidirectional sync, the inverse of
M6 (which only INSTANCES an existing prefab). Scope is FLAT: a single-root hierarchy of GameObjects,
components, transforms, and asset references. No nested prefabs, no variant chains. One builder `.cs`
per prefab under a `Prefabs/` folder that mirrors `SceneBuilders/`, round-tripped and seamless (no
buttons, auto-sync both directions).

## Measured foundation (spikes, with evidence)

Two spikes established the facts this milestone is built on. Each result was measured against the real
editor or read out of the tree; none is derivable from the foundation spec alone.

- **Edit-in-place preserves fileIDs; overwrite-from-fresh re-mints them.** Regenerating a prefab by
  overwriting the path with a freshly-built hierarchy re-mints the local fileIDs of UNCHANGED objects
  on any structural change (add or remove a child). Loading the existing asset, mutating the loaded
  object graph in place, and saving it back preserves every unchanged object's fileID and mints new
  IDs only for genuinely-new nodes. This is the same non-destructive incremental apply the scene
  direction already runs (M1b never-wipe), executed by `PlanExecutor.Execute(plan, map, scene)`
  (`com.codescenes/Editor/PlanExecutor.cs:34`).
- **`GlobalObjectId.GetGlobalObjectIdSlow` works OFF-SCENE for asset internals.** Called on an object
  inside `LoadPrefabContents`, it yields `idType=1` (ImportedAsset), `assetGUID` = the prefab's GUID,
  `targetObjectId` = the object's local fileID, and the string parses and resolves back to the same
  object. Core's `IdentityMapEntry.GlobalObjectId` is an opaque string, so the existing stamp at
  `com.codescenes/Editor/SceneBuilderBuild.cs:330` needs ZERO Core change to cover prefab internals.
- **`ObjectChangeEvents.changesPublished` fires for editor prefab edits.** A field edit in Prefab Mode
  and an instance Apply both raise `changesPublished`, the same trigger the scene->code path already
  subscribes at `com.codescenes/Editor/SceneBuilderAutoSync.cs:126`. The reader for on-disk `.cs`
  regeneration is the existing `FileSystemWatcher` on `*.cs`
  (`com.codescenes/Editor/SceneBuilderAutoSync.cs:190`).
- **`LoadPrefabContents` is readable through the existing root-based reader.** The one root it returns
  is a live `GameObject` whose `root.scene` is a valid `Scene`. `SceneSnapshotReader.FromRoots`
  (`com.codescenes/Editor/SceneSnapshotReader.cs:84`) and `ReadNode` (`:159`) already take roots, so
  the same reader reads a prefab's contents with no new read path.
- **A code-defined prefab instances Connected with no new instancing code.** After the first
  `SaveAsPrefabAsset`, `scene.Instance("path")` / `Instance(Prefabs.X)` resolves the prefab by GUID
  through `PlanExecutor`'s `InstantiatePrefab` arm (`AssetDatabase.GUIDToAssetPath` ->
  `PrefabUtility.InstantiatePrefab`, `com.codescenes/Editor/PlanExecutor.cs:131-149`) and the placed
  instance reports `PrefabInstanceStatus.Connected`.

## The load-bearing architectural constraint (the centerpiece)

**Scene and prefab build/sync share ONE code core. There is no `PrefabBuilderBuild` /
`PrefabBuilderSync` fork.** This is a design directive from the owner, and it is what the code shape
already supports: the middle of the pipeline (parse -> materialize -> diff -> reconcile ->
source-patch -> sidecar) is Unity-free Core and scene-agnostic. Only a few adapter call sites touch
the editor. The milestone REFACTORS the two scene entry points, `SceneBuilderBuild.Run`
(`com.codescenes/Editor/SceneBuilderBuild.cs:175`) and `SceneBuilderSync.Run`
(`com.codescenes/Editor/SceneBuilderSync.cs:161`), to extract their editor-boundary hooks behind ONE
abstraction, a build/sync TARGET, then adds a prefab target as a second implementation of that
abstraction.

This is the milestone's cross-cutting invariant, stated per the foundation invariant-hoisting rule as
owner + mechanism, not as per-task guidance:

- **ONE owning task extracts the target seam and re-expresses the scene path in terms of it FIRST**,
  before any prefab code exists. The existing scene EditMode tests are the regression guard that the
  refactor did not change scene behavior.
- **ONE shared mechanism** (the target abstraction) is what every build/sync consumer calls. A prefab
  code path that duplicates scene logic instead of routing through the shared seam is a DEFECT, caught
  by the scope pass, not a stylistic preference.

The ONLY hooks that differ between the scene target and the prefab target are the following. Everything
else in the pipeline is shared and called once.

- **Read the roots to snapshot.** Scene: `scene.GetRootGameObjects()`
  (`com.codescenes/Editor/SceneSnapshotReader.cs:56`). Prefab: the single root of
  `PrefabUtility.LoadPrefabContents(path)`. Both feed `SceneSnapshotReader.FromRoots` / `ReadNode`
  (`:84`, `:159`) unchanged.
- **Execute into the target.** Scene: `PlanExecutor.Execute(plan, map, scene)` into the live scene.
  Prefab: the same `Execute(plan, map, root.scene)` into the `LoadPrefabContents` preview scene. The
  existing signature is reused because `root.scene` is a valid `Scene`.
- **Persist.** Scene: `EditorSceneManager.SaveScene`
  (`com.codescenes/Editor/SceneBuilderBuild.cs:251`). Prefab:
  `PrefabUtility.SaveAsPrefabAsset(contents, path)` followed by `PrefabUtility.UnloadPrefabContents`.
- **Stamp identity.** Both: `GlobalObjectId.GetGlobalObjectIdSlow`
  (`com.codescenes/Editor/SceneBuilderBuild.cs:330`), which works off-scene for the prefab's internals
  (measured above). No Core change.
- **Trigger edit-detection.** Scene and prefab alike ride `ObjectChangeEvents.changesPublished`
  (`com.codescenes/Editor/SceneBuilderAutoSync.cs:126`) for editor -> code, and the `*.cs`
  `FileSystemWatcher` (`:190`) for code -> asset.
- **Route paths.** A new `Prefabs/` folder constant in `SceneBuilderPaths`
  (`com.codescenes/Editor/SceneBuilderPaths.cs`), a prefab-aware `SceneBuilderRouter`
  (`com.codescenes/Editor/SceneBuilderRouter.cs`) mapping `.cs` <-> `.prefab`, and the sidecar
  target-path field holding the `.prefab` path.

## The fileID-stability invariant (measured; encoded as a checked mechanism)

**Regenerating a prefab MUST edit the LOADED CONTENTS in place. It MUST NOT rebuild a fresh hierarchy
and overwrite the path.** The update path is: `LoadPrefabContents(path)` -> mutate the existing object
graph via `PlanExecutor`'s non-destructive materialize -> `SaveAsPrefabAsset(contents, path)` ->
`UnloadPrefabContents`. Measured: overwrite-from-fresh re-mints the fileIDs of unchanged objects on any
structural change, which breaks edit reconciliation because the sidecar's stamped
`targetObjectId`/local-fileID no longer resolves; edit-in-place preserves every unchanged object's
fileID and mints new IDs only for new nodes. This is the SAME never-wipe rule the scene direction
already obeys (foundation section 5, M1b), which is why it reinforces the shared-core design: the
prefab target loads the contents as the container and `PlanExecutor` diffs and applies into it exactly
as it does for a live scene.

Stated as owner + mechanism + check:

- **ONE owner:** the prefab target's write path. Every prefab write funnels through it. There is no
  second prefab-writing site.
- **ONE mechanism:** `PlanExecutor.Execute(plan, map, loadedRoot.scene)` into the loaded contents,
  then `SaveAsPrefabAsset(contents, path)`. No caller assembles a fresh root and overwrites the path
  on the UPDATE path.
- **The check that fails on bypass:** an EditMode test regenerates a prefab after a structural change
  (add a child) and asserts an UNCHANGED sibling object's `GlobalObjectId` / local fileID is identical
  across the regeneration. A write that overwrites-from-fresh re-mints that fileID and fails the test.

Boundaries:

- **The CREATE path legitimately mints all fileIDs.** The first `SaveAsPrefabAsset` when the asset does
  not exist yet has no prior contents to load; only the UPDATE path (asset already on disk) is bound to
  edit-in-place.
- **fileIDs are GUID-seeded, so a moved or renamed `.prefab` re-mints everything.** Treat a moved
  prefab as a NEW asset unless its GUID is preserved through the `.meta`. The sidecar keys on the GUID,
  so a preserved-GUID move keeps identity and a GUID change is a fresh create, not a corruption.

## Additions to the contract

Flagged here per foundation section 3's rule. No new Core MODEL type is introduced: a prefab is a
`SceneModel` with a single-element `Roots`, and the whole pipeline (`IdentityMap`, `Diff`,
`Materialize`, `Reconcile`, source-patch, sidecar) is reused verbatim.

New authoring types (Unity-free, in `com.codescenes/Runtime/`):

- **`IPrefabDefinition`**: the recognized shape for a prefab builder, sibling to `ISceneDefinition`
  (`com.codescenes/Runtime/ISceneDefinition.cs:7`). Its method is `void Build(PrefabRoot root)`.
- **`PrefabRoot`**: single-rooted, sibling to `SceneRoot` (`com.codescenes/Runtime/SceneRoot.cs:12`).
  It configures the ONE root and its subtree. `Add` on a child node is allowed (the subtree is a
  normal GameObject hierarchy); a second top-level root is rejected (a prefab is single-root).

New adapter concepts (Editor-side, `com.codescenes/Editor/`):

- The build/sync **TARGET** abstraction and its two implementations (scene target, prefab target).
- Prefab **routing**: a `Prefabs/` folder constant plus a prefab-aware `SceneBuilderRouter` arm.
- Prefab **identity stamping** via `GlobalObjectId.GetGlobalObjectIdSlow` over the loaded contents.

Sidecar: the existing `IdentityMap` gains a target-path field carrying the `.prefab` path where the
scene sidecar carried the `.unity` scene path. No new sidecar section. `AssetEntry`
(`SceneBuilder.Core/Identity/AssetEntry.cs:5`) is reused for the self-registered prefab.

Everything else binds to `00-foundation.md` verbatim (section 2 seam, section 3 model, section 4
identity, section 5 directions, section 7 conflict, section 13 create-with-payload).

## Goal

Define a flat, single-root prefab asset in a code builder, have it saved to a `.prefab` on disk, and
keep the builder `.cs` and the `.prefab` in seamless bidirectional sync: an edit to the asset (in
Prefab Mode, or an instance Apply) reconciles back to the builder source, and an edit to the builder
source regenerates the asset edit-in-place.

## In scope

- A builder implementing `IPrefabDefinition.Build(PrefabRoot root)`, one `.cs` per prefab under
  `Prefabs/`, parsed by the existing Roslyn path.
- Materialize (code -> prefab): build the single-root hierarchy of GameObjects, components,
  transforms, and asset references into a loaded-contents preview scene and save it as a `.prefab`.
- Edit-in-place regeneration on every subsequent build (the fileID-stability invariant above).
- Reconcile (prefab -> code): a field edit, add, remove, rename, or reparent made in Prefab Mode
  reconciles back to the builder `.cs` through the existing structural-matching identity model.
- Seamless auto-sync both directions under the SAME persisted master toggle
  (`com.codescenes/Editor/SceneBuilderAutoToggle.cs`), no buttons on the happy path.
- Self-registration of the saved prefab into the sidecar `Assets[]` as
  `AssetEntry { Guid, LastKnownPath, TypeHint = "Prefab" }`, so it composes with M6 and the typed
  `Prefabs.X` facade with no new instancing code.

## Out of scope

Deferred to `specs/needs_research/nested-prefabs-and-variants.md`:

- **Nested prefabs**: a code-authored prefab that instances ANOTHER prefab inside its own hierarchy.
- **Variant chains**: authoring a prefab variant, or base-vs-override disentanglement for an instance
  of a code-defined prefab.

Also out of scope:

- **Multi-root prefabs.** A prefab is single-root; `PrefabRoot` enforces it and a second top-level root
  is a located error, never silently accepted.
- Editing a prefab through an INSTANCE's overrides (M10's territory). This milestone edits the ASSET.

## Authoring API

One `.cs` per prefab under `Prefabs/`. The per-node builder grammar (`NodeHandle`, `Component<T>`,
`Transform`, asset refs) in `com.codescenes/Runtime/` is reused verbatim; only the root type differs.

```csharp
public class Car : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add("Car");
        car.Component<Rigidbody>(rb => rb.Set(r => r.mass, 1200f));
        var body = car.Add("Body");
        body.Component<MeshRenderer>(mr => mr.Set(x => x.sharedMaterial, Assets.Material.CarPaint));
        car.Add("FrontLeftWheel").Transform(pos: (-1, 0, 1.5f));
    }
}
```

- `PrefabRoot` configures the single root and its subtree. `root.Add(...)` adds a CHILD (allowed); a
  second top-level root is rejected by the single-root validator.
- **The parser needs a new recognition arm.** `BuilderParser.FindBuildMethod`
  (`SceneBuilder.Core/Parsing/BuilderParser.cs:123`) hard-codes the interface identifier
  `"ISceneDefinition"` (`:130`) and then works off the Build parameter NAME, not its type
  (it returns `param.Identifier.Text`, `:171`). The new arm accepts a class implementing
  `IPrefabDefinition` whose `Build` parameter is a `PrefabRoot`, and threads the parameter name through
  the same statement walk. Recognition is the only parse change; the statement grammar is unchanged.

## Core deliverables

Mostly reuse, stated explicitly:

- **No new Core model type.** `SceneModel` with single-element `Roots` represents a prefab. `Diff`,
  `Materialize`, `Reconcile`, source-patch, canonical serializer, and `IdentityMap` are reused as-is.
- **Single-root validation**, the one new Core-side rule: a parsed `PrefabRoot` builder that produces
  more than one top-level root is a located error (foundation section 7), surfaced, never accepted.
  This is the only Core behavior added.
- **Parser recognition arm** (above) for `IPrefabDefinition` / `PrefabRoot`.

## Editor adapter deliverables

- **The target abstraction + scene-target refactor.** Extract the six differing hooks (read roots,
  execute, persist, stamp, trigger, route) behind ONE build/sync target. Re-express
  `SceneBuilderBuild.Run` (`:175`) and `SceneBuilderSync.Run` (`:161`) in terms of it. Regression-
  guarded by the existing scene EditMode tests.
- **The prefab target.** Read the single `LoadPrefabContents(path)` root, execute the plan into
  `root.scene` via `PlanExecutor.Execute` (`com.codescenes/Editor/PlanExecutor.cs:34`), persist with
  `SaveAsPrefabAsset(contents, path)` then `UnloadPrefabContents`. The UPDATE path edits the loaded
  contents in place (the fileID-stability invariant); the CREATE path builds the initial contents and
  saves.
- **Prefab identity stamping** via `GlobalObjectId.GetGlobalObjectIdSlow` over the loaded contents
  (`:330`), yielding `idType=1` ids that Core stores as opaque strings.
- **Prefab routing.** A `Prefabs/` folder constant in `SceneBuilderPaths`
  (`com.codescenes/Editor/SceneBuilderPaths.cs`), a prefab-aware `SceneBuilderRouter`
  (`com.codescenes/Editor/SceneBuilderRouter.cs`) mapping `.cs` <-> `.prefab`, and the sidecar
  target-path field holding the `.prefab` path.
- **Self-registration.** On save, the prefab is written into the sidecar `Assets[]` as
  `AssetEntry { Guid, LastKnownPath, TypeHint = "Prefab" }` (the same shape
  `AssetReferenceResolver` already harvests, `com.codescenes/Editor/AssetReferenceResolver.cs:252`), so
  `PrefabFacadeManifestGenerator` (`com.codescenes/Editor/PrefabFacadeManifestGenerator.cs`) picks up
  the real `.prefab` automatically and it becomes `Prefabs.X`-addressable.
- **Two auto-sync triggers**, wired into `SceneBuilderAutoSync` under the SAME persisted master toggle
  (`SceneBuilderAutoToggle.Enabled`, `com.codescenes/Editor/SceneBuilderAutoToggle.cs`):
  - code -> prefab: the existing `*.cs` `FileSystemWatcher` (`:190`) also watches `Prefabs/*.cs` and
    routes a changed builder to the prefab target's build.
  - prefab -> code: a Prefab-Mode `ObjectChangeEvents.changesPublished` (`:126`) event routes to the
    prefab target's reconcile.

## Composition with M6

A code-defined prefab is immediately instanceable in a scene by GUID with NO new instancing code.
`scene.Instance("path")` and `scene.Instance(Prefabs.X)` resolve through the existing
`InstantiatePrefab` arm of `PlanExecutor` (`AssetDatabase.GUIDToAssetPath` ->
`PrefabUtility.InstantiatePrefab`, `com.codescenes/Editor/PlanExecutor.cs:131-149`). Measured: the
saved prefab instances Connected. The `Assets[]` self-registration above is what makes the GUID
resolvable and the `Prefabs.X` facade entry appear.

## Identity / sidecar

- **Prefab-internal identity is `GlobalObjectId` with `idType=1` (ImportedAsset)**: `assetGUID` = the
  prefab GUID, `targetObjectId` = the object's local fileID. Core stores it as the opaque
  `IdentityMapEntry.GlobalObjectId` string, so no Core identity change is needed.
- **The sidecar lives beside the `.cs` under `Prefabs/`**, and its target-path field holds the
  `.prefab` path (where the scene sidecar held the `.unity` path).
- **Prefab-internal renames and reparents reconcile through the existing structural-matching identity
  model** (foundation section 4: match by LogicalId, then Name, then SiblingIndex). No new anchor type;
  the prefab's internal objects are ordinary GameObjects/components under one root.

## Decomposition guidance

The build/sync target seam is this milestone's cross-cutting invariant. Hoist it, and hoist the
edit-in-place fileID invariant, per the foundation invariant-hoisting rule: name the owner, the shared
mechanism, and the check that fails on bypass. Do NOT restate either as per-task guidance across the
prefab tasks.

Ordered tasks:

1. **Target seam extraction + scene-target refactor (INVARIANT OWNER, FIRST).** Extract the six
   editor-boundary hooks behind ONE build/sync target and re-express `SceneBuilderBuild.Run` (`:175`)
   and `SceneBuilderSync.Run` (`:161`) in terms of it. No prefab code yet. The scene EditMode tests are
   the regression guard: they must stay green, proving scene behavior is unchanged.
   TOUCHES: `com.codescenes/Editor/SceneBuilderBuild.cs`, `com.codescenes/Editor/SceneBuilderSync.cs`,
   the new target abstraction file(s), `unity-gate/Assets/GateTests/` (the scene regression tests, run
   as the guard).
2. **Prefab target: build + persist + edit-in-place, and identity stamping (fileID-invariant OWNER).**
   Implement the prefab target's read (`LoadPrefabContents`), execute
   (`PlanExecutor.Execute(plan, map, root.scene)`), persist (`SaveAsPrefabAsset` + `UnloadPrefabContents`),
   and the CREATE-vs-UPDATE split, with the fileID-stability check as its owned mechanism. Stamp
   identity via `GetGlobalObjectIdSlow` over the loaded contents.
   TOUCHES: the prefab target file, `com.codescenes/Editor/PlanExecutor.cs` (only if a seam is needed),
   `com.codescenes/Editor/SceneSnapshotReader.cs` (reused, declare if touched),
   `unity-gate/Assets/GateTests/` (the fileID-stability EditMode test).
3. **Prefab reconcile / sync + routing + sidecar.** Wire the prefab target's reconcile path, the
   `Prefabs/` folder constant, the prefab-aware `SceneBuilderRouter` mapping `.cs` <-> `.prefab`, the
   sidecar target-path field, and `Assets[]` self-registration.
   TOUCHES: `com.codescenes/Editor/SceneBuilderPaths.cs`, `com.codescenes/Editor/SceneBuilderRouter.cs`,
   the sidecar/target-path plumbing, `com.codescenes/Editor/AssetReferenceResolver.cs` (if the harvest
   is shared), `unity-gate/Assets/GateTests/`.
4. **Authoring surface + parser recognition + single-root validator.** Add `IPrefabDefinition` and
   `PrefabRoot` (`com.codescenes/Runtime/`), the `BuilderParser.FindBuildMethod` recognition arm
   (`SceneBuilder.Core/Parsing/BuilderParser.cs:123-171`), and the single-root Core validator.
   TOUCHES: `com.codescenes/Runtime/IPrefabDefinition.cs`, `com.codescenes/Runtime/PrefabRoot.cs`,
   `SceneBuilder.Core/Parsing/BuilderParser.cs`, the validator file, `SceneBuilder.Core.Tests/`,
   `unity-gate/Assets/GateTests/`.
5. **Seamless auto-sync trigger wiring, both directions.** Extend the `*.cs` `FileSystemWatcher`
   (`:190`) to `Prefabs/*.cs` -> prefab build, and route a Prefab-Mode
   `ObjectChangeEvents.changesPublished` (`:126`) -> prefab reconcile, both under
   `SceneBuilderAutoToggle.Enabled`.
   TOUCHES: `com.codescenes/Editor/SceneBuilderAutoSync.cs`,
   `com.codescenes/Editor/SceneBuilderAutoToggle.cs` (read-only if unchanged),
   `com.codescenes/Editor/SceneBuilderRouter.cs`, `unity-gate/Assets/GateTests/`.

Rules that bind the decomposition:

- **Owned-defect files in TOUCHES, or split.** Every file a task edits belongs in its TOUCHES. A task
  that would touch a file it did not declare must be split.
- **Pin the whole symmetric operation set, not a subset.** The prefab reconcile matrix (add / remove /
  field-change / rename / reparent / reorder) is pinned as a whole in task 3's tests; pruning it to a
  subset is the failure the rule exists to prevent.

## Core / adapter test plan (RED)

Core (headless, `SceneBuilder.Core.Tests`):

- **Recognition**: a class implementing `IPrefabDefinition` with `Build(PrefabRoot root)` is
  recognized by `BuilderParser` and its statements walk into a single-root `SceneModel`; an
  `ISceneDefinition` builder is still recognized unchanged.
- **Single-root validation**: a `PrefabRoot` builder that produces two top-level roots yields a
  located error, no model.
- **Round-trip**: parse -> canonical serialize -> parse of a flat prefab builder is byte-stable; a
  single-root `SceneModel` materializes to the expected plan and reconciles back to the same source.

Adapter (EditMode, `unity-gate/Assets/GateTests/`, the boundary where prefab bugs escape):

- **Build creates the asset**: author a flat prefab from code, build, assert a `.prefab` exists on
  disk with the authored hierarchy (root, children, components, transforms, asset refs).
- **fileID stability**: regenerate after a structural change and assert an UNCHANGED sibling object's
  `GlobalObjectId` / local fileID is identical across the regeneration (the fileID-stability proof).
- **Idempotence**: a second build with no code change emits 0 plan ops.
- **Instanceable**: instance the code-defined prefab in a scene via `Instance(Prefabs.X)` and assert a
  Connected instance.
- **Scene regression**: a scene build and a scene sync still work through the refactored shared seam.

## Unity confirmation checklist

All EditMode (the adapter boundary), one assertion each:

1. Author a flat prefab from code, build. Expected: a `.prefab` exists on disk with the authored
   root, children, components, and transforms.
2. Edit a field in Prefab Mode, let auto-sync fire. Expected: the builder `.cs` is rewritten with the
   new value, no button pressed.
3. Edit the builder `.cs`, let auto-sync fire. Expected: the `.prefab` updates EDIT-IN-PLACE. Assert an
   unchanged sibling object's fileID is unchanged across the regeneration.
4. Build a second time with no change. Expected: 0 plan ops (idempotent).
5. Instance the code-defined prefab in a scene via `Instance(Prefabs.X)`. Expected: a Connected
   instance appears at the authored placement.
6. Scene regression: build and sync a scene builder. Expected: scene behavior is unchanged through the
   refactored shared seam (the scene EditMode tests stay green).

## Dependencies

- **M0**: build/plan seam, `IdentityMap`, `AssetEntry`.
- **M1 / M1b**: flat hierarchy + transforms, and the non-destructive incremental materialize
  (never-wipe) the edit-in-place invariant reuses.
- **M2**: reconcile + Roslyn source-patch.
- **M3**: components + serialized fields.
- **M4**: asset references (asset-ref fields inside the prefab, and the `Assets[]` cache /
  `TypeHint="Prefab"` self-registration).
- **M6**: prefab instancing (`Instance(path)` / `Instance(Prefabs.X)` by GUID; the composition path).
- **M-Auto**: seamless auto-sync (the master toggle, the `*.cs` file watcher, the
  `ObjectChangeEvents` trigger the prefab triggers reuse).

## Risks / notes

- **Edit-in-place is load-bearing.** A prefab UPDATE that overwrites the path from a fresh hierarchy
  re-mints unchanged fileIDs and breaks reconciliation. Only the CREATE path (no prior asset) mints all
  fileIDs. The fileID-stability EditMode test is the guard.
- **fileIDs are GUID-seeded.** A moved or renamed `.prefab` with a NEW GUID re-mints everything; treat
  it as a new asset unless the GUID is preserved via the `.meta`. The sidecar keys on the GUID.
- **`LoadPrefabContents` objects have no fileID until saved and MUST be unloaded.** Every load pairs
  with an `UnloadPrefabContents`, or the preview scene leaks.
- **Single-root is enforced, not assumed.** A second top-level root is a located error.
- **The refactor must not regress scene behavior.** The target-seam extraction is task 1, guarded by
  the existing scene EditMode tests before any prefab code lands.
