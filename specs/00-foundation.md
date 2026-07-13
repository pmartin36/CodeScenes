# SceneBuilder — Foundation Spec (the contract)

This is the authoritative contract. Every milestone spec (M0–M7) binds to the types, identity
model, seam, and conventions defined here. Milestone specs MUST use these type names verbatim and
MUST NOT invent parallel concepts; if a milestone needs a new type, it names it and flags it at the
top of its doc as an addition to this contract.

---

## 1. Product framing (one paragraph, for grounding)

SceneBuilder makes a Unity scene a **code-native artifact** an LLM (or human) can author, read, and
validate as a flat C# builder, while a **bidirectional sync** keeps that code and the live Unity
scene in agreement. The wedge is code-native authoring (LLMs work in their native substrate; a whole
scene fits in context). The moat is the sync layer (identity + reconciliation), which nobody has
built. Construction is commodity; the value is the durable, living code↔scene relationship.

---

## 2. Architecture — the Core / Editor seam

Two components, split along the **testability seam**:

### `SceneBuilder.Core` — zero Unity dependency
A standalone .NET class library (`netstandard2.1`). References only Roslyn
(`Microsoft.CodeAnalysis.CSharp`) and a JSON library. **This is where all hard logic lives and where
`tdd-pipeline` runs real, headless tests** via a sibling `SceneBuilder.Core.Tests` xUnit project.

Owns:
- The **data model** (§3) — POCOs, no `UnityEngine` types.
- The **canonical serializer** — deterministic text form of a `SceneModel`.
- The **differ** — `Diff(desired, actual) → ChangeSet`.
- **Materialize** — `SceneModel + Snapshot + IdentityMap → Plan` (ordered ops for code→scene).
- **Reconcile** — `SceneModel + Snapshot + IdentityMap → SourcePatch` (edits for scene→code).
- **C# parse/patch** — Roslyn-based: read a builder file into a `SceneModel`, and apply a
  `SourcePatch` back into the source text, formatting-preserving.
- The **IdentityMap** read/write (§4).

### `SceneBuilder.Editor` — Unity-only adapter (thin, dumb)
A Unity asmdef referencing `UnityEditor` + Core. **Deliberately logic-light** — it is confirmed by
driving Unity manually (and later via EditMode tests in batchmode), not by unit tests.

Owns exactly four responsibilities:
1. **Execute** a `Plan` against the live scene via Editor APIs (`EditorSceneManager`, `GameObject`,
   `SerializedObject`, `PrefabUtility`, `AssetDatabase`).
2. **Read** the live scene into a `SceneSnapshot` (§3), stamped with `GlobalObjectId`s.
3. **Capture** edits via `ObjectChangeEvents.changesPublished` (trigger only) and `sceneSaved`.
4. **Resolve** asset path↔GUID and `GlobalObjectId`↔object.

**Dependency direction:** Editor → Core. Core NEVER references Editor or `UnityEngine`. The
user-facing fluent builder API (which *does* use `UnityEngine.Vector3` etc.) lives in the Editor
assembly and **lowers** to Core POCOs at build time.

### How Core ships into Unity
Core's source is compiled two ways: (a) the standalone `dotnet` project + xUnit tests for TDD/CI;
(b) referenced into Unity via an asmdef with no Unity references (or a precompiled DLL in a package).
Same source, two consumers. TDD happens against (a) with `dotnet test` — fully headless, real
behavior.

---

## 3. The data model (Core POCOs)

All value types are Unity-free POCOs. The Editor adapter converts to/from `UnityEngine` types at the
boundary.

```
SceneModel
  SchemaVersion : int
  Roots         : GameObjectNode[]          // ordered

GameObjectNode
  LogicalId     : string                    // stable anchor, see §4
  Name          : string
  Tag           : string                    // default "Untagged"
  Layer         : int                        // default 0
  Active        : bool                       // default true
  IsStatic      : bool                       // default false
  Transform     : TransformData
  Components     : ComponentData[]           // ordered; excludes the Transform
  Children       : GameObjectNode[]          // ordered

TransformData                                // RectTransform is a variant (M-later)
  Kind          : "Transform" | "RectTransform"
  Position      : Vec3
  Rotation      : Quat                        // stored as quat; authored as euler, see note
  Scale         : Vec3

ComponentData
  LogicalId     : string                     // stable anchor for the component
  Type          : TypeRef
  Fields        : Map<string, ValueNode>     // key = serialized propertyPath (e.g. "_health", "m_Mass")

TypeRef
  FullName      : string                     // e.g. "UnityEngine.Rigidbody"
  AssemblyHint  : string?                    // for MonoBehaviours resolved via script GUID

ValueNode  =  one of
  Primitive(kind: bool|int|long|float|double|string, value)
  Enum(typeFullName, members: string[], isFlags: bool)   // simple enum: members=[name], isFlags=false; [Flags]: OR-combined members
  Vec2 | Vec3 | Vec4 | Quat | Color(...)      // structured value POCOs
  AssetRef(ref: AssetRef)                      // reference to a project asset
  ObjectRef(targetLogicalId: string)           // reference to another node/component IN this scene
  List(items: ValueNode[])
  Nested(fields: Map<string, ValueNode>)       // nested [Serializable] struct/class
  Unsupported(rawToken: string)                // escape hatch; round-trips verbatim, flagged

AssetRef
  Guid          : string                      // AUTHORITATIVE identity (Unity .meta GUID)
  FileId        : long                         // sub-object id within the asset (0 = main)
  TypeHint      : string                       // e.g. "Material", "Mesh", "MonoScript"
  DisplayPath   : string                       // human-readable, NOT authoritative; re-derived

Vec3 { x,y,z : float }   Quat { x,y,z,w : float }   Color { r,g,b,a : float }   // etc.
```

**SceneSnapshot** mirrors `SceneModel`'s shape but each object/component additionally carries its
`GlobalObjectId` (string) as read from the live scene. It is what the adapter produces from Unity and
what the differ consumes as "actual state."

**Rotation note:** stored canonically as a quaternion; the builder API authors it as Euler angles
for readability, and codegen emits Euler. Canonical serialization uses quaternion to avoid ambiguity.

---

## 4. Identity — the linchpin (three ids, one map)

Three distinct identities, connected by the IdentityMap:

- **LogicalId** — Core's stable id for a node/component, the anchor that appears (implicitly) in code.
  Derivation (Core, at parse time), in priority order:
  1. Explicit handle: a `var player = scene.Add("Player")` → LogicalId derived from the handle name.
  2. Explicit `.Id("...")` call if the author provided one.
  3. Synthesized from `parentLogicalId / name / siblingIndex` and **persisted** in the IdentityMap so
     it stays stable even if a later edit would change the positional derivation. If a synthesized id
     becomes ambiguous (e.g. two same-named siblings reordered), Reconcile surfaces a **conflict**
     rather than guessing.
- **GlobalObjectId** — Unity's durable scene-object identity (stable across save/reload/session;
  exists only after first save; prefab-instance objects keyed on the `(targetPrefabId, targetObjectId)`
  pair). The correctness anchor for sync.
- **Source anchor** — a Roslyn span (the invocation/statement) that a LogicalId maps to, so write-back
  patches the exact call.

**IdentityMap** (the sidecar), persisted as JSON next to the builder file:
```
FooScene.sbmap.json
  SchemaVersion
  Scene        : "Assets/Scenes/Foo.unity"
  Entries[] :
    LogicalId
    GlobalObjectId          // "" until first save
    Kind                    // GameObject | Component
    ComponentType?          // for components
    ParentLogicalId?
  Assets[] :                // asset-ref cache: display paths re-derived from these
    Guid, LastKnownPath, TypeHint
```
The map keeps GUIDs/GlobalObjectIds **out of the clean C# source**. Source stays readable; the map
carries identity.

---

## 5. The two directions (Core operations)

### Code → Scene (`Materialize`)
1. Parse builder file (Roslyn) → `SceneModel` (desired).
2. Read live scene → `SceneSnapshot` (actual) via adapter.
3. `Diff(desired, actual)` keyed on LogicalId↔GlobalObjectId (via IdentityMap) → `ChangeSet`.
4. Lower `ChangeSet` → **`Plan`**: ordered ops —
   `CreateObject, DestroyObject, SetParent, ReorderChild, SetName/Tag/Layer/Active/Static,
    AddComponent, RemoveComponent, SetField(path,value), SetReference(path,target),
    SetAssetRef(path,guid,fileId)`.
5. Adapter executes the Plan **in place** (reconcile-into-existing — NEVER wipe-and-recreate; this
   preserves GlobalObjectId identity). New objects get their GlobalObjectId recorded to the map on save.

### Scene → Code (`Reconcile`)
1. Read live scene → `SceneSnapshot` (actual).
2. Parse builder file → `SceneModel` (expected).
3. `Diff(expected, actual)` keyed on GlobalObjectId → `ChangeSet`.
4. Generate a **`SourcePatch`** = span-local edits to the builder file (patch an argument, append a
   statement, delete a statement), applied via Roslyn preserving surrounding formatting.
5. Changes that cannot be localized to a single construct → **conflict**, surfaced, never flattened.

**Correctness rule:** `ObjectChangeEvents` is only a *trigger* telling us *when/roughly where* to
look. The authority is always a fresh `SceneSnapshot` diffed on `GlobalObjectId`. Reconcile on every
domain reload and focus-regain, because the event stream can miss edits.

---

## 6. The builder file is "special" (the round-trip constraint)

To make Roslyn write-back reliable, a SceneBuilder source file is a recognized shape, not arbitrary
C#: a class implementing `ISceneDefinition` whose `Build(SceneRoot scene)` body is composed of
recognized builder calls (`.Add`, `.Transform`, `.Component<T>`, handles, closures). Arbitrary
interleaved logic (loops, conditionals, external calls that generate structure) is **not** supported
in the round-tripped region — this is the flat/near-isomorphic constraint we committed to, and it is
what makes clean bidirectional sync possible. The tool validates the file conforms and errors loudly
(located) if it does not.

Illustrative surface (exact API is M-owned):
```csharp
public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var player = scene.Add("Player").Transform(pos: (0,0,0));
        player.Add<Rigidbody>(rb => rb.Set(r => r.mass, 5f));
        var door = scene.Add("Door");
        scene.Add("Button").Component<Button>(b => b.OnClickTarget(door)); // handle → ObjectRef
    }
}
```

---

## 7. Error & conflict philosophy

- **Fail loud, located.** Every error names the object/field and the source or scene location
  (`Player > Weapon: component 'Rigidbdy' not found`). No silent drops.
- **Unsupported serialized values round-trip verbatim** via `ValueNode.Unsupported` and are flagged,
  never lost.
- **One authoritative direction per reconcile**, with a preview/plan the user confirms. Never
  last-write-wins; conflicts are surfaced.

---

## 8. Testing strategy (how each milestone is verified)

- **Core (TDD, real tests):** `SceneBuilder.Core.Tests` (xUnit, `dotnet test`, headless). Covers the
  data model, canonical serializer, differ, Materialize→Plan, Reconcile→SourcePatch, Roslyn
  parse/patch, IdentityMap. These are the `tdd-pipeline`'s RED/GREEN tests — real behavior, no mocks.
- **Editor adapter (manual + later EditMode):** the user drives Unity per a **confirmation checklist**
  and, from M2 on, optional Unity Test Framework EditMode tests runnable via
  `-batchmode -runTests` for regression.
- A milestone is DONE only when: Core tests green in CI **and** the user's Unity confirmation
  checklist passes on a real edit.

---

## 9. Milestone-spec template (every M0–M7 doc follows this exactly)

```
# Mn — <title>
## Goal            (1–2 sentences: the user-visible capability this slice adds)
## In scope        (bullet list, precise)
## Out of scope    (bullet list — parked items this M explicitly does NOT do)
## Core deliverables
   - Types added/changed (referencing §3 contract)
   - Functions/behaviors, each phrased as a testable contract
## Editor adapter deliverables
   - The thin Unity-side pieces
## Authoring API added   (the builder surface this M introduces, with a code sample)
## IdentityMap / sidecar changes
## Core test plan   (the concrete RED tests tdd-pipeline will write — behaviors, not impl)
## Unity confirmation checklist   (exact steps the user performs; expected result each step)
## Dependencies    (which prior milestones this builds on)
## Risks/notes
```

---

## 10. Milestone contracts (summary — full docs in NN-*.md)

- **M0 Skeleton & harness** — scaffold Unity 6 project + Core/Editor/tests; sidecar format; build one
  hardcoded empty `Root` GameObject; establish the build trigger + Core/Editor round-trip of a Plan.
- **M1 Flat hierarchy + transforms, one-way** — `SceneModel` for GO tree + names + transforms;
  Materialize; write IdentityMap with GlobalObjectIds. One direction only.
- **M2 Sync-back for transform/name/parent** — Snapshot reader + Reconcile + Roslyn SourcePatch for
  move/rename/reparent/reorder. **First proof of the moat.**
- **M3 Components + serialized fields** — add/remove components; typed setters + generic `.Set(path,
  value)`; primitives/enums/vectors/colors; both directions. Includes `[SerializeField]` privates.
- **M4 Asset references** — path→GUID resolve+persist, display re-derive; MeshRenderer.material,
  MeshFilter.mesh, MonoScript identity, asset object-ref fields; both directions; move/rename stable;
  missing-asset errors.
- **M5 Cross-object references (handles)** — `ObjectRef` model; handle resolution; round-trip when a
  reference is rewired in Unity.
- **M6 Prefab instances (whole, no override round-trip)** — instantiate-by-GUID; detect instance
  presence on sync; overrides read-only/deferred.
- **M7 Robustness** — self-event suppression, domain-reload survival, external-edit reconciliation,
  canonical determinism hardening, repeated round-trip stability.
- **M8 UnityEvents / OnClick wiring** — author & round-trip `UnityEvent` persistent listeners
  (target object, method, argument mode); the button-`OnClick`-a-method case end to end.
- **M9 `[SerializeReference]` polymorphism** — author & round-trip managed-reference fields
  (`managedReferenceValue`): polymorphic/interface-typed serialized fields, incl. null and type change.
- **M10 Prefab-instance override round-trip** — represent `m_Modification.m_Modifications` (per-property
  overrides, added/removed components & GameObjects) keyed on `(targetPrefabId, targetObjectId)`; both
  directions. Builds on M6's whole-prefab support.
- **M11 Animation — common easing patterns (v0)** — generate simple `AnimationClip` assets from a
  declarative set of common easing curves (linear, ease-in/out quad/cubic, bounce, elastic, etc.)
  applied to named properties; reference them from `Animator`/`Animation`. Advanced animation content
  is explicitly deferred to `needs_research`.

### Folders
- `specs/*.md` — the active contract (this file) + milestone specs **M0–M11**.
- `specs/completed/` — a milestone moves here once its Core tests are green in CI **and** the user's
  Unity confirmation checklist passes.
- `specs/needs_research/` — open problems not yet spec-ready, each a research stub (not a build
  milestone) until promoted: **advanced animation content** (arbitrary curves, tangents, animation
  events, retargeting), **animation FSM / AnimatorController state machines** (phased: a simple v0
  set — states, transitions, parameters, conditions — promotable to a milestone after M11, then an
  advanced v1 set — layers, sub-state machines, blend trees), **multi/additive scenes**, **headless
  CI generation**, **live per-keystroke sync**.

---

## 11. Contract additions ledger (types/ops the milestones add to this contract)

§3/§4/§5 name several concepts without giving them POCO shapes; milestones type them. This ledger is
the authoritative index so the additions stay coherent. Each is defined in the owning milestone doc.

| Added | Shape (summary) | Owner |
|---|---|---|
| `Plan`, `PlanOp` (base) + `CreateObject` | ordered-op container from Materialize; op vocabulary in §5 | M0 |
| `IdentityMap`/`IdentityMapEntry`/`AssetEntry` | POCO typing of the §4 sidecar JSON | M0 |
| On-disk layout | Core = dual-consumed (dotnet project **and** Unity embedded package, same source) | M0 |
| `ChangeSet`/`ChangeOp` | differ output consumed by Materialize/Reconcile | M1 |
| `ParseResult { Model; Anchors: LogicalId->SourceSpan }` | Roslyn parse result; `Anchors` added in M2 | M1/M2 |
| `SourcePatch`/`SourceEdit` (`PatchArgument`, `MoveStatement`, `ReorderStatement`), `SourceSpan` | span-local, formatting-preserving code edits | M2 |
| `Conflict`, `ReconcileResult { Patch; Conflicts }` | the "surfaced, never flattened" output | M2 |
| `member:<name>` field-key + adapter `ResolveAuthoredPaths` | **amends section 3**: `Fields` keys are serialized paths *after* the adapter resolves transient `member:*` keys authored via typed setters | M3 |
| `PrefabInstanceNode`, `PrefabInstanceKey (targetPrefabId,targetObjectId)` | prefab-instance model + pair-key identity (section 4) | M6 |
| `SyncCheckpoint` (`sbstate.json`), `SuppressionScope` | reload-survival state + self-event suppression | M7 |
| `ValueNode.UnityEventListeners` (+`UnityEventListener`), op `SetUnityEvent` | persistent-listener model | M8 |
| `ValueNode.ManagedReference`, op `SetManagedReference` | `[SerializeReference]` polymorphic value | M9 |
| `PropertyOverride`/`OverrideTarget`/`AddedComponent` + 4 override collections on `PrefabInstanceNode` | prefab override round-trip | M10 |
| `AnimationClipSpec`/`AnimationTrack`/`EasingKind`/`GeneratedClipRef` | easing-clip generation | M11 |

**Promoted to milestone scope (2026-07-13, both actively used):** `[Flags]` enum combinations are now
**in M3 scope** (`Enum` carries OR-combined members); dynamic/multi-arg (EventDefined) UnityEvent
listeners are now **in M8 scope** (forwarding the event's own runtime args to a matching method).

**Still parked as `Unsupported` (round-trips verbatim, flagged):** `[SerializeReference]` **graph
sharing** — shared `rid` objects / reference cycles — is genuinely open and rare; tracked in
`needs_research/serializereference-graph-sharing.md`. Promote if demand appears.

---

## 12. Packaging & sample

The plugin ships as ONE UPM package, `com.scenebuilder`, developed in this repo and consumed by a
Unity project (e.g. `SceneBuilderTest`) via a local `file:` reference. Layout:

```
com.scenebuilder/
  package.json            # lists the RoundTripDemo under "samples"
  Runtime/                # SceneBuilder.Authoring — the fluent ISceneDefinition/SceneRoot API (refs UnityEngine)
  Editor/                 # SceneBuilder.Editor — adapter: menu, PlanExecutor, snapshot reader, ObjectChangeEvents (refs UnityEditor + Core + Authoring)
  <Core dependency>       # SceneBuilder.Core — same source the dotnet tests build; asmdef noEngineReferences
  Samples~/
    RoundTripDemo/
      ExampleScene.cs     # the example builder file
      README.md           # (or a Readme.asset) the two-way demo script
```

- **`Samples~` is the idiomatic mechanism** — hidden from compilation until the user clicks **Import**
  in Package Manager, which copies it into `Assets/Samples/com.scenebuilder/<version>/RoundTripDemo/`.
  It lives in the repo *inside the one package* — NOT a separate sibling package or a second `file:` ref.
- **The sample generates its scene on first Build** (menu: SceneBuilder ▸ Build) rather than shipping a
  pre-baked `.unity` + sidecar. Why: (1) robustness — the sidecar's `GlobalObjectId`s are minted against
  the real scene in the user's own project, avoiding cross-project identity staleness; (2) it is the
  better demo — materializing a scene from code shows the wedge, then editing the scene shows the moat.
  The Readme is a 3-beat script: **Build** (code→scene) → **edit the `.cs` and rebuild** → **edit in
  Unity and Sync** (scene→code).
- **User-authored builder files** live in the project's `Assets/…` (referencing the Authoring
  assembly), next to their `Assets/Scenes/*.unity` + `*.sbmap.json`. Core parses builder files as text
  (Roslyn), never executes them, so their location is flexible for Core; the Unity project is their home
  for type-checking and versioning.
- **Development sequencing:** the M1/M2 confirmation example is authored directly in the test project's
  `Assets` first (fastest iteration), then promoted verbatim into `Samples~/RoundTripDemo/` once the
  round-trip is proven. **The confirmation example IS the shipped sample.**
