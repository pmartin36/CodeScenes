# Spec 27 — Typed child-GameObject selectors for `.RemoveChild` / `.AddChild`

> **Why this exists.** CodeScenes' core principle: an LLM writes scene-builder code the **compiler**
> verifies, and a misunderstanding fails to COMPILE rather than silently doing the wrong thing.
> Spec 24 shipped child-GameObject override authoring on prefab instances with **string paths**
> (`.RemoveChild("LeftTurret/Antenna")`, `.AddChild("LeftTurret", "MuzzleFlash", …)`). Strings defeat
> the principle: a typo or a hierarchy rename sails past the compiler and only surfaces at Build.
> Specs 24/25 already made the analogous surface — `.On(sel => sel.Turret.Barrel, …)` — compiler-checked
> by generating typed façade accessors and having the Roslyn parser read the member chain out of the
> source text. This spec extends **that same machinery** (no parallel mechanism) to `.RemoveChild` and
> `.AddChild`.

## The two overloads (on `InstanceHandle<TRef>`)

1. `RemoveChild<TNode>(Func<TRef, TNode> selector)` — the removed **source child** has a façade type,
   so `.RemoveChild(sel => sel.LeftTurret.Antenna)` is compiler-checked: a typo or a stale name is a
   COMPILE error, and a hierarchy rename auto-syncs the accessor (exactly like `.On`).
2. `AddChild<TNode>(Func<TRef, TNode> parentSelector, string name[, Action<NodeHandle> configure])` —
   the **parent** is façade-checked (`sel => sel.LeftTurret`); the **new child name** stays a `string`
   because it is a genuinely new object with no façade type yet.

The existing string overloads (`RemoveChild(string)`, `AddChild(string, string[, cfg])`) remain, for
children with no façade type / dynamic cases.

## Guarantees

- **Compiler-catch.** A typed selector segment that no longer exists (typo / stale after a Unity
  rename) fails to compile against the generated façade type; at parse/resolution it is a located
  `UnknownFacadeReference` conflict — never a silent removal or an add to the wrong parent.
- **Same representation as the string form.** A typed selector resolves through the `FacadeCatalog`
  (RealName-joined `ChildPath`) to the identical `RemovedGameObjects` / `AddedGameObjects.Parent`
  representation the string form produces. Downstream diff / plan / execute is unchanged.
- **Rename auto-syncs.** A prefab-child rename fires the AssetPostprocessor → façade regen →
  `FacadeReferencePatcher` chain that rewrites the typed selector's renamed segment in place (keyed on
  durable child `LocalId`), leaving every other chain and the new-child name string byte-identical —
  the same patch that already covers `.On`.
- **Typed-by-default emit.** Scene→code sync emits the typed `.RemoveChild(sel => …)` /
  `.AddChild(sel => parent, "New")` form when the catalog resolves the path, falling back to the string
  form only with no/absent catalog — matching `.On`'s emit.

## Reused machinery (no parallel mechanism)

- Parse: `BuilderParser.Facade.cs` `TryReadSelectorSegments` + `FacadeCatalog.TryResolveSelector`
  (shared by `ApplyRemoveChild` / `ApplyAddChild` via `TryResolveChildSelectorArg`).
- Emit: `ReconcilerInstances.Nested.cs` `TryRenderScopedSelector` (ChildPath → `sel => sel.A.B`).
- Drop/revert match: `SourcePatchApplier.ScopedOn.cs` `SelectorKeyMatches`.
- Rename: `FacadeReferencePatcher` Pass 2, method-name set broadened to `{On, RemoveChild, AddChild}`.

## Unity confirmation checklist (→ EditMode `unity-gate/Assets/GateTests/TypedChildSelectorTests.cs`)

1. `TypedRemoveChild_RemovesLiveSourceChild_SameAsStringForm` — a builder using
   `.Instance(Prefabs.Tank).RemoveChild(t => t.LeftTurret.Antenna)` built by `SceneBuilderBuild.Run`
   removes the live `LeftTurret/Antenna` source child (and leaves the `Barrel` sibling intact).
2. `TypedAddChild_CreatesLiveChild_UnderResolvedParent` — `.AddChild(t => t.LeftTurret, "MuzzleFlash")`
   creates a live `MuzzleFlash` child under the resolved `LeftTurret` sub-object.
3. `RenameTurretChild_AutoSyncsTypedRemoveAndAddChildSelectors` — a real `LeftTurret → Cannon` prefab
   rename rewrites both typed selectors (`t.LeftTurret → t.Cannon`) in the on-disk builder, leaving the
   `RightTurret` chain and the `"MuzzleFlash"` name string unchanged.
