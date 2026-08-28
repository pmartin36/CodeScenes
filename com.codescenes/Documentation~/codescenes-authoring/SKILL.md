---
name: codescenes-authoring
description: Author or modify a Unity scene (or prefab) in code with CodeScenes. Write a C# builder implementing ISceneDefinition; CodeScenes parses the source (it never runs it) and materializes the described scene into the live editor, keeping code and scene in two-way sync. Use whenever you need to build or edit a Unity scene programmatically instead of clicking in the editor.
---

# Authoring Unity scenes with CodeScenes

CodeScenes turns a Unity scene into one flat, diffable C# file. You write the file; CodeScenes builds
the scene from it and keeps the two in sync both ways.

## The one rule that governs everything

CodeScenes **parses the builder's source text — it never executes your `Build` method.** So the body
must be plain, declarative authoring calls (the API below), not real logic. No loops that compute
values, no `if` branches, no reading files, no `new`-ing Unity objects. Think of `Build` as a
description of the scene, written as method calls, that a parser reads.

Consequence: values are literals. `Transform(pos: (1f, 2f, 3f))`, not `Transform(pos: Compute())`.

## Place and size by relationship — do NOT hand-measure

Before you reach for the editor to measure an asset's bounds or compute coordinates: usually you
don't have to. CodeScenes has editor-time solvers that position and size objects by their
*relationship* to others, and they re-solve when things move. Prefer these over measured numbers:

- **`.AlignTo(target, y: AxisAlign.AbutMax)`** — rest or align an object against another object's face
  (rest one object on another's surface, or sit it flush against a face). No computing a Y from the
  mesh's bounds. Each axis
  (`x`/`y`/`z`) takes an `AxisAlign` preset — `AbutMin`/`AbutMax` (flush outside, on the target's min/max
  side), `AlignMin`/`AlignMax` (near/far faces coincide), or `AlignCenter` — with an optional
  `.Offset(f)`.
- **`.FitSize(width: 2f)`** — make an object an exact **world** size regardless of the mesh's native
  dimensions, rotation, or parent scale. No computing a scale factor. Pass any of `width/height/depth`.
- **`.Between(from, to, fraction, Between.Axis.X)`** — position an object along ONE axis at a fraction
  between two anchor objects (`0` = against `from`, `1` = against `to`, unclamped). Use it to position one
  object along a line, and to *spread several* objects evenly: N objects at fractions like
  `0.2f/0.4f/0.6f/0.8f`, one call each, not N guessed coordinates. `AlignCenter` would stack them all at
  the same centre; `Between` is what spaces them out.

So instead of "measure the tile, then place the next at x+2.4", snap and fit and fit-between. Placement
is a per-axis decision, and each axis can be driven relative to another object: rest on a surface with
`AlignTo(surface, y: AxisAlign.AbutMax)`, sit flush against another object's face with
`AlignTo(other, x: AxisAlign.AbutMax)`, edge-align or centre with `AlignMin`/`AlignMax`/`AlignCenter`,
and spread along an axis with `Between`. One `AlignTo` can drive several axes against a single target in
one call.

If a position is really "relative to that other object," reach for `AlignTo`/`Between` — do NOT write a
literal `Transform(pos: ...)` for it. A guessed coordinate ignores the object's own size (after
`FitSize`) and the thing it should sit relative to, so it drifts or overlaps; that is the bug the solvers
exist to prevent. Reserve `Transform(pos:)` for deliberate absolute anchors, never for an object placed
relative to another.

## Where the file goes

- Builders live in **`<ProjectRoot>/SceneBuilders/<Name>.cs`** — a folder next to `Assets/`, NOT
  under `Assets/` (a `.cs` under `Assets/` would trigger a domain reload on every keystroke).
- The class is `public class <Name> : ISceneDefinition` with one method, `public void Build(SceneRoot scene)`.
- Start the file with:
  ```csharp
  using UnityEngine;                                  // short component names (MeshFilter, Rigidbody)
  using SceneBuilder.Authoring;
  using static SceneBuilder.Authoring.AssetRefs;      // Asset(...) and Builtin(...) unqualified
  ```

The scene it governs is created/updated on build. When you change the file, the scene updates; when
you edit the scene in the editor, the file updates. You do not press a button — sync is automatic.

### Which scene a builder drives (and targeting one that already exists)

A builder is matched to a scene by its filename plus a scene path recorded in a sidecar file next to it,
`SceneBuilders/<Name>.sbmap.json`. By default a builder `<Name>.cs` drives a scene at
`Assets/SceneBuilder/<Name>.unity`, creating it on the first build if it does not exist. You normally
never touch the sidecar — CodeScenes writes and maintains it.

To drive a scene that **already exists** at another path (one you built by hand, or anywhere under
`Assets/`), point CodeScenes at it before the first build by creating the sidecar yourself next to the
builder:

```json
// SceneBuilders/<Name>.sbmap.json
{ "Scene": "Assets/<YourFolder>/<YourScene>.unity" }
```

Then open that scene and let sync run (or `CodeScenes > Build > Current Scene`); from then on the builder
drives that scene and CodeScenes fills in the rest of the sidecar.

Adopting an existing scene is **non-destructive**: objects already in the scene that your builder does
not declare are left untouched — CodeScenes only removes an object it created itself and you have since
deleted from the code. You can point a builder at a populated scene and add statements for its objects
incrementally; nothing you have not written about is wiped. (Existing objects are not reverse-imported
into the code — they coexist in the scene alongside what the builder manages.)

## Authoring API — the calls you use inside `Build`

Full reference: **`Packages/com.codescenes/Documentation~/authoring-api.md`** (every type and member).
The common surface:

```csharp
// A GameObject:
var go = scene.Add("Name");                       // returns a NodeHandle
go.Transform(pos: (0f,1f,0f), rot: (0f,90f,0f), scale: (2f,2f,2f));   // any arg optional

// Components — Component<T>() adds it; the closure sets serialized fields by their SERIALIZED name:
scene.Add("Floor")
    .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")))
    .Component<MeshRenderer>(c => c.Set("m_Materials", new[] { Asset("Assets/Materials/Green.mat") }))
    .Component<BoxCollider>(c => c.Set("m_Size", new Vector3(10f, 0f, 10f)))
    .Transform(scale: (5f,5f,5f));

// Children nest via NodeHandle.Add:
scene.Add("Parent").Add("Child").Transform(pos: (0f,1f,0f));

// A prefab or model asset dropped in whole (mesh + materials + hierarchy):
scene.Instance("Assets/Kenney/CarKit/sedan.fbx").Transform(pos: (-4f,0f,0f));

// Assets: Builtin(...) for engine resources (meshes: "Cube","Sphere","Plane","Capsule","Cylinder"),
// Asset("Assets/...") for a project asset, Asset("Assets/x.fbx","SubMesh") for a sub-asset.

// Cross-object references: capture one object's component and pass it to another's field:
var target = scene.Add("Target");
scene.Add("Watcher").Component<SomeBehaviour>(c => c.SetRef("m_Target", target.Ref<Transform>()));
```

Component fields use Unity's **serialized names** (`m_Mesh`, `m_Materials`, `m_IsKinematic`,
`m_Size`, `m_SortingOrder`), not the C# property names. When unsure of a field name, check the
component in the Inspector's Debug mode or the authoring-api.md notes.

Other `NodeHandle` calls: `.RectTransform(...)` (UI), `.Tag`, `.Layer`, `.Active(false)`,
`.Static(true)` (the spatial solvers `.FitSize` / `.AlignTo` / `.Between` are covered above).
`ComponentHandle` also has `.OnClick(...)` / `.OnEvent(...)` for wiring UnityEvents. See the reference.

## A complete builder

```csharp
using UnityEngine;
using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;

public class Playground : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var cam = scene.Add("Main Camera").Transform(pos: (0f, 3f, -8f), rot: (20f, 0f, 0f));
        cam.Component<Camera>();
        cam.Component<AudioListener>();

        scene.Add("Sun").Transform(rot: (50f, -30f, 0f)).Component<Light>(c => c.Set("m_Type", 1));

        scene.Add("Floor")
            .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")))
            .Component<MeshRenderer>()
            .Transform(scale: (5f, 5f, 5f));

        scene.Add("Ball")
            .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Sphere")))
            .Component<MeshRenderer>()
            .Component<Rigidbody>()
            .Transform(pos: (0f, 3f, 0f));
    }
}
```

## Your own components and game logic

CodeScenes authors the *scene*, not runtime behavior. For anything that has to run, gameplay, input,
scoring, write ordinary Unity **MonoBehaviours** in `Assets/Scripts/` (normal C#, real logic, `Update`,
etc.). Then attach them in the builder exactly like a built-in component and set their serialized
fields:

```csharp
// Assets/Scripts/BallController.cs is a normal MonoBehaviour with public/[SerializeField] fields.
scene.Add("Ball")
    .Component<Rigidbody>()
    .Component<BallController>(c => c.Set("power", 12f));   // your script, your field
```

So the split is: **logic lives in your MonoBehaviours; the builder only places objects, attaches those
components, sets their fields, and wires events.** The builder is parsed (not run), so never put game
logic in `Build` itself.

For a repeated structure (a wall segment used many times, an obstacle), you can author it once as a
code prefab (`IPrefabDefinition`, same authoring API) and instance it, or just repeat the statements.

## Hard rules (violating these is the usual cause of a broken build)

1. `Build` is parsed, not run — declarative calls and literal values only.
2. The builder file is in `SceneBuilders/`, outside `Assets/`.
3. Component field names are Unity serialized names (`m_...`), set via `.Set("m_Field", value)`.
4. Use `Builtin(...)` only for built-in **meshes**; built-in materials render magenta in URP — use a
   project `Asset("Assets/Materials/...")` for materials.
5. Reference another object with `handle.Ref<T>()` + `.SetRef(...)`, never by trying to look it up.

## How to verify a build

After writing/editing the builder, the scene syncs automatically. Confirm by opening the scene and
checking the objects exist as described, and that the Console has no `[CodeScenes]` errors. A parse or
resolve error is reported in the Console with the file and line; fix it in the builder.
