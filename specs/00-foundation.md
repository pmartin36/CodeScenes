# CodeScenes — Foundation Spec (the contract)

This is the authoritative contract. Every milestone spec (M0–M11, plus M1b/M2b/M2c/M-UI) binds to the
types, identity model, seam, and conventions defined here. Milestone specs MUST use these type names verbatim and
MUST NOT invent parallel concepts; if a milestone needs a new type, it names it and flags it at the
top of its doc as an addition to this contract.

---

## 1. Product framing (one paragraph, for grounding)

CodeScenes makes a Unity scene a **code-native artifact** an LLM (or human) can author, read, and
validate as a flat C# builder, while a **bidirectional sync** keeps that code and the live Unity
scene in agreement. The wedge is code-native authoring (LLMs work in their native substrate; a whole
scene fits in context). The moat is the sync layer (identity + reconciliation), which nobody has
built. Construction is commodity; the value is the durable, living code↔scene relationship.

---

## 2. Architecture — the Core / Authoring / Editor seam

Three assemblies, split along the **testability seam**:

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

### `SceneBuilder.Authoring` — the user-facing fluent API (zero Unity dependency)
The third assembly, living in `com.codescenes/Runtime/` with `"references": []` and **no
`UnityEngine` types** — the fluent `ISceneDefinition` / `SceneRoot` / `NodeHandle` / `ComponentHandle`
surface the author writes against. Transforms and vectors are authored as plain float tuples
(`pos: (0,0,0)`), not `UnityEngine.Vector3`. The methods are compile-time scaffolding only: SceneBuilder
parses the source **text** (Roslyn) to build the scene, so the handles return themselves for chaining
and perform no work at runtime. `autoReferenced: true`, so Unity's predefined assemblies see it in scope.

### `SceneBuilder.Editor` — Unity-only adapter (thin, dumb)
A Unity asmdef referencing `UnityEditor` + Core + Authoring. **Deliberately logic-light** — its real
Unity behavior is covered by EditMode tests in `unity-gate/` (§8), not by headless unit tests.

Owns exactly four responsibilities:
1. **Execute** a `Plan` against the live scene via Editor APIs (`EditorSceneManager`, `GameObject`,
   `SerializedObject`, `PrefabUtility`, `AssetDatabase`).
2. **Read** the live scene into a `SceneSnapshot` (§3), stamped with `GlobalObjectId`s.
3. **Capture** edits via `ObjectChangeEvents.changesPublished` (trigger only) and `sceneSaved`.
4. **Resolve** asset path↔GUID and `GlobalObjectId`↔object.

**Dependency direction:** Editor → Core, Editor → Authoring. Core and Authoring NEVER reference
Editor or `UnityEngine`. The Editor adapter parses the Authoring-shaped source into Core POCOs at
build time.

**Builder location (a §-constraint, not a preference):** user-authored builder sources and their
identity sidecars live at `<ProjectRoot>/SceneBuilders/`, **outside** Unity's asset pipeline — read
and written with plain `File` IO, never through `AssetDatabase`, so a write never triggers a domain
reload. Only the *scene* stays under `Assets/`. IDE support (IntelliSense, type-checking) is restored
by injecting the builder into Unity's generated `Assembly-CSharp.csproj` (`OnGeneratedCSProject`),
which the IDE reads but Unity never compiles from — the same mechanism Unity's own Rider/VS packages
use. (Owned by `SceneBuilderPaths` + `BuilderProjectInjector`; §12.)

### How Core ships into Unity
Core is compiled once as the standalone `dotnet` project (with xUnit tests for TDD/CI), and a Core
post-build target **stages the prebuilt `SceneBuilder.Core.dll`** (plus its 9 Roslyn/System dependency
DLLs) into `com.codescenes/Plugins/`. Unity consumes that prebuilt DLL — it never compiles Core from
source. TDD happens against the `dotnet` project with `dotnet test` — fully headless, real behavior.

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
  MonoScriptGuid: string?                    // IDENTITY-AUTHORITATIVE: when non-null, Equals/GetHashCode
                                             //   short-circuit on it alone (FullName/AssemblyHint ignored)

ValueNode  =  one of
  Primitive(kind: bool|int|long|float|double|string, value)
  Enum(typeFullName, members: string[], isFlags: bool)   // simple enum: members=[name], isFlags=false; [Flags]: OR-combined members
  Vec2 | Vec3 | Vec4 | Quat | Color(...)      // structured value POCOs
  AssetRef(ref: AssetRef?)                     // reference to a project asset; ref == null means None/unassigned (symmetric with ObjectRef's null)
  ObjectRef(targetLogicalId: string)           // reference to another node/component IN this scene
  List(items: ValueNode[])
  Nested(fields: Map<string, ValueNode>)       // nested [Serializable] struct/class
  Unsupported(rawToken: string)                // escape hatch; round-trips verbatim, flagged

AssetRef
  Guid          : string                      // AUTHORITATIVE identity (Unity .meta GUID)
  FileId        : long                         // sub-object id within the asset (0 = main)
  IsBuiltin     : bool                         // built-in resource, not a project asset
  TypeHint      : string                       // e.g. "Material", "Mesh", "MonoScript"; when IsBuiltin,
                                              //   the authored type qualifier (empty when the bare name suffices)
  DisplayPath   : string                       // human-readable, NOT authoritative; re-derived. When IsBuiltin,
                                              //   carries the built-in object NAME ("Cube"), not a project path
// Identity/equality is (Guid, FileId, IsBuiltin); DisplayPath/TypeHint are non-authoritative.

Vec3 { x,y,z : float }   Quat { x,y,z,w : float }   Color { r,g,b,a : float }   // etc.
```

**SceneSnapshot** mirrors `SceneModel`'s shape but each object/component additionally carries its
`GlobalObjectId` (string) as read from the live scene. It is what the adapter produces from Unity and
what the differ consumes as "actual state."

**Rotation note:** stored canonically as a quaternion; the builder API authors it as Euler angles
for readability, and codegen emits Euler. Canonical serialization uses quaternion to avoid ambiguity.

**RectTransform note:** `TransformData.Kind == "RectTransform"` carries additional UI fields
(`anchoredPosition`, `sizeDelta`, `anchorMin`, `anchorMax`, `pivot`, `offsetMin`/`offsetMax`) beyond
`Position/Rotation/Scale`. The RectTransform milestone owns their shape + round-trip; other milestones
treat a RectTransform's UI fields as `Unsupported` (preserved, flagged) until then. UI work (M8
Button/OnClick) depends on this milestone landing.

---

## 4. Identity — the linchpin (three ids, one map)

Three distinct identities, connected by the IdentityMap:

- **LogicalId** — Core's stable id for a node/component, the anchor that appears (implicitly) in code.
  Derivation (Core, at parse time), in priority order:
  1. Explicit handle: a `var player = scene.Add("Player")` → LogicalId is the handle (var) name **used
     bare/verbatim, NOT path-qualified by parent**.
  2. Explicit `.Id("...")` call if the author provided one — the literal, verbatim.
  3. Synthesized from `parentLogicalId / name / siblingIndex` and **persisted** in the IdentityMap so
     it stays stable even if a later edit would change the positional derivation.

  Tiers 1–2 (handle name, `.Id` literal) are used verbatim, so they form a **single global namespace**;
  only the tier-3 synthesized id is **per-parent**. A sibling group distinguishable only by position
  (≥2 same-named siblings, all tier-3, no handle and no `.Id`) is a **hazard**: any statement move would
  silently re-point identity. Such groups are detected on **every** `BuilderParser.Parse` — the one
  chokepoint both directions reach — and surfaced (located) as `ParseResult.Ambiguities`. Parse does NOT
  throw; the **policy is per-consumer**: Build refuses (never guesses), Sync heals by injecting `.Id(...)`.
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
    Name                    // structural fingerprint — feeds IdentityRemapper matching
    SiblingIndex            // structural fingerprint — feeds IdentityRemapper matching
  Assets[] :                // asset-ref cache: display paths re-derived from these
    Guid, LastKnownPath, TypeHint
```
The map keeps GUIDs/GlobalObjectIds **out of the clean C# source**. Source stays readable; the map
carries identity. `Name` + `SiblingIndex` are the structural fingerprint `IdentityRemapper` uses to carry
a GlobalObjectId across a rename/reparent/reorder that re-derives the LogicalId (match by LogicalId, then
Name, then SiblingIndex, parent-by-parent). `Assets[]` is a merge-only cache — **never pruned**; a GUID no
longer referenced persists as a harmless fossil by design.

---

## 5. The two directions (Core operations)

### Code → Scene (`Materialize`)
1. Parse builder file (Roslyn) → `SceneModel` (desired).
2. Read live scene → `SceneSnapshot` (actual) via adapter.
3. `Diff(desired, actual)` keyed on LogicalId↔GlobalObjectId (via IdentityMap) → `ChangeSet`.
4. Lower `ChangeSet` → **`Plan`**: ordered ops —
   `CreateObject, DestroyObject, SetParent, ReorderChild, SetName/Tag/Layer/Active/Static,
    AddComponent, RemoveComponent, ReorderComponent, SetField(path,value),
    SetAssetRef(path,guid,fileId)`. (`SetReference(path,target)` for cross-object refs is **forthcoming**
    — M5-pending, not yet emitted.) The `Plan` also carries **`Skipped : SkippedField[]`** — fields the
    plan deliberately does NOT write (see §7), surfaced for preview rather than emitted destructively.
5. Adapter executes the Plan **in place** (reconcile-into-existing — NEVER wipe-and-recreate; this
   preserves GlobalObjectId identity). New objects get their GlobalObjectId recorded to the map on save.

**Non-destructive invariant (Materialize — load-bearing; every caller inherits it):** Materialize
destroys or mutates a scene object/component **only if it is in the IdentityMap** (this tool created it
from code on a prior build) **AND absent from the desired model** (the code removed it). Anything **not
in the map** — a GameObject or component the user added in the editor — is **preserved untouched: never
destroyed, never modified.** Concretely, the `Diff` in step 3 must NOT emit `DestroyObject` /
`RemoveComponent` for an actual-scene item whose `GlobalObjectId` is absent from the map; unmapped
actuals are invisible to Materialize's removal logic. This is what lets "Build" run against a scene the
user has also hand-edited without nuking their work — it is a reconcile, not a replace. (Owned/tested by
the non-destructive-materialize milestone; stated here because it binds every code→scene caller.)

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
        player.Component<Rigidbody>(rb => rb.Set(r => r.mass, 5f));
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
- **One authoritative direction per reconcile.** Never last-write-wins; conflicts are surfaced. Sync
  writes directly and silently — there is no confirmation dialog or preview-and-approve step, because
  the product is seamless-by-default (the user never presses a button).
- **Never emit a destructive default for an unresolved target.** An op whose target could not be
  resolved is **skipped** and reported via `Plan.Skipped` as a `SkippedField{Reason="Unresolved"}` —
  never written. Concretely, an `AssetRef` that exists but carries no resolved GUID (`Ref != null &&
  Guid == ""`) is skipped, because emitting it as a null-GUID clear would silently destroy the live
  value. Only an **explicitly-authored** `Asset(null)` (a null `Ref`) clears a field to None.

---

## 8. Testing strategy (how each milestone is verified)

The gate is **`./verify.sh`** and it has two layers.

- **Layer 1 — Core (always):** `dotnet build SceneBuilder.sln && dotnet test SceneBuilder.sln` — the
  fast headless `SceneBuilder.Core.Tests` suite (xUnit, seconds). Covers the data model, canonical
  serializer, differ, Materialize→Plan, Reconcile→SourcePatch, Roslyn parse/patch, IdentityMap. These
  are the `tdd-pipeline`'s RED/GREEN tests — real behavior, no mocks. This is the inner loop for
  pure-Core work, and it runs on every gate invocation.
- **Layer 2 — Unity EditMode (required whenever Unity-facing code changes):** the real editor suite in
  `unity-gate/` (`unity -runTests -batchmode -testPlatform EditMode`, minutes), exercising live
  `GameObject`/`SerializedProperty`/`GlobalObjectId`/`AssetDatabase` behavior. It runs — and MUST pass —
  whenever the change touches `com.codescenes/` or `unity-gate/` (or `GATE_FORCE_UNITY=1` forces it).
  It gates on BOTH the process exit code AND the results XML: a missing or failed `results.xml` is a
  **FAILURE**, never "probably fine", and a skipped/ignored test is never a pass. A pure-Core change
  skips Layer 2 and says so — a skip never counts as a Unity pass. Any change to `com.codescenes/` or
  to Unity-observable behavior is **not complete** without an EditMode test in
  `unity-gate/Assets/GateTests/` that runs the real behavior.
- The Unity Authoring + Editor adapter is a **first-class `tdd-pipeline` deliverable** — written
  end-to-end, never hand-wired by the assistant.
- **Gate command:** `./verify.sh`. Nothing ships without it.

**DONE = a quoted `GATE PASS` line from `./verify.sh`.** That verdict line (`GATE PASS: ...`) is the
ONLY reliable check — a wrapper's or subshell's exit code is not the gate's exit code and has fooled
agents into reporting a failed gate as green. Never report a gate as passed on an exit code alone.

---

## 9. Milestone-spec template (every milestone doc follows this exactly)

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
- **M1b Non-destructive Materialize (reconcile-into-existing Build)** — Build reconciles in place, never
  wipes; enforces §5 via `IdentityMap.IsManaged` so user-hand-added objects are never destroyed and
  GlobalObjectIds persist across rebuilds. *(Build-order note: listed here next to M1 conceptually, but
  depends on M2's full scene-discovering snapshot reader — schedule M1b after M2.)*
- **M2 Sync-back for transform/name/parent** — Snapshot reader + Reconcile + Roslyn SourcePatch for
  move/rename/reparent/reorder. **First proof of the moat.**
- **M2b Structural sync-back (create/delete objects, scene→code)** — `AppendStatement`/`RemoveStatement`
  + handle-introduction + `ReconcileResult` map deltas; create/delete a GameObject in the editor round-trips.
- **M2c Flag sync-back (tag/layer/active/static, scene→code)** — Reconcile diffs the four GameObject
  flags on existing mapped objects → patch/introduce/remove the `.Tag/.Layer/.Active/.Static` call.
- **M3 Components + serialized fields** — add/remove/**reorder** components; typed setters + generic
  `.Set(path, value)`; primitives/enums (incl. `[Flags]`)/vectors/colors/nested/list; both directions.
  Includes `[SerializeField]` privates.
- **M4 Asset references** — path→GUID resolve+persist, display re-derive; material/mesh/MonoScript/asset
  fields; **clear-to-None** (`AssetRef(null)`); both directions; move/rename stable; missing-asset errors.
- **M5 Cross-object references (handles)** — `ObjectRef` model; handle resolution; round-trip when a
  reference is rewired in Unity.
- **M6 Prefab instances (whole, no override round-trip)** — instantiate-by-GUID; detect instance
  presence on sync; overrides read-only/deferred.
- **M7 Robustness** — self-event suppression, domain-reload survival, external-edit reconciliation,
  canonical determinism hardening, repeated round-trip stability.
- **M-UI RectTransform sync** — RectTransform UI fields (anchoredPosition/sizeDelta/anchors/pivot), both
  directions; `.RectTransform(...)` authoring; the layout foundation M8's UI Buttons sit on.
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
- **M-Auto Seamless automatic sync (the product, not a toggle)** — sync is on by default, with no
  buttons, no toggles and no panel: code→scene fires from the plugin's own **debounced file watcher** on
  the builder under `<ProjectRoot>/SceneBuilders/` (Unity does not watch outside `Assets/`); scene→code
  fires from `ObjectChangeEvents`. Debounce + one-authoritative-direction-per-cycle + self-event
  suppression prevent clobber and feedback loops. Depends on **M1b** (in-place build) + **M7**
  (self-event suppression). *(The current `specs/14-m-auto-live-sync.md` still describes an opt-in
  toggle/drift-panel shape — that shape is wrong and MUST be re-specified as seamless-by-default.)* Full
  always-on continuous per-keystroke sync stays parked in `needs_research`.

### Folders
- `specs/*.md` — the active contract (this file) + milestone specs **M0, M1, M1b, M2, M2b, M2c, M3–M11, M-UI, M-Auto**.
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
| `IdentityMapEntry.Name` / `.SiblingIndex` | structural fingerprint feeding `IdentityRemapper` matching (§4) | M2b |
| On-disk layout | Core builds once as the `dotnet` project; its prebuilt `SceneBuilder.Core.dll` (+9 dep DLLs) is staged into `com.codescenes/Plugins/` by a post-build target — Unity consumes the DLL, not the source | M0 |
| `FieldMap` | ordered, immutable `string`→`ValueNode` map; backs `ComponentData.Fields` + `ValueNode.Nested.Fields` | M1 |
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
| `InstantiatePrefab` PlanOp | instantiate a prefab asset into the scene | M6 |
| `IdentityMap.IsManaged(goid)→bool` | single guard so Materialize never destroys unmapped user objects (§5) | M1b |
| `AppendStatement`/`RemoveStatement`; `ReconcileResult.AddedEntries`/`RemovedLogicalIds` | structural sync-back (create/delete objects) | M2b |
| `PatchFlagArgument`/`IntroduceFlagCall`/`RemoveFlagCall` | flag (tag/layer/active/static) sync-back | M2c |
| `ReorderComponent` PlanOp (`ReorderStatement` reused) | component reorder | M3 |
| `Plan.Skipped : SkippedField[]` (`SkippedField{LogicalId,Path,Reason}`) | fields the plan does not write, surfaced for preview (§5, §7) | M3 |
| `IdentityRemapper.Remap(model, priorMap)` | carries GlobalObjectIds across a LogicalId re-derivation via LogicalId→Name→SiblingIndex structural matching (§4) | M2b |
| `TypeRef.MonoScriptGuid : string?` | identity-authoritative type key; `Equals`/`GetHashCode` short-circuit on it when non-null (§3) | M3 |
| `AssetRef(null)` via `SetAssetRef` null-guid | clear an asset field to None | M4 |
| `AssetRef.IsBuiltin : bool` + `AssetRefs.Builtin(name[, typeHint])` + `SkippedField.Reason="Unresolved"` | author Unity built-in resources; equality is `(Guid,FileId,IsBuiltin)`; an unresolved ref is skipped, not written as a destructive clear (§7) | M-Builtin |
| `TransformData` RectTransform Vec2 fields; `SetRectTransform`; `SourceExpr.Vec2Literal`; `.RectTransform(...)` | RectTransform UI sync | M-UI |
| Builder relocation: `<ProjectRoot>/SceneBuilders/`, `SceneBuilderPaths`, `BuilderProjectInjector` (`OnGeneratedCSProject`) | builder outside `Assets/`, IDE support via csproj injection (§2, §12) | M-Auto |
| Debounced file watcher + `ObjectChangeEvents` triggers (reuses M7 `SuppressionScope`; no new Core type) | seamless-by-default automatic sync — no toggles, no panel | M-Auto |

**Promoted to milestone scope (2026-07-13, both actively used):** `[Flags]` enum combinations are now
**in M3 scope** (`Enum` carries OR-combined members); dynamic/multi-arg (EventDefined) UnityEvent
listeners are now **in M8 scope** (forwarding the event's own runtime args to a matching method).

**Still parked as `Unsupported` (round-trips verbatim, flagged):** `[SerializeReference]` **graph
sharing** — shared `rid` objects / reference cycles — is genuinely open and rare; tracked in
`needs_research/serializereference-graph-sharing.md`. Promote if demand appears.

---

## 12. Packaging & sample

The plugin ships as ONE UPM package, `com.codescenes`, developed in this repo. The repo's own Unity
consumer — where the gate runs — is `unity-gate/`, which embeds the package via a relative package ref.
Layout:

```
com.codescenes/
  package.json
  Runtime/                # SceneBuilder.Authoring — the fluent ISceneDefinition/SceneRoot API; refs: [] (no UnityEngine)
  Editor/                 # SceneBuilder.Editor — adapter: menu, PlanExecutor, snapshot reader, ObjectChangeEvents,
                          #   SceneBuilderPaths, BuilderProjectInjector (refs UnityEditor + Core + Authoring)
  Plugins/                # prebuilt SceneBuilder.Core.dll + its 9 Roslyn/System dependency DLLs,
                          #   staged by the Core dotnet project's post-build target — never hand-copied
```

- **Core enters Unity as a prebuilt DLL, not source.** The `SceneBuilder.Core.csproj` post-build target
  copies `SceneBuilder.Core.dll` (and 9 dependency DLLs) into `com.codescenes/Plugins/`. This keeps
  Core Unity-free while giving the adapter a reference to link against.
- **User-authored builder files live at `<ProjectRoot>/SceneBuilders/`, OUTSIDE `Assets/`** (see §2),
  each alongside its `*.sbmap.json` sidecar. They are read and written with plain `File` IO — never
  through `AssetDatabase`, so a write never triggers a domain reload. Only the *scene* lives under
  `Assets/Scenes/*.unity`. IDE support (IntelliSense, type-checking) comes from `BuilderProjectInjector`
  injecting these files into Unity's generated `Assembly-CSharp.csproj` (`OnGeneratedCSProject`), which
  the IDE reads but Unity never compiles from.
- **Target state (unbuilt):** a shipped `Samples~/RoundTripDemo/` importable from Package Manager, and a
  3-beat demo (Build code→scene → edit the `.cs` and rebuild → edit in Unity and Sync scene→code). No
  `Samples~` folder exists in the repo yet.

---

## 13. Composition rules (create-with-payload seams)

A scene object created in the editor rarely arrives bare — it usually carries components, asset/cross
references, or UnityEvents. Round-tripping such an object spans milestones (the structural milestone
creates the GameObject; M3 its components; M4/M5 its refs; M8 its events). To prevent "works in the
demo, drops data on the real edit," these rules bind **every** milestone whose Reconcile can encounter a
**newly-created, still-unmapped** object:

1. **Single-pass, dependency-ordered where possible.** In ONE Reconcile, a created object's statement is
   appended first, then — in the same pass, once its `AddedEntry` makes it mapped in-memory — its
   components, fields, and refs are appended onto that statement. Reconcile builds its in-memory identity
   map incrementally so later stages see the just-created object as mapped.
2. **Guaranteed convergence if multi-pass.** Where single-pass ordering is genuinely infeasible, the
   milestone MUST (a) append what it can, (b) **report** (Conflict) every deferred piece with
   object + what + why, and (c) guarantee a SECOND Reconcile (no further scene edits) completes the
   remainder and is then a no-op. Silent partial application is forbidden (§7).
3. **No orphaned payload.** A component/ref/event whose owning object could not be created or anchored is
   reported, never attached to the wrong construct.
4. **Delete cascades.** Deleting a created object removes its statement AND the payload statements
   authored on it; payload whose owner survives is untouched.

Each participating milestone's spec must state which of its Reconcile behaviors take part in a
create-with-payload composition and cite this section, and include at least one test that creates an
object **with** that payload in a single scene edit and asserts convergence.
