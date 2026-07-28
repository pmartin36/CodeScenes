# M-AssetCatalogs — Typed asset catalogs (compiler-checked asset references)

> **Why this milestone exists — it is the governing principle applied to the last big string surface.**
> CodeScenes' whole point is that an LLM writes scene-builder C# that the **compiler** verifies: if the
> model misunderstands, the code **fails to compile** rather than silently doing the wrong thing
> (foundation §1, §6; spec 22's opening; spec 25's opening; spec 26's opening). String **asset paths**
> defeat this. `Asset("Assets/Materials/Stone.mat")` sails past the C# compiler on a typo
> (`Stone.mate`), on a rename (`Stone.mat → Granite.mat`), or on a move to another folder — and surfaces
> only at Build/runtime as a located error, after the LLM already committed the wrong text. Asset paths
> are the **single biggest remaining magic-string surface** in the authoring API (spec 25 dispatched the
> prefab-path surface; spec 26 dispatched tags/layers). This milestone replaces the string surface with
> compiler-checked typed catalogs — `Assets.Materials.Environment.Rocks.Stone`,
> `Assets.Meshes.Models.Props.Barrel`, `Assets.Sprites.UI.Icons.Coin` — so a typo fails to compile and a
> rename either auto-rewrites the reference or fails to compile, never silently mis-resolves.
>
> **The mechanism is the one the product already runs on, reused verbatim.** CodeScenes never RUNS the
> builder — it parses the source **text** with Roslyn. `Prefabs.Tank` and `t.Turret.Barrel` (spec 25)
> work because the parser reads those member chains out of the text and maps them, through a generated
> **manifest**, to a prefab GUID / durable child id; the generated types carry no runtime behavior and
> exist only so the code compiles and IntelliSense completes. **A typed asset reference is more of that
> same scaffolding**: `Assets.Materials.Environment.Rocks.Stone` is a member chain the parser reads
> exactly as it reads `Prefabs.Tank`, emitted into the compilation by a source generator on **spec 26's shipped toolkit
> framework**, resolved through a manifest to the **same** `AssetRef` the string `Asset("path")` form
> produces. This milestone is a **second (well, third) catalog on that framework** — not new machinery.

---

## Additions to the contract

This milestone adds **one on-disk manifest format**, **one Core POCO** (its deserialized shape), **one
generated catalog shape** (`Assets.*`), **one editor manifest producer**, **one source generator on the
spec-26 framework**, and **one rename patcher** — every one of them mirroring a piece that spec 25/26
already shipped. **No existing POCO changes shape.** `AssetRef`, `ValueNode.AssetRef`, `SetAssetRef`, the
§4 sidecar `Assets[]` cache, `AssetReferenceResolver.LoweringResolver`, and `SerializedFieldBridge` are
**reused verbatim** — the typed member lowers to the identical `AssetRef` the string form lowers to, so
all downstream is untouched.

| Added | Shape (summary) | Mirrors |
|---|---|---|
| **`AssetCatalog` (Core POCO + JSON)** | deserialized shape of `Assets.sbassets.json`; `Entries[]` ordered `(Group, FolderSegments, MemberName, Guid, FileId)` Ordinal; forward/reverse lookups | `FacadeCatalog` (`SceneBuilder.Core/Model/FacadeCatalog.cs`), `ProjectCatalogManifest` |
| **Asset manifest `Assets.sbassets.json`** | editor-written under `<ProjectRoot>/SceneBuilders/Generated/`, plain `File` IO, no `AssetDatabase`, no domain reload; the generator's `AdditionalFile` input AND the parser's resolution source | `Prefabs.sbfacade.json` (spec 25), `ProjectCatalog.sbcatalog.json` (spec 26) |
| **Generated `Assets` catalog** | `public static class Assets { public static class Materials { public static class Environment { public static class Rocks { public static AssetReference Stone => default!; } } … } … }` — emitted INTO the compilation; makes `c.Set(x => x.sharedMaterial, Assets.Materials.Environment.Rocks.Stone)` compiler-checked | `Prefabs`/façade emit (`FacadeEmit`), `Tags`/`Layers` emit (`CatalogEmit`) |
| **`AssetCatalogGenerator` (editor producer)** | scans `AssetDatabase` by type, builds `AssetCatalog`, `WriteTextIfChanged` — compare-before-write, deterministic, idempotent | `PrefabFacadeManifestGenerator`, `ProjectCatalogGenerator` |
| **`AssetCatalogSourceGenerator` (`IIncrementalGenerator`)** | keys on the `Assets.sbassets.json` AdditionalFile, `AddSource`s the `Assets` types via a new `AssetCatalogEmit` | `PrefabFacadeGenerator` + `FacadeEmit`; `CatalogSourceGenerator` + `CatalogEmit` |
| **`AssetReferencePatcher` (Core)** | formatting-preserving Roslyn edit; rewrites a renamed leaf identifier AND/OR moved folder segments of an `Assets.<Type>.<folders…>.<Name>` chain keyed on durable `(Guid, FileId)`; a vanished target left untouched (→ compile error) | `FacadeReferencePatcher` (`SceneBuilder.Core/Facades/FacadeReferencePatcher.cs`) |
| **`Assets.<Type>.<folders…>.<Name>` member-chain parse** | new `ValueNodeParser` arm resolving the chain through `AssetCatalog` to a `ValueNode.AssetRef` | `Prefabs.X` parse (`BuilderParser.Instance.cs:47`), `Assets.Materials.Environment.Rocks.Stone` reads like `l.intensity` |

The invariant "**generated C# must compile**" (foundation, spec 22) and "**compiles AND is
analyzer-clean**" (spec 26) both apply: the `Assets` catalog must always emit compiling source, and the
emitted typed form must be analyzer-clean.

---

## Goal

Author every asset reference as a **compiler-checked, IntelliSense-completing C# member chain** —
`c.Set(x => x.sharedMaterial, Assets.Materials.Environment.Rocks.Stone)`,
`c.Set(x => x.sharedMesh, Assets.Meshes.Models.Props.Barrel)` — generated from the project's real assets,
instead of a magic path string. The member shape is `Assets.<Type>.<folder segments…>.<Name>`: the asset
**type** is always the first group, followed by the asset's project folder path (the folders under
`Assets/`, sanitized), then the leaf name (extension dropped). A typo, or a stale reference after a Unity
rename or move, now **fails to compile** with a located error at the exact bad accessor; a rename (leaf
change) or a move (folder change) that IS synced **auto-rewrites** the accessor — a move now rewrites the
folder segments of the member in place, keyed on the durable `(Guid, FileId)`, and re-resolves its path
from the fresh manifest. The generated types carry no runtime behavior — the
parser reads the member chain exactly as it reads `Prefabs.Tank`, and the typed member lowers to the
**identical `AssetRef`** the string `Asset("path")` form lowers to, so Build / SetAssetRef /
SerializedFieldBridge are untouched. The string `Asset(...)` / `Builtin(...)` forms remain supported
(fallback); the typed form is preferred and is what Sync emits.

## In scope

- **The `Assets` catalog, type-first then folder-path** — a generated static class `Assets` whose first
  nesting level is the asset **type group**, then one nested static class per **project folder segment**
  (the folders under `Assets/`, sanitized), and finally one property per asset named by its file (or
  sub-object) name with the extension dropped: `Assets.Materials.Environment.Rocks.Stone`,
  `Assets.Meshes.Models.Props.Barrel`, `Assets.Sprites.UI.Icons.Coin`, `Assets.Scenes.Scenes.MainMenu`,
  `Assets.ScriptableObjects.Config.EnemyConfig`, `Assets.AnimationClips.Anim.Idle`. Each property is typed
  `SceneBuilder.Authoring.AssetReference` (the existing authoring type, `com.codescenes/Runtime/
  AssetReference.cs`), so it drops in anywhere `Asset(...)` is accepted today (a scalar object-ref field,
  or an element of an `AssetReference[]` for `m_Materials`). It has no runtime body that runs.
- **Catalogued asset type groups (v0):** **Materials, Meshes, Sprites, Scenes, ScriptableObjects,
  AnimationClips.** These cover the asset kinds real builders reference through serialized fields.
  Textures, AudioClips, Fonts, and further kinds are trivial follow-on groups on the same mechanism, not
  v0.
- **The member shape is `Assets.<Type>.<folder segments…>.<Name>` — type-first, folder-path,
  deterministic, always.** The asset type is ALWAYS the first group; the folders under `Assets/` follow
  (each a nested class); the leaf property is the file (or sub-object) name with the extension dropped.
  Two rationale, both serving the north star:
  - **Type-first makes every reference self-document its asset type.** An LLM assigning `sharedMaterial`
    reaches into `Assets.Materials.` and knows everything under it is a Material; autocomplete narrows the
    same way. This mirrors how the model already thinks about the field it is assigning.
  - **The folder path makes members provably collision-free WITHOUT numeric suffixes.** Within one type
    group two assets collide only if they share BOTH the folder path AND the name — which the filesystem
    forbids (no two `Stone.mat` in one folder). `Assets/Environment/Rocks/Stone.mat` →
    `Assets.Materials.Environment.Rocks.Stone` and `Assets/Characters/Skin/Stone.mat` →
    `Assets.Materials.Characters.Skin.Stone` are distinct references with NO suffix. This replaces the
    former flat `Stone` / `Stone2` numeric disambiguation, which was bad for the north star: an LLM could
    not tell `Stone` from `Stone2` and the compiler accepted either (a silent wrong-asset hazard).
- **Leading-folder collapse (2026-07-28 refinement — drop a folder segment that duplicates the group).**
  A leading folder segment whose sanitized identifier equals the type-group name is DROPPED from the
  member chain, because the stutter hurts LLM-writability and is the common case (assets live in a
  type-named folder). So `Assets/Materials/Green.mat` (group `Materials`, folders `[Materials]`) emits
  `Assets.Materials.Green`, NOT `Assets.Materials.Materials.Green`; `Assets/Scenes/MainMenu.unity` →
  `Assets.Scenes.MainMenu`; `Assets/Materials/Environment/Rocks/Stone.mat` →
  `Assets.Materials.Environment.Rocks.Stone` (only the LEADING duplicate is dropped, not a deeper one).
  **Collision-aware:** apply the collapse ONLY when the collapsed member does not collide with another in
  the same group. `Assets/Materials/Green.mat` collapsing to `Assets.Materials.Green` would collide with a
  hypothetical `Assets/Green.mat` (folders `[]`, same group+name); when the collapsed form is already
  taken, keep the folder segment (do not collapse) so uniqueness holds. Deterministic and byte-stable.
  Pure member-name refinement: resolution identity (`Guid`/`FileId`/path) is unchanged.
- **Identifier sanitization + last-resort disambiguation** — **every path segment** (the type group,
  each folder segment, and the leaf name) is sanitized independently to a valid C# identifier via the
  **shipped** `CatalogEmit.SanitizeIdentifier` convention (spaces/punctuation/leading-digit/keyword
  handled); the leaf name has its extension dropped first. Namespacing is nested static classes per
  segment (`Assets` → `Materials` → `Environment` → `Rocks` → property `Stone`). Because the folder path
  is part of the member, the common cross-folder same-name case (`Stone.mat` in two folders) is **already
  distinct** and MUST NOT be suffixed. Only the RARE residual edges collide:
  - two **different** folder (or leaf) names that sanitize to the **same** identifier (e.g. `My Assets`
    vs `My-Assets`, both → `My_Assets`), or
  - two **sub-objects with the same name** inside one `.fbx`/texture (same container, same group).

  These survivors — and only these — are disambiguated **deterministically, byte-stably**, ordered by
  durable **GUID Ordinal** (then FileId) and suffixed by the shipped `IdentifierAllocator` (`Stone`,
  `Stone2`) as a **last resort**, explicitly noted as rare. Uniqueness is required only among the
  siblings within one nested class (one folder level of one group), exactly as façade ref-type
  uniqueness is required only within a per-prefab container (spec 25, "Cross-prefab type-name
  collisions"). Two assets that share both folder path and name inside one type group cannot occur — the
  filesystem forbids it — so no suffix is ever emitted for the ordinary cross-folder case.
- **Sub-assets (fbx meshes, sliced sprites)** — a mesh or sprite that lives **inside** an imported model
  or texture is catalogued the same way: `Assets.Meshes.Models.Props.Barrel` where `Barrel` is a
  sub-object of `Assets/Models/Props/Barrel.fbx`. The folder segments are the **container file's** folder
  path; the leaf name derives from the **sub-object** name, not the file. Its manifest entry carries the
  container **Path**, the folder **segments**, the **SubAsset** name, and the sub-object **FileId** — so
  the produced `AssetRef` is byte-identical to `Asset("Assets/Models/Props/Barrel.fbx", "Barrel")` (the
  spec-21 sub-asset form).
- **The Unity-facing manifest producer** (`AssetCatalogGenerator`, EditMode-tested) — scans
  `AssetDatabase` by type, writes `Assets.sbassets.json` via plain `File` IO (`SceneBuilderPaths.
  WriteTextIfChanged`): **no `AssetDatabase` write, no domain reload** (foundation §2, §12), deterministic
  ordering, compare-before-write/idempotent. Regenerated on asset import/move/delete/rename.
- **The source generator** — a new `AssetCatalogSourceGenerator` (`IIncrementalGenerator`) in the
  shipped `CodeScenes.Analyzers` package, **structurally identical** to `PrefabFacadeGenerator`: keys on
  the `Assets.sbassets.json` AdditionalFile, `AddSource`s the `Assets` types via a new `AssetCatalogEmit`
  (mirrors `FacadeEmit`). Empty/missing manifest emits **nothing** (fail-safe: a `Assets.X` reference then
  fails to compile with an ordinary CS error, never a wrong value). Byte-stable.
- **Injector wiring** — `BuilderProjectInjector` injects `Assets.sbassets.json` as a **third**
  `<AdditionalFiles>` item beside `ProjectCatalog.sbcatalog.json` and `Prefabs.sbfacade.json`
  (`BuilderProjectInjector.cs:214-231`); idempotent and reference-equality-preserving exactly as the
  existing two.
- **Parse + resolve** — reading `Assets.<Type>.<folders…>.<Name>` from source text resolves through the
  `AssetCatalog` to the **same** `AssetRef { DisplayPath, SubAsset }` the string `Asset(...)` form
  produces at parse time, so downstream lowering (`LoweringResolver.Resolve`) yields the same
  `(guid, fileId, typeHint)` and Build is untouched. An **unknown** member is a compile error (the
  generator never emitted it); a member **referenced but stale** against the catalog is a **located parse
  conflict** (`ConflictKind.UnknownFacadeReference`, reused), mirroring `Instance(Prefabs.Nope)`
  (`BuilderParser.Instance.cs:62`).
- **Emit typed by default** — Sync/Reconcile emit the typed `Assets.<Type>.<folders…>.<Name>` form **by
  default** when the referenced asset resolves to a catalog member (mirroring spec 25's `SourcePropertyName`
  emit preference, `ReconcilerInstances.cs:103`); fall back to `Asset(...)` / `Builtin(...)` when the asset
  is not catalogued (built-ins always fall back).
- **Rename/move auto-sync** — an `AssetReferencePatcher` pass (mirroring `FacadeReferencePatcher`)
  rewrites the typed reference in live builders when an asset's member chain changed but its durable
  `(Guid, FileId)` did not. A **rename** (name change) rewrites the **leaf** identifier; a **move**
  (folder change) rewrites the **folder segments** of the member chain in place (the folders under
  `Assets/` are part of the member now, so a move IS a source edit — unlike the former flat shape). Both
  are keyed on the durable `(Guid, FileId)`. A reference whose durable target vanished is left untouched
  (→ compile error, the safety net).
- **Deterministic / byte-stable generation** — identical asset set ⇒ byte-identical manifest and emitted
  catalog, on any machine, no timestamps/ordering nondeterminism. Reuses the toolkit's tested byte-stable
  emit + sanitization + allocator conventions.
- **Both `Asset(...)` and `Builtin(...)` string forms remain fully supported** (fallback). Existing
  builders keep working unchanged; both spellings lower to the identical `AssetRef`.

## Out of scope

- **Built-in resources** (`Builtin("Cube")`, primitives, `Default-Material`) — they have **no project
  GUID** (they live in Unity's built-in containers, `BuiltinCatalog`), so they are **not catalogued** and
  stay on `Builtin(...)`. A typed `BuiltinResources.*` catalog keyed on the built-in name/fileId is a
  possible separate follow-on, not here.
- **Asset type groups beyond the six** (Textures, AudioClips, Fonts, PhysicMaterials, …) — trivial
  additions on the same mechanism, specced/added later as demand appears.
- **Authoring / creating / editing assets.** The producer READS the asset database; it never creates or
  mutates assets. (Same boundary spec 25 drew for prefab structure.)
- **Typed refs to in-scene objects** — that is the LogicalId/handle model (§4, M5), a per-scene concern,
  unrelated to project-asset catalogs.
- **An analyzer that verifies "does this serialized field accept this asset TYPE"** — the compiler already
  does this: `Assets.Materials.Environment.Rocks.Stone` is an `AssetReference` and the `.Set` overload
  type-checks it; a
  material assigned to a mesh field surfaces as a resolve/Build error (spec 22's honest boundary),
  unchanged here. The spec-26 analyzer's `SB1101` already nudges the raw string `.Set("m_X", …)` form.
- **A `CodeFixProvider` auto-fix** turning string `Asset("path")` into `Assets.G.M` — noted as an
  analyzer follow-on below; not required here.
- **IMGUI / a settings panel / any toggle.** The producer + generator run automatically; no button
  (foundation "seamless, non-user-driven" contract).

## Core deliverables

`SceneBuilder.Core` is Unity-free; it holds the manifest POCO, the parse arm, and the patcher, and
resolves nothing Unity-specific. The `AssetCatalogEmit` module lives in the ns2.0 `CodeScenes.Analyzers`
package (it cannot reference Core — mirrors `FacadeEmit`/`CatalogEmit`, which re-declare the schema by
shape, not by shared type).

### Types added (referencing the shipped shapes)

```
AssetCatalog                          // deserialized shape of Assets.sbassets.json. The source GENERATOR
                                      //   consumes it (as an AdditionalFile) to emit `Assets` types INTO
                                      //   the compilation; the PARSER consumes it to resolve member chains;
                                      //   the PATCHER consumes it (old+new) to rewrite renamed segments.
                                      //   Unity-free. Mirrors FacadeCatalog.
  SchemaVersion : int
  Entries       : AssetCatalogEntry[] // ordered by (Group, FolderSegments, MemberName, Guid, FileId)
                                      //   Ordinal — byte-stable

AssetCatalogEntry
  Group          : string             // "Materials" | "Meshes" | "Sprites" | "Scenes" |
                                      //   "ScriptableObjects" | "AnimationClips" — the FIRST nested class
                                      //   (the asset type, always present)
  FolderSegments : string[]           // the SANITIZED folder segments UNDER Assets/ (the container file's
                                      //   folder path for a sub-asset) — the intermediate nested classes,
                                      //   in order. Empty for an asset directly under Assets/. Together
                                      //   with Group + MemberName this is the full member chain
                                      //   Assets.<Group>.<FolderSegments…>.<MemberName>.
  MemberName     : string             // sanitized C# identifier (leaf, extension dropped), UNIQUE among
                                      //   its SIBLINGS (same Group + same FolderSegments); a last-resort
                                      //   GUID-ordinal + IdentifierAllocator suffix only for the rare
                                      //   residual sanitize-collision / same-name sub-object case
  Guid           : string             // the asset's .meta GUID — the DURABLE key (patcher keys on this)
  FileId         : long               // 0 for a main asset; the sub-object fileId for an fbx mesh /
                                      //   sliced sprite (with Guid, the second half of the durable key)
  Path           : string             // the asset's CURRENT path — feeds AssetRef.DisplayPath (display;
                                      //   non-authoritative, re-derived every regen from the GUID). The
                                      //   FolderSegments are re-derived from this Path every regen too.
  SubAsset       : string             // "" for a main asset; the sub-object name for an fbx mesh /
                                      //   sliced sprite — feeds AssetRef.SubAsset
```

`AssetCatalog` exposes lookups mirroring `FacadeCatalog`: `TryGetEntry(group, folderSegments, member, out entry)`
(forward, for Parse), `TryGetMember(guid, fileId, out group, out folderSegments, out member)` (reverse,
for emit + patcher), `Serialize()` / `Deserialize(json)` via the existing `CanonicalJson`.

### Functions / behaviors (testable contracts — headless, no Unity)

1. **`AssetCatalog` generation is deterministic and byte-stable.** `AssetCatalogBuilder.Build(entries)`
   (fed unordered) produces a byte-identical `AssetCatalog` across runs and input orderings: entries
   ordered `(Group, FolderSegments, MemberName, Guid, FileId)` Ordinal, and any last-resort member
   identifiers allocated in **GUID Ordinal** (then FileId) order, no timestamps. (Same determinism bar as
   spec 25 §8 / spec 26 #17.)
2. **`Assets.<Type>.<folders…>.<Name>` parses to the same `AssetRef` the string form yields.** Given
   source with `c.Set(x => x.sharedMaterial, Assets.Materials.Environment.Rocks.Stone)`, the parser reads
   the member chain `Assets.Materials.Environment.Rocks.Stone`, looks
   `(Materials, [Environment, Rocks], Stone)` up in the `AssetCatalog`, and yields a
   `ValueNode.AssetRef(new AssetRef { DisplayPath = entry.Path, SubAsset = entry.SubAsset })` — **equal to**
   what `c.Set(x => x.sharedMaterial, Asset("Assets/Environment/Rocks/Stone.mat"))` yields (identity
   `(Guid, FileId, IsBuiltin)` matches post-lowering; `DisplayPath` matches at parse). A sub-asset member
   `Assets.Meshes.Models.Props.Barrel` yields
   `AssetRef { DisplayPath = "Assets/Models/Props/Barrel.fbx", SubAsset = "Barrel" }`, equal to
   `Asset("Assets/Models/Props/Barrel.fbx", "Barrel")`.
3. **An unknown / stale member is a located parse conflict, never a silent drop.**
   `Assets.Materials.Environment.Rocks.Nope` (or a member removed from the catalog) with the
   `AssetCatalog` present yields a `Conflict`
   `{ Kind = UnknownFacadeReference, Location = <the member-access span> }` (reused channel), mirroring
   `Instance(Prefabs.Nope)`. With NO catalog the chain is left to the fallthrough (it never resolves), so
   the generator-emitted-nothing case surfaces as a CS compile error.
4. **The folder path disambiguates cross-folder same-name — no suffix — and each path segment is
   sanitized.** Two `Stone.mat` assets in **different folders** (`Assets/Environment/Rocks/Stone.mat`,
   `Assets/Characters/Skin/Stone.mat`, distinct GUIDs) generate
   `Assets.Materials.Environment.Rocks.Stone` and `Assets.Materials.Characters.Skin.Stone` — **distinct
   members, NO numeric suffix** — each resolving to its **own** `(Guid, Path)`, never cross-resolving.
   Every segment is sanitized: a `"2Fast.mat"` → leaf `_2Fast`; a `"class.mat"` → `@class`; a folder
   `"Big Rocks"` → `BigRocks` or `Big_Rocks` per the shipped sanitizer — all compile. The last-resort
   suffix fires ONLY for a residual sanitize-collision among siblings: two sibling folder names that
   sanitize identically (`My Assets` vs `My-Assets` → both `My_Assets`) or two same-named sub-objects in
   one file are ordered by GUID Ordinal (then FileId) and suffixed (`…2`) — a **rare** case, never the
   ordinary cross-folder one.
5. **Type groups are independent namespaces.** A `Stone.mat` and a `Stone.mesh` in the **same folder**
   `Assets/Env/` do **not** collide — they are `Assets.Materials.Env.Stone` and `Assets.Meshes.Env.Stone`
   (the type group is the first, always-present segment). Likewise a `Stone.mat` and a `Stone.png` sprite
   in one folder are `Assets.Materials.Env.Stone` vs `Assets.Sprites.Env.Stone`, distinguished by the type
   group. The catalog builder allocates identifiers within each group's own tree.
6. **Reverse lookup drives emit-typed-by-default.** `AssetCatalog.TryGetMember(guid, fileId, …)` returns
   the `(Group, FolderSegments, Member)` for a catalogued asset — the full member chain to emit; a
   built-in `(guid, fileId)` (or an un-catalogued asset) returns false so emit falls back to
   `Builtin(...)` / `Asset(...)`.
7. **`AssetReferencePatcher.Patch(builderSource, oldCatalog, newCatalog)`** is a formatting-preserving
   Roslyn edit (mirrors `FacadeReferencePatcher`): for every `Assets.<Type>.<folders…>.<Name>` member
   chain whose durable `(Guid, FileId)` — looked up via `oldCatalog` — is unchanged but whose full member
   chain `(Group, FolderSegments, Member)` differs in `newCatalog`, it rewrites the changed identifier
   token(s) in place. This covers a **rename** (leaf token differs), a **move** (one or more folder-segment
   tokens differ, or segments are added/removed), or both at once — the patcher replaces the whole
   `Assets.…` member-access chain with the new chain, preserving surrounding trivia. Every other chain is
   byte-identical; a chain whose durable target no longer exists in `newCatalog` is **left untouched**
   (safety net). Byte-identical output when nothing renamed or moved.
8. **Stale typed reference fails to compile (the safety-net half).** After regeneration, a
   `Assets.Materials.Environment.Rocks.Stone` naming a removed/renamed/moved member that was NOT patched
   no longer exists on the regenerated `Assets.Materials.Environment.Rocks` static class (the leaf is gone,
   or the folder nested class no longer holds it), so the builder **fails the Roslyn compile check** with a
   located CS error at the stale accessor — proving the compiler catches what a stale string path would
   not. This dual behavior — auto-sync rewrite when sync runs, compile error when it does not — is the
   milestone's core value (mirrors spec 25 behaviors 7–8).
9. **A MOVE rewrites the member's folder segments (auto-synced) and stays correct.** An asset moved to
   another folder keeps its GUID and `MemberName` but its **folder segments change**, so its member chain
   changes (`Assets.Materials.Environment.Rocks.Stone` → `Assets.Materials.Foundations.Stone`). Because the
   durable `(Guid, FileId)` is unchanged, `AssetReferencePatcher` **rewrites the folder segments of the
   chain in place** in every live builder, and the re-parsed source resolves to the **new** path. This is a
   real change from the former flat shape (where a move needed no edit): the folders are part of the member
   now, so a move IS a source edit — still auto-synced, still keyed on the durable `(Guid, FileId)`, and a
   move the LLM hand-writes with a stale folder path still **fails to compile** (the stale nested class does
   not exist). It remains the concrete win over a string `Asset("old/path")`, which would go silently stale
   on a move; here it either auto-rewrites or fails the compile check, never silently mis-resolves.
10. **`AssetCatalogEmit` (ns2.0) emits compiling, byte-stable source from a manifest.** The `Assets`
    static class with a per-**type-group** nested class, then a nested class per **folder segment** (built
    from each entry's `FolderSegments`, merged into a shared tree so sibling assets share folder classes),
    and one `AssetReference`-typed property per entry at its leaf; empty/missing manifest emits nothing
    (fail-safe); identical manifest ⇒ byte-identical source. Folder classes are emitted in Ordinal order
    so the tree is deterministic.

### The value-parse threading seam (a real, specified change)

`ValueNodeParser.Parse(ExpressionSyntax expr)` is today a catalog-free static (`ValueNodeParser.cs:20`),
and its generic `MemberAccessExpressionSyntax → ValueNode.Enum` arm (`ValueNodeParser.cs:52`) would
otherwise **misparse** `Assets.Materials.Environment.Rocks.Stone` as an enum. This milestone:

- Threads an **`AssetCatalog?`** into value parsing, the same way `FacadeCatalog?` is threaded through
  `BuilderParser.Parse(source, existingMap, facadeCatalog)` (`BuilderParser.cs:28`) and `ParserContext`
  (`BuilderParser.cs:759`). `BuilderParser.Parse` gains an `AssetCatalog?` parameter; it flows to the
  `ValueNodeParser` calls (`BuilderParser.cs`, `BuilderParser.Instance.cs`, `BuilderParser.Spatial.cs`).
- Adds a **new arm BEFORE** the generic enum arm: a `MemberAccessExpressionSyntax` whose leftmost
  identifier is the reserved root `Assets` and whose full chain — the type group, the intervening folder
  segments, and the leaf, of any depth — resolves in the `AssetCatalog` produces a `ValueNode.AssetRef`;
  if the chain does **not** resolve (and the catalog is present) it is the located conflict of behavior 3.
  The arm walks the whole `Assets.<Type>.<folders…>.<Name>` chain (splitting group / folder segments /
  leaf) and keys on catalog resolution, not on the bare identifier `Assets` (an `Assets`-rooted chain that
  resolves is an asset ref; anything else falls through to the existing enum/identifier arms), so a
  user-defined `enum Assets` is not hijacked when it does not resolve.

## Editor adapter deliverables

All in `com.codescenes/Editor/`. Thin Unity-side pieces; real behavior covered by EditMode tests.

- **`AssetCatalogGenerator` (new, `[InitializeOnLoad] : AssetPostprocessor`)** — mirrors
  `PrefabFacadeManifestGenerator` / `ProjectCatalogGenerator`. Scans the project by type and builds the
  `AssetCatalogEntry[]`. For **every** entry it derives the `FolderSegments` from the asset's path by
  stripping the leading `Assets/` and the file name, then sanitizing each remaining folder segment
  (`AssetCatalogBuilder.Build` does the sanitization + last-resort suffixing; the producer supplies raw
  path parts). A sub-asset uses its **container file's** folder path.
  - **Materials** — `AssetDatabase.FindAssets("t:Material")` → each `.mat` main asset (Path, GUID,
    FileId=0, SubAsset="", FolderSegments from the path).
  - **Meshes** — standalone `.mesh` assets **plus** sub-mesh objects inside imported models: for each
    model asset, `AssetDatabase.LoadAllAssetsAtPath` and take each `Mesh` sub-object (Path=model path,
    SubAsset=mesh name, FileId via `AssetDatabase.TryGetGUIDAndLocalFileIdentifier`), exactly the
    `AssetReferenceResolver.TryResolveSubObject` identity path.
  - **Sprites** — standalone sprites plus sliced-sprite sub-objects inside textures (same sub-object
    pattern).
  - **Scenes** — `t:SceneAsset` (Path, GUID, FileId=0).
  - **ScriptableObjects** — `t:ScriptableObject` main assets.
  - **AnimationClips** — `t:AnimationClip` main assets plus clip sub-objects inside models.
  - Builds the `AssetCatalog` via `AssetCatalogBuilder.Build`, serializes with `CanonicalJson`, writes
    `SceneBuilderPaths` `Assets.sbassets.json` (a new `AssetManifestPath` const beside `FacadeManifestPath`,
    `SceneBuilderPaths.cs:54`) with `WriteTextIfChanged` (compare-before-write, byte-stable no-ops).
    **Never `AssetDatabase`, so no domain reload.** No `.cs` is written — the source generator emits the
    types.
- **Regen triggers** — `OnPostprocessAllAssets` regenerates when an asset of a catalogued type is
  imported / moved / renamed / deleted (a rename or a re-slice shows up as a re-import). On editor load,
  a deferred `EditorApplication.delayCall` primes it once (mirrors the shipped producers). Idempotent:
  identical asset set ⇒ identical bytes ⇒ no rewrite (so the file watcher / IDE is not churned).
- **Reference-patch on rename OR move** — after a regen that changed bytes, read the prior on-disk
  manifest as the OLD catalog, call `AssetReferencePatcher.Patch` over every builder under
  `SceneBuilders/`, and `SceneBuilderPaths.WriteIfChanged` the result (routing through `WriteIfChanged`,
  not `WriteTextIfChanged`, so the code→scene watcher records a self-echo suppression — exactly as
  `PrefabFacadeManifestGenerator.PatchBuildersForRename` does, `PrefabFacadeManifestGenerator.cs:176`). A
  **move** now changes the member's folder segments, so — unlike the former flat shape — the patch pass
  edits builders on a move too, not only on a rename; both are keyed on the durable `(Guid, FileId)`.
- **Injector** — `BuilderProjectInjector.Inject` appends a **third** `AppendItemIfAbsent(..,
  "AdditionalFiles", RelativePath(projectDirectory, assetManifestPath))` beside the two shipped
  `<AdditionalFiles>` items (`BuilderProjectInjector.cs:219-231`), guarded by the same `analyzersDirectory
  != null` / donor-project condition; idempotent, reference-equality-preserving.
- **`AssetCatalogLoader` (new)** — mirrors `FacadeCatalogLoader`: loads + caches the deserialized
  `AssetCatalog` from `Assets.sbassets.json` for Build/Sync to thread into `BuilderParser.Parse` and
  Reconcile emit. Absent/unparseable ⇒ null (string forms unaffected).

## Authoring API added

This milestone adds **no new builder call**. It adds the generated `Assets` catalog and makes the existing
`.Set` asset-field forms compiler-checked. `Assets.<Type>.<folders…>.<Name>` is a get-only static property
typed `SceneBuilder.Authoring.AssetReference` — the same type `Asset(...)` / `Builtin(...)` return
(`com.codescenes/Runtime/AssetReference.cs`) — so it drops in unchanged.

```csharp
// After this milestone, in a builder under <ProjectRoot>/SceneBuilders/Arena.cs:
public class Arena : ISceneDefinition {
    public void Build(SceneRoot scene) {
        scene.Add("Wall").Component<MeshRenderer>(r =>
            // typed: type-first + folder-path, compiler-checked & autocompletes. Two Stone materials in
            // different folders are distinct members with NO numeric suffix.
            r.Set(x => x.sharedMaterials, new[] {
                Assets.Materials.Environment.Rocks.Stone,
                Assets.Materials.Characters.Skin.Stone }));

        scene.Add("Barrel").Component<MeshFilter>(f =>
            // sub-asset mesh inside Models/Props/Barrel.fbx — resolves to
            // Asset("…/Models/Props/Barrel.fbx","Barrel")'s AssetRef.
            f.Set(x => x.sharedMesh, Assets.Meshes.Models.Props.Barrel));

        // Fallback (still supported): the string spelling lowers to the SAME AssetRef.
        scene.Add("Floor").Component<MeshRenderer>(r =>
            r.Set(x => x.sharedMaterial, Asset("Assets/Materials/Concrete.mat")));

        // Built-ins are NOT catalogued (no project GUID) — stay on Builtin(...).
        scene.Add("Cube").Component<MeshFilter>(f => f.Set(x => x.sharedMesh, Builtin("Cube")));
    }
}
//  Assets.Materials.Environment.Rocks.Stoen  → does not compile (leaf member does not exist)
//  Assets.Materials.Enviroment.Rocks.Stone   → does not compile (stale/typo folder nested class)
```

- `Assets`, `Assets.Materials`, `Assets.Materials.Environment`, `Assets.Materials.Environment.Rocks`, … are
  generated static classes; `Assets.Materials.Environment.Rocks.Stone` is a get-only `AssetReference`
  property with no runtime body that runs. The parser reads the full chain from the text and maps it, via
  the manifest, to `(Path, SubAsset)` → the same GUID/FileId the string form lowers to.
- A typo in the leaf (`…Rocks.Stoen`) or a stale folder segment (after a move that was not synced) does
  not exist as a member → **compile error** at the accessor. A stale reference after a rename that was not
  synced → **compile error** likewise.

## IdentityMap / sidecar changes

**None.** The per-scene `*.sbmap.json` (foundation §4) is unchanged in shape; a serialized asset field
still records the same `AssetRef` (`Guid`/`FileId`), and the `Assets[]` move-recovery cache (§4) is
populated exactly as before by the `LoweringResolver.Harvested` path — the typed member lowers through
the same resolver. `Assets.sbassets.json` is a **separate** generated project-state manifest under
`SceneBuilders/Generated/` (alongside `ProjectCatalog.sbcatalog.json` and `Prefabs.sbfacade.json`), never
merged into or read from any sbmap; it is regenerated (never hand-edited), deterministic, byte-stable, and
is the parser's / emit's / patcher's source of truth for
`(Group, FolderSegments, Member) ↔ (Guid, FileId, Path, SubAsset)`.

## Core test plan (RED behaviors — headless, no Unity)

New tests in `SceneBuilder.Core.Tests` (parse + patcher + catalog) and `SceneBuilder.Analyzers.Tests`
(the source generator, via the shipped in-memory `AdditionalText` harness), style
`Subject_Condition_ExpectedOutcome`. Maps 1:1 to the Core behaviors above.

1. `AssetCatalog_SameEntries_ByteIdentical` — `AssetCatalogBuilder.Build` on shuffled input →
   byte-identical `AssetCatalog`; entries `(Group, FolderSegments, MemberName, Guid, FileId)` ordered; any
   last-resort members allocated in GUID Ordinal (then FileId) order; no timestamps.
2. `TypedAssetRef_ParsesToSameAssetRefAsString` — `Assets.Materials.Environment.Rocks.Stone` and
   `Asset("Assets/Environment/Rocks/Stone.mat")` parse to equal `ValueNode.AssetRef` (same DisplayPath at
   parse; equal `AssetRef` identity post-lowering with a stub resolver). Sub-asset variant:
   `Assets.Meshes.Models.Props.Barrel` == `Asset("…/Models/Props/Barrel.fbx","Barrel")`.
3. `UnknownTypedAssetRef_YieldsLocatedConflict` — `Assets.Materials.Environment.Rocks.Nope` with the
   catalog present → one `Conflict { Kind = UnknownFacadeReference, Location = <member-access span> }`, no
   silent drop.
4. `CrossFolderSameName_DistinctMembers_NoSuffix` — two `Stone.mat` (distinct GUIDs) in
   `Assets/Environment/Rocks/` and `Assets/Characters/Skin/` → `Assets.Materials.Environment.Rocks.Stone`
   and `Assets.Materials.Characters.Skin.Stone`, **no numeric suffix**, each resolving to its own Path; a
   residual sanitize-collision (`My Assets` vs `My-Assets` folders, or two same-named sub-objects) →
   deterministic GUID-ordered `…2` suffix; keyword/leading-digit/space segment cases compile.
5. `TypeGroups_AreIndependentNamespaces` — `Stone.mat` + `Stone.mesh` in one folder `Assets/Env/` →
   `Assets.Materials.Env.Stone` + `Assets.Meshes.Env.Stone`; `Stone.mat` + `Stone.png` in one folder →
   `Assets.Materials.Env.Stone` + `Assets.Sprites.Env.Stone`, no collision (type group is the first
   segment).
6. `ReverseLookup_CataloguedHit_BuiltinMiss` — `TryGetMember` returns `(Group, FolderSegments, Member)` for
   a catalogued asset; false for a built-in `(guid, fileId)`.
7. `AssetPatcher_RewritesRenamedAndMovedMember_KeepsUnchanged_LeavesVanished` — a rename `Stone→Granite`
   (same `Guid, FileId`) rewrites the leaf `…Rocks.Stone → …Rocks.Granite`; a move (same `Guid, FileId`,
   folder changed) rewrites the folder segments `Assets.Materials.Environment.Rocks.Stone →
   Assets.Materials.Foundations.Stone`; both formatting-preservingly; every other chain byte-identical; a
   member whose durable target vanished left untouched.
8. `StaleTypedRef_AfterRegen_FailsCompileCheck` — a builder referencing a removed/moved member, compiled
   against the regenerated `Assets` catalog via the Roslyn compile assertion, reports a located CS error at
   the stale accessor (leaf or folder nested class gone).
9. `MovedAsset_RewritesFolderSegments_NewPathResolved` — a move (GUID stable, path changed) produces a new
   catalog with the same `MemberName` but new `FolderSegments` + `Path`; `AssetReferencePatcher` **rewrites
   the folder segments** of the chain in the source, and re-parsing the patched source against the new
   catalog yields an `AssetRef` with the **new** `DisplayPath`. (A move now IS a source edit.)
10. `EmitTyped_ByDefault_FallsBackWhenUncatalogued` — Reconcile emit, given the catalog, emits
    `Assets.Materials.Environment.Rocks.Stone` for a catalogued material ref and `Builtin("Cube")` /
    `Asset("path")` for an un-catalogued / built-in ref.
11. `Generator_AssetsManifest_EmitsPropertyPerEntry` (analyzers project) — a manifest with
    `Materials:[Environment/Rocks/Stone, Environment/Rocks/Rust]`, `Meshes:[Models/Props/Barrel]` →
    generated `Assets.Materials.Environment.Rocks.Stone`, `Assets.Materials.Environment.Rocks.Rust`,
    `Assets.Meshes.Models.Props.Barrel` (shared folder nested classes merged), each `AssetReference`-typed;
    a builder referencing them compiles.
12. `Generator_Deterministic_ByteStable` / `Generator_MissingManifest_EmitsNothing_ReferenceFailsToCompile`
    — identical manifest ⇒ byte-identical source; no manifest ⇒ no `Assets` type ⇒ ordinary CS error at
    the reference (fail-safe).
13. `EmittedTypedAssetRef_IsAnalyzerClean` — the typed form the tool emits is analyzer-clean (spec 26's
    strengthened invariant: the tool never emits source it would itself reject).

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/Assets/GateTests/`)

Per CLAUDE.md, the Unity-facing pieces need EditMode coverage against a live editor project. New file
`AssetCatalogTests.cs` (+ a small asset fixture proving the collision cases:
`Assets/Environment/Rocks/Stone.mat` and a **second** `Assets/Characters/Skin/Stone.mat` — same name,
different folders; `Assets/Environment/Rocks/Stone.png` — a sprite sharing the name AND folder of the
first material, different type; `Assets/Models/Props/Barrel.fbx` with a `Barrel` sub-mesh;
`Assets/Scenes/MainMenu.unity`; `Assets/Config/EnemyConfig.asset` ScriptableObject),
style `Direction_Scenario_Expectation`. Each item is a concrete, assertable behavior.

1. **Manifest reflects real project assets, type-first + folder-path.** With the fixture assets present,
   run `AssetCatalogGenerator.Regenerate`. **Expected:** `<ProjectRoot>/SceneBuilders/Generated/
   Assets.sbassets.json` exists and contains a `Materials` entry with `FolderSegments=[Environment,Rocks]`,
   `MemberName=Stone` and a second with `FolderSegments=[Characters,Skin]`, `MemberName=Stone` (both leaf
   `Stone`, **NO numeric suffix** — the folder path disambiguates); a `Sprites` entry
   `FolderSegments=[Environment,Rocks]`, `MemberName=Stone`; a `Meshes` entry
   `FolderSegments=[Models,Props]`, `MemberName=Barrel` (with `SubAsset="Barrel"` and a non-zero sub-object
   `FileId`); a `Scenes` entry `FolderSegments=[Scenes]`, `MemberName=MainMenu`; a `ScriptableObjects`
   entry `FolderSegments=[Config]`, `MemberName=EnemyConfig`. A second run with no change rewrites
   **nothing** (byte-identical); Unity performed **no domain reload** for the write.
2. **Injector adds the asset manifest to the donor csproj.** Run `BuilderProjectInjector.Inject` over a
   fixture `Assembly-CSharp.csproj` with a builder present. **Expected:** the result contains an
   `<AdditionalFiles Include="…Assets.sbassets.json" />` item beside the project-catalog and prefab-façade
   `<AdditionalFiles>` items; a non-donor project is unchanged; re-injecting is idempotent (same instance).
3. **Cross-folder same-name resolves to DISTINCT assets (no suffix).** Author
   `r.Set(x => x.sharedMaterial, Assets.Materials.Environment.Rocks.Stone)` on one renderer and
   `Assets.Materials.Characters.Skin.Stone` on another, and Build. **Expected:** the first renderer's
   `sharedMaterial` is the **Environment/Rocks** Stone asset (the GUID `Asset("Assets/Environment/Rocks/
   Stone.mat")` resolves) and the second is the **Characters/Skin** Stone asset (its own distinct GUID) —
   two different materials, no `Stone2` member anywhere; each matches the equivalent string-form Build
   (equal serialized field).
4. **Same-folder same-name different-type is distinguished by the type group.** In the one folder
   `Assets/Environment/Rocks/`, author `r.Set(x => x.sharedMaterial, Assets.Materials.Environment.Rocks.Stone)`
   and `r.Set(x => x.sprite?, Assets.Sprites.Environment.Rocks.Stone)` (a sprite field). **Expected:** the
   material field gets the **`Stone.mat`** GUID and the sprite field gets the **`Stone.png`** sprite GUID —
   the two `Stone` members coexist under different type groups (`Assets.Materials.…` vs `Assets.Sprites.…`)
   and resolve to their own assets; neither collides nor cross-resolves.
5. **Sub-asset mesh Builds correctly.** `f.Set(x => x.sharedMesh, Assets.Meshes.Models.Props.Barrel)`
   Builds the `MeshFilter.sharedMesh` to the **Barrel** sub-mesh inside `Models/Props/Barrel.fbx` (the
   sub-object FileId, not the main asset) — equal to `Asset("Assets/Models/Props/Barrel.fbx","Barrel")`.
6. **Rename auto-sync (leaf rewrite).** Rename `Environment/Rocks/Stone.mat → Environment/Rocks/Granite.mat`.
   **Expected:** the catalog regenerates (`Assets.Materials.Environment.Rocks.Granite`), AND
   `AssetReferencePatcher` rewrites the live builder's `Assets.Materials.Environment.Rocks.Stone` →
   `Assets.Materials.Environment.Rocks.Granite` (only the leaf token changed); the builder still compiles
   (`BuilderCompileCheck` reports no errors) and re-Builds to the same material (GUID unchanged).
7. **Move auto-sync rewrites the member's folder segments.** Move `Environment/Rocks/Granite.mat` to
   `Assets/Foundations/Granite.mat`. **Expected:** the catalog regenerates (`FolderSegments=[Foundations]`),
   AND `AssetReferencePatcher` **rewrites the live builder's folder segments in place**:
   `Assets.Materials.Environment.Rocks.Granite` → `Assets.Materials.Foundations.Granite` (assert the source
   **was patched**, NOT left unchanged — a move IS a source edit now); the builder still compiles and
   re-Builds to the same material (GUID unchanged). This is the concrete win over a string path, delivered
   by an auto-rewrite rather than a no-op.
8. **Safety net.** Hand-edit the builder to reference `Assets.Materials.Environment.Rocks.Stone` again
   (stale — the asset is now at `Foundations/Granite`) WITHOUT syncing, then run the compile check.
   **Expected:** a **located CS error** at the accessor (neither the `Rocks` folder class holds `Stone`
   any longer nor does the leaf exist) — caught at compile time, not silently mis-resolved.
9. **Emit typed by default.** Edit the Stone material assignment in the SCENE (scene→code sync) on a
   renderer whose material is catalogued. **Expected:** the emitted `.Set` uses the full
   `Assets.<Type>.<folders…>.<Member>` chain (e.g. `Assets.Materials.Environment.Rocks.Stone`), not
   `Asset("path")`; a scene edit assigning a built-in mesh emits `Builtin(...)` (fallback).
10. **New asset falls back until regen.** Reference a material added AFTER the last regen. **Expected:**
    before regen the typed member does not exist (compile error / author uses `Asset("path")` fallback,
    which Builds); after the import-triggered regen the member exists and compiles — the freshness
    fail-safe.

## Gate integration (`./verify.sh`)

- The Core behaviors (parse, patcher, catalog builder) and the generator behaviors run **headless in
  Layer 1** — ordinary `dotnet test` projects already in `SceneBuilder.sln` (`SceneBuilder.Core.Tests`,
  `SceneBuilder.Analyzers.Tests`), so `dotnet test SceneBuilder.sln` picks them up; no `verify.sh`
  structural change.
- The generator's `Assets` output is **compile-asserted** (it must emit compiling C#, tests #11–#12), and
  the emitted typed form is **analyzer-clean** (#13) — the strengthened "generated C# must compile AND is
  analyzer-clean" invariant (spec 26). A write/codegen path that could emit non-compiling source is a bug.
- The manifest producer + injector + typed round-trip + rename/move auto-sync run in **Layer 2** (they
  touch `com.codescenes/`), gated on the EditMode `results.xml` per the checklist. The verdict line
  (`GATE PASS` / `GATE FAIL`) remains the only reliable check.

## Analyzer tie-in (follow-on, not required here)

The shipped spec-26 analyzer already nudges the raw string serialized-path form (`SB1101`,
`.Set("m_X", …) → .Set(x => x.member, …)`). A natural **follow-on** (not this milestone) is a new nudge
(e.g. `SB1107`, Info) on the string `Asset("Assets/…")` / `.Set(x => x.field, Asset("path"))` form
suggesting the typed `Assets.<Type>.<folders…>.<Name>` — pure syntactic call-shape match, mirroring `SB1102`
(`Instance("path") → Instance(Prefabs.X)`). It is deliberately deferred: the compile-error-on-typo value
already lands from the generated type; the nudge is IDE polish.

## Dependencies

- **Spec 26 — CodeScenes.Analyzers toolkit (SHIPPED). HARD dependency.** This milestone is a **third
  source generator on that toolkit's framework**. It reuses verbatim: **(a)** the source-generator shape —
  `AssetCatalogSourceGenerator` is an `IIncrementalGenerator` in `CodeScenes.Analyzers` following the
  `AdditionalTextsProvider` → `RegisterSourceOutput` → `AddSource` pattern (`CatalogSourceGenerator.cs`,
  `PrefabFacadeGenerator.cs`); **(b)** `AdditionalFiles` injection — `BuilderProjectInjector` already
  injects two `<AdditionalFiles>` items; this adds a third (`BuilderProjectInjector.cs:214`); **(c)** the
  byte-stable emit / `SanitizeIdentifier` / `IdentifierAllocator` de-dup / MiniJson-parse conventions
  (`CatalogEmit.cs`) and the empty/missing-manifest fail-safe; **(d)** the editor manifest-producer
  pattern — `ProjectCatalogGenerator` / `PrefabFacadeManifestGenerator` (plain `File` IO to
  `SceneBuilders/Generated/`, compare-before-write, no `AssetDatabase`, no domain reload) is the template.
- **Spec 25 — Typed prefab façades (SHIPPED). HARD dependency (pattern).** `FacadeCatalog`,
  `FacadeCatalogBuilder`, `FacadeReferencePatcher`, the `Prefabs.X` parse arm
  (`BuilderParser.Instance.cs`), the `UnknownFacadeReference` conflict channel, the `SourcePropertyName`
  emit-typed-by-default preference (`ReconcilerInstances.cs`), and `FacadeCatalogLoader` are the direct
  templates the asset equivalents mirror.
- **M4 / spec 17 / spec 21 — the asset-reference surface (SHIPPED).** `AssetRefs.Asset(path[, sub])` /
  `Builtin(name[, hint])` (`AssetReference.cs`), `ValueNode.AssetRef` + `AssetRef`
  (`Model/AssetRef.cs`, identity `(Guid, FileId, IsBuiltin)`), `AssetReferenceResolver.LoweringResolver`
  (path[+sub] → `(guid, fileId, typeHint)`, `AssetReferenceResolver.cs`), `SetAssetRef` / `WriteAssetRef`,
  and the §4 `Assets[]` move-recovery cache — all reused verbatim; the typed member lowers through them
  unchanged. The string forms remain supported (fallback).
- **`ValueNodeParser` (`SceneBuilder.Core/Parsing/ValueNodeParser.cs`)** — the current home of asset-value
  parsing (`ParseAsset`/`ParseBuiltin`, the `MemberAccess → Enum` fallthrough), extended with the reserved
  `Assets`-root arm and threaded with the `AssetCatalog`.
- **The Roslyn compile check** used by the gate — the enforcer of behavior 8 (stale ⇒ compile error).

## Risks / notes

- **The value-parse `Assets`-root disambiguation is the single biggest structural risk.** `ValueNodeParser`
  currently maps any `A.B.C` member access to `ValueNode.Enum` (`ValueNodeParser.cs:52`). The new arm MUST
  run first for an `Assets`-rooted chain that **resolves in the catalog**, and MUST fall through
  otherwise, so a user's `enum Assets` (or any unrelated `Assets.`-prefixed access) is not hijacked. The
  arm now walks a chain of **arbitrary depth** (the folder path can be many segments), splitting it into
  group / folder segments / leaf, and keys on catalog resolution, not on the bare identifier `Assets` or a
  fixed depth. This must be pinned by a test that a non-resolving `Assets.Foo.Bar` still parses as before
  (and one that a real deep `Assets.Materials.Environment.Rocks.Stone` resolves) — a false hijack would
  break unrelated code, the exact false-positive class spec 26 forbids.
- **Large projects → a huge, deeply-nested generated catalog.** A project with thousands of
  materials/meshes/sprites across a deep folder tree produces a large `Assets` static class with many
  nested folder classes. Mitigations, all cheap: the type-first split then per-folder nesting keeps each
  individual class **small** (a folder class holds only its own assets + immediate sub-folders), so
  autocomplete stays narrow at every level and the deep tree does not bloat any single class; the
  `IIncrementalGenerator` re-emits only when the manifest changes; the manifest producer scopes a regen to
  the touched asset type where a postprocess batch allows (a material import need not re-scan meshes). The
  deeper member chains are longer to type but self-documenting and IntelliSense-completed segment by
  segment. If catalog size becomes a real IDE-perf problem, a future refinement is a `partial`-class split
  per group or a referenced-only catalog — noted, not v0. Do not regress into a full-project rescan per
  keystroke (the CLAUDE.md sync-performance constraint).
- **Manifest freshness fails SAFE (the spec-26 asymmetry).** An asset **added** after the last regen has
  no member yet → the typed reference does not compile (safe: the author regenerates, or uses the string
  `Asset("path")` fallback, which still Builds). An asset **removed** → its member's stale accessor fails
  to compile at the reference site (safe: the author is told). An asset **moved** changes its member's
  folder segments, so the patcher rewrites live builders and the regenerated catalog makes any
  un-rewritten stale folder path fail to compile — still safe, never silent. Neither add/remove/move is a
  silent wrong value. Regen is fast (File IO outside `Assets/`, no domain reload) and triggered on asset
  import/move/rename/delete. This safe-failure property is why the **generator** may use the manifest.
- **Built-ins have no project GUID and are deliberately un-catalogued.** `Builtin("Cube")` stays a string
  (the built-in name is resolved through `BuiltinCatalog`, not the AssetDatabase). Emit falls back to
  `Builtin(...)` for any built-in `(guid, fileId)`. A future `BuiltinResources.*` catalog is a separate
  follow-on.
- **Sub-asset member shape.** An fbx mesh / sliced sprite is catalogued under its **sub-object** name at
  the **container file's** folder path (`Assets.Meshes.Models.Props.Barrel`), with the manifest carrying
  `Path` (the container) + `FolderSegments` (the container's folders) + `SubAsset` + the sub-object
  `FileId`. The durable key is `(Guid, FileId)` — a re-slice/reorder that changes a sprite's fileId is a
  new durable target (the patcher treats it as add/remove), matching the sub-object identity Unity itself
  assigns. Two sub-objects of the **same file** sharing a name are the one true within-group residual
  collision (same folder, same leaf) and are disambiguated by the GUID-then-fileId-ordered last-resort
  allocator.
- **The folder path is the primary disambiguator; a numeric suffix is a last resort only.** The ordinary
  cross-folder same-name case (`Stone.mat` in two folders) is distinguished by the folder segments, NOT by
  a suffix — replacing the old flat `Stone` / `Stone2` scheme, which an LLM could not tell apart and the
  compiler accepted either way (silent wrong-asset). A suffix fires ONLY for a residual sanitize-collision
  among siblings (two folder/leaf names sanitizing to the same identifier, or two same-named sub-objects in
  one file). When it does fire it keys on the **durable GUID (then FileId), never a scan-order index** — a
  folder-order or find-result-order key would be non-deterministic across machines and reorder-fragile;
  GUID Ordinal is stable, so the rare `…2` keeps its meaning across regens (mirrors spec 25's `LocalId`-keyed
  disambiguator rationale).
- **Rename AND move safety is dual, and both halves matter** (inherited from spec 25). `AssetReferencePatcher`
  rewrites the accessor when sync runs; the regenerated `Assets` type makes a non-rewritten stale accessor
  fail to compile. The patcher is conservative: rewrite ONLY when the durable `(Guid, FileId)` is
  unchanged; never guess a new target for a vanished member (leave it to the compile error).
- **A move now edits builder source — a real change from the former flat shape.** Because the folder path
  is part of the member chain, moving an asset to another folder changes its member and the patcher must
  rewrite the folder segments in place (behavior 9 / checklist 7); the former flat shape needed no edit on
  a move. This is a deliberate trade: the member now self-documents the asset's location and the compiler
  catches a stale hand-written folder path, at the cost of one more auto-patch case. It is still keyed on
  the durable `(Guid, FileId)`, still fails safe (a non-rewritten stale path is a compile error), and adds
  no new user-visible step — the move-triggered regen already runs the patch pass.
- **The manifest is generated truth, not user-editable.** A hand-edit to `Assets.sbassets.json` is
  clobbered on the next regen (like the injected csproj and the façade manifest). Disposable,
  deterministic output.
- **VSCode caveat (inherited).** `OnGeneratedCSProject` (which drives `BuilderProjectInjector`) is called
  only by the Rider/VS packages, not VSCode — the same known limitation the relocated builder and the
  façade catalog already carry, not a new one.
</content>
</invoke>
