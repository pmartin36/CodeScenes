# M0 — Skeleton & harness

### Additions to the contract
These concepts are named in §5/§4 of the foundation but are not given concrete POCO shapes in §3.
M0 defines them; all later milestones reuse these exact names.
- **`Plan`** — the ordered-op container produced by Materialize (§5 step 4). POCO:
  `Plan { SchemaVersion:int; ScenePath:string; Ops: PlanOp[] }`.
- **`PlanOp`** — abstract base for the op vocabulary listed in §5. M0 defines the base plus the single
  concrete op it needs: **`CreateObject`**. The remaining §5 ops (`SetParent`, `ReorderChild`,
  `SetName/Tag/Layer/Active/Static`, `AddComponent`, `RemoveComponent`, `SetField`, `SetReference`,
  `SetAssetRef`, `DestroyObject`) are declared as reserved names here and implemented in M1+.
- **`IdentityMap` / `IdentityMapEntry` / `AssetEntry`** — POCO typing of the §4 sidecar JSON.

No new *value* types (§3 data model) are introduced; M0 does not touch `SceneModel`, `GameObjectNode`,
`TransformData`, or `SceneSnapshot`.

## Goal
Stand up the whole harness end to end: an in-editor menu command builds ONE hardcoded empty `Root`
GameObject into a fresh scene by constructing a Core `Plan`, handing it to the Editor adapter to
execute, saving the scene, and writing the IdentityMap sidecar. This proves the Core→Plan→Editor→scene
path and the confirm loop with zero parsing and zero sync.

## In scope
- Repository + project scaffold: the standalone `SceneBuilder.Core` (`netstandard2.1`) library, the
  `SceneBuilder.Core.Tests` xUnit project, the Unity 6 (6000.x) project, and the `SceneBuilder.Editor`
  asmdef, wired so `dotnet test` runs Core headless and Unity consumes the same Core source.
- The canonical on-disk layout (below), fixed now so every later milestone slots into it.
- The `Plan` container + the `CreateObject` op + deterministic JSON (de)serialization of a `Plan`.
- The `IdentityMap` sidecar POCOs + deterministic JSON read/write in the `FooScene.sbmap.json` shape
  from §4.
- One Editor menu command (`SceneBuilder/Build Demo Scene`) that: news up a `Plan` with a single
  `CreateObject("Root")`, executes it via the adapter into a new empty scene, saves the scene to
  `Assets/Scenes/Demo.unity`, and writes `Assets/Scenes/Demo.sbmap.json`.
- The Editor `PlanExecutor` skeleton able to execute exactly `CreateObject`.

## Out of scope
- Any Roslyn parsing or authoring API (M1).
- `SceneModel`, `SceneSnapshot`, `Diff`, `Materialize`, `Reconcile` (M1/M2).
- Recording real `GlobalObjectId`s (M1 — M0 writes entries with `GlobalObjectId: ""`).
- Transforms, components, fields, references, prefabs (M1+).
- Any scene→code direction (M2).

## Core deliverables

### Types added/changed (referencing §3 contract)
- `Plan { SchemaVersion:int; ScenePath:string; Ops: PlanOp[] }` — new (flagged above).
- `PlanOp` abstract base; `CreateObject : PlanOp { LogicalId:string; Name:string }` — new (flagged).
- `IdentityMap { SchemaVersion:int; Scene:string; Entries: IdentityMapEntry[]; Assets: AssetEntry[] }`
  — POCO for §4.
- `IdentityMapEntry { LogicalId:string; GlobalObjectId:string; Kind:"GameObject"|"Component";
  ComponentType:string?; ParentLogicalId:string? }` — POCO for §4.
- `AssetEntry { Guid:string; LastKnownPath:string; TypeHint:string }` — POCO for §4 (empty in M0).

### Functions/behaviors (each a testable contract)
- **Plan serialization is deterministic and round-trips.** Given a `Plan` with one
  `CreateObject("Root","Root")`, `PlanJson.Serialize` produces stable JSON (fixed key order, invariant
  culture, `\n` newlines) and `PlanJson.Deserialize` of that JSON yields a `Plan` equal to the original.
- **Serialization is stable across runs.** Given the same `Plan`, two independent `Serialize` calls
  produce byte-identical output (no dictionary-ordering or culture drift).
- **`CreateObject` carries LogicalId + Name only.** Given a `CreateObject`, its serialized form contains
  `op:"CreateObject"`, `logicalId`, and `name`, and no transform/component data.
- **IdentityMap round-trips.** Given an `IdentityMap` for scene `Assets/Scenes/Demo.unity` with one
  `GameObject` entry `{LogicalId:"Root", GlobalObjectId:"", ParentLogicalId:null}`,
  `IdentityMapJson.Serialize`→`Deserialize` yields an equal map, with `Entries` order preserved and
  `Assets` serialized as an empty array.
- **IdentityMap file shape matches §4.** Given the above map, the serialized JSON has top-level
  `schemaVersion`, `scene`, `entries`, `assets` keys in that order.

## Editor adapter deliverables
- `SceneBuilder.Editor` asmdef referencing `UnityEditor` + the Core asmdef (Editor→Core only; §2).
- `PlanExecutor.Execute(Plan)` handling `CreateObject`: `new GameObject(name)` parented under the active
  scene root, tracked by `LogicalId` in an in-memory `LogicalId→GameObject` table for the run.
- `SceneBuilderMenu` `[MenuItem("SceneBuilder/Build Demo Scene")]`: creates a new empty scene
  (`EditorSceneManager.NewScene`), runs the executor, saves to `Assets/Scenes/Demo.unity`
  (`EditorSceneManager.SaveScene`), then writes `Assets/Scenes/Demo.sbmap.json` via Core’s
  `IdentityMapJson` (one `Root` entry, `GlobalObjectId:""`).
- No snapshot read, no diff — M0 executes a literal plan.

## Authoring API added
None. M0 hardcodes the `Plan` inside the Editor menu command. The fluent `ISceneDefinition` /
`SceneRoot` authoring surface is introduced in M1.

## IdentityMap / sidecar changes
- Establishes the sidecar file name convention `<SceneName>.sbmap.json` living next to the target scene
  asset, and the on-disk JSON shape from §4.
- M0 writes one `GameObject` entry (`LogicalId:"Root"`) with `GlobalObjectId:""` (real ids land in M1
  on save) and an empty `Assets` array.

## Core test plan
`SceneBuilder.Core.Tests` (xUnit, `dotnet test`, headless — §8):
- `Plan_WithSingleCreateObject_RoundTripsThroughJson` — construct → serialize → deserialize → equal.
- `Plan_Serialize_IsByteIdenticalAcrossCalls` — determinism (no ordering/culture drift).
- `CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields`.
- `IdentityMap_RoundTripsThroughJson_PreservingEntryOrder`.
- `IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder`.
- `IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray`.
- `PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation` (§7 fail-loud): deserializing an unknown `op`
  string throws a located error naming the offending op token.

## Unity confirmation checklist
1. Open the Unity 6 (6000.x) project → menu shows **SceneBuilder ▸ Build Demo Scene**.
   *Expected:* menu item present, no compile errors in the Console.
2. Click **Build Demo Scene**.
   *Expected:* a new scene opens containing exactly one root GameObject named `Root` (no children,
   default transform); scene saves to `Assets/Scenes/Demo.unity` without errors.
3. Inspect the project folder.
   *Expected:* `Assets/Scenes/Demo.sbmap.json` exists, matches the §4 shape, and contains one entry
   `{ "logicalId":"Root", "globalObjectId":"", "kind":"GameObject", "parentLogicalId":null }`.
4. Re-open `Demo.unity` after a domain reload.
   *Expected:* `Root` is still present (scene actually persisted).

## Dependencies
None — this is the base milestone.

## On-disk layout (established here, fixed for all milestones)
```
UnitySceneBuilder/
  SceneBuilder.sln                         # ties Core + Tests for `dotnet test`
  SceneBuilder.Core/                       # ALSO a Unity embedded package (see below)
    package.json                           # Unity package manifest: com.scenebuilder.core
    SceneBuilder.Core.asmdef               # noEngineReferences:true — zero Unity refs (§2)
    SceneBuilder.Core.csproj               # netstandard2.1; refs Microsoft.CodeAnalysis.CSharp + JSON
    Model/            # SceneModel, GameObjectNode, TransformData, ValueNode… (M1+)
    Plan/             # Plan, PlanOp, CreateObject… + PlanJson
    Identity/         # IdentityMap, IdentityMapEntry, AssetEntry + IdentityMapJson
    Serialization/    # canonical serializers
  SceneBuilder.Core.Tests/
    SceneBuilder.Core.Tests.csproj         # net8.0, xUnit; ProjectReference → Core.csproj
  unity/
    Assets/
      SceneBuilder/Editor/
        SceneBuilder.Editor.asmdef         # refs UnityEditor + SceneBuilder.Core (Editor→Core)
        SceneBuilderMenu.cs
        PlanExecutor.cs
      Scenes/                              # Demo.unity + Demo.sbmap.json land here
    Packages/manifest.json                 # "com.scenebuilder.core":"file:../../SceneBuilder.Core"
    ProjectSettings/ProjectVersion.txt     # 6000.x
```
**Same source, two consumers (§2 "How Core ships into Unity"):** the physical `SceneBuilder.Core/`
folder is both a `dotnet` project (compiled by `SceneBuilder.Core.csproj` for TDD/CI) and a Unity
embedded package (compiled by `SceneBuilder.Core.asmdef` with `noEngineReferences:true`). One source
tree; TDD runs against the `dotnet` build per §8.

## Risks/notes
- Dual-compilation of `SceneBuilder.Core/` (csproj + asmdef in the same folder): Unity ignores `.csproj`
  and compiles via the asmdef, so there is no double-build inside Unity. If Unity’s IDE project
  generation trips on the stray `.csproj`, the fallback is a package containing a precompiled
  `SceneBuilder.Core.dll` produced by `dotnet build` — same seam, extra rebuild step.
- `GlobalObjectId` does not exist until first save (§4); M0 deliberately persists `""` and defers real
  id capture to M1 to keep the harness path minimal.
- Keep `PlanExecutor` logic-light (§2): it maps ops to Editor calls and nothing more. All op *meaning*
  stays in Core.
