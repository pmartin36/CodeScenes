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
`.Static(true)`, and the spatial helpers `.FitSize`, `.SurfaceSnap`, `.Between`. `ComponentHandle`
also has `.OnClick(...)` / `.OnEvent(...)` for wiring UnityEvents. See the reference.

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
