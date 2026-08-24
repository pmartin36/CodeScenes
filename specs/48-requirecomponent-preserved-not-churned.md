# Spec 48: a `[RequireComponent]`-required component is preserved, not churned

A component Unity auto-adds because another present component declares `[RequireComponent(typeof(T))]`
— canonically `CanvasRenderer`, required by `Graphic` and therefore by every `Image` /
`TextMeshProUGUI` — is planned for removal on EVERY code→scene build when the builder source does not
also declare it. Unity refuses the removal (the requiring component still depends on it), so the same
error is logged every build and the component is never actually removed: churn with no progress.
Observed live during a one-shot authoring run (SceneBuilderOneShot, Claude session `a6792c4f`). The fix
makes the reconciler treat a live component required (directly or transitively) by a surviving
component as intrinsic, never drift, through one guard at the single removal site — so no builder ever
declares a RequireComponent dependency.

## The measured defect

Reproduced live, code→scene build of a scene containing a `TextMeshProUGUI` whose builder never
mentions `CanvasRenderer`. Console, verbatim, once per build:

```
Can't remove CanvasRenderer because TextMeshProUGUI (Script) depends on it
```

The author's workaround was to hang `.Component<UnityEngine.CanvasRenderer>()` on every `Image`/`TMP`
graphic so the desired model would stop treating the live `CanvasRenderer` as extra — a declaration
every future author would have to remember on every graphic.

Owner of the removal decision: `Differ.EmitComponentEdits`, the loop at
`SceneBuilder.Core/Diff/Differ.cs:369-387`. It walks the live scene's `actualComps` and, for any
actual component whose `(Type.FullName, Ordinal)` key is absent from `desiredIndexByKey`, emits a
`RemoveComponent` (`Differ.cs:380`, the ONLY `new RemoveComponent` for a plain GameObject). Because the
builder omits `CanvasRenderer`, it is absent from desired, so it is unconditionally classified as drift
and a `RemoveComponent` op is emitted. `PlanExecutor` then runs `UnityEngine.Object.DestroyImmediate(rc)`
(`com.codescenes/Editor/PlanExecutor.cs:307-315`); Unity refuses the destroy while the requiring
component is present, logging the line above. Nothing is removed, so the op re-emits and re-fails every
build.

`RequireComponentPredicate` exists (`com.codescenes/Editor/RequireComponentPredicate.cs`) and yet does
not prevent this, for two independent reasons:

- **It answers one hardcoded question, not the general one.** `RequiresRectTransform(TypeRef)`
  (`RequireComponentPredicate.cs:17`) walks `[RequireComponent]` transitively (`Walk`, `:28-62`) but
  returns true only when the required type is `RectTransform` (`:49`). It cannot report that
  `TextMeshProUGUI` requires `CanvasRenderer`, or yield a type's required-set at all.
- **It is consulted only on the desired-model promote/add side.** Its single caller is
  `DesiredModelLoader.cs:171` → `RectTransformPromotion.Promote`, which flips a node's
  `Transform.Kind`. Nothing in the removal path ever calls it. The removal decision lives in Core
  (`Differ.cs`), which is Unity-free and holds no `[RequireComponent]` reflection, so no removal has
  ever asked the RequireComponent question.

## The fix

Owner: the removal loop in `Differ.EmitComponentEdits` (`Differ.cs:369-387`) — the one site that
births a plain-GameObject `RemoveComponent`. The guard goes there so every current and future removal
inherits it; there is exactly one such site to guard.

One shared mechanism: before emitting a `RemoveComponent` for an actual component `C`, the loop asks
whether `C`'s type is required — directly or transitively — by any SURVIVING component on the same
object (a component present in `desiredComps`, i.e. one the builder kept). If so, `C` is intrinsic and
no `RemoveComponent` is emitted. Keying on the surviving (desired) requirers, not merely on any live
sibling, is what keeps it correct when the requirer itself is deleted: remove `TextMeshProUGUI` from
source and `CanvasRenderer` loses its only surviving requirer, so it becomes legitimately removable in
the same pass rather than orphaned.

The `[RequireComponent]` reflection is Unity-only, so the Editor injects it into `Differ.Diff` /
`Materializer.Materialize` as a closure — exactly as `RectTransformPromotion.Promote` already receives
`Func<TypeRef, bool> requiresRectTransform` from `DesiredModelLoader.cs:171`. The closure is backed by
a generalized `RequireComponentPredicate` that returns a type's transitive required-set (reusing the
existing `Walk` traversal at `RequireComponentPredicate.cs:28-62`) instead of testing one hardcoded
target. Core owns the guard and its rule; the Editor owns only the reflection the guard consults. No
builder declares a RequireComponent dependency, and the `.Component<CanvasRenderer>()` workarounds are
deleted.

Leaving the required component out of `desiredComps` (rather than injecting it into the desired list)
keeps `desiredKeys`/`actualKeys` unequal, so the `SetEquals`-gated reorder pass (`Differ.cs:389-404`)
stays dormant and no component-order churn is introduced by the fix.

## Accept when

A scene whose only builder declaration is a `TextMeshProUGUI` (or an `Image`) — Unity auto-adds
`CanvasRenderer` via the graphic's `[RequireComponent]` — builds, then builds again, from source that
never names `CanvasRenderer`, and:

- zero `Can't remove CanvasRenderer because ... depends on it` console errors across both builds;
- zero `RemoveComponent` plan-ops target the required `CanvasRenderer`;
- the live `CanvasRenderer` still exists after each build, and the second build is a fixed point (no
  ops over that component).

Regression coverage, both layers:

- **Core** — a `Differ`/`Materializer` test drives the removal loop with an injected required-set
  predicate over a snapshot where a live required component is absent from desired: assert no
  `RemoveComponent` op is emitted for it while its requirer survives, and assert one IS emitted once the
  requirer is dropped from desired. This is a Unity-observable behavior; the injected predicate lets Core
  prove the plan-op suppression on POCO fixtures.
- **EditMode** (`unity-gate/Assets/GateTests/`, sibling to `RectTransformRequirePromotionTests.cs`) — a
  real editor scene with a `TextMeshProUGUI` built twice from a `CanvasRenderer`-free builder asserts a
  clean console (no dependency-removal error via `LogAssert`), the live `CanvasRenderer` present after
  each build, and a fixed-point second build. Per the CLAUDE.md hard requirement, the real
  `[RequireComponent]` reflection and `DestroyImmediate` refusal are only exercised against a live editor.
