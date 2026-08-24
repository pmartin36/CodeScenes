# Hero demo — Minigolf

Not a tool milestone. This is the showcase scene CodeScenes builds for the launch video and the
marketing site. It is content: no `verify.sh` gate, no tdd-pipeline run.

**The point of the demo.** Show that an AI agent, given this brief and the CodeScenes skill, can
author a playable Unity scene *in code*, laying out a course, placing props, setting up physics,
camera, lighting, and UI, and wiring gameplay, while CodeScenes keeps the C# and the live editor
scene in two-way sync.

## What to build

A genuinely playable game of **minigolf** with **three courses of increasing difficulty: easy,
medium, and hard.** The fixed requirements are only these:

- Three courses/holes, easy → medium → hard.
- The courses have **obstacles** (the kit has plenty: blocks, diamonds, bumps, windmill, castle,
  tunnels).
- At least one course has an **incline** (the kit has ramps and hills).
- It actually plays in Play mode: physics-driven putting, the ball kept on the course, reaching the
  hole completes it.

Everything else is yours to decide — run wild. The putt mechanic, camera, UI, art direction, how the
three courses are laid out and how the player moves between them, difficulty tuning, scoring, audio.
Make it feel good and look good on camera; it is the launch showcase.

## What CodeScenes authors vs. what you hand-write

- **CodeScenes authors the SCENE**, in `MinigolfScene.cs` (an `ISceneDefinition`): the course layout,
  the ball, flag, and hole, the camera and lights, and the UI (canvas, panels, buttons, the stroke
  text). It attaches components to those objects, sets their serialized fields, and wires UnityEvents
  such as the restart button's OnClick. Repeated structure can be authored inline, toggled with
  `.Active(...)`, or, if it helps, defined once as a code prefab (`IPrefabDefinition`) and instanced —
  authoring prefabs from code is supported.
- **Gameplay logic is ordinary MonoBehaviours** under `Assets/Scripts/` (for example a putt
  controller, a hole trigger, a small game manager). CodeScenes does not author game logic; it
  attaches these components to the authored objects and sets their fields. The builder is parsed from
  its source text, never executed, so every bit of runtime behavior lives in these hand-written
  scripts, not in the builder.

## Assets available to you

Browse these folders and choose what fits — nothing here is assigned to a specific role:

- `Assets/Kenney/Minigolf/Models/FBX format/` — the minigolf kit (126 pieces): course tiles, straights,
  corners, ramps, walls, holes, flags, balls, clubs/putters, obstacles (windmill, castle), tunnels.
- `Assets/Kenney/UI/PNG/` — UI sprites (panels, buttons, icons). `Assets/Kenney/UI/Sounds/` — UI
  click/tap sounds. `Assets/Kenney/UI/Font/` — a font.
- `Assets/Audio/` — gameplay SFX: `putt.wav`, `wall.wav`, `hole.wav` (rudimentary placeholders).
- `Assets/Kenney/Nature/`, `Assets/Kenney/Characters/`, `Assets/Kenney/CarKit/` — extra props if they
  help dress the scene.

Kenney FBX materials are generated on import against the active render pipeline, so they render
correctly. For any primitive you author yourself, assign a project `.mat` — `Builtin(...)` is for
built-in meshes only, never materials.

## Where it lives

Its own builder `MinigolfScene.cs` in `SceneBuilders/`, and its own scene. Each `ISceneDefinition`
routes to its own scene independently, so nothing else is disturbed.

## Real constraints to work within

- The builder is parsed, not run — declarative authoring calls and literal values only; put all logic
  in the MonoBehaviours.
- A large tiled layout becomes many repeated statements in the builder, so it reads better on camera
  as a compact, deliberately composed hole than as a huge grid.
- Component fields are set by their Unity serialized names (`m_Mass`, `m_IsKinematic`, ...) via
  `.Set(...)`, and cross-object references via `handle.Ref<T>()` + `.SetRef(...)`.
