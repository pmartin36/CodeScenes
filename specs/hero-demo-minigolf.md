# Minigolf

Build a small, polished, genuinely playable minigolf game in Unity.

## The game

Three courses of increasing difficulty — **easy, medium, and hard**:

- Each is a minigolf hole: the player putts a ball from a tee around the course and into the hole.
- The courses have **obstacles** (blocks, bumps, a windmill or castle, tunnels — pick what suits each
  difficulty).
- At least one course has an **incline** (a ramp or a hill).
- It must actually play: physics-driven putting, the ball kept on the course, sinking it in the hole
  completes the hole, and the player progresses easy → medium → hard.

Everything else is your call — the putt mechanic, camera, UI, art direction, scoring, audio, how the
courses are laid out, and how the player moves between them. Make it feel good and look good; treat it
as a showcase piece.

## Assets available

Browse these folders and use whatever fits — nothing is assigned to a particular role:

- `Assets/Kenney/Minigolf/Models/FBX format/` — the minigolf kit (126 pieces): tiles, straights,
  corners, ramps, walls, holes, flags, balls, clubs, obstacles (windmill, castle), tunnels.
- `Assets/Kenney/UI/PNG/` — UI sprites (panels, buttons, icons). `Assets/Kenney/UI/Sounds/` — UI
  click/tap sounds. `Assets/Kenney/UI/Font/` — a font.
- `Assets/Audio/` — gameplay sound effects: `putt.wav`, `wall.wav`, `hole.wav`.
- `Assets/Kenney/Nature/`, `Assets/Kenney/Characters/`, `Assets/Kenney/CarKit/` — extra props to
  dress the scene if useful.

## How to build it

Build the scenes with **CodeScenes** — see the `codescenes-authoring` skill for how to author a Unity
scene in code. Write any gameplay logic as ordinary Unity MonoBehaviours (the skill explains how they
fit with the authored scene).
