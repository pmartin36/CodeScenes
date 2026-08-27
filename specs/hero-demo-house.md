# House Walkthrough

Build a small, polished, third-person walkthrough of a single-floor house in Unity.

## The scene

One floor of a house with **three rooms** — a **dining room**, a **living room** (with a TV), and a
**kitchen** — connected so the player can walk between them:

- A character walks room to room with **WASD**, and the camera pans / looks around freely.
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
  `Kitchen_Cabinet1/2/Small`, `Kitchen_1/2/3Drawers`. Plus the character: `Character_Casual_Quaternius_CC0.fbx`
  (a static humanoid mesh — see note).
- The house **shell** is not an asset — build floor / walls / room dividers / doorways from primitive
  cubes and planes with `FitSize` + `AlignTo`.

Character animation: `Character_Casual_Quaternius_CC0.fbx` is an un-rigged mesh. Rig + animate it via
Mixamo (manual, one-time) and re-import, or drive a simpler capsule if the walkthrough only needs
locomotion. Movement (WASD controller, free camera) is ordinary Unity MonoBehaviour gameplay, not part
of the authored scene.

## How to build it

Build the scene with **CodeScenes** — see the `codescenes-authoring` skill for how to author a Unity
scene in code, and especially the "place and size by relationship" solvers (`FitSize` / `AlignTo` /
`Between`), which are the intended tool for the mixed-scale furniture. Write any gameplay logic (the
character controller, the free camera) as ordinary Unity MonoBehaviours (the skill explains how they fit
with the authored scene).
