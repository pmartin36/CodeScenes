# needs_research — Prefab authoring (defining/saving prefab ASSETS from code)

**Status:** research stub, not a build milestone. Promote to a milestone once the open questions below
have concrete-enough answers to write a spec against the foundation contract.

This is the **inverse of M6** (`specs/07-m6-prefab-instances.md`). M6 *instances* an **existing**
prefab/model asset into a scene (`scene.Instance("path")`). This file is about **creating and saving a
prefab asset itself** from code — defining a reusable "Car" once and having it exist as a real
`.prefab` on disk, ready to instance many times.

## Problem
CodeScenes authors **scenes**, not assets. So today there is no way to author a reusable prefab from
code — an LLM must repeat the full component/transform/material setup at every placement (or, with M6,
lean on prefab/model assets a human made in the editor). The natural, DRY way to build component-based
content is to define a prefab once and instance it. That capability is missing, and it is a genuine
want: reusable authored building blocks are how real scenes are composed.

## Why it's hard (and parked)
- **A prefab is a scene-fragment-as-asset.** Constructing the hierarchy reuses the existing
  scene-authoring machinery (`Add`/`Component<T>`/`Transform`/asset refs), so the *build* side is
  largely free. The new surface is **saving** it — `PrefabUtility.SaveAsPrefabAsset` — and giving the
  saved `.prefab` its own GUID + internal-object fileID identity, distinct from scene identity.
- **Round-trip is the blocker, as always.** Editing the prefab asset in Unity → code must reconcile
  against a *prefab asset*, not an open scene. And the moment a code-defined prefab is *instanced*
  (M6/M10), instance overrides vs. the code-defined base become a two-authority problem.
- **Builder-file model is an open design.** One scene = one builder `.cs` today. Does one prefab = one
  builder `.cs` (a `Prefabs/` folder mirroring `SceneBuilders/`)? How do a scene builder and the prefab
  builders it instances cross-reference by GUID?
- **Nested prefabs / variants** multiply all of the above.

## Open questions to resolve before promotion
1. **Authoring surface:** a `PrefabDefinition : ISceneDefinition`-style entry with a `PrefabRoot`? A
   `scene.DefinePrefab(...)` inline form? One builder file per prefab asset (mirroring one-file-per-scene)?
2. **Identity:** how the prefab asset's GUID and its internal objects' fileIDs are minted, persisted
   (a per-prefab sidecar?), and kept stable across regeneration — the same identity discipline scenes
   get, but for an asset.
3. **Round-trip guarantee:** do we reconcile edits to the prefab asset back to its builder, or is the
   asset code-authoritative (build-only, like a generated asset)? Where is the line?
4. **Composition with M6:** a code-defined prefab should be immediately `scene.Instance(...)`-able by
   GUID; confirm the two milestones share one asset-identity model.
5. **Overrides:** when an instance of a code-defined prefab is overridden in a scene (M10), how do the
   base (code-defined) and the override (scene) stay disentangled.

## Related
- Pairs with / sequences after **M6** (`specs/07`, prefab *instances*) — instance existing prefabs
  first, then author new ones.
- Reuses **M3** (components/fields), **M1/M2** (hierarchy + transform + reconcile), **M4** (asset refs).
- Overlaps the general "author an asset, not a scene" concern also present in
  [advanced-animation.md](advanced-animation.md) (clips/controllers as authored assets).
