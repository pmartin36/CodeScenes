# Completed milestones

Milestone specs move here (from `specs/`) once **both** are true:

1. The milestone's `SceneBuilder.Core.Tests` are green in CI (real, headless behavior tests).
2. The user's Unity confirmation checklist for that milestone passes on a real edit in the Editor.

Completed so far: M0-M6 (01-07), M-Auto and its supporting milestones (14-23), M10 prefab overrides (11),
M-nested-props (24), typed prefab façades (25), the CodeScenes analyzers toolkit (26), typed child
selectors (27), typed asset catalogs (28), multi-scene builders (29), and the live-verify bug-fix pass (30).

Note: 29 and 30 additionally passed live-editor validation via the Unity CLI (`unity-live-verify`), not just
the batchmode gate — 30 fixed a cluster of gate-passing defects that live testing surfaced (see its spec).

Still pending in `specs/`: 08 (M7 robustness, rescoped), 09 (M8 UnityEvents, reframed to typed
method-lambda), 10 (M9 SerializeReference), 12 (M11 animation, blocked on Animator research), 13 (M-UI
RectTransform). `00-foundation.md` stays in `specs/` as the living base contract.
