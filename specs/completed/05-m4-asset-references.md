# M4 — Asset references (both directions)

### Additions to the contract
None. M4 introduces no new Core types. It fully exercises the existing `ValueNode.AssetRef` /
`AssetRef` POCO (§3), the `SetAssetRef(path,guid,fileId)` Plan op (§5), and the sidecar `Assets[]`
cache (§4). The one authoring addition — the `Asset("...")` helper — is Editor-side fluent sugar
that lowers to `ValueNode.AssetRef`; it is not a Core type.

The **None / cleared** case is likewise not a new type: it is the `ref == null` inhabitant of the
existing `ValueNode.AssetRef(ref: AssetRef?)` (§3, symmetric with `ObjectRef`'s null). Clearing is
carried by the existing `SetAssetRef(path, guid, fileId)` op with a **null/empty `guid`** (the None
form) — no new Plan op. Its authoring sugar — `Asset(null)` — is Editor-side, like
`Asset("...")`, and lowers to `ValueNode.AssetRef(null)`.

## Goal
A component field can reference a project asset (material, mesh, texture, MonoScript,
AnimatorController, or another prefab-as-asset). The author writes a readable `DisplayPath`; the
system resolves and persists the asset's **GUID** as the authoritative identity, so the reference
survives the asset being moved or renamed, and fails loud when the asset is deleted. Sync runs both
directions: code sets asset-ref fields in the scene; scene edits to asset-ref fields patch the code.

## In scope
- Referencing project assets from serialized component fields whose serialized value is a
  `UnityEngine.Object` asset: e.g. `MeshRenderer.sharedMaterial` / `sharedMaterials[i]`,
  `MeshFilter.sharedMesh`, texture fields, `Animator.runtimeAnimatorController`, a `MonoScript`
  target, and prefab-asset references (a field pointing at a prefab `GameObject` asset, not a scene
  instance — instances are M6).
- **GUID as authoritative identity.** Author by `DisplayPath`; the adapter resolves path→GUID at
  build time; Core stores and persists the GUID. `FileId` identifies the sub-object (0 = main asset).
- **DisplayPath is derived, never authoritative.** On every read it is re-derived from the GUID via
  the sidecar `Assets[]` cache / AssetDatabase. It exists only for human readability in source.
- **MonoBehaviour script identity via MonoScript GUID.** Completes the M3 note: a component's
  `TypeRef` for a `[SerializeField]` MonoBehaviour is anchored to its `MonoScript` asset GUID in the
  dedicated `TypeRef.MonoScriptGuid` field, so the type resolves independent of assembly/namespace churn.
- **Materialize (code→scene):** emit `SetAssetRef(path, guid, fileId)` ops; adapter sets the
  serialized field's `objectReferenceValue` to the resolved asset (sub-object via `fileId`).
- **Reconcile (scene→code):** detect an asset-ref field whose GUID/FileId changed in the snapshot;
  patch the source argument to the new asset's re-derived `DisplayPath`; persist the new GUID to the
  sidecar `Assets[]` cache.
- **Set an asset field to None / clear it — both directions**, modeled as `ValueNode.AssetRef(null)`
  per §3 (the parallel of M5's `ObjectRef` null case):
  - **Materialize (code→scene):** authoring a null/None asset field (`Asset(null)`)
    lowers to `ValueNode.AssetRef(null)` and materializes as **clearing** the field — the adapter
    sets `SerializedProperty.objectReferenceValue = null`. Emitted as `SetAssetRef(path, guid=null,
    fileId=0)` (the None form of the existing op).
  - **Reconcile (scene→code):** an asset field the user set to **None** in Unity (snapshot holds
    `AssetRef(null)` where the source assigned an asset) → patch the source argument to the None
    form (`Asset(null)`); no `Assets[]` entry is required for a cleared field.
  - Clearing is distinct from **missing/unresolvable** (a stale GUID with no owning asset stays a
    loud, located error §7); an explicit None is a legitimate authored value, never an error.
- **Move/rename stability:** asset moved or renamed → GUID unchanged → ref still resolves;
  `DisplayPath` re-derived to the new path on next read (a Reconcile may update the literal in source
  for readability, but identity never depended on it).
- **Missing / unresolvable asset:** GUID no longer maps to any asset → **loud, located error** (§7);
  never silent-drop, never null-coerce.
- Lists/arrays of asset refs (`sharedMaterials`) via `ValueNode.List` of `ValueNode.AssetRef`.

## Out of scope
- Scene-to-scene object references (GameObject/Component handles) — that is **M5**.
- Prefab **instances** in the scene and their overrides — **M6 / M10**.
- Creating or editing the asset's own contents (materials, meshes) — M4 only references existing
  assets; it never authors them. (Generated clips are M11.)
- `[SerializeReference]` managed references — **M9**.
- Nested asset creation or importing new files.

## Core deliverables
Types added/changed (all already in §3 — used, not invented):
- `ValueNode.AssetRef(ref: AssetRef)` — the value carried by an asset-ref field.
- `AssetRef { Guid, FileId, TypeHint, DisplayPath }` — `Guid` authoritative, `FileId` (0 = main),
  `DisplayPath` re-derived.
- Plan op `SetAssetRef(path, guid, fileId)` (§5) emitted by Materialize.
- Sidecar `Assets[] { Guid, LastKnownPath, TypeHint }` (§4).

Functions/behaviors (each a testable contract):
- **Lowering:** the builder's `Asset(displayPath)` lowers to a `ValueNode.AssetRef` carrying the
  resolved `Guid`/`FileId` supplied by the adapter boundary; Core never itself touches a filesystem
  or AssetDatabase — it receives the GUID. Given a resolver that maps `"Assets/Materials/Red.mat"` →
  `(guid, fileId=0)`, lowering produces `AssetRef { Guid=guid, FileId=0, TypeHint="Material" }`.
- **Canonical serialization:** an `AssetRef` serializes deterministically keyed on `Guid`+`FileId`
  (authoritative) with `DisplayPath` present but marked non-authoritative; two `AssetRef`s with the
  same `Guid`/`FileId` but different `DisplayPath` are equal for diffing.
- **Diff:** `Diff(desired, actual)` compares asset-ref fields on `(Guid, FileId)` only; a differing
  `DisplayPath` alone is **not** a change. A differing `Guid` or `FileId` is a change.
- **Materialize → Plan:** a `SceneModel` whose field holds an `AssetRef` produces a
  `SetAssetRef(path, guid, fileId)` op; a `List` of `AssetRef` produces ordered `SetAssetRef` ops per
  index.
- **Reconcile → SourcePatch:** a snapshot field whose `(Guid, FileId)` differs from the parsed
  source produces a `SourcePatch` that rewrites the `Asset("...")` argument to the re-derived
  `DisplayPath`; and writes the new `Guid` to `Assets[]`.
- **Asset-ref on a newly-created object (§13).** An asset-ref field on a GameObject/component that was
  editor-created in the same edit is appended onto that just-created statement in the same Reconcile pass
  (owner mapped in-memory via M2b's `AddedEntry`, §13 rule 1), or reported and converged on a guaranteed
  second Sync (§13 rule 2) — never silently dropped. Cites §13 (create-with-payload).
- **Move stability:** given the same `Guid` with a new `LastKnownPath`, re-derivation yields the new
  `DisplayPath` and Diff reports **no** identity change.
- **Missing GUID:** a `Guid` that the resolver cannot map produces a located error naming the object,
  component, field, and last-known path (`Player > MeshRenderer.sharedMaterial: asset
  {guid} (was 'Assets/Materials/Red.mat') not found`); it is never dropped or coerced to null. The
  runtime throw ships in the adapter's `AssetReferenceResolver` — Core receives GUIDs and never touches
  the AssetDatabase, so a missing GUID can only surface at that boundary.
- **Sidecar cache round-trip:** `Assets[]` reads and writes `{ Guid, LastKnownPath, TypeHint }`;
  `DisplayPath` for a known GUID is served from this cache without an AssetDatabase hit when possible.
- **None / clear:** a field holding `ValueNode.AssetRef(null)` materializes to `SetAssetRef(path,
  guid=null, fileId=0)` (the field is cleared, not skipped); Diff treats `AssetRef(null)` vs a
  non-null `AssetRef` as a change (both directions), and `AssetRef(null)` vs `AssetRef(null)` as no
  change. Reconcile of a snapshot field that is `AssetRef(null)` against a source that assigned an
  asset produces a `SourcePatch` rewriting the argument to the None form (`Asset(null)`).
  Canonical serialization of `AssetRef(null)` is a deterministic None token, distinct
  from any populated `AssetRef`.

## Editor adapter deliverables
- **Path↔GUID↔object resolution** via `AssetDatabase`: `AssetPathToGUID`, `GUIDToAssetPath`,
  `LoadAssetAtPath`, and `LoadAllAssetsAtPath` + `AssetDatabase.TryGetGUIDAndLocalFileIdentifier`
  for sub-object `FileId`. Supplies `(Guid, FileId, TypeHint)` to Core at lowering; supplies
  re-derived `DisplayPath` from a `Guid` at read.
- **Write** asset refs: execute `SetAssetRef` by resolving `(guid, fileId)`→`UnityEngine.Object` and
  assigning `SerializedProperty.objectReferenceValue` (sub-object resolved via `fileId`), then
  `ApplyModifiedProperties`. A `SetAssetRef` with a **null/empty `guid`** (the None form) assigns
  `objectReferenceValue = null` — clears the field — rather than resolving an asset.
- **Read** asset refs into the `SceneSnapshot`: for each object-reference field pointing at an asset,
  emit `TryGetGUIDAndLocalFileIdentifier(objectReferenceValue)` → `AssetRef { Guid, FileId,
  TypeHint }`, plus the re-derived `DisplayPath`. An asset field whose `objectReferenceValue` is
  `null` reads as `ValueNode.AssetRef(null)` (None), not as an error.
- **MonoScript resolution:** for MonoBehaviours, resolve the component's `MonoScript` and its asset GUID
  (via `AssetDatabase`) and stamp it into the component's `TypeRef.MonoScriptGuid`. `ComponentTypeResolver`
  then resolves the type GUID→path→`MonoScript`→class FIRST (surviving assembly/namespace churn), falling
  back to `TypeRef.FullName`.
- **Missing asset:** when a GUID resolves to no asset, the adapter's `AssetReferenceResolver` throws the
  located error directly (an `InvalidOperationException` naming the object/field/GUID/last-known path);
  it never assigns null silently.

## Authoring API added
`Asset(displayPath)` — an Editor-side fluent factory returning an asset-ref value that lowers to
`ValueNode.AssetRef`. Usable anywhere a serialized asset field is set.

```csharp
public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var cube = scene.Add("Cube");
        cube.Add<MeshFilter>(mf => mf.Set(m => m.sharedMesh, Asset("Assets/Meshes/Cube.fbx")));
        cube.Add<MeshRenderer>(mr => mr.Set(r => r.sharedMaterial, Asset("Assets/Materials/Red.mat")));
        // list of asset refs
        cube.Add<MeshRenderer>(mr => mr.Set(r => r.sharedMaterials, new[] {
            Asset("Assets/Materials/Red.mat"),
            Asset("Assets/Materials/Blue.mat"),
        }));
    }
}
```
Source stays path-readable; the sidecar carries the authoritative GUID.

## IdentityMap / sidecar changes
- Populate and maintain the sidecar `Assets[]` cache (§4): one entry per referenced asset,
  `{ Guid, LastKnownPath, TypeHint }`.
- On Materialize: ensure every referenced GUID has an `Assets[]` entry with its current path.
- On Reconcile: when a scene edit introduces a new GUID, add/update its `Assets[]` entry; the entry's
  `LastKnownPath` is the source of the re-derived `DisplayPath` written into source.
- `DisplayPath` literals in the C# source are cosmetic mirrors of `Assets[].LastKnownPath`; the map,
  not the source, is authoritative for identity.

## Core test plan
Path→GUID resolution is **mocked at the adapter boundary** — Core is handed a GUID and never resolves
paths itself. RED tests:
1. **Lowering:** `Asset("Assets/Materials/Red.mat")` + a stub resolver → `ValueNode.AssetRef` with
   `Guid` set, `FileId=0`, `TypeHint="Material"`.
2. **AssetRef round-trip:** SceneModel → canonical serialize → parse → equal `AssetRef` (`Guid`,
   `FileId`, `TypeHint` preserved).
3. **Diff ignores DisplayPath:** two `AssetRef`s equal `Guid`/`FileId`, different `DisplayPath` →
   Diff reports no change.
4. **Diff on identity:** differing `Guid` (or `FileId`) → Diff reports a change.
5. **Materialize → Plan:** field with `AssetRef` → `SetAssetRef(path, guid, fileId)`; list of refs →
   ordered per-index ops.
6. **Reconcile → SourcePatch:** snapshot `(Guid,FileId)` ≠ source → patch rewrites the `Asset("...")`
   argument to the re-derived path and updates `Assets[]`.
7. **Move stability:** same `Guid`, new `LastKnownPath` → re-derived `DisplayPath` = new path, Diff
   reports no identity change.
8. **Missing GUID → error:** resolver returns none for a GUID → located error naming
   object/component/field/last-known-path; no drop, no null.
9. **Sub-object FileId:** `AssetRef` with `FileId != 0` round-trips and produces
   `SetAssetRef(path, guid, fileId)` carrying the non-zero `FileId`.
10. **Sidecar `Assets[]` read/write:** write cache → read back `{ Guid, LastKnownPath, TypeHint }`
    intact; re-derivation of `DisplayPath` uses the cache.
11. **MonoScript identity:** a MonoBehaviour `TypeRef` whose `MonoScriptGuid` is set round-trips and
    resolves the same type when the assembly hint differs.
12. **`Materialize_NullAssetRef_ClearsField`:** a field holding `ValueNode.AssetRef(null)` →
    `Plan` contains `SetAssetRef(path, guid=null, fileId=0)` (clear), not a skip and not a populated
    op.
13. **`Reconcile_AssetRefClearedToNone_PatchesSourceToNull`:** source assigns
    `Asset("Assets/Materials/Red.mat")`, snapshot field is `AssetRef(null)` → `SourcePatch` rewrites
    the argument to the None form (`Asset(null)`); no `Assets[]` entry required.
14a. **`Reconcile_AssetRefOnNewObject_Converges`** (§13 create-with-payload). A newly editor-created
    object carrying an asset-ref field in one edit → the `Asset("…")` argument is appended onto that
    object's just-created statement in the same pass (owner mapped via M2b's `AddedEntry`), or reported
    and converged on a second Sync; second Sync of the unchanged scene is a no-op; no silent drop.
14. **Diff on None:** `AssetRef(null)` vs a non-null `AssetRef` → change (both directions);
    `AssetRef(null)` vs `AssetRef(null)` → no change; canonical None token distinct from any
    populated `AssetRef` and round-trips.

## Unity confirmation checklist
1. Author `FooScene` referencing `Assets/Materials/Red.mat` on a Cube's `MeshRenderer`; run
   Materialize. **Expected:** Cube's material in the Inspector is Red.mat.
2. In Unity, drag a different material (Blue.mat) onto the `MeshRenderer` material slot; trigger
   Reconcile. **Expected:** source updates to `Asset("Assets/Materials/Blue.mat")`; sidecar
   `Assets[]` gains Blue.mat's GUID.
3. Move/rename `Red.mat` to `Assets/Art/Crimson.mat` in Unity; run Materialize (or Reconcile).
   **Expected:** the reference still resolves (GUID unchanged); `DisplayPath` re-derives to the new
   path — no broken reference.
4. Delete `Blue.mat` from the project; run sync. **Expected:** a loud, located error naming the
   object, `MeshRenderer.sharedMaterial`, the GUID, and the last-known path — the field is not
   silently cleared.
5. Assign a mesh via `Asset(".../Cube.fbx")` and confirm the `MeshFilter.sharedMesh` slot is set
   (sub-object `FileId` correct for an FBX with multiple meshes).
6. **Clear a field to None in Unity.** With `MeshRenderer.sharedMaterial` assigned, set the material
   slot to **None** in the Inspector; trigger Reconcile. **Expected:** the source updates to the
   None form (`Asset(null)`); the field is not treated as a missing-asset error.
7. **Author None → scene.** Write the field as `Asset(null)` in source; run
   Materialize. **Expected:** the corresponding slot in the Inspector is **empty** (cleared), not
   left at its prior asset.

## Dependencies
- **M3** — components + serialized fields (typed setters, generic `.Set`, `SceneSnapshot` of fields);
  M4 adds the asset-ref value kind and completes the M3 MonoScript-identity note.
- **M2** — Reconcile + Roslyn `SourcePatch` (argument rewrite mechanism reused).
- **M1** — Materialize → Plan pipeline and IdentityMap.

## Risks/notes
- **Sub-object `FileId`:** multi-object assets (FBX with sub-meshes, sprite sheets) require correct
  `FileId`; `TryGetGUIDAndLocalFileIdentifier` is the authority. `FileId=0` main-asset assumption
  must not leak into sub-object refs.
- **GUID-not-found vs. path-not-found:** a moved asset (GUID present, path stale) must NOT be treated
  as missing; only a GUID with no owning asset is an error. Keep these paths distinct.
- **Cache staleness:** `Assets[].LastKnownPath` can lag the real path; it is a readability hint, not
  identity — always re-derive from AssetDatabase when present, fall back to cache when headless.
- **MonoScript for types with no script asset** (built-in components) has no MonoScript GUID; those
  keep the plain `TypeRef.FullName` path from M3 — only user MonoBehaviours use the MonoScript GUID.
