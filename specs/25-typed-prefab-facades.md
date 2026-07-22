# M-Facades — Typed prefab façades (compiler-checked prefab references & internal hierarchy)

> **Why this milestone exists.** CodeScenes' core principle is that every authoring surface is optimized
> so that (1) an LLM can write and understand it easily, and (2) if the LLM misunderstands, the
> **compiler** catches the mistake and guides the fix. Today two of the most-used authoring surfaces
> violate that principle by being **magic strings**: a prefab reference
> (`scene.Instance("Assets/Prefabs/Tank.prefab")`) and any prefab-internal child path
> (`.On("Turret/Barrel", …)`, spec 24). A typo, or a path gone stale after a Unity rename, sails past
> the C# compiler and surfaces only at Build/runtime as a located error — after the LLM already
> committed the wrong text. This milestone makes both **compiler-checked** by generating typed
> accessors from each prefab's real hierarchy, and it is a **hard prerequisite** for M-nested-props
> (`specs/24-nested-prefab-overrides.md`), whose nested-target addressing switches from string paths to
> these typed accessors.
>
> **The mechanism is the trick the whole product already runs on.** CodeScenes never RUNS the builder —
> it parses the source **text** with Roslyn, and the authoring API is compile-time scaffolding. The
> typed setter `.Set((Light l) => l.intensity, v)` works because the parser reads the member chain
> `l.intensity` out of the text (`BuilderParser.cs:461-463`, `SimpleLambdaExpressionSyntax { Body:
> MemberAccessExpressionSyntax }`); the lambda exists only so the compiler type-checks it. Typed prefab
> accessors are **more of that same scaffolding**, generated from the prefab: `Prefabs.Tank` and
> `t.Turret.Barrel` are member chains the parser reads exactly the same way, and the generated types
> carry **no runtime behavior** — they exist only so the code compiles and IntelliSense autocompletes.

---

## Additions to the contract

This milestone introduces Core types not present in foundation §3/§4, and one new generated artifact
(the façade manifest). Each is flagged here per §1. No existing POCO changes shape; `AssetRef`,
`PrefabInstanceNode`, `PrefabInstanceKey`, and the §4 sidecar are reused verbatim.

```
PrefabHierarchy                       // Unity-free POCO the ADAPTER fills (reads the prefab asset) and
                                      //   the generator consumes. One per project prefab.
  Guid          : string             // the prefab asset's .meta GUID (§3 AssetRef.Guid — AUTHORITATIVE)
  AssetPath     : string             // re-derivable display path; NON-authoritative
  Root          : PrefabHierarchyNode

PrefabHierarchyNode                   // one per GameObject inside the prefab
  RealName      : string             // the live object's real name (e.g. "Front Wheel") — sanitize source
  LocalId       : long               // the object's DURABLE prefab-internal fileID (GlobalObjectId
                                      //   .targetObjectId within the asset) — reorder/rename STABLE.
                                      //   The disambiguator + selector→pair-key resolution key on THIS.
  NestedPrefabGuid : string?         // non-null when this node is itself a nested-prefab instance;
                                      //   the generator COMPOSES that prefab's façade type here (below)
  Children      : PrefabHierarchyNode[]  // ordered by the prefab asset's child order

FacadeCatalog                         // in-memory shape of the generated manifest (Prefabs.sbfacade.json).
                                      //   The PARSER consumes it to resolve member chains; Unity-free.
  Entries       : FacadeEntry[]      // one per project prefab; ordered by (PropertyName, Guid) Ordinal

FacadeEntry
  PropertyName  : string             // the C# identifier under `Prefabs.` (e.g. "Tank"); sanitized,
                                      //   unique across the whole project (disambiguated by Guid ordinal)
  Guid          : string             // → the prefab GUID (what `Prefabs.<PropertyName>` resolves to)
  Root          : FacadeNode

FacadeNode
  TypeName      : string             // generated ref-type name (e.g. "TankRef"); unique within the
                                      //   prefab's container (cross-prefab uniqueness via the container)
  Children      : FacadeChild[]      // ordered by child LocalId (durable, reorder-stable)

FacadeChild
  PropertyName  : string             // the C# identifier on the parent façade (e.g. "Barrel"); sanitized,
                                      //   unique AMONG SIBLINGS (duplicate-sibling disambiguated, below)
  RealName      : string             // the child object's REAL name — the map back from identifier to
                                      //   object name; feeds child-path re-derivation
  LocalId       : long               // the child's durable prefab-internal fileID — the addressing key
                                      //   (distinguishes duplicate-named siblings; feeds pair-key resolve)
  Node          : FacadeNode         // the child's own façade type (COMPOSED from the nested prefab's
                                      //   FacadeEntry when the source node has NestedPrefabGuid)
```

**Generated artifacts (outside `Assets/`, IDE-only scaffolding — never compiled by Unity):**
- The **façade sources** under `<ProjectRoot>/SceneBuilders/Generated/*.cs` — the `Prefabs` catalog plus
  one per-prefab container of ref types. Injected into `Assembly-CSharp.csproj` by the EXISTING
  `BuilderProjectInjector` (its `BuilderFiles()` already globs every `*.cs` recursively under the
  builders dir), so the IDE type-checks them but Unity never compiles them and never domain-reloads.
- The **façade manifest** `<ProjectRoot>/SceneBuilders/Generated/Prefabs.sbfacade.json` — the serialized
  `FacadeCatalog`, the parser's source of truth for `PropertyName → (Guid | RealName | LocalId)`.

**Ledger (mirrors foundation §11):** `PrefabHierarchy`/`PrefabHierarchyNode` (adapter→generator input),
`FacadeCatalog`/`FacadeEntry`/`FacadeNode`/`FacadeChild` (manifest model + parser resolution),
`PrefabFacadeGenerator.Generate` (byte-stable codegen), the `Prefabs.X`/`.On(sel => sel.A.B, …)`
member-chain parse, and the `Prefabs.sbfacade.json` manifest are all owned by **M-Facades**.

---

## Goal

Author prefab references and their internal hierarchy as **compiler-checked, IntelliSense-completing C#
member chains** — `scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, …)` — generated from each
prefab's real structure, instead of magic strings. A typo, or a stale reference after a Unity rename,
now **fails to compile** with a located error pointing the LLM at the exact bad accessor, and a rename
that IS synced rewrites the accessor automatically. The generated types carry no runtime behavior; the
Roslyn parser reads the member chains exactly as it already reads `(Light l) => l.intensity`.

## In scope

- **The `Prefabs` catalog** — a generated static class with one property per project prefab
  (`Prefabs.Tank`, `Prefabs.Coin`, …), each typed as that prefab's root ref type. `Prefabs.<X>` parses to
  the prefab's GUID.
- **Per-prefab hierarchy façades** — for each prefab, a container of generated ref types mirroring its
  GameObject tree: the root type exposes a typed property per child, recursively
  (`TankRef.Turret : TurretRef`, `TurretRef.Barrel : NodeRef`, …). `t.Turret.Barrel` parses to the child
  **path** and the durable prefab-internal id. Types carry NO runtime behavior.
- **Injection** of the generated sources into `Assembly-CSharp.csproj` via the EXISTING
  `BuilderProjectInjector`/`OnGeneratedCSProject` — no new IDE machinery, no domain reload.
- **Parse** of the two member chains from source text: `Prefabs.X` → catalog property name → GUID;
  `sel => sel.A.B` → property-name segments → (real child path, durable ids), resolved through the
  manifest. Downstream resolution (path → durable pair-key) is unchanged from the string approach.
- **Deterministic / byte-stable generation** — identical prefab hierarchy ⇒ byte-identical façade
  sources and manifest, on any machine, with no timestamps or ordering nondeterminism.
- **Regeneration on prefab structural change** — a child added/removed/renamed, or a new/deleted prefab,
  regenerates the affected façade(s) + manifest. Fast: File IO under `SceneBuilders/` (outside `Assets/`)
  + a csproj re-inject; **no domain reload**.
- **Duplicate-sibling disambiguation at codegen** — two children both named "Wheel" cannot both be a C#
  property, so the generator emits distinct, deterministic identifiers (`Wheel`, `Wheel2`, … keyed on the
  durable `LocalId`) — never a duplicate/ambiguous identifier — and records each one's `LocalId` so
  resolution is not name-only. **This is the spec-16 hazard (name-path duplicate-blindness) caught at
  COMPILE time, and surfaced as two distinct compiler-checked accessors — call it a feature.**
- **C#-identifier sanitization** — spaces/symbols/keywords/leading-digits in object names become valid
  identifiers (`"Front Wheel"` → `FrontWheel`, `"2Fast"` → `_2Fast`, `event` → `@event`) **with the real
  object name preserved in `FacadeChild.RealName`** so the child path re-derives to the true name.
- **Nested-prefab façade composition** — a child that is itself a nested-prefab instance is typed with
  that nested prefab's own root ref type (its FacadeEntry composed in), so `t.Turret.Barrel` descends
  into the nested prefab's generated hierarchy; the nested tree is never duplicated inline.
- **Upgrade M6's `Instance("path")` to `Instance(Prefabs.X)`** — the typed form is preferred and is what
  codegen/sync emit; the string form is retained as a fallback (see Relationship / migration).

## Out of scope

- **Typed refs for non-prefab assets** (materials, scenes, sprites, meshes). A natural follow-up (a
  `Materials.X` / `Scenes.X` catalog on the same mechanism), specced separately — not here.
- **Typed refs to in-SCENE objects.** That is the LogicalId/handle model (§4) — a separate, per-scene
  concern; this milestone types prefab **assets** and their internal structure only.
- **Authoring prefab STRUCTURE** — creating/editing prefab assets, adding/removing prefab-internal
  objects, prefab variants as an authored concept. The generator READS prefab structure; it never
  authors it. (Same boundary M6/M10/M-nested-props drew.)
- **The nested-override materialize/reconcile round-trip depth** — that is M-nested-props (spec 24).
  M-Facades delivers the typed **addressing surface** (`.On(sel => sel.A.B, …)`) and its parse +
  path/durable-id resolution; the deep add/remove/revert semantics for nested overrides are spec 24's,
  which consumes this milestone.
- **A file watcher on the prefab assets as the regen trigger's transport.** Prefab asset changes are
  observed through Unity's `AssetPostprocessor` (adapter); M-Auto's builder-file watcher is unrelated.

## Core deliverables

### Types added/changed (referencing §3)

- Add `PrefabHierarchy` / `PrefabHierarchyNode` (adapter→generator input) and
  `FacadeCatalog` / `FacadeEntry` / `FacadeNode` / `FacadeChild` (manifest model + parser resolution) —
  all Unity-free POCOs (Additions above). No change to `SceneModel`, `GameObjectNode`, `ComponentData`,
  `ValueNode`, `AssetRef`, `PrefabInstanceNode`, `PrefabInstanceKey`.

### Functions / behaviors (testable contracts)

1. **Façade generation is deterministic and byte-stable.** `PrefabFacadeGenerator.Generate(hierarchy)`
   produces façade source (and `FacadeCatalog.Serialize()` the manifest) that is **byte-identical** for
   the same `PrefabHierarchy` across repeated runs and input orderings — children emitted in `LocalId`
   order, no timestamps, fixed formatting. (Same determinism bar as the canonical serializer, §8.)
2. **`Prefabs.<X>` parses to the prefab GUID.** Given source containing `scene.Instance(Prefabs.Tank)`,
   the parser reads the member access `Prefabs.Tank`, looks `"Tank"` up in the `FacadeCatalog`, and
   yields the same `AssetRef { Guid }` that `scene.Instance("Assets/Prefabs/Tank.prefab")` yields. The
   resulting `PrefabInstanceNode.SourcePrefab` is identical for the two spellings. An unknown
   `Prefabs.Nope` is a **located parse conflict** (§7), never a silent drop.
3. **A member chain parses to the right child path.** Given `.On(t => t.Turret.Barrel, …)`, the parser
   reads the `SimpleLambdaExpressionSyntax` body as a `MemberAccessExpressionSyntax` chain (the same
   node shape as `l.intensity` at `BuilderParser.cs:461-463`), extracts the property-name segments
   `["Turret","Barrel"]`, and resolves them through the manifest to the real child path `"Turret/Barrel"`
   and the target's durable `LocalId`. When a property name was sanitized/disambiguated (e.g.
   `t.Chassis.FrontWheel`), the re-derived path uses `RealName` (`"Chassis/Front Wheel"`), NOT the
   identifier.
4. **Duplicate-sibling is disambiguated deterministically and resolves distinctly.** Two prefab children
   both named `"Wheel"` generate two distinct properties `Wheel` / `Wheel2` (deterministic, ordered by
   `LocalId`), never a duplicate identifier. `t.Axle.Wheel` and `t.Axle.Wheel2` resolve to **distinct**
   `LocalId`s (hence distinct pair-keys downstream), even though both re-derive the same duplicate name
   path `"Axle/Wheel"`. **Name-path is not identity** — the durable `LocalId` is (spec-16). The generator
   MAY instead surface a codegen conflict recommending a forced rename; the DEFAULT is the deterministic
   disambiguator so generation never fails.
5. **Sanitization round-trips to the real name.** For every generated property, `FacadeChild.RealName`
   (or `FacadeEntry`'s prefab name) recovers the exact original object name: `FrontWheel → "Front Wheel"`,
   `_2Fast → "2Fast"`, `@event → "event"`. Resolving a selector segment to a child path uses `RealName`,
   so a sanitized identifier never corrupts the addressed path.
6. **Rename regenerates the façade (and the manifest).** Given a `PrefabHierarchy` in which a child's
   `RealName` changed `Turret → Cannon` (its `LocalId` unchanged), regeneration produces a façade whose
   property is now `Cannon` (type `CannonRef`) and a manifest whose `FacadeChild.RealName` is `"Cannon"`
   under the SAME `LocalId`. The durable id is the anchor; the identifier and path are re-derived.
7. **Reference-patch on rename (the auto-sync half).** Core exposes
   `FacadeReferencePatcher.Patch(builderSource, oldCatalog, newCatalog)`: a formatting-preserving
   Roslyn edit that, for every `Prefabs.X`/`.On(sel => sel.A.B, …)` member chain whose durable target
   (`Guid` / `LocalId`) is unchanged but whose identifier changed, rewrites the identifier segment
   (`t.Turret` → `t.Cannon`) in place — the same durable-key-keyed re-derivation spec 24 applies to
   string `ChildPath`s. A chain whose durable target no longer exists is **left untouched** (so it
   fails to compile — the safety net, behavior 8), never silently rewritten to a guess.
8. **Stale reference fails to compile (the safety-net half).** After regeneration, a member chain naming
   a removed/renamed accessor that was NOT patched (e.g. an offline edit, or a chain the patcher could
   not localize) no longer resolves against the regenerated façade type, so the builder **fails the
   compile check** with a located CS error at the stale accessor. This dual behavior — auto-sync rewrite
   when sync runs, compile error when it does not — is the milestone's core value: a stale reference is
   never silently mis-resolved (contrast a stale string path).
9. **Nested-prefab composition.** When `PrefabHierarchyNode.NestedPrefabGuid` is non-null, the generator
   types that node with the nested prefab's own root `FacadeNode` (composed from its `FacadeEntry`),
   rather than emitting a duplicate inline tree; `t.Turret.Barrel` descends into the nested prefab's
   generated hierarchy and resolves `Barrel`'s `LocalId` in the nested prefab's scope. Editing the
   nested prefab regenerates it once and both façades reflect the change.
10. **`Instance("path")` remains a supported fallback.** The string form still parses to a GUID exactly
    as M6 specified; the typed and string forms lower to the **same** `PrefabInstanceNode`. Likewise a
    string `.On("Turret/Barrel", …)` and the typed `.On(t => t.Turret.Barrel, …)` lower identically.
    The typed form is preferred and is what codegen and Reconcile EMIT.

## Editor adapter deliverables

- **Read the prefab hierarchy.** For each project prefab (`AssetDatabase.FindAssets("t:Prefab")` plus
  imported model prefabs, per M6), load its contents (`PrefabUtility.LoadPrefabContents` /
  `AssetDatabase.LoadAssetAtPath<GameObject>`), walk the transform tree, and build a `PrefabHierarchy`:
  each node's `RealName`, its durable prefab-internal `LocalId` (`GlobalObjectId.targetObjectId` /
  `AssetDatabase.TryGetGUIDAndLocalFileIdentifier` on the asset object), and `NestedPrefabGuid` when the
  node is a nested-prefab instance (`PrefabUtility.GetCorrespondingObjectFromSource` →
  `AssetDatabase.GUIDFromAssetPath`). Unloads contents when done. No façade LOGIC in the adapter — it
  fills the POCO and calls the Core generator.
- **Write / inject the façades.** Run `PrefabFacadeGenerator.Generate` + `FacadeCatalog.Serialize`, write
  the sources + manifest under `<ProjectRoot>/SceneBuilders/Generated/` with plain `File` IO (never
  `AssetDatabase`, so no domain reload — §2), via `SceneBuilderPaths.WriteIfChanged` for byte-stable
  no-ops. `BuilderProjectInjector` picks them up automatically (`BuilderFiles()` globs `*.cs` recursively
  under the builders dir); the manifest `.json` is not `*.cs`, so it is never injected/compiled.
- **Regen triggers.** An `AssetPostprocessor.OnPostprocessAllAssets` hook regenerates the affected
  façade(s) + manifest when a prefab/model asset under the project is imported, moved, renamed, or
  deleted (add/remove/rename of a prefab-internal object shows up as a re-import of that asset). Debounce
  and regenerate only the touched prefabs where possible. Deleting a prefab drops its `FacadeEntry` and
  its container source.
- **Reference-patch on rename.** After regeneration, when a child's identifier changed but its `LocalId`
  did not, call `FacadeReferencePatcher.Patch` over every builder under `SceneBuilders/` and
  `WriteIfChanged` the result, so live builders keep compiling. A chain whose durable target vanished is
  left to fail the compile check (safety net), never guessed.
- **Do NOT parse generated façades as scenes.** Only `ISceneDefinition` builder files are parsed into
  `SceneModel`s; the generated `Generated/*.cs` are IDE-only scaffolding and are excluded from the
  scene-builder discovery set (they declare no `ISceneDefinition`).
- **Resolve at build/sync.** `Prefabs.X` → GUID and selector segments → real child path + durable id come
  from the manifest (`FacadeCatalog`); the child-path → durable pair-key step is unchanged from the M6 /
  spec-24 addressing path.

## Authoring API added

Extends M6's `scene.Instance(...)`. `Instance` gains a **typed overload** taking a catalog ref
(`Prefabs.Tank`), which types the returned handle with the prefab's root ref type so `.On(...)` can
compiler-check the selector. The existing string overload remains (fallback). `.On(...)` gains a **typed
selector overload** `.On(Func<TRootRef, TNodeRef> selector, Action<ScopedHandle> closure)` alongside the
string form spec 24 introduces; both lower identically.

```csharp
public class ArenaScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        // Prefabs.Tank is compiler-checked & autocompletes; it replaces "Assets/Prefabs/Tank.prefab".
        scene.Instance(Prefabs.Tank)
             .Transform(pos: (3, 0, 5))

             // root override (M10) — unchanged
             .Override(t => t.Set((TankController c) => c.health, 200))

             // typed nested addressing — the compiler verifies t.Turret and t.Turret.Barrel exist;
             // IntelliSense completes `t.Turret.` -> { Barrel, Antenna }. Sanitized child shown too.
             .On(t => t.Turret.Barrel, barrel => barrel
                 .Override(b => b.Set((Light l) => l.intensity, 4f)))
             .On(t => t.Chassis.FrontWheel, w =>            // "Front Wheel" -> FrontWheel; RealName round-trips
                 w.Override(x => x.Set((WheelCollider c) => c.radius, 0.4f)));

        // Fallback (still supported): the string spelling lowers to the SAME instance node.
        scene.Instance("Assets/Prefabs/Coin.prefab").Transform(pos: (0, 1, 0));
    }
}
```

- `Prefabs` is the generated catalog; `Prefabs.Tank` is a get-only static property typed `TankRef`
  (the generated root ref). It has no runtime body that ever runs — the parser reads `Prefabs.Tank`
  from the text and maps it to the prefab GUID.
- `t : TankRef`; `t.Turret : TurretRef`; `t.Turret.Barrel : NodeRef`. Get-only, no runtime behavior.
  Every `.On(t => t.A.B, …)` lowers to the same target a string `.On("A/B", …)` would, with
  `Target.SubKey` = the durable pair-key resolved by the adapter (spec 24) and the re-derived child path
  for display.
- Duplicate siblings appear as distinct, autocompleting properties (`t.Axle.Wheel`, `t.Axle.Wheel2`) —
  the spec-16 ambiguity is now visible and type-safe rather than a silent name collision.

## IdentityMap / sidecar changes

- **New generated manifest, not a per-scene sidecar change.** `Prefabs.sbfacade.json` (the serialized
  `FacadeCatalog`) lives under `<ProjectRoot>/SceneBuilders/Generated/`. It is regenerated (never
  hand-edited), deterministic, byte-stable, and is the parser's source of truth for `PropertyName → Guid`
  and selector-segment → `(RealName, LocalId)`. It is not injected into any csproj (it is `.json`).
- **Per-scene `*.sbmap.json` is unchanged in shape.** A prefab instance entry still stores
  `SourcePrefabGuid` + `PrefabKey` (M6); nested override records still key on the durable pair-key
  (spec 24). The typed accessor is purely an authoring-surface + parse concern — it resolves to the same
  GUID / durable ids those records already hold.
- The asset cache (`Assets[]`, §4) already covers each prefab's GUID (M6); nested-prefab GUIDs pulled in
  by composition join it (merge-only, never pruned).

## Core test plan (RED behaviors — headless, no Unity)

1. `Generate_SameHierarchy_ByteIdentical` — two `Generate` runs on the same `PrefabHierarchy` (and a
   shuffled-input variant) produce byte-identical façade source AND manifest; children ordered by
   `LocalId`; no timestamps.
2. `PrefabsRef_ParsesToPrefabGuid` — `scene.Instance(Prefabs.Tank)` and
   `scene.Instance("Assets/Prefabs/Tank.prefab")` parse to `PrefabInstanceNode`s with the identical
   `SourcePrefab.Guid`. `Prefabs.Nope` yields a located parse conflict, not a drop.
3. `MemberChain_ParsesToChildPathAndLocalId` — `.On(t => t.Turret.Barrel, …)` resolves through the
   manifest to child path `"Turret/Barrel"` and Barrel's `LocalId`; a string `.On("Turret/Barrel", …)`
   lowers to the identical target.
4. `SanitizedSegment_ReDerivesRealName` — `.On(t => t.Chassis.FrontWheel, …)` resolves to
   `"Chassis/Front Wheel"` (RealName), not `"Chassis/FrontWheel"`; keyword/leading-digit cases
   (`@event`, `_2Fast`) round-trip to `"event"`, `"2Fast"`.
5. `DuplicateSibling_DisambiguatesDeterministically_AndResolvesDistinct` — two `"Wheel"` children
   generate `Wheel`/`Wheel2` ordered by `LocalId`; `t.Axle.Wheel` and `t.Axle.Wheel2` resolve to
   distinct `LocalId`s though both re-derive `"Axle/Wheel"`; regeneration produces the same assignment
   (stable across a live reorder, because `LocalId` is durable). *(Guards the spec-16 hazard at codegen.)*
6. `Rename_RegeneratesFacade_KeepsLocalId` — a hierarchy with `Turret → Cannon` (same `LocalId`)
   regenerates a façade whose property is `Cannon`/type `CannonRef` and a manifest whose `RealName` is
   `"Cannon"` under the unchanged `LocalId`.
7. `ReferencePatcher_RewritesRenamedSegment_KeepsUnchangedChains` — `FacadeReferencePatcher.Patch`
   rewrites `t.Turret` → `t.Cannon` (durable target unchanged) formatting-preservingly, leaves every
   other chain byte-identical, and leaves a chain whose durable target vanished untouched.
8. `StaleChain_AfterRegen_FailsCompileCheck` — a builder referencing a removed accessor, compiled
   against the regenerated façade via the Roslyn compile assertion, reports a located CS error at the
   stale accessor (the safety net) — proving the compiler catches what a stale string would not.
9. `NestedPrefab_FacadeComposes` — a node with `NestedPrefabGuid` is typed with the nested prefab's root
   ref; `t.Turret.Barrel` resolves `Barrel`'s `LocalId` in the nested prefab's scope; the nested tree is
   not duplicated inline.
10. `TypedAndString_LowerIdentically` — for both `Instance` and `.On`, the typed and string spellings
    produce equal `SceneModel`/target output (fallback equivalence, behavior 10).
11. `Manifest_RoundTrips` — `FacadeCatalog.Serialize` → parse → serialize is byte-identical, including
    `LocalId`, `RealName`, disambiguated `PropertyName`s, and composed nested entries.
12. `Generator_SanitizesToValidUniqueIdentifiers` — spaces/symbols/keywords/leading-digits all yield
    compilable identifiers; sibling collisions after sanitization are disambiguated (e.g. `"a b"` and
    `"a-b"` both → `A_b`? → distinct `A_b`, `A_b2`), asserted to compile.

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/Assets/GateTests/`)

Each step is a real editor action against a live project with a multi-level Tank prefab (a Turret nested
prefab containing a Barrel with a `Light`; a Chassis with two `"Wheel"` children and a `"Front Wheel"`).

1. With the project's prefabs present, confirm `<ProjectRoot>/SceneBuilders/Generated/Prefabs.*.cs` +
   `Prefabs.sbfacade.json` exist, are byte-stable across a second regen, and are injected into
   `Assembly-CSharp.csproj` (present as `<Compile Include="SceneBuilders/Generated/…"/>`), so the IDE
   type-checks them; Unity performed **no domain reload** for the write.
2. Author `scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, b => b.Override(x => x.Set((Light l) =>
   l.intensity, 4f)))`, Build. **Expected:** the Tank instance appears; the Barrel shows the bold
   override; the instance keeps its `GlobalObjectId` (no re-instantiate). The typed and the equivalent
   string builder produce the identical scene.
3. **Rename** the Turret child inside the Tank prefab `Turret → Cannon` in Unity. **Expected:** the
   façade regenerates (`t.Cannon…`), AND the reference-patch rewrites the live builder's `t.Turret` →
   `t.Cannon`; the builder still compiles (`BuilderCompileCheck` reports no errors) and re-Builds to the
   same Barrel (durable id unchanged, override preserved).
4. **Safety net:** hand-edit the builder to reference `t.Turret` again (stale) WITHOUT syncing, then run
   the compile check. **Expected:** a **located CS error** at `t.Turret` (the accessor no longer exists
   on `TankRef` after the rename) — the mistake is caught at compile time, not silently mis-resolved.
5. The Chassis's `"Front Wheel"` child autocompletes as `t.Chassis.FrontWheel`; authoring an override on
   it Builds to the object named `"Front Wheel"` (RealName round-trip), not a nonexistent `"FrontWheel"`.
6. The two `"Wheel"` children autocomplete as `t.Chassis.Wheel` and `t.Chassis.Wheel2`; overriding a
   property on `Wheel2` affects only the second wheel (distinct durable id), NOT the first — the
   duplicate-sibling ambiguity is compiler-visible and correctly resolved (spec-16, at compile time).
7. Add a NEW child under the Tank prefab, save the prefab. **Expected:** the façade regenerates to expose
   the new accessor (autocompletes) with no domain reload; a builder referencing it compiles and Builds.

## Dependencies

- **M6** (`specs/completed/07-m6-prefab-instances.md`) — `scene.Instance(...)`, `PrefabInstanceNode`,
  `PrefabInstanceKey`, prefab/model-prefab detection, GUID resolution, and the string `Instance("path")`
  form this milestone upgrades and retains as a fallback. Hard dependency.
- **`BuilderProjectInjector` / `SceneBuilderPaths` (§2, §12)** — the existing csproj-injection mechanism
  the generated façades reuse verbatim; `BuilderFiles()` already globs the `Generated/` sources.
- **Spec 16** (`specs/completed/16-duplicate-sibling-identity.md`) — the identity principle the
  disambiguator obeys: two same-named siblings must not silently mis-resolve. M-Facades turns that hazard
  into two distinct, compile-checked accessors keyed on the durable `LocalId` (not the name path).
- **M3** (typed `.Set((T x) => x.member, …)` member-chain parse) — the exact Roslyn shape
  (`SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax }`, `BuilderParser.cs:461-463`)
  this milestone reuses to read `Prefabs.X` and selector chains. The Roslyn compile assertion (§ "Generated
  C# must compile", CLAUDE.md) is the safety-net enforcer.
- **The Roslyn compile check** used by the gate — the enforcer of behavior 8 (stale ⇒ compile error).

## Relationship / migration

- **Hard prerequisite for M-nested-props (spec 24).** Spec 24's nested-target addressing
  (`.On("Turret/Barrel", …)`) becomes the typed `.On(t => t.Turret.Barrel, …)` overload delivered here;
  both lower to the same durable-pair-keyed target. **M-Facades sequences BEFORE M-nested-props** in the
  build order. Spec 24 owns the deep nested-override materialize/reconcile/revert semantics; M-Facades
  owns the typed addressing surface + its parse/resolution and hands spec 24 a compiler-checked path.
- **Upgrades M6.** `Instance("path")` → `Instance(Prefabs.X)` is the preferred, codegen-/sync-emitted
  form. **The string `Instance("path")` form REMAINS SUPPORTED as a fallback** — existing builders keep
  working unchanged, and both spellings lower to the identical `PrefabInstanceNode`. Same for the string
  vs typed `.On`. Rationale: a hard cutover would break every shipped builder and every hand-authored one
  for no correctness gain, since the string form still resolves to a GUID; the typed form's value is
  compile-time checking + autocomplete + rename-safety, which is opt-in-by-preference, not mandatory.

## Risks/notes

- **Rejected: name-path-as-identity (spec-16).** A duplicate-named sibling's façade property
  (`Wheel`/`Wheel2`) is distinct in C#, but the re-derived child path is duplicate-blind. Identity is the
  durable `LocalId`, and the disambiguator + manifest key on it — never the path. Every selector →
  target resolution passes through `LocalId`, so two same-named siblings never cross-resolve.
- **Disambiguator MUST key on the durable `LocalId`, not a live sibling index.** A sibling-index
  disambiguator would be reorder-fragile (the spec-16 positional defect): reordering the two Wheels would
  swap `Wheel`/`Wheel2`'s meaning. The prefab-internal fileID (`LocalId`) is stable across reorders
  within the asset, so the assignment is durable; a prefab reorder is itself an asset edit that
  regenerates the façade, and the `LocalId`-keyed order is preserved.
- **Rename safety is dual, and both halves matter.** Auto-sync (`FacadeReferencePatcher`) rewrites the
  accessor when sync runs; the regenerated façade type makes a NON-rewritten stale accessor fail to
  compile. If only one existed, a rename would either silently mis-resolve (path-only) or break every
  builder on every rename (compile-only). The patcher must be conservative: rewrite ONLY when the durable
  target is unchanged; never guess a new target for a vanished accessor (leave it to the compile error).
- **Generation performance.** Regeneration walks a single changed prefab's hierarchy and writes text
  outside `Assets/` — bounded and reload-free. Regenerating EVERY prefab on EVERY asset change would be
  wasteful; scope regen to the touched prefabs (and any prefab that composes a changed nested prefab).
  Do not regress into a full-project regen per keystroke (the CLAUDE.md sync-performance constraint).
- **Cross-prefab type-name collisions.** Two prefabs each with a `"Turret"` child would collide on
  `TurretRef` at assembly scope; the generator MUST scope each prefab's ref types to a per-prefab
  container (nested types / a per-prefab class), so uniqueness is required only WITHIN a prefab. Test 12
  and the compile assertion pin this.
- **Generated façades must never be parsed as scenes.** They declare no `ISceneDefinition`; the scene
  discovery set must exclude `Generated/`. A regression here would try to reconcile a façade file as a
  scene builder.
- **The manifest is generated truth, not user-editable.** A hand-edit to `Prefabs.sbfacade.json` is
  clobbered on the next regen (like the injected csproj, §2). It is disposable, deterministic output.
- **Nested-prefab `LocalId` scope.** A composed nested-prefab node resolves its children's `LocalId`s in
  the nested prefab's own asset scope; the adapter's selector→pair-key step (spec 24) must walk to the
  correct nested-prefab source object (`GetCorrespondingObjectFromSourceAtPath`) — the same correspondence
  chain spec 24 already mandates. M-Facades supplies the durable ids; spec 24 consumes them.
- **VSCode caveat (inherited).** `OnGeneratedCSProject` is called only by the Rider/VS packages, not
  VSCode (`BuilderProjectInjector` remarks). IDE recovery for the façades is therefore not universal —
  the same known limitation the relocated builder already carries, not a new one.
