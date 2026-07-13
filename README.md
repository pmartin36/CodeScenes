# SceneBuilder

> **Build your Unity scenes in code. Edit them in the editor. Keep both in sync — automatically.**

Define a Unity scene as a plain C# file. Hit **Build** and it constructs the real scene. Then move
things around in the Unity editor, and your code rewrites itself to match. It's a two-way street
between your code and your scene — and it's what finally lets AI assistants build Unity scenes *well*.

> 🚧 **Early development.** The design is locked and the engine is being built test-first. Not usable
> yet — ⭐ the repo to follow along. See [Status](#status).

---

## Why you'd want this

If you've asked an AI assistant to build a Unity scene, you know the pain: it fumbles through the
editor one click at a time, loses the thread, and leaves you nothing clean to review. And if you've
ever put a `.unity` file in git, you know the *other* pain — unreadable diffs and merge conflicts you'd
never wish on a teammate.

SceneBuilder fixes both by making your scene **code**:

- 🤖 **AI builds scenes the way it's actually good at — writing code.** Your whole scene fits in the
  model's context as one readable file it can generate, refactor, and sanity-check. No more clicking
  around the editor on your behalf.
- 🔁 **True two-way sync.** Change the code → the scene updates. Drag something in the editor → the code
  updates. You're never trapped on one side.
- 🌳 **Scenes you can diff, review, and merge.** Your scene is clean C# in git — real pull-request
  diffs, not a wall of YAML.
- 🎛️ **You keep the editor.** This isn't "give up Unity's editor." Lay things out visually when that's
  faster; the code just follows along.

---

## What it looks like

Write a scene:

```csharp
public class MainMenu : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var player = scene.Add("Player").Transform(pos: (0, 1, 0));
        player.Component<Rigidbody>(rb => rb.Set(r => r.mass, 5f));

        var door = scene.Add("Door").Transform(pos: (4, 0, 0));
        scene.Add("OpenButton").Component<Button>(b => b.OnClick(door, nameof(Door.Open)));
    }
}
```

Hit **SceneBuilder ▸ Build** — the scene appears in Unity, wired up and ready.

Now drag `Player` somewhere in the Scene view and save. Your file updates itself:

```diff
-        var player = scene.Add("Player").Transform(pos: (0, 1, 0));
+        var player = scene.Add("Player").Transform(pos: (2.5f, 1, -3));
```

That's the whole idea: **code ⇄ scene, always in agreement.**

---

## Getting started

> ⚠️ Not usable yet — this is the intended flow for the first release.

1. **Install** — in Unity, open *Package Manager ▸ Add package from git URL* and paste:
   ```
   https://github.com/pmartin36/UnitySceneManager.git
   ```
2. **Write a scene** — create `Assets/Scenes/MainMenu.cs`:
   ```csharp
   public class MainMenu : ISceneDefinition
   {
       public void Build(SceneRoot scene) => scene.Add("Hello").Transform(pos: (0, 1, 0));
   }
   ```
3. **See it appear** — click **Build** in the SceneBuilder panel and your scene materializes. Prefer
   hands-off? Toggle on **Auto-build** and it rebuilds whenever you save the file.
4. **Edit either side** — tweak the code and rebuild, or move things in the editor and watch the file
   update. Point your AI assistant at the `.cs` file and let it go.

Prefer to see it run first? Import the **Round-Trip Demo** sample from the package
(*Package Manager ▸ SceneBuilder ▸ Samples ▸ Import*) and follow its README.

---

## How it works

Your builder file is the source of truth. SceneBuilder compares what your code describes against
what's actually in the scene and reconciles the difference — in whichever direction you changed. Every
object gets a stable identity, so an edit in the editor updates the *right* line of code, and a
rebuild never wipes and recreates your scene. Flat, AI-generated code round-trips perfectly; keep
loops and helpers for procedural bits and those stay code-driven.

## Status

Active, early development — designed spec-first, then built test-first.

| | |
|---|---|
| ✅ **Design** | Complete — see [`specs/`](specs/); `specs/00-foundation.md` is the contract. |
| 🔨 **Engine** (`SceneBuilder.Core`) | Building now, behind a real test gate. |
| ⏭️ **Unity editor plugin** | Next up. |
| 🔮 **Then** | Components & fields, asset & cross-object references, prefabs, UnityEvents, animation. |

⭐ Star the repo to follow along.

---

## Contributing / poking around

The engine is plain .NET with zero Unity dependency, so it runs fully headless:

```sh
dotnet test SceneBuilder.sln
```

Everything — architecture and every milestone — is written up in [`specs/`](specs/). Start with
`specs/00-foundation.md`.

## License

TBD.

<sub>Repo is currently named `UnitySceneManager`; the plugin is `SceneBuilder`. Both will be renamed
once the name settles.</sub>
