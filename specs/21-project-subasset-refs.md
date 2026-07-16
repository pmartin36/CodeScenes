# M-SubAsset — Project sub-asset references (authoring a mesh/sub-object inside an imported asset)

### Additions to the contract

**One field, no new ValueNode kind — the project-side twin of M-Builtin (§17).** This milestone adds
`SubAsset : string` to the §3 `AssetRef` POCO and reuses the entire existing asset-ref machinery
(`ValueNode.AssetRef`, the `SetAssetRef(path, guid, fileId)` Plan op, `Differ`, `Materializer`,
`ComponentReconciler`, the sidecar `Assets[]` cache). It introduces **no** new `ValueNode` case,
**no** new `PlanOp`, and **no** new sidecar collection.

Where §17 (M-Builtin) taught the system to name **one object inside a shared editor-install
container** (`Builtin("Cube")`), this milestone teaches it to name **one object inside an imported
project asset** (`Asset("Assets/Models/Barrel.fbx", "BarrelMesh")`). Both are the same underlying
identity — an object addressed by `(guid, fileId)` where `fileId` is a **non-main sub-object id**. The
write path already resolves such refs (`ResolveAssetObject(guid, fileId)` matches sub-objects by
`fileId` via `LoadAllAssetsAtPath`, verified), and the read path already **captures** a hand-assigned
sub-object's `(guid, fileId)`. The two genuine gaps are (a) an authoring **form** to name a sub-asset,
and (b) a **lowering** path that resolves `(project path, sub-asset name) → sub-object fileId` instead
of collapsing to the main asset.

| Added | Shape (summary) | Owner |
|---|---|---|
| `AssetRef.SubAsset : string` | the sub-object NAME persisted in source; empty ⇒ the ref names the main asset (today's behavior); **amends §3** | M-SubAsset |
| `AssetRefs.Asset(path, subAsset)` | a **second string arg** on the existing `Asset(...)` factory; lowers to `ValueNode.AssetRef` | M-SubAsset |
| path-resolver delegate gains a `string? subAsset` parameter | `Func<string, string?, (guid, fileId, typeHint)?>` — symmetric with M-Builtin's `builtinResolver` | M-SubAsset |

The **None** case is unchanged and stays `Asset(null)` (M4). `SubAsset` is empty for every existing
`Asset("path")` ref and every deserialized older model, so main-asset authoring is byte-for-byte
unchanged (regression-critical, §"Core deliverables").

---

## Goal

A component field can reference a **sub-object of an imported project asset** — most importantly a
**Mesh inside an FBX/OBJ**, but equally a sub-material, a sub-texture, or a Sprite inside a sliced
sprite-sheet — from code, by the asset path plus the sub-object's name:
`Asset("Assets/Models/Barrel.fbx", "BarrelMesh")`.

Today this is impossible: `Asset("Assets/Models/Barrel.fbx")` resolves to the FBX's **main** object
(its root GameObject), and assigning that to a `MeshFilter.m_Mesh` (`PPtr<Mesh>`) is a type mismatch —
so **a non-built-in mesh cannot be authored onto a MeshFilter at all.** After this milestone,
`Asset("Assets/Models/Barrel.fbx", "BarrelMesh")` assigns the real sub-mesh, and a MeshFilter that a
user hand-assigns to that sub-mesh in the editor syncs back as
`Asset("Assets/Models/Barrel.fbx", "BarrelMesh")` — not the wrong, main-asset-collapsing
`Asset("Assets/Models/Barrel.fbx")`.

## The gap (observed, not theorized)

Verified against the shipped code:

- **Lowering (code→scene) resolves the MAIN asset only.** `AssetReferenceResolver.LoweringResolver.Resolve(displayPath)`
  (`com.codescenes/Editor/AssetReferenceResolver.cs`) does `AssetDatabase.AssetPathToGUID` →
  `GUIDToAssetPath` → `LoadMainAssetAtPath(currentPath)` (line 112) →
  `TryGetGUIDAndLocalFileIdentifier(main, …)` (line 121). It takes the **main** object's `fileId`.
  There is no way for an authored ref to name a sub-object, so `Asset("…/Barrel.fbx")` yields the
  FBX's root GameObject — never its Mesh. Assigning that to `MeshFilter.m_Mesh` is a `PPtr<Mesh>` type
  mismatch. **This is the capability hole.**
- **The write path already handles sub-objects.** `ResolveAssetObject(guid, fileId)` (line 275) tries
  the main-asset fast path, then falls through to `LoadAllAssetsAtPath(path)` and matches
  `TryGetGUIDAndLocalFileIdentifier(candidate) == (guid, fileId)` (lines 291–303). Given a correct
  sub-object `fileId`, it returns the sub-object. **No write-side change is needed** — the problem is
  purely that lowering never produces a sub-object `fileId`.
- **The read path already CAPTURES a sub-object's identity, but loses the name.**
  `ReadObjectReference` (line 165) calls `TryGetGUIDAndLocalFileIdentifier(obj, out guid, out fileId)`
  (line 181), so a hand-assigned sub-mesh reads back with the **correct** `(guid, subFileId)`. But it
  sets `DisplayPath = AssetDatabase.GetAssetPath(obj)` (line 197) — the FBX path with **no sub-object
  name** — and leaves nothing that would emit a 2-arg form. On the next emit it would render
  `Asset("…/Barrel.fbx")`, which re-lowers to the **main** asset: a churn-and-mis-assign on every
  sync. **This is the round-trip hole** and the make-or-break detail of this spec.

**Why `Asset("path")` alone cannot close this.** An FBX with N meshes exposes N sub-objects that all
share the single asset path; they are distinguished **only by `fileId`**. A path-only form cannot name
a specific one, and cannot round-trip back to it. A second coordinate — the sub-object **name** — is
required, exactly as M-Builtin required a name to pick one object out of a shared container.

## The authoring form — chosen: `Asset(path, subAssetName)`

**Decision: a second, optional string argument on the existing `Asset(...)` factory.**

```csharp
Asset("Assets/Models/Barrel.fbx", "BarrelMesh")   // the FBX's sub-mesh named "BarrelMesh"
Asset("Assets/Materials/Red.mat")                  // unchanged — the main asset
```

**Why this shape.**
- **Symmetric with M-Builtin.** `Builtin(name, typeHint)` is already a 1-or-2-string-arg invocation
  headed by a bare identifier; `Asset(path, subName)` is the same syntactic shape the parser already
  matches (§17 proved this arm). Reuse of a proven Roslyn arm, not new machinery.
- **Reuses the identifier the author already knows.** No new keyword. `Asset` stays the single word
  for "a project asset"; the sub-object is just a refinement of *which* object at that path. A reader
  sees one namespace (`Asset` = project, `Builtin` = editor-install) and the second arg reads as prose
  an LLM emits naturally.
- **Keeps `path` and `subName` as two clean strings.** This is the decisive advantage over the
  fragment alternative below: the project **path** stays a pure path everywhere it is used as a cache
  key, so every existing GUID↔path mechanism keeps working untouched.

**Rejected: `Asset("Assets/Models/Barrel.fbx#BarrelMesh")` (path with a `#` fragment).** It avoids a
new field by packing both coordinates into `DisplayPath`, but that overloads the one string the whole
M4 move-recovery machine keys on:
- `LoweringResolver.Resolve` **harvests** `AssetEntry { LastKnownPath = currentPath }` where
  `currentPath` is the *plain* path (line 128). Move-recovery then matches
  `entry.LastKnownPath == authoredPath` (`RecoverGuidFromCache`, line 148). A fragment-bearing
  `authoredPath` (`"…/Barrel.fbx#BarrelMesh"`) would never equal the plain `LastKnownPath` — so a
  moved FBX would fail to recover. Fixable only by stripping the fragment in several places, i.e. by
  re-deriving the two coordinates the 2-arg form keeps separate for free.
- `ComponentReconciler.CollectAssetEntries` writes `LastKnownPath = r.DisplayPath` (line 361); the
  DisplayPath-re-derivation on move writes the plain path; both would silently corrupt or drop the
  fragment.
- `#` is not reserved in asset names, so the split is ambiguous.

The 2-arg form costs one non-authoritative model field and zero disturbance to the path/cache
machinery. That is the right trade.

## In scope

- **Authoring any project sub-object by `(path, name)`** from a serialized `UnityEngine.Object` field:
  a Mesh inside an FBX/OBJ on `MeshFilter.m_Mesh`; and — **for free, via the same generic rule** — a
  sub-material, a sub-texture, or a **Sprite sliced out of a sprite-sheet** on an `Image.m_Sprite` /
  any `PPtr<Sprite>`. No allowlist: any named sub-object the editor exposes at the path is authorable.
- **Materialize (code→scene):** `Asset(path, name)` lowers to a `ValueNode.AssetRef` carrying the
  sub-object's `(guid, fileId)` (a non-main `fileId` under the asset's project GUID) and emits the
  existing `SetAssetRef(path, guid, fileId)` op; the adapter assigns the real sub-object.
- **Reconcile (scene→code):** a field holding a sub-object reads as a populated `ValueNode.AssetRef`
  with `DisplayPath` = the asset path and `SubAsset` = the sub-object's name; the source is patched to
  `Asset(path, name)` — replacing today's wrong `Asset(path)`.
- **Lists mixing every ref form** — `m_Materials = { Asset("proj.mat"), Builtin("Default-Material"),
  Asset("model.fbx", "SubMat") }` — build in order and round-trip.
- **Move/rename survival:** the sub-object shares the FBX's GUID; a moved FBX recovers via the existing
  `Assets[]` path-keyed recovery, and `DisplayPath` re-derives to the new path while `SubAsset` is
  unchanged.
- **Full regression of the 1-arg `Asset("path")` form** — `SubAsset` empty ⇒ main-asset behavior,
  bit-for-bit as M4.
- **Unresolvable sub-asset name → loud, located error** (§7) listing the available sub-asset names at
  that path — never a silent skip, never a collapse to the main asset, never a field clear.

## Out of scope

- **Creating or editing the sub-asset's contents** — generating a Mesh, editing a Material. This
  milestone only **references** existing imported sub-objects. (Authoring generated meshes/clips is a
  later, separate concern.)
- **Built-in resources** (`Library/unity default resources`, `Resources/unity_builtin_extra`) — that
  is M-Builtin (§17, completed). This spec is its project-side twin, not a replacement.
- **Prefab instances** in the scene and their overrides — M6 / M10. Referencing a **prefab-as-asset**
  (the main GameObject) is already M4's `Asset("path")`; referencing a *sub-GameObject inside a
  prefab asset* is not this spec's mesh/sub-object case and stays out.
- **Scene-to-scene object references** — M5.
- **A per-field type qualifier to disambiguate two sub-objects that share a name inside one file**
  (e.g. a Mesh and a Material both named `"Foo"`). Such an intra-file name collision is a **loud,
  located error** here (§"Editor adapter deliverables"), mirroring §17's unqualifiable-ambiguity dead
  end. A 3rd type-qualifier arg is flagged **OPEN** (§"Risks/notes") should collisions prove common in
  practice; it is deliberately not shipped in the first pass to keep the surface minimal.

## Core deliverables

Types added/changed:
- **`AssetRef.SubAsset : string`** (`SceneBuilder.Core/Model/AssetRef.cs`) — `""` by default, so every
  existing `Asset(...)` ref and every deserialized older model is unchanged. When non-empty it carries
  the sub-object **name** (`"BarrelMesh"`). `DisplayPath` continues to carry the **project path**
  (never the name), keeping it a valid `Assets[]`/move-recovery key.
- **`AssetRef.Equals`/`GetHashCode` — UNCHANGED, keyed on `(Guid, FileId, IsBuiltin)`.** `SubAsset` is
  **non-authoritative for identity**, exactly like `DisplayPath` and `TypeHint`: post-lowering the
  sub-object `FileId` already distinguishes one sub-asset from another, so including `SubAsset` in
  identity would add nothing *and* risks Diff churn if a live read's `obj.name` ever differed in
  case/whitespace from the authored name. The one pre-lowering collision this leaves — two *unresolved*
  refs `Asset("x.fbx","A")` and `Asset("x.fbx","B")` comparing equal (both `Guid==""`, `FileId==0`) —
  is **benign**: the pipeline only ever compares a resolved snapshot against a resolved-or-parsed
  source, never two unresolved sub-asset refs. (Flagged **OPEN** in §"Risks/notes" for ratification.)
- **`SkippedField.Reason = "Unresolved"`** — already exists (M-Builtin); reused unchanged.

Functions/behaviors (each a testable contract):

- **Parse `Asset("path", "name")`** — `ValueNodeParser.ParseAsset` (today accepts **exactly 1** arg)
  extends to **1 or 2** arguments, mirroring `ParseBuiltin`:
  - `Asset("p")` → `AssetRef { DisplayPath = "p", SubAsset = "" }` (unchanged).
  - `Asset("p", "s")` → `AssetRef { DisplayPath = "p", SubAsset = "s" }`.
  - `Asset(null)` → `AssetRef(null)` (unchanged; the 1-arg null-literal None form).
  - `Asset("p", null)`, `Asset()`, `Asset("a","b","c")`, or any non-string-literal 2nd arg →
    `ValueNode.Unsupported(expr.ToString())`. The parser stays **total** — never throws.
  Only the bare identifier `Asset` matches (`AssetRefs.Asset(…)` falls to `Unsupported`, as today).
- **Emit `Asset("path", "name")`** — `SourceExpr.ValueNodeLiteral`'s non-built-in `AssetRef` arm
  (line 108, today `"Asset(" + StringLiteral(DisplayPath) + ")"`) splits on `SubAsset`:
  - `AssetRef { IsBuiltin: false, SubAsset: "" }` → `Asset("p")` (unchanged).
  - `AssetRef { IsBuiltin: false, SubAsset: not "" }` → `Asset("p", "s")`.
  Both args go through the existing `SourceExpr.StringLiteral` escaping. The built-in and `Asset(null)`
  arms are untouched.
- **Lowering** — the path-resolver delegate on `AssetRefLowering.Lower` changes from
  `Func<string, (string guid, long fileId, string typeHint)?>` to
  `Func<string, string?, (string guid, long fileId, string typeHint)?>` — the 2nd arg is the optional
  sub-asset name (symmetric with the existing `builtinResolver`). `LowerAssetRef`'s non-built-in branch
  calls `resolvers.Path(reference.DisplayPath, string.IsNullOrEmpty(reference.SubAsset) ? null : reference.SubAsset)`.
  On a hit it returns `reference with { Guid = guid, FileId = fileId, TypeHint = typeHint2 }` — and
  because `with` copies untouched properties, **`SubAsset` and `DisplayPath` are preserved verbatim
  with no special handling** (unlike the built-in branch, which had to actively *avoid* stamping
  `TypeHint`; here the sub-asset name lives in its own field and lowering never touches it). A null
  return leaves the node unresolved and **does not throw** (the adapter throws loud — M4 contract).
- **Materialize — UNCHANGED.** `Materializer.EmitFieldOp` already emits `SetAssetRef { Guid, FileId }`
  from the ref; a sub-object is just a non-main `FileId`, so no change is needed. The existing
  unresolved-ref guard (`IsUnresolved` ⇒ `SkippedField { Reason = "Unresolved" }`, not a clearing op)
  already covers a sub-asset ref that failed to resolve.
- **Diff — UNCHANGED code, asserted behavior:** `Asset("x.fbx","A")` vs `Asset("x.fbx","B")` differ
  post-lowering (different `FileId`); `Asset("x.fbx","A")` vs `Asset("x.fbx")` (main) differ (different
  `FileId`); a sub-asset vs a `Builtin` differ (different `Guid`); a differing `SubAsset` **name** with
  equal `(Guid, FileId)` is **not** an identity change (non-authoritative).
- **Reconcile text-currency** — `ComponentReconciler.AuthoredTextIsCurrent` (line 304) compares
  `(DisplayPath, IsBuiltin, TypeHint)` for an `AssetRef` pair; it must **additionally compare
  `SubAsset`**, because the emitted source text is now a function of it too. Without this, a sub-mesh
  **renamed in the DCC tool but keeping its `fileId`** (identity-equal, stale text) would never be
  rewritten. This is the exact parallel of §17's `TypeHint` addition to the same method.
- **Sidecar — UNCHANGED.** A project sub-asset **is** a project asset with a real GUID, so
  `ComponentReconciler.CollectAssetEntries` correctly harvests it (`!IsBuiltin && Guid != ""`, line
  360) with `LastKnownPath = DisplayPath` (the asset path). `SubAsset` is **not** stored in the sidecar
  and does not need to be — the sidecar's job is GUID↔path recovery; the name lives in source and in
  the model. Move-recovery keys on the asset path and works unchanged.
- **Canonical serialization** — `SubAsset` round-trips through `CanonicalJson` as a plain camelCase
  `subAsset` string alongside the existing five properties (System.Text.Json serializes it
  automatically, as it does `isBuiltin`).

## Editor adapter deliverables

All in `com.codescenes/Editor/AssetReferenceResolver.cs` unless noted.

- **`LoweringResolver.Resolve(string displayPath)` → `Resolve(string displayPath, string? subName)`**
  — the Core path-resolver delegate. When `subName` is null/empty, behavior is **exactly today's**
  (main asset). When `subName` is non-empty, after recovering the GUID and re-deriving `currentPath`
  as it does now, it resolves the sub-object **by name** instead of taking the main asset:
  - `AssetDatabase.LoadAllAssetsAtPath(currentPath)`, select the object whose `o.name == subName`.
  - Take **its** `TryGetGUIDAndLocalFileIdentifier(o, …)` → `(guid, fileId)`, and `o.GetType().Name` as
    the (informational) `typeHint`.
  - Harvest `AssetEntry { Guid = guid, LastKnownPath = currentPath, TypeHint }` as today (the sub-asset
    shares the asset's GUID and path — move-recovery unchanged).
  - **On zero matches** → the **loud, located error** of §7 naming the object/component/field, the bad
    sub-asset name, and **listing the available sub-object names at that path** (from the same
    `LoadAllAssetsAtPath` scan) — e.g. `Player > MeshFilter.m_Mesh: no sub-asset named 'Barre' in
    'Assets/Models/Barrel.fbx'. Available: BarrelMesh, BarrelLid, BarrelMaterial.` Never a collapse to
    the main asset, never a clear.
  - **On >1 match** (intra-file name collision) → a located error naming the colliding candidates and
    their types (mirroring §17's unqualifiable-ambiguity error). The resolver **should** first attempt
    to disambiguate by the field's **expected `PPtr` type** when that type is derivable — flagged
    **OPEN** below because the threading of the expected type into the resolver is an implementation
    choice the pipeline must confirm.
- **Located pre-pass** — `BuiltinRefValidator.Validate` / `DesiredModelLoader` already walk the
  desired-but-unlowered model to produce **located** errors before lowering (they, not
  `AssetRefLowering`, know the object/component/field). Extend this pass to validate a **project
  sub-asset ref** (`!IsBuiltin && SubAsset != "" && Guid == ""`) the same way it validates built-ins:
  attempt the `(path, name)` resolution and throw the located "no sub-asset named … / available: …"
  error. The always-on unlocated backstop remains the throw inside `Resolve`, so a caller that skips
  the pre-pass still fails loud (never a silent skip).
- **`ReadObjectReference` — emit the sub-asset name on read** (the round-trip fix). For a project
  asset (the existing non-built-in branch, line 192), when the resolved object is **not the main
  asset at its path**, set `SubAsset = obj.name`:
  ```
  var path = AssetDatabase.GetAssetPath(obj);
  var isSubObject = AssetDatabase.LoadMainAssetAtPath(path) != obj;   // NOT "fileId != 0"
  … DisplayPath = path, SubAsset = isSubObject ? obj.name : ""
  ```
  **The discriminator is "is this the main asset", not `fileId != 0`** — main assets also carry a
  non-zero `fileId` (`TryGetGUIDAndLocalFileIdentifier(main)` returns e.g. `100000`/`2100000`, not 0),
  so a `fileId != 0` test would wrongly tag every main asset as a sub-object. (This one Unity-behavior
  claim — main-asset `fileId` is non-zero and `LoadMainAssetAtPath` returns the main object — is the
  single item worth an editor probe; it is long-standing Unity behavior.)
- **`WriteAssetRef` / `ResolveAssetObject` — UNCHANGED.** `ResolveAssetObject(guid, fileId)` already
  resolves sub-objects by `fileId` via `LoadAllAssetsAtPath` (lines 291–303, verified). The write side
  needs no change; a resolved sub-asset ref writes correctly today.
- **Authoring `using` — UNCHANGED.** `Asset(` already triggers
  `SourcePatchApplier.EnsureAssetRefsUsing`; the 2-arg form is still an `Asset(` call, so the compile
  guarantee already holds.

## Authoring API added

An overload on the existing `SceneBuilder.Authoring.AssetRefs` static class
(`com.codescenes/Runtime/AssetReference.cs`), returning the same inert `AssetReference` handle
`Asset(displayPath)` returns. Like all authoring sugar, it is compile-time scaffolding SceneBuilder
parses from source and never executes.

```csharp
public static AssetReference Asset(string displayPath);                       // existing — main asset / None
public static AssetReference Asset(string displayPath, string subAssetName);  // NEW — a named sub-object
```

```csharp
using static SceneBuilder.Authoring.AssetRefs;

public class BarrelScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        // The gap this closes: a project mesh (a sub-object of an FBX) authored from code.
        var barrel = scene.Add("Barrel");
        barrel.Component<MeshFilter>(c => c.Set("m_Mesh", Asset("Assets/Models/Barrel.fbx", "BarrelMesh")));

        // Every ref form mixes freely in one list.
        barrel.Component<MeshRenderer>(c => c.Set("m_Materials", new[] {
            Asset("Assets/Materials/Red.mat"),          // project main asset
            Builtin("Default-Material"),                // editor built-in (§17)
            Asset("Assets/Models/Barrel.fbx", "BarrelMat"), // project sub-material
        }));

        // A sprite sliced out of a sprite-sheet — the same generic (path, name) rule.
        var icon = scene.Add("Icon");
        icon.Component<UnityEngine.UI.Image>(c => c.Set("m_Sprite", Asset("Assets/UI/Atlas.png", "coin")));
    }
}
```

## IdentityMap / sidecar changes

**None.** A project sub-asset shares its parent asset's GUID and path, so it participates in the
existing `Assets[]` cache exactly like any M4 asset: one entry keyed on the GUID with
`LastKnownPath` = the asset path. `SubAsset` (the name) is not cached — it is re-derived live from the
asset on every read and persisted only in source. Move-recovery, keyed on the path, is unchanged.

## Core test plan

Sub-asset resolution is **mocked at the adapter boundary** — Core is handed `(guid, fileId)` by a stub
resolver and never touches Unity. Additions go into the existing `AssetRef*Tests.cs` files
(xUnit, `Subject_Condition_ExpectedOutcome`).

**Parse** (`AssetRefParseTests.cs`):
1. `Parse_AssetWithSubAssetName_YieldsSubAsset` — `Asset("p", "s")` →
   `AssetRef { DisplayPath = "p", SubAsset = "s", Guid = "", IsBuiltin = false }`.
2. `Parse_AssetSingleArg_YieldsEmptySubAsset` — regression: `Asset("p")` →
   `SubAsset == ""` (unchanged).
3. `Parse_AssetNullLiteral_StillYieldsNoneRef` — regression: `Asset(null)` → `AssetRef(null)`.
4. `Parse_AssetWithNonStringOrWrongAritySecondArg_YieldsUnsupported` — `Asset("p", null)`,
   `Asset("p", someVar)`, `Asset()`, `Asset("a","b","c")` → `Unsupported`, verbatim token, no throw.
5. `Parse_AssetBuiltinSubAssetInSameList_YieldsAllThreeKinds` — a list mixing `Asset("p")`,
   `Builtin("Cube")`, `Asset("m.fbx","Sub")` → three nodes with the right
   `SubAsset`/`IsBuiltin`/null shape, in order.

**Emit** (`AssetRefParseTests.cs` / a SourceExpr test file):
6. `SourceExpr_SubAssetRef_EmitsTwoArgAssetCall` → exactly `Asset("p", "s")`.
7. `SourceExpr_MainAssetRef_StillEmitsOneArgAssetCall` — regression: `SubAsset == ""` → `Asset("p")`.
8. `Parse_EmitSubAsset_TextRoundTripsIdentically` — parse → emit → identical source text, both forms.

**Value/identity** (`AssetRefValueTests.cs`):
9. `AssetRef_EqualityIgnoresSubAssetName` — same `(Guid, FileId, IsBuiltin)`, differing `SubAsset` →
   **equal** (non-authoritative, matches the `DisplayPath`/`TypeHint` precedent).
10. `ValueNodeAssetRef_SubAsset_CanonicalRoundTrips` — SceneModel → canonical JSON → parse → equal
    ref, `SubAsset` preserved; `subAsset` present in the JSON.

**Lowering** (`AssetRefLoweringTests.cs`):
11. `Lowering_SubAssetRef_PassesNameToResolverAndSetsGuidFileId` — stub asserts it receives
    `("Assets/Models/Barrel.fbx", "BarrelMesh")` and returns `(fbxGuid, 4300012, "Mesh")` →
    `Guid`/`FileId` set to the sub-object's.
12. `Lowering_SubAssetRef_PreservesDisplayPathAndSubAsset` — after lowering, `DisplayPath` is still the
    path and `SubAsset` is still `"BarrelMesh"` (the anti-churn pin: the name is never overwritten).
13. `Lowering_MainAssetRef_PassesNullSubNameToResolver` — regression: `Asset("p")` calls the resolver
    with `subName == null` and routes to the existing main-asset path.
14. `Lowering_SubAssetResolverReturnsNull_LeavesNodeUnresolvedNoThrow`.

**Materialize** (`AssetRefMaterializeTests.cs`):
15. `Materialize_SubAssetRef_EmitsSetAssetRefWithSubObjectFileId` — the op carries the non-main
    `FileId`.
16. `Materialize_MixedRefList_EmitsOrderedSetAssetRefPerIndex` — `{ Asset(main), Builtin, Asset(sub) }`
    → ordered `[0] [1] [2]` ops, each with the right `(guid, fileId)`.
17. `Materialize_UnresolvedSubAssetRef_IsSkippedNotClearing` — a sub-asset ref with `Guid == ""`,
    `SubAsset != ""` → `SkippedField { Reason = "Unresolved" }`, **no** `SetAssetRef` (reuses the
    M-Builtin data-loss guard; asserts it covers the sub-asset case).

**Diff** (`AssetRefDiffTests.cs`):
18. `Diff_TwoSubAssetsSameFileMainVsSub_ReportsChange` — `Asset("x.fbx")` (main) vs
    `Asset("x.fbx","Sub")` (resolved, different `FileId`) → change.
19. `Diff_SubAssetSameGuidFileIdDifferentName_NoChange` — equal `(Guid, FileId)`, differing `SubAsset`
    → no change.
20. `Diff_SubAssetVsBuiltin_ReportsChange` — different `Guid`.

**Reconcile** (`AssetRefReconcileTests.cs`):
21. `Reconcile_SnapshotSubAssetAgainstMainAssetSource_PatchesToTwoArgForm` — source `Asset("x.fbx")`,
    snapshot a sub-mesh → `SourcePatch` rewrites the argument to `Asset("x.fbx", "BarrelMesh")`.
22. `Reconcile_SubAssetNameChanged_PatchesEvenWhenIdentityEqual` — the `AuthoredTextIsCurrent` pin:
    equal `(Guid, FileId)` but a changed `SubAsset` name → the text is still rewritten.
23. `Reconcile_ResyncUnchangedSubAsset_IsANoOp` — stability: no patch, no churn on a second sync.
24. `Reconcile_SubAssetRef_HarvestsAssetEntryWithAssetPath` — `AddedAssets` gains one entry with
    `LastKnownPath` = the FBX path (not the name), proving move-recovery participation.
25. `Reconcile_SubAssetRefOnNewObject_Converges` — §13 create-with-payload: a newly editor-created
    object carrying a sub-asset ref → appended onto its just-created statement in the same pass, or
    converged on a guaranteed second (no-op) sync; never a silent drop.

## Unity confirmation checklist → EditMode tests

These become EditMode round-trips in `unity-gate/Assets/GateTests/` per CLAUDE.md — a new
`RoundTripSubAssetRefTests.cs` in the established `Direction_Scenario_Expectation` style, driving the
real Build / Sync path against a live scene, asserting **object identity** (not Inspector labels).

**Test fixture deliverable (a required, standalone task).** unity-gate ships **no** model with a mesh
sub-object today, and a bare `AssetDatabase.CreateAsset(new Mesh(), path)` does **not** exercise the
sub-asset path — that mesh becomes the **main** asset. The fixture must be a **true sub-object**.
Recommended (no binary, deterministic, created at `[SetUp]`):

```csharp
var main = new Mesh { name = "BarrelMain" };
AssetDatabase.CreateAsset(main, "Assets/Models/Barrel.subasset.asset");   // main object at the path
var sub  = new Mesh { name = "BarrelMesh" };
AssetDatabase.AddObjectToAsset(sub, main);                                // a genuine SUB-object
AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(main));
```

Now `Asset("Assets/Models/Barrel.subasset.asset", "BarrelMesh")` must resolve to `sub` (fileId ≠
main's), and `Asset("Assets/Models/Barrel.subasset.asset")` resolves to `main`. **Alternative:** copy a
small real `.fbx`/`.obj` into `Assets/Models/` and import it via `ModelImporter` — closer to the real
user case, at the cost of a checked-in binary. Either is acceptable; a container `.asset` with
`AddObjectToAsset` is the lighter default. The fixture must be torn down in `[TearDown]`.

**The headline test — switch a MeshFilter between a custom (project sub-asset) mesh and a built-in
mesh, and back:**

1. **Author the project sub-mesh.** Build a builder assigning
   `MeshFilter.m_Mesh = Asset("Assets/Models/Barrel.subasset.asset", "BarrelMesh")`; Build.
   **Expected:** the assigned mesh **`==`** `sub` (the sub-object), and **`!=`** `main`. Assert object
   identity, not the label — this catches the main-asset-collapse bug.
2. **Swap to a built-in mesh in the scene.** In the editor, drag the built-in **Sphere** mesh into the
   MeshFilter slot; Sync. **Expected:** the source becomes `Builtin("Sphere")` (via §17).
3. **Swap back to the project mesh.** Assign `sub` back into the slot (or re-author); Sync/Build.
   **Expected:** the source returns to `Asset("Assets/Models/Barrel.subasset.asset", "BarrelMesh")`,
   and the assigned mesh `==` `sub` again.
4. **Re-Sync unchanged → no-op.** Sync twice with no edit between. **Expected:** no `SourcePatch`, no
   churn — in particular the source never degrades to `Asset("…/Barrel.subasset.asset")` (the
   anti-churn pin for the read-side `SubAsset` emit + `AuthoredTextIsCurrent` rule).

**Also:**

5. **Scene → code emits the 2-arg form.** Hand-assign the MeshFilter to `sub` in a fresh scene; Sync.
   **Expected:** the source gains `Asset("…/Barrel.subasset.asset", "BarrelMesh")` — **not**
   `Asset("…/Barrel.subasset.asset")` — and the field is not in `Plan.Skipped`.
6. **Mixed ref list builds in order.** Author `m_Materials = { Asset("Assets/Materials/Red.mat"),
   Builtin("Default-Material"), Asset("…/Barrel.subasset.asset", "BarrelMat") }`; Build. **Expected:**
   all three slots assigned, in order, each to the right object (assert identity).
7. **Unknown sub-asset name → loud, located error.** Author
   `Asset("…/Barrel.subasset.asset", "Barre")`; Build. **Expected:** a located error naming
   object/component/field, the bad name, and the **available** sub-asset names; the live slot is left
   **untouched — not cleared**, and Build does not silently succeed.
8. **Move survival.** Build with the sub-mesh ref, then move the asset to
   `Assets/Models/Renamed.subasset.asset` in the editor; Sync/Build. **Expected:** the ref still
   resolves (GUID unchanged), `DisplayPath` re-derives to the new path, `SubAsset` unchanged, and no
   broken reference.
9. **Main-asset regression.** `Asset("Assets/Materials/Red.mat")` on a `MeshRenderer` still assigns the
   material main asset and round-trips as the 1-arg form — proving the 2-arg change did not disturb M4.

## Dependencies

- **M4** (`specs/completed/05-m4-asset-references.md`) — the entire `AssetRef` / `ValueNode.AssetRef` /
  `SetAssetRef` / lowering-resolver / `Assets[]` sidecar machinery this milestone extends. Direct
  extension.
- **M-Builtin** (`specs/completed/17-builtin-resources.md`) — the *pattern* this spec parallels: one
  non-authoritative field, the located-error pre-pass (`BuiltinRefValidator.Validate` /
  `DesiredModelLoader`), the `AuthoredTextIsCurrent` text-drift rule, the unresolved-ref skip guard,
  and the sub-object `fileId` resolution in `ResolveAssetObject` (already present) that both reuse.
- **M3** — components + serialized fields (`.Set`, the field bridge).
- **M2 / M2b** — Reconcile + Roslyn `SourcePatch` argument rewriting; `AddedEntry` in-memory mapping
  for the §13 create-with-payload test (#25).
- **§13** (foundation) — create-with-payload composition rules.

## Placement

Numbered **21**, after M-Builtin's family (§17) as its project-side twin. It sits in the pending queue
**after spec 20** (`20-unqualified-type-names.md`), alongside the asset-reference line of work. It is
**independent of M-Auto (§14), M5 (§06), and M6 (§07)** — it needs nothing from live-sync, cross-object
refs, or prefab instances — and is a small, high-value ergonomic extension of M4 + M-Builtin. Because
it closes a hard capability hole (a non-built-in mesh cannot be authored onto a MeshFilter at all) with
a contained, well-understood change, it **can be pulled early** ahead of the larger pending milestones.

## Risks/notes

- **The read-side "is this the main asset?" test is load-bearing and must NOT be `fileId != 0`.** Main
  assets carry a non-zero `fileId`, so a `fileId != 0` discriminator would tag every ordinary
  `Asset("Red.mat")` as a sub-object and emit a spurious 2-arg form. The discriminator is
  `LoadMainAssetAtPath(path) != obj`. Confirmation #4/#9 pin both sides (no spurious sub-name on a
  main asset; correct sub-name on a real sub-object).
- **The round-trip emit is the make-or-break detail.** The scene already stores the correct
  `(guid, fileId)` for a hand-assigned sub-mesh; the only reason it doesn't round-trip today is that
  read drops the name and emit renders the 1-arg form. If read fails to set `SubAsset`, every sync
  rewrites the field to the main-asset-collapsing `Asset("path")` and mis-assigns on the next Build —
  permanent churn under continuous sync. Tests #4/#5 and Core #12/#21/#22 pin it.
- **OPEN — `SubAsset` in identity `Equals`?** This spec keeps `Equals` = `(Guid, FileId, IsBuiltin)`
  (name non-authoritative), accepting a benign, never-exercised pre-lowering collision (two unresolved
  sub-asset refs of one file comparing equal) in exchange for zero Diff churn from `obj.name`
  case/whitespace. The alternative — add `SubAsset` to `Equals`, matching §17's IsBuiltin rationale —
  hardens the collision at the cost of that churn risk. **Recommend keeping it out of identity; flag
  for ratification.**
- **OPEN — intra-file name collision (a Mesh and a Material both named `"Foo"` in one FBX).** The
  shipped behavior is a loud, located error listing the colliding candidates and their types. A
  cleaner resolution is to disambiguate by the field's **expected `PPtr` type** (the field is a
  `PPtr<Mesh>`, so pick the Mesh) — but threading the expected type into the resolver is a real design
  choice (the located pre-pass knows the component/field; the unlocated `Resolve` backstop does not).
  Alternatively a 3rd type-qualifier arg `Asset(path, name, typeName)`, emitted only when ambiguous,
  mirrors §17. **Both deferred; recommend shipping the loud error first and adding disambiguation only
  if collisions occur in practice.** Real FBX sub-mesh names are effectively unique, so this is a rare
  edge, not a common case.
- **`Asset(path, null)` is a malformed call, not a None form.** The None form is the 1-arg
  `Asset(null)`; a 2-arg call with a null 2nd arg parses to `Unsupported` (§"Parse"). Documented so a
  user's stray null does not silently become "clear the field".
- **Sprite-sheet and sub-material coverage is free but untested-by-default.** The generic `(path,
  name)` rule covers Sprites sliced from a sprite-sheet and sub-materials/sub-textures with no extra
  code. The headline test exercises a sub-mesh; a sprite-sheet case is worth one extra EditMode test
  (#6 already exercises a sub-material) but is not a separate deliverable.
