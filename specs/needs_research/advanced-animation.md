# needs_research — Advanced animation content authoring

**Status:** research stub, not a build milestone. Promote to a milestone (Mn) once the open
questions below have answers concrete enough to write a spec against the foundation contract.

## Problem
M11 covers a fixed library of parametric easing curves generated into simple `AnimationClip` assets.
Everything richer is deferred here:

- **Multi-keyframe / hand-authored curves** — arbitrary keyframes, tangents (in/out, weighted),
  broken tangents, constant/auto modes.
- **Animation events** — `AnimationEvent` function calls on the timeline (name, time, args).
- **Avatar/humanoid retargeting, IK, root motion.**

> **Note:** the controller side — state machines, transitions, parameters, conditions, layers, blend
> trees — is tracked separately in [animation-fsm.md](animation-fsm.md), which scopes a simple v0 set
> and an advanced v1 set. This file is limited to clip **content**.

## Why it's hard (and parked)
- These are complex *asset* graphs, not scene structure — a different serialization surface from the
  scene model, with their own fileID/GUID identity concerns.
- **Round-trip is the blocker**, not construction: reverse-engineering an arbitrary hand-edited curve
  or state machine back into a clean code representation is the same "decompile a generator" problem
  the whole product deliberately constrains away. M11 sidesteps it by only supporting presets and
  flagging divergence.

## Open questions to resolve before promotion
1. Is the authored form declarative (keyframe list) or does it stay preset-based with a larger preset
   library? Where is the line past which we only *reference* a hand-authored asset, never author it?
2. What round-trip guarantee (if any) do we offer for state machines — read-only reflection into code,
   or none?
3. Identity/stability of generated clip sub-assets and controller sub-objects across regeneration.

## Related
Depends on M4 (asset references) and M11 (easing v0) shipping first.
