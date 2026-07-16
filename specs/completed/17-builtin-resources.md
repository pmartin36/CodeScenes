# M-Builtin — Unity built-in resources (authoring primitives from code)

### Additions to the contract

**One field, no new ValueNode kind.** This milestone adds `IsBuiltin : bool` to the §3 `AssetRef`
POCO and reuses the entire existing asset-ref machinery (`ValueNode.AssetRef`, the
`SetAssetRef(path, guid, fileId)` Plan op, `Differ`, `ComponentReconciler`). It introduces **no** new
`ValueNode` case, **no** new `PlanOp`, and **no** new sidecar collection.

Why reuse rather than a new kind: a built-in **is** an object addressed by `(guid, fileId)` — the two
container GUIDs are real, and the on-disk YAML for a Cube is literally
`m_Mesh: {fileID: 10202, guid: 0000000000000000e000000000000000, type: 0}` (verified, §"Research").
Nothing about identity, diffing, planning, or writing differs from an ordinary sub-object asset ref;
the shipped `AssetReferenceResolver.ResolveAssetObject(guid, fileId)` **already resolves built-ins
correctly today** (verified). The only genuine difference is **how a human/LLM names one in source**,
because the container path is shared by every object inside it. That is a *presentation* concern —
one discriminator bit, not a parallel type hierarchy. A new `ValueNode.BuiltinRef` would fork Diff,
Materialize, Reconcile, canonical serialization, and the sidecar for zero behavioral gain.

| Added | Shape (summary) | Owner |
|---|---|---|
| `AssetRef.IsBuiltin : bool` | discriminates the built-in namespace from the project path namespace; **amends §3** | M-Builtin |
| `AssetRefs.Builtin(name)` / `Builtin(name, typeHint)` | Editor-side authoring sugar lowering to `ValueNode.AssetRef` | M-Builtin |
| `SkippedField.Reason = "Unresolved"` | new value of the existing field; an unresolved ref is skipped, never materialized as a clear | M-Builtin |

The **None** case is unchanged and stays `Asset(null)` (M4). `Builtin(null)` is **not** a None form —
it is a malformed call (§"Unresolvable").

---

## Goal

A component field can reference a Unity **built-in resource** — the primitive meshes
(Cube/Sphere/Capsule/Cylinder/Plane/Quad), `Default-Material`, `Sprites-Default`, the UI sprites and
shaders — from code, by a readable name: `Builtin("Cube")`. Today these are silently skipped, so
**a primitive cannot be authored from code at all** and a scene Cube round-trips as a hole. After
this milestone, authoring `Builtin("Cube")` assigns the real built-in mesh, and a Cube created in the
editor syncs back as `Builtin("Cube")` in source.

## The gap (observed, not theorized)

`GameObject ▸ 3D Object ▸ Cube` produces a `MeshFilter.m_Mesh` + `MeshRenderer.m_Materials[0]`
pointing into Unity's **built-in resource containers**, not the project. Both directions currently
refuse them, by explicit code:

- **Read** — `com.scenebuilder/Editor/AssetReferenceResolver.cs:204` returns
  `ValueNode.Unsupported("BuiltinResource")` for either built-in GUID. The field never reaches the
  builder; `Materializer.EmitFieldOp` records it in `Plan.Skipped` and a Console warning fires.
- **Write** — `AssetReferenceResolver.WriteAssetRef:249` short-circuits on `IsBuiltinGuid` and leaves
  the live value untouched with a warning.
- **Lowering** — `LoweringResolver.Resolve:108` warns and returns `(containerGuid, 0, "Object")` for
  an authored container path.

Build no longer *throws* on a primitive (that regression is fixed), but the capability is absent:
**there is no way to author a primitive from code.**

**Why `Asset("path")` cannot close this (the crux).** Every built-in mesh shares the single container
path `Library/unity default resources`; they are distinguished **only by `fileId`**. So
`Asset("Library/unity default resources")` names 512 objects at once — ambiguous by construction, and
unable to round-trip back to a specific one. A distinct authoring form is required.

## Research — verified against the real editor (Unity 6000.5.3f1)

All findings below were produced by running the installed editor in batchmode against the live
`AssetDatabase`, not from documentation. They are the factual basis of this spec; the pipeline must
not re-derive them by guessing.

**The two containers** (`AssetDatabase.LoadAllAssetsAtPath`, object counts verified):

| Container path | GUID | Holds |
|---|---|---|
| `Library/unity default resources` | `0000000000000000e000000000000000` | 512 objects: **13 Mesh**, 444 MonoScript, 36 Texture2D, 9 ComputeShader, 6 Shader, 2 Material, 1 GUISkin, 1 Font |
| `Resources/unity_builtin_extra` | `0000000000000000f000000000000000` | 256 objects: 206 Shader, **20 Material**, 14 Texture2D, **9 Sprite**, 4 LightmapParameters, 1 Cubemap, 1 TextAsset, 1 LightingDataAsset |

**⚠ `Resources.GetBuiltinResource<T>(name)` is the WRONG API and MUST NOT be used.** Its documented
`.fbx` names resolve to Unity's *legacy* meshes, **not** the meshes a real primitive uses. Verified:

| Call | Returns | fileId | The mesh a real primitive actually uses |
|---|---|---|---|
| `GetBuiltinResource<Mesh>("Cube.fbx")` | `Cube` | 10202 | ✅ 10202 — matches |
| `GetBuiltinResource<Mesh>("Sphere.fbx")` | `pSphere1` | 10200 | ❌ primitive uses `Sphere` **10207** |
| `GetBuiltinResource<Mesh>("Capsule.fbx")` | `polySurface2` | 10205 | ❌ primitive uses `Capsule` **10208** |
| `GetBuiltinResource<Mesh>("Cylinder.fbx")` | `pCylinder1` | 10203 | ❌ primitive uses `Cylinder` **10206** |
| `GetBuiltinResource<Mesh>("Plane.fbx")` | `pPlane1` | 10204 | ❌ primitive uses `Plane` **10209** |
| `GetBuiltinResource<Mesh>("Quad.fbx")` | `Quad` | 10210 | ✅ matches |
| `GetBuiltinResource<Mesh>("New-Sphere.fbx")` | `Sphere` | 10207 | (the primitive's sphere, under a legacy alias) |
| `GetBuiltinResource<Mesh>("Cube")` | **null** | — | (the extension is mandatory) |

Building on those names would silently assign the wrong mesh for 4 of the 6 primitives — a data bug
that looks correct in a screenshot. `AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat")`
*does* work for the extra container, but it is redundant (below) and covers only that container.

**The authority is `LoadAllAssetsAtPath` + `TryGetGUIDAndLocalFileIdentifier`** — matching on
**object `name` + concrete type**, which is exactly what the primitives use. Verified ground truth
(`GameObject.CreatePrimitive` → the assigned object's `(name, guid, fileId)`):

| Primitive | Mesh name | Mesh guid / fileId | Material |
|---|---|---|---|
| Cube | `Cube` | `e000…` / **10202** | `Default-Material`, `f000…` / **10303** |
| Sphere | `Sphere` | `e000…` / **10207** | same |
| Capsule | `Capsule` | `e000…` / **10208** | same |
| Cylinder | `Cylinder` | `e000…` / **10206** | same |
| Plane | `Plane` | `e000…` / **10209** | same |
| Quad | `Quad` | `e000…` / **10210** | same |

Other realistic names, verified resolvable by (name, type):
`Material`: `Default-Material` 10303, `Default-Diffuse` 10302, `Default-Line` 10306, `Default-Particle`
10301, `Default-Skybox` 10304, `Default-Terrain-Standard` 10652, `Sprites-Default` 10754, `Sprites-Mask`
10758. `Sprite`: `UISprite` 10905, `Checkmark` 10901, `Background` 10907, `InputFieldBackground` 10911,
`Knob` 10913, `DropdownArrow` 10915, `UIMask` 10917. `Shader`: `Standard` 46, `Sprites/Default` 10753,
`Unlit/Texture` 10752, `UI/Default` 10770.

**Name uniqueness — the disambiguation rule.** Across the full 768-object namespace, **the only bare
names that collide are Sprite/Texture2D and Material/Texture2D pairs** inside `unity_builtin_extra`
(`Background`, `Checkmark`, `DropdownArrow`, `InputFieldBackground`, `Knob`, `UIMask`, `UISprite`,
`UnitySplash-Dark`, `UnitySplash-Light`, `Default-Particle`, `Default-ParticleSystem`) plus four
MonoScripts (`Graph`, `GraphGUI`, `Node`, `AssemblyDefinitionAsset`). **`(name, concrete type)` is
unique for every type a user would realistically reference** — verified: `("UISprite", Sprite)` →
fileId 10905, `("UISprite", Texture2D)` → fileId 10904. Hence the optional type qualifier
(`Builtin("UISprite", "Sprite")`) and nothing more elaborate.

**The write path already works.** The shipped `ResolveAssetObject(guid, fileId)` was executed verbatim
against built-ins and resolved **every** case correctly — `(e000…, 10202)` → Mesh `Cube`,
`(f000…, 10303)` → Material `Default-Material`, `(f000…, 10905)` → Sprite `UISprite`,
`(f000…, 10904)` → Texture2D `UISprite`. It works because `GUIDToAssetPath(e000…)` returns
`Library/unity default resources`, `LoadMainAssetAtPath` on a container returns null (so the
main-asset fast path is skipped), and the existing `LoadAllAssetsAtPath` + fileId-match loop finds
the object. **No change to `ResolveAssetObject` is required** — only the removal of the
`IsBuiltinGuid` short-circuit that stops execution before reaching it.

Other verified facts the pipeline may rely on:
- `AssetDatabase.Contains(builtinObject)` is **true** — so the read path reaches the built-in branch.
- Built-in refs survive scene save + reload as `{fileID: 10207, guid: 0000000000000000e000000000000000}`.
- `SerializedProperty.type` for an object-reference field is `PPtr<Mesh>` / `PPtr<$Sprite>` — the
  expected type is extractable, but **this spec does not depend on it** (see the qualifier design).
- **Perf:** `LoadAllAssetsAtPath` on both containers = **<1 ms warm** (first call 0 ms; 20 iterations
  over 768 objects = 1 ms total). Unity caches the containers; a per-sync scan is affordable. A
  process-lifetime cache is still specified below because sync runs per keystroke.

**Version stability.** The `(guid, fileId) → name` mapping was verified on **6000.5.3f1 only**; it
cannot be verified across versions from this machine. The 102xx fileIds are long-standing Unity
constants (they appear in every `.unity` YAML since the Unity 3.x era), but this spec **does not rely
on that**. The design is stable *by construction*: **the NAME is the only thing persisted in source**,
and `(guid, fileId)` is re-derived live from the running editor on every read and every lowering.
Both sides of a Diff are derived within the same session, so they always agree. If a future Unity
renumbered a fileId, authored code keeps working with no migration; if it *removed* a name, that
surfaces as the loud located error of §"Unresolvable" rather than a silent wrong assignment. **No
fileId, and no name→fileId table, may be hardcoded in Core or the adapter** — that is a hard
requirement of this spec, not a preference.

## In scope

- **Authoring a built-in resource by name** from any serialized `UnityEngine.Object` field:
  `Builtin("Cube")` on `MeshFilter.m_Mesh`, `Builtin("Default-Material")` on `MeshRenderer.m_Materials`,
  `Builtin("UISprite", "Sprite")` on an `Image.m_Sprite`.
- **Any object in either container**, resolved generically by (name, type). No allowlist, no curated
  table of "supported" primitives — a name the editor can resolve is authorable.
- **The optional type qualifier** `Builtin(name, typeHint)`, required only when the bare name is
  ambiguous within the built-in namespace, and emitted by Sync only when required.
- **Materialize (code→scene):** `Builtin("Cube")` lowers to a `ValueNode.AssetRef` carrying
  `(containerGuid, fileId, IsBuiltin=true)`; emits the existing `SetAssetRef(path, guid, fileId)` op;
  the adapter assigns the real built-in object.
- **Reconcile (scene→code):** a field holding a built-in reads as a populated `ValueNode.AssetRef`
  with `IsBuiltin=true` and `DisplayPath` = the object's name; the source is patched to
  `Builtin("Cube")` — **replacing today's skip**.
- **Lists of built-ins** (`m_Materials`) via `ValueNode.List` of `ValueNode.AssetRef`, and free mixing
  of `Asset(...)`, `Builtin(...)`, and `Asset(null)` in one list.
- **Unresolvable built-in name → loud, located error** (§7), never a silent skip and never a clear.
- **Closing the "unresolved ref silently clears the field" hole** for every asset ref, built-in or not
  (§"Core deliverables", `Materializer`).

## Out of scope

- **Creating a primitive as one authoring call** (e.g. `scene.AddPrimitive("Cube")` that adds the
  GameObject + MeshFilter + MeshRenderer + collider in one statement). This milestone makes the
  *references* authorable; the sugar that composes a whole primitive is a separate, later concern.
- **Built-in `MonoScript`s** (444 of them). Component script identity is M4's `TypeRef.MonoScriptGuid`
  path, not an `AssetRef`. A `[SerializeField] MonoScript` field pointing at a built-in script is
  in scope only insofar as the generic (name, type) rule covers it; the four colliding MonoScript
  names (`Graph`, `GraphGUI`, `Node`, `AssemblyDefinitionAsset`) are **unresolvable-ambiguous** and
  produce the §"Unresolvable" error rather than a guess.
- **Editing a built-in's contents** — they are read-only editor-install objects.
- **Authoring by the container path** (`Asset("Library/unity default resources")`). It stays a loud
  error (§"Unresolvable"), now pointing the author at `Builtin(...)`.
- **Render-pipeline built-ins** (URP/HDRP `Lit`, etc.). Those are package *assets* with ordinary
  GUIDs — already M4's `Asset("path")`, nothing to do.
- Scene-to-scene object refs (M5), prefab instances (M6).

## Core deliverables

Types added/changed:
- **`AssetRef.IsBuiltin : bool`** (`SceneBuilder.Core/Model/AssetRef.cs`) — `false` by default, so
  every existing `Asset(...)` ref and every deserialized older sidecar/model is unchanged. When true,
  `DisplayPath` carries the built-in **object name** (e.g. `"Cube"`), not a project path, and
  `TypeHint` carries the **authored type qualifier** (empty when the bare name suffices).
- **`AssetRef.Equals`/`GetHashCode`** extend to `(Guid, FileId, IsBuiltin)`. Post-lowering this adds
  nothing (the GUID already implies it), so it never makes two equal refs unequal; it exists so two
  *unresolved* refs (`Guid == ""`) from different namespaces — `Asset("Cube")` vs `Builtin("Cube")` —
  cannot collide. `TypeHint`/`DisplayPath` remain non-authoritative for identity (M4 §Diff holds).
- **`SkippedField.Reason`** gains the value `"Unresolved"` (the field and its `"Unsupported"` default
  already exist).

Functions/behaviors (each a testable contract):

- **Parse `Builtin("Cube")`** — `ValueNodeParser` matches an `InvocationExpressionSyntax` whose
  `Expression` is the bare `IdentifierNameSyntax` `Builtin` (the same shape it already matches for
  `Asset`), with **1 or 2 string-literal arguments**:
  - `Builtin("Cube")` → `AssetRef { DisplayPath = "Cube", IsBuiltin = true, Guid = "", TypeHint = "" }`.
  - `Builtin("UISprite", "Sprite")` → additionally `TypeHint = "Sprite"`.
  - `Builtin(null)`, `Builtin()`, `Builtin("a","b","c")`, or any non-string-literal argument →
    `ValueNode.Unsupported(expr.ToString())` (the parser stays **total** — it never throws).
  As with `Asset`, only the bare identifier matches; `AssetRefs.Builtin("Cube")` falls to
  `Unsupported`. The `Builtin` arm must sit beside the existing `Asset` arm, **before** the general
  `MemberAccessExpressionSyntax` enum arm.
- **Emit `Builtin(...)`** — `SourceExpr.ValueNodeLiteral` gains, **before** the existing populated
  `ValueNode.AssetRef` arm:
  - `AssetRef { IsBuiltin: true, TypeHint: "" }` → `Builtin("Cube")`
  - `AssetRef { IsBuiltin: true, TypeHint: "Sprite" }` → `Builtin("UISprite", "Sprite")`
  Both arguments go through the existing `SourceExpr.StringLiteral` escaping. `IsBuiltin=false` is
  unchanged (`Asset("...")` / `Asset(null)`).
- **Lowering** — `AssetRefLowering.Lower` gains a second, optional delegate:
  ```csharp
  public static SceneModel Lower(
      SceneModel model,
      Func<string, (string guid, long fileId, string typeHint)?> resolver,
      Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver = null)
  ```
  A ref with `IsBuiltin == true` routes to `builtinResolver(DisplayPath, TypeHint is "" ? null : TypeHint)`;
  all other refs keep the existing path verbatim. On a hit, lowering sets **`Guid` and `FileId` only,
  and preserves the authored `TypeHint` and `DisplayPath` verbatim** — it must NOT overwrite
  `TypeHint` with the resolved type name, or `Builtin("Cube")` would round-trip into
  `Builtin("Cube", "Mesh")` and the source would churn on every sync. (This is the one place built-in
  lowering deliberately differs from `Asset` lowering, which *does* stamp `TypeHint`.) A null return
  or a null `builtinResolver` leaves the node unresolved and **does not throw** (matching the existing
  `Asset` contract — the adapter is what fails loud).
- **Materialize — an unresolved ref is SKIPPED, never materialized as a clear.** `Materializer.EmitFieldOp`
  currently emits `SetAssetRef { Guid = assetRef.Ref?.Guid }` unconditionally; a ref that named
  something but failed to resolve (`Ref != null && Guid == "" && DisplayPath != ""`) therefore lowers
  to a **null-GUID op, which the adapter executes as "clear the field"** — silently destroying the
  live value. That is the M4 None form firing on a *failure*. Fix at the chokepoint every caller
  inherits: such a ref emits `SkippedField { Reason = "Unresolved" }` instead of a `SetAssetRef`.
  `Ref == null` (the genuine `Asset(null)`) still emits the clearing op unchanged; a resolved ref
  (built-in or not) is unchanged. This applies to list elements too.
- **Diff** — unchanged code, asserted behavior: two built-in refs with the same `(Guid, FileId)` are
  equal regardless of `TypeHint`; `Builtin("Cube")` vs `Builtin("Sphere")` differ (different
  `FileId`); `Builtin("Cube")` vs `Asset("Assets/M/Cube.fbx")` differ (different `Guid`).
- **Reconcile text-currency** — `ComponentReconciler.AuthoredTextIsCurrent` compares `DisplayPath` for
  an `AssetRef` pair; it must additionally compare **`IsBuiltin` and `TypeHint`**, because for a
  built-in both appear in the emitted source text. Without this, a ref whose identity is equal but
  whose authored *form* is stale (e.g. an ambiguity that gained/lost its qualifier) would never be
  rewritten.
- **Sidecar exclusion** — `ComponentReconciler.CollectAssetEntries` must **skip** `IsBuiltin` refs. A
  built-in has no project path, so an `Assets[]` entry would record a meaningless `LastKnownPath` and
  pollute the move-recovery scan. Likewise `AssetRefResolver.ReDerive`/`Resolve` must treat a built-in
  as already-resolved (its `DisplayPath` is a live-derived name, never re-derived from the cache) and
  never report it missing.
- **Canonical serialization** — `IsBuiltin` round-trips through `CanonicalJson` as a plain
  camelCase `isBuiltin` boolean alongside the existing four properties (which are always written).

## Editor adapter deliverables

All in `com.scenebuilder/Editor/AssetReferenceResolver.cs` unless noted.

- **`BuiltinCatalog` (new, adapter-side)** — the single place that knows the two containers:
  ```csharp
  internal static class BuiltinCatalog
  {
      internal static UnityEngine.Object? Resolve(string name, string? typeHint, out bool ambiguous);
      internal static bool TryDeriveName(string guid, long fileId, out string name, out string typeName, out bool nameIsAmbiguous);
      internal static IEnumerable<string> Suggest(string name, string? typeHint); // near-miss names for the error
  }
  ```
  Built by scanning `AssetDatabase.LoadAllAssetsAtPath` over both container paths and calling
  `AssetDatabase.TryGetGUIDAndLocalFileIdentifier` per object — **never** `Resources.GetBuiltinResource`
  (§Research), and **never** a hardcoded name→fileId table. Cached for the editor process lifetime
  (built lazily on first use); the containers ship with the editor and cannot change within a session.
  `Resolve` matches `o.name == name` and, when `typeHint` is non-null, `o.GetType().Name == typeHint`
  (exact concrete-type match, so `Sprite` and `Texture2D` are distinguishable); it sets `ambiguous`
  when >1 object matches.
- **`LoweringResolver.ResolveBuiltin(string name, string? typeHint)`** — the `builtinResolver`
  delegate Core calls. Returns `(containerGuid, fileId, typeHint)` from `BuiltinCatalog.Resolve`.
  **Throws the located error** (§"Unresolvable") on a miss or an unqualifiable ambiguity — mirroring
  `LoweringResolver.Resolve`'s existing throw-on-missing-asset, which is what keeps the loud failure
  loud rather than letting Core's no-throw lowering degrade into a silent clear. **Never harvests**
  into `Assets[]`.
- **`LoweringResolver.Resolve` — delete the built-in branch** (`:108`). An authored
  `Asset("Library/unity default resources")` must no longer succeed with a warning and a fabricated
  `(guid, 0, "Object")`; it becomes the loud error of §"Unresolvable", naming `Builtin(...)` as the
  fix. (`IsBuiltinPath` exists only for that branch and is removed with it.)
- **`ReadObjectReference` — replace the `Unsupported("BuiltinResource")` return** (`:204`) with a real
  read: `BuiltinCatalog.TryDeriveName(guid, fileId, …)` → `ValueNode.AssetRef(new AssetRef { Guid =
  guid, FileId = fileId, IsBuiltin = true, DisplayPath = name, TypeHint = nameIsAmbiguous ? typeName
  : "" })`. **The qualifier is emitted only when the bare name is ambiguous** — that is what keeps
  `Builtin("Cube")` clean and `Builtin("UISprite", "Sprite")` correct. A built-in GUID whose `fileId`
  derives no object (a version removed it) → the located error, not `Unsupported`.
- **`WriteAssetRef` — delete the `IsBuiltinGuid` short-circuit** (`:249`) so execution reaches the
  existing `ResolveAssetObject(guid, fileId)`, which resolves built-ins correctly as-is (§Research).
  No other write change. The existing missing-asset throw then covers a built-in whose fileId no
  longer resolves.
- **`IsBuiltinGuid`** is retained (the read side needs it to choose the built-in branch).
- **Emitted-code compile guarantee** — `SourcePatchApplier.EnsureAssetRefsUsing` currently injects
  `using static SceneBuilder.Authoring.AssetRefs;` when it emits `Asset(`. It must fire for `Builtin(`
  too; both factories live on `AssetRefs`, so the same single using covers them. Without this, a Sync
  that introduces the first `Builtin(...)` into a file emits source that fails CS0103 — which the
  gate's Roslyn compile assertion treats as a bug, not a style issue (CLAUDE.md).

## Authoring API added

`Builtin(name)` / `Builtin(name, typeHint)` — an Editor-side fluent factory on the existing
`SceneBuilder.Authoring.AssetRefs` static class (`com.scenebuilder/Runtime/AssetReference.cs`),
returning the same inert `AssetReference` handle `Asset(...)` returns. Like `Asset`, it is
compile-time scaffolding: SceneBuilder parses the source text and never executes the builder.

```csharp
public static AssetReference Builtin(string name);
public static AssetReference Builtin(string name, string typeHint);
```

**Why this shape.** It is a plain invocation headed by a bare identifier — byte-for-byte the syntactic
shape `ValueNodeParser` already matches for `Asset`, so Roslyn parsing is a copy of a proven arm
rather than new machinery. It reads as prose an LLM emits naturally (`Builtin("Cube")`), it is
symmetric with `Asset("path")` (same class, same `using static`, same `.Set(...)` composition, freely
mixed in one list), and the two namespaces stay visibly distinct — a reader always knows whether a
name is a project path or an editor built-in. The optional second argument keeps the common case
clean while making the ~11 genuinely ambiguous UI names expressible; because Sync emits the qualifier
only when it is required, source never accumulates noise. Rejected: `Builtin.Mesh("Cube")` (member
access + invocation — a more complex parse, and multiplies the API surface per type for no gain);
`Asset.Builtin("Cube")` (the parser matches bare identifiers only); `Primitive("Cube")` (too narrow —
materials, sprites and shaders are built-ins too).

```csharp
using static SceneBuilder.Authoring.AssetRefs;

public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        // The gap this closes: a primitive, authored from code.
        var cube = scene.Add("Cube").Transform(pos: (0, 0.5f, 0));
        cube.Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cube")));
        cube.Component<MeshRenderer>(c => c.Set("m_Materials", new[] { Builtin("Default-Material") }));

        // Built-ins and project assets mix freely in one list.
        var floor = scene.Add("Floor");
        floor.Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")));
        floor.Component<MeshRenderer>(c => c.Set("m_Materials", new[] {
            Asset("Assets/Materials/Red.mat"),
            Builtin("Default-Material"),
        }));

        // The qualifier — only where the bare name is ambiguous (Sprite vs Texture2D).
        var icon = scene.Add("Icon");
        icon.Component<UnityEngine.UI.Image>(c => c.Set("m_Sprite", Builtin("UISprite", "Sprite")));
    }
}
```

## IdentityMap / sidecar changes

**None.** Built-in refs are deliberately **excluded** from the sidecar `Assets[]` cache: that cache
exists to re-derive a `DisplayPath` from a GUID and to recover a moved asset's GUID by its
`LastKnownPath` (M4). A built-in has no project path, cannot move, cannot be deleted by the user, and
its name is re-derived live from the container on every read. An entry would be a meaningless
`LastKnownPath` polluting the move-recovery scan. Both `CollectAssetEntries` (Reconcile) and
`LoweringResolver`'s harvest (Build) must skip `IsBuiltin` refs.

## Core test plan

Built-in resolution is **mocked at the adapter boundary** — Core is handed `(guid, fileId)` by a stub
`builtinResolver` and never touches Unity. New file `SceneBuilder.Core.Tests/BuiltinRefTests.cs`
(plus the noted additions to existing files), xUnit, style `Subject_Condition_ExpectedOutcome`.

**Parse** (`BuiltinRefTests.cs`, fixtures as file-scope `ISceneDefinition` source strings, matching
`AssetRefParseTests.cs`):
1. `Parse_BuiltinStringLiteral_YieldsBuiltinAssetRefWithNameEmptyGuid` — `Builtin("Cube")` →
   `AssetRef { DisplayPath = "Cube", IsBuiltin = true, Guid = "", TypeHint = "" }`.
2. `Parse_BuiltinWithTypeQualifier_YieldsTypeHint` — `Builtin("UISprite", "Sprite")` →
   `DisplayPath = "UISprite"`, `TypeHint = "Sprite"`, `IsBuiltin = true`.
3. `Parse_BuiltinWithNonStringArgOrWrongArity_YieldsUnsupported` — `Builtin()`, `Builtin(null)`,
   `Builtin(someVar)`, `Builtin("a","b","c")` → `Unsupported`, verbatim raw token, no throw.
4. `Parse_QualifiedBuiltinInvocation_YieldsUnsupported` — `AssetRefs.Builtin("Cube")` → `Unsupported`
   (bare-identifier match only, mirroring `Asset`).
5. `Parse_AssetAndBuiltinInSameList_YieldsBothKinds` — a list mixing `Asset("…")`, `Builtin("Cube")`,
   `Asset(null)` → three nodes with the right `IsBuiltin`/null shape in order.

**Emit:**
6. `SourceExpr_BuiltinRefWithoutQualifier_EmitsBareBuiltinCall` → exactly `Builtin("Cube")`.
7. `SourceExpr_BuiltinRefWithQualifier_EmitsQualifiedBuiltinCall` → exactly `Builtin("UISprite", "Sprite")`.
8. `SourceExpr_NonBuiltinRef_StillEmitsAssetCall` — regression: `IsBuiltin=false` unchanged
   (`Asset("…")`, `Asset(null)`).
9. `Parse_EmitBuiltin_TextRoundTripsIdentically` — parse → emit → identical source text, both forms.

**Value/identity** (extend `AssetRefValueTests.cs`):
10. `AssetRef_EqualityKeysOnGuidFileIdAndIsBuiltin` — same `(Guid, FileId)` + differing `TypeHint`/
    `DisplayPath` → equal; differing `IsBuiltin` with both `Guid == ""` → **not** equal.
11. `ValueNodeAssetRef_Builtin_CanonicalRoundTrips` — SceneModel → canonical JSON → parse → equal
    ref, `IsBuiltin` preserved; `isBuiltin` present in the JSON.

**Lowering** (extend `AssetRefLoweringTests.cs`):
12. `Lowering_BuiltinRef_RoutesToBuiltinResolverAndSetsGuidFileId` — stub returns
    `("0000000000000000e000000000000000", 10202, "Mesh")` → `Guid`/`FileId` set.
13. `Lowering_BuiltinRef_PreservesAuthoredTypeHintAndDisplayPath` — the pin against source churn:
    `Builtin("Cube")` lowered with a resolver returning `typeHint: "Mesh"` keeps `TypeHint == ""` and
    `DisplayPath == "Cube"`; `Builtin("UISprite","Sprite")` keeps `TypeHint == "Sprite"`.
14. `Lowering_BuiltinResolverReturnsNull_LeavesNodeUnresolvedNoThrow`.
15. `Lowering_NoBuiltinResolverSupplied_LeavesBuiltinNodeUnresolvedNoThrow` — the default-null overload.
16. `Lowering_NonBuiltinRef_DoesNotCallBuiltinResolver` — `Asset("…")` still routes to the path resolver.

**Materialize** (extend `AssetRefMaterializeTests.cs`):
17. `Materialize_BuiltinRef_EmitsSetAssetRefWithContainerGuidAndFileId`.
18. `Materialize_BuiltinRefList_EmitsOrderedSetAssetRefPerIndex` — indexed paths `m_Materials[0]`, `[1]`.
19. `Materialize_UnresolvedAssetRef_IsSkippedNotClearing` — **the data-loss guard**: a ref with
    `Ref != null`, `Guid == ""`, `DisplayPath != ""` → `Plan.Skipped` carries
    `Reason = "Unresolved"` and `Ops` contains **no** `SetAssetRef` for that path. Both `IsBuiltin`
    true and false.
20. `Materialize_NullAssetRef_StillClearsField` — regression: `Ref == null` (`Asset(null)`) still
    emits the clearing `SetAssetRef(guid: null)`; the §19 guard must not swallow the real None form.

**Diff** (extend `AssetRefDiffTests.cs`):
21. `Diff_BuiltinSameGuidFileIdDifferentTypeHint_NoChange`.
22. `Diff_BuiltinDifferentFileId_ReportsChange` — `Builtin("Cube")` → `Builtin("Sphere")`.
23. `Diff_BuiltinVsProjectAsset_ReportsChange` — different container/project GUID.

**Reconcile** (extend `AssetRefReconcileTests.cs`):
24. `Reconcile_SnapshotBuiltinAgainstAssetSource_PatchesToBuiltinForm` — source `Asset("…/Red.mat")`,
    snapshot a built-in → `SourcePatch` rewrites the argument to `Builtin("Default-Material")`.
25. `Reconcile_SnapshotBuiltinChanged_PatchesName` — source `Builtin("Cube")`, snapshot `Sphere`
    (different `FileId`) → argument rewritten to `Builtin("Sphere")`.
26. `Reconcile_BuiltinTypeHintChanged_PatchesEvenWhenIdentityEqual` — the `AuthoredTextIsCurrent`
    pin: equal `(Guid, FileId)` but differing `TypeHint` → the text is still rewritten.
27. `Reconcile_BuiltinRef_AddsNoAssetEntries` — `ReconcileResult.AddedAssets` is empty for a
    built-in-only change (sidecar exclusion), while a sibling `Asset(...)` change still harvests.
28. `Reconcile_BuiltinRefOnNewObject_Converges` — **§13 create-with-payload**: a newly editor-created
    object carrying a built-in ref in one edit → the `Builtin("…")` argument is appended onto that
    object's just-created statement in the same pass (owner mapped via M2b's `AddedEntry`, §13 rule
    1), or reported and converged on a guaranteed second Sync (§13 rule 2); the second Sync of an
    unchanged scene is a no-op; never a silent drop.
29. `Reconcile_ResyncUnchangedBuiltin_IsANoOp` — stability: no patch, no churn.

## Unity confirmation checklist

These become EditMode round-trips in `unity-gate/Assets/GateTests/` per CLAUDE.md — a new
`RoundTripBuiltinRefTests.cs` in the established `Direction_Scenario_Expectation` style, driving
`SceneBuilderBuild.Run` / the Sync path against a live scene. The two existing built-in tests in
`RoundTripAssetRefTests.cs` (`RoundTrip_ScenePrimitive_BuiltinResourcesDoNotBreakBuild`,
`CodeToScene_AuthoredBuiltinRef_DoesNotThrowAndDoesNotClear`) pin the **old skip** behavior and must
be updated to the new contract, not left asserting the hole.

1. **Author a primitive from code (the headline).** Write a builder assigning `Builtin("Cube")` to a
   `MeshFilter` and `Builtin("Default-Material")` to a `MeshRenderer`; Build. **Expected:** the
   object renders as a real Cube; the Inspector's Mesh slot reads `Cube` and the material slot
   `Default-Material`. Assert identity, not the label: the assigned mesh `==` the mesh of a live
   `GameObject.CreatePrimitive(PrimitiveType.Cube)` — this is what catches the `Sphere.fbx`/`pSphere1`
   class of bug.
2. **All six primitives.** Repeat for Sphere/Capsule/Cylinder/Plane/Quad. **Expected:** each equals
   its `CreatePrimitive` counterpart. (`Sphere`, `Capsule`, `Cylinder`, `Plane` are exactly where
   `Resources.GetBuiltinResource` would silently assign the wrong legacy mesh.)
3. **Scene Cube → code (replacing today's skip).** `GameObject ▸ 3D Object ▸ Cube` in the editor;
   Sync. **Expected:** the source gains `Builtin("Cube")` and `Builtin("Default-Material")`; the
   fields are **no longer** in `Plan.Skipped` and no "not supported" Console warning fires.
4. **Swap in the editor.** With `Builtin("Cube")` authored, drag the built-in `Sphere` mesh into the
   MeshFilter slot; Sync. **Expected:** the source updates to `Builtin("Sphere")`.
5. **Built-in → project asset and back.** Replace the Cube's material with `Assets/Materials/Red.mat`
   in the Inspector; Sync → source becomes `Asset("Assets/Materials/Red.mat")`. Author
   `Builtin("Default-Material")` again; Build → the slot returns to `Default-Material`.
6. **Mixed list.** Author `m_Materials` = `{ Asset("Assets/Materials/Red.mat"), Builtin("Default-Material") }`;
   Build. **Expected:** both slots assigned, in order.
7. **The ambiguous name.** Author `Builtin("UISprite", "Sprite")` on an `Image.m_Sprite`; Build.
   **Expected:** the Sprite (fileId 10905) is assigned, not the Texture2D (10904). Then Sync an
   editor-assigned built-in `UISprite` sprite. **Expected:** source emits the **qualified** form.
8. **Bare name stays bare.** Sync a scene Cube twice with no edit between. **Expected:** source reads
   `Builtin("Cube")` — never `Builtin("Cube", "Mesh")` — and the second Sync is a **no-op** (the
   anti-churn pin for the `TypeHint`-preservation rule).
9. **Unresolvable name → loud, located error.** Author `Builtin("Cub")`; Build. **Expected:** a
   located error naming object, component, field and the bad name, with near-miss suggestions; the
   live slot is **left untouched — not cleared** and the Build does not silently succeed.
10. **Unqualifiable ambiguity → loud error.** Author bare `Builtin("UISprite")` where both a Sprite
    and a Texture2D match and no qualifier disambiguates. **Expected:** the located error names both
    candidate types and tells the author to qualify; nothing is guessed.
11. **Container path is refused.** Author `Asset("Library/unity default resources")`. **Expected:** a
    loud located error pointing at `Builtin(...)` — not the old warning-and-continue.
12. **Save/reload survival.** Build a Cube, save the scene, reopen it. **Expected:** the mesh ref
    survives (YAML `{fileID: 10202, guid: 0000000000000000e000000000000000}`) and a Sync is a no-op.
13. **Emitted code compiles.** A Sync that introduces the **first** `Builtin(...)` into a builder file
    that never contained an `Asset(...)`. **Expected:** `using static SceneBuilder.Authoring.AssetRefs;`
    is injected and the file compiles (the existing Roslyn compile assertion covers this).

## Dependencies

- **M4** (`specs/completed/05-m4-asset-references.md`) — the entire `AssetRef` / `ValueNode.AssetRef` /
  `SetAssetRef` / lowering-resolver machinery this milestone reuses. This is a direct extension of it.
- **M3** — components + serialized fields (`.Set`, the `SerializedFieldBridge`).
- **M2** — Reconcile + Roslyn `SourcePatch` argument rewriting.
- **M2b** — `AddedEntry` in-memory mapping, for the §13 create-with-payload test (#28).
- **§13** (foundation) — create-with-payload composition rules.

## Risks/notes

- **`Resources.GetBuiltinResource` is a trap, and it is the obvious thing to reach for.** It is the
  API every tutorial and every LLM will suggest, and it returns the **wrong mesh for 4 of the 6
  primitives** (§Research). The failure is silent and plausible-looking (you get *a* sphere). The
  pipeline must resolve via `LoadAllAssetsAtPath` + `TryGetGUIDAndLocalFileIdentifier` only.
  Confirmation step #1/#2 asserts object identity against `CreatePrimitive` precisely to catch it.
- **Never hardcode fileIds or a name→fileId table.** The mapping is verified on 6000.5.3f1 only. The
  design's version-robustness rests entirely on deriving `(guid, fileId)` live and persisting only the
  name; a hardcoded table would silently rot into wrong assignments on a version bump. The fileId
  table in §Research is *test data and documentation*, not a source of truth to embed.
- **The `TypeHint`-preservation rule is load-bearing, and it is unintuitive.** Built-in lowering must
  **not** stamp the resolved type into `TypeHint` the way `Asset` lowering does. If it does, every
  Sync rewrites `Builtin("Cube")` → `Builtin("Cube", "Mesh")`, and since the product's premise is
  continuous invisible sync, that is permanent source churn on every keystroke. Test #13 and
  confirmation #8 pin it from both sides.
- **The unresolved-ref clearing hole is a pre-existing data-loss bug this spec closes globally**, not
  just for built-ins. It is currently masked only because `LoweringResolver.Resolve` throws before
  Core can emit the null-GUID op — i.e. safety depends on every resolver remembering to throw. Per
  CLAUDE.md's rule, the guard belongs at the `Materializer` chokepoint every present and future
  caller inherits. **Decision to confirm:** this changes `Materializer` behavior for non-built-in
  refs too; if any existing Core test asserts that an unresolved `Asset(...)` emits a null-GUID op,
  it is asserting the bug and must be updated.
- **Sprite/Texture2D ambiguity is real, not theoretical** — the entire default uGUI sprite set
  (`UISprite`, `Background`, `Checkmark`, `Knob`, …) exists as both. Any design that resolved by bare
  name alone would assign a `Texture2D` where a `Sprite` was meant roughly half the time.
- **Perf is a non-issue but the cache is still required.** Both containers scan in <1 ms warm, but
  sync runs per keystroke; build the catalog once per editor process. It cannot go stale — the
  containers ship inside the editor installation.
- **`Asset.None` is parser-only and uncompilable** (`AssetRefs` exposes no `None` member, and `Asset`
  is a method). Out of scope here, but the adjacent `ValueNodeParser` arm and its test
  `Parse_AssetDotNone_YieldsAssetRefNull` pin behavior no user can author. Flagged, not fixed.
- **Built-in `MonoScript` names can collide with each other** (`Graph`, `GraphGUI`, `Node`,
  `AssemblyDefinitionAsset`) — the type qualifier cannot separate them since the type is identical.
  They correctly fall to the unqualifiable-ambiguity error (#10). Component script identity does not
  route through this path (it uses `TypeRef.MonoScriptGuid`), so this is a genuine dead end, not a gap.
