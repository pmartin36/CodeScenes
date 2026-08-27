# House Walkthrough

Build a small, polished, third-person walkthrough of a single-floor house in Unity.

## The scene

One floor of a house with **three rooms** — a **dining room**, a **living room** (with a TV), and a
**kitchen** — connected so the player can walk between them:

- A **player character** walks room to room with **WASD**, and the camera pans / looks around freely.
- A **second character wanders the house on its own** using a **NavMesh** — a `NavMeshAgent` pathing
  between rooms/waypoints. The furniture placed in the scene are baked as NavMesh obstacles, so the
  NPC walks *around* the sofa, table, and counters rather than through them (a natural showcase of the
  authored layout driving navigation). Bake the NavMesh over the authored floor (the AI Navigation
  package is already in the project).
- Each room is **furnished and dressed** so it reads as a real room: the dining room has a table and
  chairs; the living room has a sofa, a coffee table, and a TV; the kitchen has counters, a fridge, and
  a stove. Add rugs, lamps, shelves, plants, wall art as suits each room.
- The house **shell** (floor and walls, room dividers, doorways between rooms) is built from primitives,
  sized and placed with CodeScenes — not sourced as a model.
- It must actually be walkable: the character moves on the floor, doesn't pass through walls or
  furniture, and can move between all three rooms.

Everything else is your call — the exact floor plan, camera feel, lighting, how the rooms connect, and
any interaction. Make it feel good and look good; treat it as a showcase piece.

## Place and size by relationship — don't hand-measure

The furniture is random free assets at whatever native size they happen to be, so don't hard-code
coordinates. Author by **relationship** and let the CodeScenes solvers do the work:

- **`.FitSize(...)`** each prop to a sensible real-world size (a sofa ~2 m wide) regardless of its native
  scale. Reach for this first on any sourced prop.
- **`.AlignTo(target, ...)`** to sit furniture on the floor and flush against walls (`AbutMax`/`AbutMin`
  for outside faces; `AlignMin`/`AlignMax`/`AlignCenter` for same-side faces; `.Offset(f)` to nudge).
  `AlignTo` resolves in the target's local frame by default, so furniture against a wall that isn't
  world-axis-aligned still lands right; `space: AlignSpace.World` or `frame:` overrides.
- **`.Between(a, b, fraction, axis)`** to space chairs along a table, art along a wall, etc.

## Assets available

Browse these folders and use whatever fits — nothing is assigned to a particular role. (The project has
`com.unity.cloud.gltfast` so the `.glb` files import natively.)

- `Assets/House/PolyPizza/` — living-room + dining furniture (GLB): `Sofa_CMHTOculus`,
  `CoffeeTable_FranciscoHui`, `TV_AlexSafayan`, `TVStand_CMHTOculus`, `Bookshelf_WanjaPfluger`,
  `Plant_scaranto`, `FloorLamp_DanniBittman`, `Rug_AlexSafayan`, `DiningTable_CreativeTrio`,
  `DiningChair_CMHTOculus` (instance it 4+ times), `Chandelier_CreativeTrio`.
- `Assets/House/Quaternius/` — kitchen (FBX): `Kitchen_Fridge`, `Kitchen_Oven`, `Kitchen_Sink`,
  `Kitchen_Cabinet1/2/Small`, `Kitchen_1/2/3Drawers`. Plus **two rigged characters**:
  `Character_Casual_Quaternius_CC0.fbx` (use as the WASD player) and `Character_Suit_Quaternius_CC0.fbx`
  (use as the NavMesh wanderer) — both ship with `Walk`/`Run`/`Idle` clips (see note).
- The house **shell** is not an asset — build floor / walls / room dividers / doorways from primitive
  cubes and planes with `FitSize` + `AlignTo`.

Character animation: both `Character_Casual_...` and `Character_Suit_...` are already rigged
(`CharacterArmature`) and ship with their own clips in the same FBX — `Walk`, `Run` (+ Back/Left/Right),
`Idle`, `Roll`, `Wave`, and more. Set the FBX import `Rig` appropriately and drive those clips from an
Animator (a walk/idle blend keyed off `NavMeshAgent.velocity` for the wanderer); no Mixamo or external
animation is needed. Movement (the WASD controller, the free camera, the NavMesh agent + baking) is
ordinary Unity gameplay/setup, not part of the CodeScenes-authored scene.

## How to build it

Build the scene with **CodeScenes** — see the `codescenes-authoring` skill for how to author a Unity
scene in code, and especially the "place and size by relationship" solvers (`FitSize` / `AlignTo` /
`Between`), which are the intended tool for the mixed-scale furniture. Write any gameplay logic (the
character controller, the free camera) as ordinary Unity MonoBehaviours (the skill explains how they fit
with the authored scene).
