# needs_research — Animation FSM (AnimatorController state machines)

**Status:** research stub, not yet a build milestone. Scoped for a **phased** promotion: a *simple v0
set* becomes a near-term milestone once M11 (easing clips) ships; an *advanced v1 set* follows.

This is distinct from clip *content* (see [advanced-animation.md](advanced-animation.md)). Here the
subject is the **controller**: states, transitions, parameters, and when to move between states.
Unity's `AnimatorController` model is robust; we capture a deliberately small slice first.

## v0 — simple set (target: promote to a milestone after M11)
Author & round-trip a single-layer controller sufficient for common gameplay wiring:
- **States**, each referencing an `AnimationClip` (via M11 / M4 asset refs); a **default state**.
- **Parameters**: `bool`, `trigger`, `int`, `float`.
- **Transitions** between named states with **simple conditions** (one or more parameter comparisons:
  `Greater/Less/Equals/NotEquals`, trigger-set, bool-is), **has-exit-time + exit time**, and
  **transition duration**. **Any State** transitions included.
- Reference the controller from an `Animator` component on a scene object.

Illustrative surface (exact API is milestone-owned):
```csharp
scene.Add("Player").Animator("Assets/Anim/Player.controller", ac => {
    ac.Param.Float("Speed"); ac.Param.Trigger("Jump");
    ac.State("Idle", idleClip).Default();
    ac.State("Run",  runClip);
    ac.Transition("Idle", "Run", when => when.Float("Speed", Cmp.Greater, 0.1f));
    ac.Transition("Run",  "Idle", when => when.Float("Speed", Cmp.Less, 0.1f));
    ac.AnyState("Jump", jumpClip, when => when.Trigger("Jump"));
});
```

## v1 — advanced set (later milestone or its own research)
- **Multiple layers** with weights, avatar **masks**, and blend modes (override/additive).
- **Sub-state machines** (nested, entry/exit nodes).
- **Blend trees** — 1D and 2D (simple directional / freeform), thresholds/positions.
- **Transition nuances** — interruption sources, ordered condition lists, can-transition-to-self,
  fixed vs normalized duration/offset.
- **StateMachineBehaviour** references on states.

## Why it's parked (and the round-trip caveat)
- The controller is a complex **asset graph** with its own sub-object identity (states/transitions
  have fileIDs), separate from the scene model — a distinct serialization surface.
- **Round-trip is the constraint, not construction.** Authoring a controller from code is
  straightforward; faithfully reflecting arbitrary hand-edits made in Unity's Animator window back
  into clean code is the "decompile a generator" problem again. v0 should mirror M11's stance:
  author from a structured spec, and **flag divergence** when a user hand-edits the controller into
  something outside the supported set rather than trying to reverse it.

## Open questions to resolve before promotion
1. Exactly where the v0/v1 line sits on conditions and transition options (which fields are in the
   simple set).
2. Round-trip guarantee for v0 — full round-trip of the simple set, or author-forward + divergence
   flag only?
3. Identity/stability of controller sub-objects (states, transitions) across regeneration — reuse
   the §4 IdentityMap approach with `(controller-asset-GUID, sub-object fileID)` keys?
4. Ordering/interaction with M11: transitions reference states which reference clips — dependency is
   M4 (asset refs) + M11 (clips) → simple FSM.

## Related
Depends on M4 and M11. The advanced set may itself split into its own research stub once v0 lands.
