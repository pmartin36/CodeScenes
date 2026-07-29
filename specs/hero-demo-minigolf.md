# Hero demo — Minigolf

Not a milestone. This is the showcase scene CodeScenes assembles for the launch video and the
marketing site. It is content, not a tool capability: no `verify.sh` gate, no tdd-pipeline run.

The game is **minigolf**. The game design itself (holes, layout, UI, rules, scope) is specified at
build time, not here. This file fixes only the decisions that are already made.

Blocked on spec 13 (RectTransform) and spec 09 (M8 UnityEvents/OnClick) shipping.

## Where it lives

Its own builder file, `MinigolfScene.cs`, and its own scene. Spec 29 routes every `ISceneDefinition`
to its own scene independently, so `DemoScene.cs` is untouched. Scene path comes from the sidecar's
`IdentityMap.Scene`, defaulting to `Assets/SceneBuilder/MinigolfScene.unity`.

Gameplay logic is ordinary MonoBehaviours under `Assets/Scripts/`. CodeScenes authors the scene,
places the props, attaches the components, and wires the buttons.

## Assets

Kenney, CC0, verified on kenney.nl:

- [Minigolf Kit](https://kenney.nl/assets/minigolf-kit), 125 assets — the course
- [UI Pack](https://kenney.nl/assets/ui-pack), 430 sprites — panel and button sprites

Both need downloading into the manual test project (`../Unity/SceneBuilderTest`, `Assets/Kenney/`).
Only Car Kit, Characters and Nature are staged today.

Kenney FBX materials are generated on import against the active pipeline, so they render correctly in
URP. CodeScenes-authored primitives still need a URP `.mat` via the asset catalog; `Builtin(...)` is
for built-in meshes only, never materials.

## Tool-state constraints on whatever gets specified later

- **No prefab authoring from code.** `scene.Instance(...)` instances existing prefab/FBX assets
  (spec 07) with root and nested overrides (specs 11/24/25/27). Defining a new prefab asset from code
  does not exist (`specs/needs_research/prefab-authoring.md` is a research stub). Repeated structures
  are authored inline, or toggled with `.Active(bool)` (`com.codescenes/Runtime/NodeHandle.cs:70`).
- **No skeletal animation.** `specs/12-m11-animation-easing.md` is blocked on Animator research, so
  nothing CodeScenes authors drives an animated rig.
- **Flat statement list.** The builder is parsed from source text, not executed. A scene of many
  repeated tiles becomes many repeated statements, so large tiled layouts read poorly on camera.
- **One active scene at a time.** Concurrent sync of several open, dirty scenes is deferred
  (`specs/needs_research/concurrent-multi-scene-sync.md`).
- Runtime `SetActive` cannot churn the builder: auto-sync is play-mode guarded at
  `com.codescenes/Editor/SceneBuilderAutoSync.cs:100`.
