# needs_research — UI Toolkit (UIElements) support

**Status:** research stub, not a build milestone. CodeScenes' first UI track is **uGUI** (spec 13
RectTransform + spec M8 UnityEvents) because uGUI lives inside the existing GameObject / `GlobalObjectId`
/ `SerializedProperty` sync engine. UI Toolkit is a **wanted future direction** — it is materially more
convenient/code-native than uGUI — but it is architecturally disjoint from every pillar of the current
engine, so it needs its own design before it can be a milestone.

## Why we want it
UI Toolkit is where Unity is investing, and it is far more ergonomic than uGUI: UI as UXML markup + USS
stylesheets + C#, retained-mode, web-like. It is a strong long-term fit for a *code-native* product.
Unity 6 steers new projects toward it for multi-resolution menus/HUD, and (as of Unity **6.2**) it gained
shipped **world-space** runtime rendering.

## Why it's parked (the architectural gap — from the 2026-07-21 UI research)
Every pillar the sync engine is built on is absent in UI Toolkit:
- **No GameObjects / no `GlobalObjectId`.** The UI is a *virtual* `VisualElement` tree in a `Panel`, not
  the scene hierarchy; a `UXML` file "should not be treated as a GameObject." Identity — the linchpin of
  §4 — does not exist for VisualElements. Sources: Unity Manual *Migrate from uGUI to UI Toolkit*
  (`docs.unity3d.com/6000.3/…/UIE-Transitioning-From-UGUI.html`), *UI Toolkit at runtime* blog
  (`unity.com/blog/engine-platform/ui-toolkit-at-runtime-get-the-breakdown`).
- **The layout is external text assets** (UXML + USS) referenced by a thin `UIDocument` component — a
  **separate round-trip surface** disjoint from the scene-object diff engine, not `SerializedProperty` data.
- **No serialized/persistent event surface at all — the killer for the value prop.** UI Toolkit
  interaction is imperative C# only (`RegisterCallback<ClickEvent>`, `clicked +=`, `Clickable`). Unity,
  verbatim: *"Callbacks can no longer be set up directly on GameObjects or stored in Prefabs. You must set
  up all callbacks at runtime, and handle them via scripting."* There is **nothing serialized to
  round-trip** for wiring (runtime data binding binds value properties, not click callbacks; the
  `SerializedObject` binding path is Editor-UI-only). Sources: *Migrate from uGUI* manual, *Click and
  pointer events* manual (`docs.unity3d.com/Manual/UIE-Click-Events.html`), runtime-binding manual
  (`docs.unity3d.com/6000.2/…/UIE-runtime-binding.html`).
- **World-space caveats:** shipped only in **6.2** (not the 6.0 baseline), and world-space UI is **not
  interactive without manually adding a physics collider** to the `UIDocument` GameObject. Sources: *How
  to Create World Space UI with UI Toolkit in Unity 6.2* (`unity.com/resources/how-to-create-world-space-ui-toolkit`),
  world-space-ui manual (`docs.unity3d.com/6000.4/…/ui-systems/world-space-ui.html`).

## The promising CodeScenes-shaped path (worth prototyping)
Because CodeScenes' thesis is *code is truth*, the missing serialized-wiring surface may be a fit, not a
blocker: instead of round-tripping serialized events, **generate a companion controller `MonoBehaviour`**
that queries elements (`UIDocument.rootVisualElement.Q<Button>("…")`) and registers callbacks in `C#` —
authored as ordinary serialized C# in the builder file. The wiring then lives *in code*, exactly where the
product wants it. This sidesteps the "no serialized event" problem entirely — but it means the UI *content*
(UXML/USS) and the UI *wiring* (generated MonoBehaviour) are two different round-trip surfaces to reconcile.

## Open questions to resolve before promotion
1. **Identity for VisualElements.** What replaces `GlobalObjectId`? Element `name`/USS selector paths are
   the natural anchor, but they have the same rename/duplicate hazard as tier-3 LogicalIds (cf. spec 16).
   Is a UXML text-diff + `UIDocument`-reference round-trip feasible *alongside* the GameObject engine, or
   does it require a parallel sync subsystem?
2. **Round-trip surface.** UXML is XML, not the recognized flat-C# builder shape (§6). Does CodeScenes
   author UXML as text, or author VisualElements in C# and treat UXML as generated output? Which side is
   truth?
3. **Wiring.** Validate the generated-controller-MonoBehaviour approach end-to-end (query + RegisterCallback
   as serialized C#). Is that in the round-trip engine's scope or a separate feature?
4. **Roadmap watch.** Does Unity ship a native serialized/persistent event mechanism for UI Toolkit (the
   gap third-party `EventBinder`-style assets fill)? That would change the wiring calculus.
5. **World-space interaction stability** across 6.2 → 6.5+ (manual collider vs auto-generated) before any
   sync integration depends on it.

## Related
Downstream of the uGUI UI track (spec 13, spec M8) proving the UI round-trip shape. Reuses the identity
model of [[identity-architecture]] / spec 16 for any element-addressing scheme. Full UI research verdict
recorded in the 2026-07-21 deep-research report.
