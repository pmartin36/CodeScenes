# needs_research — [SerializeReference] graph sharing (shared rid / cycles)

**Status:** research stub, not a build milestone. M9 handles the common `[SerializeReference]` case
(a polymorphic managed instance in a field, incl. null and type change). This stub covers the part M9
explicitly parks as `Unsupported` (round-trips verbatim, flagged).

## Problem
Unity's managed-reference serialization assigns each managed object a **`rid`** (reference id) in the
document's `references` section. Two different fields can point at the **same** managed instance
(shared reference), and instances can form **cycles** (A holds B, B holds A). M9's model
(`ValueNode.ManagedReference(concreteType, fields)`) is a *tree* — it cannot express sharing or
cycles, so those cases are captured as `Unsupported` and preserved rather than round-tripped into
clean code.

## Why it's parked
- Representing shared/cyclic object graphs in a flat, near-isomorphic C# builder needs a **handle/`rid`
  system for managed instances** (like the object-reference handles in M5, but for non-`UnityEngine.Object`
  managed instances) — a real model extension, not a tweak.
- Round-trip fidelity across regeneration requires stable identity for these managed instances, which
  Unity keys by `rid` (document-local, reassignable) — the same identity-stability concern as §4, in a
  harder place.
- It is comparatively **rare** in typical project code (the user reports infrequent use), so it does
  not earn v1 scope.

## Open questions to resolve before promotion
1. A code-side handle model for shared managed instances (declare once, reference by handle) — how does
   it read in the flat builder without reintroducing arbitrary structure?
2. Stable identity for managed instances across regeneration (map `rid ↔ handle` in the sidecar?).
3. Cycle handling in Materialize (two-pass wiring, like M5's forward-ref resolution) and in Reconcile.
4. Detection: how does Core know a snapshot managed ref is shared/cyclic vs a plain tree (from the
   adapter's read of the `references` section)?

## Related
Builds on M9 (managed-reference tree) and reuses the handle/two-pass ideas from M5. Promote only if
real usage appears.
