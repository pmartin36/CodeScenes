# needs_research — Nested prefabs and prefab variant chains (authored from code)

**Status:** research stub, not a build milestone. Promote once the open questions below have
concrete-enough answers (they need their own live spike). Flat, single-root prefab authoring shipped
separately as `specs/39-prefab-authoring.md`; this file is the deferred remainder that flat authoring
put out of scope.

This is the depth layer on top of flat prefab authoring: a code-authored prefab that itself instances
another prefab (nested), prefab variant chains, and keeping a code-defined base disentangled from a
scene override of its instance.

## What flat authoring (spec 39) already establishes
Round-tripped, seamless authoring of a single-root prefab asset from one builder `.cs`, sharing the
scene build/sync core through the target seam, with identity anchored on `GlobalObjectId(idType=1,
assetGUID, targetObjectId=fileID)` and the edit-in-place regeneration invariant (mutate loaded
contents, never overwrite-from-fresh, or fileIDs churn). Everything below reuses that foundation.

## Open questions to resolve before promotion

1. **InstantiatePrefab into prefab contents (unverified in the flat spike).** A nested prefab is
   produced by instancing prefab B inside prefab A's `LoadPrefabContents` graph, then saving A. Measure:
   does `PrefabUtility.InstantiatePrefab(B, contentsScene)` inside loaded contents produce a stable
   nested instance whose link survives `SaveAsPrefabAsset` + reload, and do the nested instance's
   internal objects keep stable identity across A's regeneration? This is the make-or-break unknown, the
   same shape as the flat fileID-stability spike but one level deeper.

2. **Nested identity model.** An object inside a nested prefab has both A's asset identity and B's
   `PrefabInstance` key (`TargetPrefabId` + `TargetObjectId`, the M6/M10 machinery). How the identity
   map composes these at depth, and how a fileID inside A that belongs to a nested B instance is keyed,
   needs a concrete model (probably reusing M10's override/instance identity, but confirm it holds when
   the outer container is an asset, not a scene).

3. **Variant chains.** A prefab variant overrides a base prefab. M6/M10 deferred variant chains. Authoring
   a variant from code (declare a base + a set of overrides) and round-tripping variant-level overrides
   vs base changes is its own authority problem: which layer owns a given value.

4. **Base vs override disentanglement.** When an instance of a code-defined prefab is overridden in a
   scene (M10), the code-defined base (owned by the prefab builder `.cs`) and the scene override (owned by
   the scene builder `.cs`) are two authorities over the same field. Decide how they stay separate so a
   base change and an override change do not fight or silently clobber each other.

## Related
- Builds directly on `specs/39-prefab-authoring.md` (flat authoring + the shared target seam + prefab
  identity), `specs/completed/07-m6-prefab-instances.md` (instancing), and
  `specs/completed/11-m10-prefab-overrides.md` (instance overrides).
- Overlaps the general "author an asset, not a scene" concern also present in
  [advanced-animation.md](advanced-animation.md).
