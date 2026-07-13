# needs_research — Multi-scene / additive scenes

**Status:** research stub, not a build milestone. v1 (M0–M11) targets a SINGLE scene per builder file.

## Problem
Unity projects routinely load multiple scenes additively (`LoadSceneMode.Additive`) — e.g. a manager
scene + streamed level chunks. Cross-scene references, ownership, and which builder file owns which
scene are all unaddressed by the single-scene model.

## Open questions to resolve before promotion
1. One builder file per scene, or a builder that composes multiple scenes?
2. **Cross-scene object references** — Unity restricts these (they don't serialize like intra-scene
   refs). How does `ValueNode.ObjectRef` (currently intra-scene, keyed on one IdentityMap) extend
   across scene boundaries? Likely needs a scene-qualified identity.
3. IdentityMap-per-scene vs a project-level map.
4. Additive load order / dependencies expressed in code.
5. Reconcile semantics when several additive scenes are open at once and edited together.

## Related
Extends the §4 identity model and §5 reconcile loop to N scenes. Best revisited after M7 (robustness)
proves the single-scene loop is stable.
