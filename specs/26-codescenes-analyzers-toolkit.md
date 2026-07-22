# M-Analyzers — CodeScenes.Analyzers toolkit (compiler-enforced authoring safety + typed catalogs)

> **Why this milestone exists — it is the direct embodiment of the product's governing principle.**
> CodeScenes' whole point is that every authoring surface is optimized so that **(1)** an LLM can write
> and understand it easily, and **(2)** if the LLM misunderstands, the **compiler catches the mistake
> and guides the fix** (foundation §1, §6, §7; spec 22's opening; spec 25's opening). This milestone is
> the machinery that turns "the compiler catches it" from an aspiration into an enforced invariant. It
> ships a Roslyn **analyzer** (surfaces authoring mistakes as compile diagnostics) and a Roslyn
> **source generator** (turns magic-string surfaces into compiler-checked typed member chains) as one
> package, and it wires both into the green gate so a mistake cannot ship silently.
>
> **Two delivery channels for every diagnostic — the second is the load-bearing one for LLM codegen:**
> 1. **Live IDE red-squiggles** (for humans) — the analyzer runs in the IDE's compilation of the
>    injected builder csproj (§12 `BuilderProjectInjector`), so a mistake underlines as-you-type.
> 2. **The green gate (`./verify.sh`) failing on analyzer diagnostics** (for LLMs) — the product's
>    authors are LLMs, which do not read squiggles; they run the gate. Making the gate **fail** on an
>    analyzer diagnostic is what *forces* the authoring model to fix it. An LLM's loop is write → run
>    the gate → read the located, coded diagnostic → fix → repeat, exactly as spec 22 closed the
>    planning-error loop. This channel is why the analyzer is gated headlessly (§"Gate integration"),
>    not just offered as IDE polish.
>
> **This is now a HIGH-PRIORITY foundational milestone** — the project owner has ranked this
> compiler-safety work above the remaining UI milestones. It builds **before** typed-prefab-façades
> (spec 25), which is **reframed** as a source generator built ON this toolkit's generator framework
> (§"How spec 25 reframes onto this framework").

---

## Additions to the contract

This milestone adds **one package**, **one shared Core library**, **one on-disk manifest format**, and
**two generated catalog shapes** to the foundation contract. Each is flagged here per foundation §1.
No existing POCO (`SceneModel`, `ValueNode`, `PlanOp`, `IdentityMap`) changes shape. The analyzer and
generator are **new consumers** of the existing parse surface plus a shared recognizer; they add no
`SceneModel`/`Plan`/sidecar change.

| Added | Shape (summary) | Owner |
|---|---|---|
| **`CodeScenes.Analyzers` package** | one Roslyn package (analyzer + source generator) shipped inside `com.codescenes/`, referenced by the INJECTED builder csproj only — Unity never compiles builders, so the analyzer/generator run in the IDE's compilation of the injected project and in the headless gate | this spec |
| **`SceneBuilder.Grammar` (shared recognizer)** | a **netstandard2.0** class library holding `FlatShapeRecognizer` — the single source of truth for "what is a recognized flat builder construct" (foundation §6), referenced by BOTH Core (`BuilderParser`) and the analyzer, so the two cannot drift | this spec |
| **`FlatShapeRecognizer.Analyze(...)` + `ShapeViolation`** | pure function over Roslyn syntax: given the `Build` body, returns the located set of flat-shape violations; `ShapeViolation { SourceSpan Span; string Code; string Message }` | this spec |
| **Project-state manifest** (`ProjectCatalog.sbcatalog.json`) | an editor-generated JSON manifest under `<ProjectRoot>/SceneBuilders/Generated/`, fed to the source generator as an `AdditionalFile`; v1 carries Tags + Layers | this spec |
| **Generated `Tags` catalog** | `public static class Tags { public const string Player = "Player"; … }` — emitted INTO the compilation by the generator; makes `.Tag(Tags.Player)` compiler-checked | this spec |
| **Generated `Layers` catalog** | `public static class Layers { public const int Ground = 6; … public static class Name { public const string Ground = "Ground"; } }` — emitted into the compilation; makes `.Layer(Layers.Ground)` compiler-checked | this spec |
| **Diagnostic ID registry `SB1xxx`** | the stable analyzer diagnostic codes (table in §"Core deliverables"), disjoint from spec 22's planning-validator `SB2xxx` codes | this spec |

The invariant "**generated C# must compile**" (foundation, spec 22) is **strengthened** by this
milestone to "**generated C# must compile AND be analyzer-clean**" (§"Gate integration", piece 3).

---

## Goal

Make the builder authoring surface **compiler-enforced**: an LLM (or human) that writes a builder the
tool cannot round-trip, or that uses a magic string where a compiler-checked form exists, is told so by
the **compiler** — as a live IDE squiggle and as a **gate failure** — with a stable code, a located
message, and a suggested fix. Concretely, after this milestone:

- A `Build` body that is not the recognized flat shape (a `for`/`if`/`switch`, a helper-method call
  that generates structure, an unrecognized builder call) is an **Error** the analyzer raises at the
  exact offending span — the same "not supported in the round-tripped region" rule the parser already
  enforces (foundation §6), now surfaced *before* Build, from the **shared** recognizer.
- A string escape-hatch (`.Set("m_Mass", 5f)`, `Instance("Assets/Prefabs/Tank.prefab")`,
  `.On("Turret/Barrel", …)`, `.Tag("Player")`, `.Layer("Ground")`) is a **Warning/Info** nudging the
  author to the compiler-checked typed form.
- A UnityEvent listener method whose signature does not fit the event is an **Error** (scaffolded until
  M8's surface lands).
- `.Tag(Tags.Player)` and `.Layer(Layers.Ground)` are **compiler-checked** against the project's real
  tags/layers via generated catalogs — a typo'd tag name **fails to compile**.

## In scope

### Component A — the analyzer (v0 is manifest-free / pure C#, deliberately)

The scariest analyzer failure is a **false positive on valid code from stale project state** — for an
LLM that is worse than no analyzer, because it trains the model to distrust and ignore diagnostics. So
**v0 diagnoses only what the C# semantic model already knows** — it needs **no** project-state manifest:

- **Flat-shape enforcement** (Error, `SB1001`–`SB1003`) — the `Build(SceneRoot)` body must be the
  recognized flat builder shape (foundation §6): no loops/conditionals/`switch`/`throw`, no
  helper-method calls that generate structure, no arbitrary interleaved logic; builder chains use only
  recognized calls; component closures contain only `.Set(...)`. **The analyzer's notion of "recognized
  construct" is the SAME code Core's `BuilderParser` recognizer runs** — see §"Core deliverables /
  The shared recognizer".
- **String-escape-hatch → typed-form nudges** (Warning/Info, `SB1101`–`SB1106`) — pure pattern-match on
  the call shape, no project state:
  - `.Set("m_X", …)` → suggest `.Set(x => x.member, …)` (typed member lambda; M3 already ships this form).
  - `Instance("Assets/…/X.prefab")` → suggest `Instance(Prefabs.X)` (spec 25's generated catalog).
  - `.On("Turret/Barrel", …)` → suggest `.On(sel => sel.Turret.Barrel, …)` (spec 24/25 typed selector).
  - `.Tag("str")` → suggest `Tags.<str>` (this milestone's generated catalog).
  - `.Layer("str")` / `.Layer(<intLiteral>)` → suggest `Layers.<name>` (this milestone's catalog).
  - untyped `.Set("path", value)` overload → suggest the typed `Set<T>(Expr<Func<C,T>>, T)` overload so
    the compiler catches value-type mismatches.
- **UnityEvent signature compatibility** (Error/Warning, `SB1201`/`SB1202`) — for the M8 wiring shape
  (prefer the typed method-lambda `.OnClick(door, d => d.Open())`), verify the referenced method's
  signature fits the event's expected arity/parameter types. Both the method and the event type are in
  the C# type system — **no manifest**. **Present-but-scaffolded:** the diagnostic and its tests are
  written now against the intended M8 surface but are **inert until that surface lands** (registered,
  guarded so it produces zero diagnostics against today's sources); this milestone does **not** depend
  on M8 and does not block on it.
- **Severity discipline (a rule, not a case-by-case choice):** **Error** ONLY for pure-C# facts that are
  **always** safe to flag — a flat-shape violation and an incompatible UnityEvent signature. Everything
  else (the typed-form nudges) is **Warning or Info**. **Precision over recall — UNDER-flag rather than
  over-flag.** Every diagnostic has a **stable ID**, a message, and a suggested fix. A diagnostic whose
  correctness would depend on project state that could be stale is **out of v0** (below).

### Component B — the source-generator framework + first catalogs (Tags & Layers)

A Roslyn **source generator** (in the same package) that emits typed catalogs **into the compilation**
(no written `.cs` files to manage) from an editor-generated **project-state manifest** (an
`AdditionalFile`):

- **The manifest** — an **editor-side** generator (which has `AssetDatabase` / `TagManager` / Unity
  reflection) writes a project-state manifest to `<ProjectRoot>/SceneBuilders/Generated/` and
  **regenerates it on project change**. Because the file lives **outside `Assets/`**, writing it is
  plain `File` IO and triggers **no domain reload** (foundation §2, §12).
- **First catalogs: `Tags` and `Layers`** — simple flat lists read from `ProjectSettings` (TagManager).
  They prove the generator framework and immediately make `.Tag(Tags.Player)` / `.Layer(Layers.Ground)`
  **compiler-checked** — a typo'd tag/layer becomes a **compile error**, not a runtime surprise.
- **Freshness fails SAFE for the generator** (this is precisely why the *generator* may use the manifest
  while the *analyzer* may not): a removed tag/layer's stale accessor resolves to nothing → an ordinary
  **compile error** at the reference site (safe: the author is told); a newly-added tag/layer is simply
  **missing until the next regen** (safe: nothing breaks, the author regenerates). Neither failure mode
  is a silent false positive on valid code — unlike a manifest-backed *analyzer*, which could flag valid
  code as bad from stale state.

## Out of scope

- **Any analyzer check that needs a project-state manifest** — "does this tag/asset/serialized-path
  exist" as an **analyzer diagnostic**. Those are handled either by **making the catalog a type** (the
  generator turns "does this tag exist" into a compile error via `Tags.X`, no analyzer needed) or are
  **deferred** to a later manifest-backed analyzer layer (`needs_research`). v0's analyzer is
  manifest-free by design (see §"Risks/notes / False-positive precision").
- **Catalogs other than Tags & Layers.** Prefab façades (`Prefabs.X`, `.On(sel => …)`) are spec 25 built
  ON this framework, not this milestone. Assets, sorting layers, input axes, scenes, etc. are future
  catalogs on the same framework.
- **Serialized-property existence for the raw `.Set("m_Mesh", …)` string form** — that needs a live
  `SerializedObject` (Unity). v0 **nudges** off the string form (`SB1101`) but does not verify the path
  exists; spec 22's headless validator and the editor Build own existence.
- **Built-in / sub-asset name existence** (`Builtin("Cube")`, `Asset(path, sub)`) — spec 22's honest
  boundary; unchanged here.
- **IMGUI / editor-window UI, a settings panel, any toggle.** The analyzer/generator run automatically;
  there is no button (foundation "seamless, non-user-driven" contract).
- **Auto-fix providers (`CodeFixProvider`).** v0 ships diagnostics with a textual `Suggestion` only;
  IDE quick-fixes are a later refinement (`needs_research`).

## Core deliverables

`SceneBuilder.Core` and the new shared library are Unity-free; they hold the model and the recognizer,
and resolve nothing Unity-specific. Each behavior below is a testable contract (§"Core test plan").

### The shared recognizer (single source of truth — anti-drift)

**CRITICAL DESIGN CONSTRAINT.** The analyzer's notion of "supported construct" MUST be the **same code**
Core's `BuilderParser` uses to recognize the flat shape. Re-implementing the grammar in the analyzer
would guarantee drift → false positives (analyzer rejects what Build accepts) or false negatives
(analyzer accepts what Build rejects). Both are forbidden (mirrors spec 22's consistency contract).

- **`SceneBuilder.Grammar` — a new `netstandard2.0` class library** holding `FlatShapeRecognizer`. It
  references `Microsoft.CodeAnalysis.CSharp` and nothing else. Its public API is expressed purely in
  Roslyn syntax types:
  ```csharp
  public static class FlatShapeRecognizer {
      // Given the Build method body, return every construct that is NOT the recognized flat shape,
      // located. Empty result == the body is a valid flat builder. Never throws.
      public static IReadOnlyList<ShapeViolation> Analyze(BlockSyntax buildBody, string sceneParamName);
  }
  public readonly struct ShapeViolation { public SourceSpan Span; public string Code; public string Message; }
  ```
  It encodes exactly today's `BuilderParser` recognition decisions — the `default: throw Fail("Unsupported
  interleaved control flow …")` in `ProcessStatement`, the `default: throw Fail("Unsupported builder call
  '.{method}(...)'")` whitelist in `ApplyChainedCalls` (`Transform/Tag/Layer/Active/Static/Id/Component/
  FitSize/SurfaceSnap`), the `"Expected a .Set(...) call in component closure"` rule, the `Add`/`Instance`
  chain shapes, and the lambda-body forms — **as returned violations instead of throws**.
- **`BuilderParser` is refactored to DERIVE its recognition from `FlatShapeRecognizer`.** Core's parser
  stops being an independent second copy of the grammar: its "is this a recognized construct" gate calls
  the shared recognizer (running it up front and failing loud on the first violation, preserving today's
  fail-located behavior), so the grammar exists in exactly **one** place. This is the inherit-by-default
  design (global CLAUDE.md): agreement is structural, not two hand-synced code paths.

**The netstandard2.0 vs 2.1 wrinkle (address explicitly).** A Roslyn **analyzer** must target
**`netstandard2.0`** and bind against the **host's** Roslyn version (the version the IDE/compiler
provides — the analyzer references `Microsoft.CodeAnalysis` at a pinned *floor* version and consumes the
host's actual assembly at load time). **Core targets `netstandard2.1`.** A `netstandard2.1` recognizer
cannot be loaded by a `netstandard2.0` analyzer host. Therefore the shared recognizer **must live in a
`netstandard2.0` library both can reference**:

- `SceneBuilder.Grammar` targets **`netstandard2.0`**. Core (ns2.1) references it (ns2.1 can consume an
  ns2.0 assembly). The analyzer (ns2.0) references it and ships it **alongside** the analyzer dll (as a
  Roslyn analyzer dependency), so both load the same recognizer assembly.
- The Roslyn reference in `SceneBuilder.Grammar` is pinned to a **conservative floor version**
  (compatible with the oldest supported IDE Roslyn and the gate's `dotnet` SDK Roslyn) and marked
  compile-time/provided so it is not duplicated into the host.
- **Fallback if a single shared assembly proves impractical** (Roslyn-version binding friction): mirror
  the recognizer **source** into the analyzer project via linked `<Compile Include="../SceneBuilder.Grammar/
  FlatShapeRecognizer.cs" />` so both compile the identical source. A source-link mirror is second choice
  (it recompiles rather than reuses) but still one source of truth. The spec's requirement is **one
  source, zero re-implementation** — the shared-assembly form is preferred.

### The analyzer (`DiagnosticAnalyzer`) — each diagnostic a testable contract

A single `DiagnosticAnalyzer` (in the `CodeScenes.Analyzers` package) registering the diagnostics below.
It operates ONLY on the C# semantic model + syntax (no manifest). Diagnostic IDs are **stable**:

| ID | Severity | Fires when | Suggested fix |
|---|---|---|---|
| `SB1001` | **Error** | The `Build` body contains an unsupported statement / control flow (loop, `if`/`switch`, `throw`, `return`, a local-function/helper call that generates structure) | "The Build body must be a flat sequence of builder calls (foundation §6). Move logic out; author the resulting objects directly." |
| `SB1002` | **Error** | A builder chain uses a call NOT in the recognized set (`ApplyChainedCalls` whitelist) | "`.{name}(…)` is not a recognized builder call. Recognized: `.Transform/.Tag/.Layer/.Active/.Static/.Id/.Component<T>/…`." |
| `SB1003` | **Error** | A component closure contains anything but `.Set(...)` | "A `.Component<T>(c => …)` closure may contain only `c.Set(…)` calls." |
| `SB1101` | **Info** | `.Set("m_X", …)` string serialized-path key | "Use the typed form `.Set(x => x.member, …)` — the compiler checks the member and its type." |
| `SB1102` | **Info** | `Instance("Assets/…/X.prefab")` string prefab path | "Use `Instance(Prefabs.X)` (spec 25) — a stale/typo'd prefab path then fails to compile." |
| `SB1103` | **Info** | `.On("A/B", …)` string internal path | "Use the typed selector `.On(sel => sel.A.B, …)` (spec 24/25)." |
| `SB1104` | **Warning** | `.Tag("str")` string tag literal | "Use `Tags.<str>` — a typo'd tag then fails to compile." |
| `SB1105` | **Warning** | `.Layer("str")` or `.Layer(<intLiteral>)` | "Use `Layers.<name>` — a typo'd/invalid layer then fails to compile." |
| `SB1106` | **Info** | The untyped `.Set(string, object)` overload where a typed `Set<T>` overload exists | "Use `Set<T>(x => x.member, value)` — the compiler catches value-type mismatches." |
| `SB1201` | **Error** | (scaffolded, M8) A UnityEvent listener method's signature is incompatible with the event's arity/parameter types | "Method `{m}` does not match `{Event}`'s signature; expected `{sig}`." |
| `SB1202` | **Warning** | (scaffolded, M8) A UnityEvent method-lambda targets a non-eligible method (non-public / static mismatch) | "Persistent-listener targets must be public instance/static methods matching the event." |

- **`SB1001`–`SB1003`** delegate to `FlatShapeRecognizer.Analyze` — the analyzer reports each returned
  `ShapeViolation` as a diagnostic at `violation.Span`, using the violation's `Code`. **Zero grammar
  logic in the analyzer.**
- **`SB1101`–`SB1106`** are pure syntactic call-shape matches on the invocation (argument literal vs
  lambda), no semantic project state.
- **`SB1201`/`SB1202`** use the C# type system (the event type's `UnityEvent`/`UnityEvent<T…>` arity and
  the referenced method symbol) — registered now, guarded inert until M8's surface exists.
- **Precision:** an Error diagnostic (`SB100x`, `SB120x`) may fire ONLY on a fact that is always true
  from pure C#. Any check that could be wrong from stale/missing project state is Warning/Info or is a
  generated-type compile error, never an analyzer Error.

### The source-generator framework + Tags/Layers generation — testable contracts

- **`ProjectCatalogManifest` (Core POCO + JSON)** — the deserialized shape of
  `ProjectCatalog.sbcatalog.json`:
  ```
  ProjectCatalogManifest
    SchemaVersion : int
    Tags   : string[]                          // from TagManager, in TagManager order
    Layers : LayerEntry[]                       // { Index:int (0..31), Name:string } for NAMED slots only
  ```
  Lives in `SceneBuilder.Core` (Unity-free); the editor generator writes it, the source generator reads
  it (as an `AdditionalFile`). Serialized via the existing `System.Text.Json` dependency.
- **`CatalogSourceGenerator` (`IIncrementalGenerator`, in the package)** — reads the manifest
  `AdditionalFile`, emits `Tags` and `Layers` **into the compilation** (no disk `.cs`):
  ```csharp
  public static class Tags {
      public const string Untagged = "Untagged";
      public const string Player   = "Player";
      // … one const per project tag; identifiers sanitized + deterministically de-duplicated
  }
  public static class Layers {
      public const int Default = 0;
      public const int Ground  = 6;                 // value = layer INDEX
      // …
      public static class Name { public const string Ground = "Ground"; /* … */ }  // name form
  }
  ```
  - **Deterministic / byte-stable:** identical manifest ⇒ byte-identical generated source, on any
    machine, no timestamps/ordering nondeterminism (mirrors spec 25). Ordered by TagManager order /
    layer index.
  - **Identifier sanitization + de-duplication:** a tag/layer name that is not a valid C# identifier
    (spaces, punctuation, leading digit, C# keyword) is sanitized deterministically; two names that
    sanitize to the same identifier are disambiguated deterministically — never an emit that fails to
    compile or is ambiguous.
  - **Empty/missing manifest fails safe:** no manifest ⇒ generator emits nothing (references to
    `Tags.X`/`Layers.X` then fail to compile with an ordinary CS error, telling the author to regenerate
    — never a silent wrong value).
  - **The framework is reusable:** the generator is structured as a manifest→typed-catalog pipeline so
    spec 25's prefab façades become another catalog on it (§"How spec 25 reframes onto this framework").

## Editor adapter deliverables

All in `com.codescenes/Editor/` unless noted. Thin Unity-side pieces; real behavior covered by EditMode
tests (foundation §8, CLAUDE.md hard requirement).

- **`ProjectCatalogGenerator` (new)** — the editor-side manifest producer. Reads
  `UnityEditorInternal.InternalEditorUtility.tags` / `.layers` (or `TagManager` via
  `AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")`), builds a
  `ProjectCatalogManifest`, and writes `<ProjectRoot>/SceneBuilders/Generated/ProjectCatalog.sbcatalog.json`
  with **plain `File` IO** (never `AssetDatabase` — no domain reload; foundation §12).
- **Regen triggers** — regenerate the manifest when project tags/layers change: on editor load, and on
  a `ProjectSettings/TagManager.asset` change (an `AssetPostprocessor.OnPostprocessAllAssets` watching
  that path, and/or an `EditorApplication` project-changed hook). Idempotent: identical tags/layers ⇒
  identical bytes ⇒ no rewrite (compare-before-write), so the file watcher / IDE is not churned.
- **`BuilderProjectInjector` — extended** (foundation §12; the pure `Inject` core at
  `BuilderProjectInjector.cs:166`). Into the donor `Assembly-CSharp.csproj` ItemGroup, in addition to
  the existing `<Compile Include="…SceneBuilders/*.cs"/>` items, inject:
  - `<Analyzer Include="{package}/Analyzers~/CodeScenes.Analyzers.dll" />` (plus its shipped
    `SceneBuilder.Grammar.dll` dependency), resolved via a new `SceneBuilderPaths` helper that returns
    the package-relative analyzer path.
  - `<AdditionalFiles Include="{ProjectRoot}/SceneBuilders/Generated/ProjectCatalog.sbcatalog.json" />`
    so the source generator receives the manifest in the IDE compilation.
  Idempotent and reference-equality-preserving exactly as the existing `Inject` (return the same
  instance when nothing changes). Only the donor project receives these (same `DonorProjectName` guard).
- **Packaging placement** — the analyzer + grammar dlls ship under **`com.codescenes/Analyzers~/`**. The
  trailing `~` makes Unity **ignore the folder entirely** (like `Samples~`), so the analyzer is **never
  imported as a Unity managed plugin** and never loaded into the editor/player domain — it is a
  compile-time artifact the IDE csproj references by path. **Unity's own RoslynAnalyzer mechanism
  (dlls labeled `RoslynAnalyzer` applied to Unity's `Assets/`-compiled assemblies) deliberately does
  NOT apply here** — builders are not in `Assets/`, so there is no Unity compilation of them to attach
  an analyzer to; the analyzer attaches to the **injected IDE csproj** and the **headless gate**, not to
  Unity's asset-database compilation.
- **Roslyn-version compatibility** — the analyzer targets `netstandard2.0` and pins
  `Microsoft.CodeAnalysis.CSharp` to a conservative floor version usable by BOTH the IDE's
  OmniSharp/Rider Roslyn AND the gate's `dotnet` SDK Roslyn; the Roslyn reference is compile-time only
  (host-provided at load). The package includes a `.props`/`.targets`-free plain-dll layout since it is
  wired by explicit `<Analyzer>` injection, not by NuGet `analyzers/` convention.

## Authoring API added

This milestone adds **no new builder call**. It adds two **generated types** the nudges point to, and
enforcement of existing forms:

- **`Tags.<TagName>` (generated `string` const)** — usable today as `scene.Add("P").Tag(Tags.Player)`.
  The `.Tag(string)` call already exists (`BuilderParser.ApplyChainedCalls` `case "Tag"`); this makes
  its argument compiler-checked. A typo (`Tags.Playr`) does not exist as a const → **compile error**.
- **`Layers.<Name>` (generated `int` const) + `Layers.Name.<Name>` (generated `string` const)** — usable
  as `.Layer(Layers.Ground)`. The `.Layer(int)` call already exists (`case "Layer"`, `(int)EvalFloat`).

The other nudge targets are owned by other milestones and are named here only so the analyzer's
suggestions are coherent: `.Set(x => x.member, …)` (M3, ships today), `Instance(Prefabs.X)` /
`.On(sel => …)` (spec 25 / 24), `.OnClick(t, x => x.Method())` (M8). The analyzer suggests them; it does
not introduce them.

```csharp
// After this milestone, in a builder under <ProjectRoot>/SceneBuilders/DemoScene.cs:
public class DemoScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        scene.Add("Hero").Tag(Tags.Player).Layer(Layers.Ground);   // both compiler-checked
        // scene.Add("Hero").Tag("Playr");                          // SB1104 (warn) → and Tags.Playr won't compile
        // for (int i=0;i<3;i++) scene.Add("Coin"+i);               // SB1001 (error): flat-shape violation
    }
}
```

## IdentityMap / sidecar changes

**None.** The identity sidecar (`*.sbmap.json`, foundation §4) is unchanged. The project-state
**manifest** (`ProjectCatalog.sbcatalog.json`) is a **separate** file with a separate lifecycle: it
describes project settings (tags/layers), not scene identity, and is never merged into or read from the
sbmap. It lives under `SceneBuilders/Generated/` (alongside spec 25's generated artifacts), not next to
a builder's sbmap.

## Core test plan

The analyzer's own RED/GREEN suite runs **headless in the Core layer** (foundation §8 Layer 1) so the
gate fails on analyzer regressions with no editor. New sibling test project
**`SceneBuilder.Analyzers.Tests`** (added to `SceneBuilder.sln`, so `dotnet test SceneBuilder.sln`
covers it), using `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` (`CSharpAnalyzerVerifier<TAnalyzer>`).
Recognizer tests may also live in `SceneBuilder.Core.Tests`. Style `Subject_Condition_ExpectedOutcome`.

**Shared recognizer (Core):**
1. `Recognizer_ValidFlatBuilder_ReturnsNoViolations` — a clean flat builder body → empty violation list.
2. `Recognizer_LoopInBody_ReturnsLocatedViolation` — a `for`/`foreach`/`if`/`switch` → a `ShapeViolation`
   with `Code == SB1001` located at the offending statement.
3. `Recognizer_UnknownBuilderCall_ReturnsViolation` — `.Wiggle(…)` in a chain → `SB1002` located.
4. `Recognizer_NonSetInComponentClosure_ReturnsViolation` — a non-`.Set` call in a `Component<T>` closure
   → `SB1003` located.
5. `Recognizer_AgreesWithBuilderParser` — for a corpus of builders, `FlatShapeRecognizer.Analyze` returns
   empty **iff** `BuilderParser.Parse` does not throw a shape error, and flags the same span when it does
   (the anti-drift consistency contract, both directions).

**Analyzer diagnostics (via `CSharpAnalyzerVerifier`, headless):**
6. `Analyzer_ValidBuilder_ZeroDiagnostics` — the known-good sample builder → **zero** Error diagnostics.
7. `Analyzer_Loop_YieldsSB1001Error` — a builder with a loop in Build → one `SB1001` Error at the loop.
8. `Analyzer_UnknownCall_YieldsSB1002` / `Analyzer_NonSetClosure_YieldsSB1003`.
9. `Analyzer_StringSetKey_YieldsSB1101Info` — `.Set("m_Mass", 5f)` → one `SB1101` Info at the string arg.
10. `Analyzer_StringPrefabPath_YieldsSB1102` / `Analyzer_StringOnPath_YieldsSB1103`.
11. `Analyzer_StringTag_YieldsSB1104Warning` / `Analyzer_StringLayer_YieldsSB1105Warning`.
12. `Analyzer_UntypedSetOverload_YieldsSB1106Info`.
13. `Analyzer_UnityEventSignatureScaffold_InertToday` — against current sources the M8 diagnostics
    (`SB1201`/`SB1202`) produce **zero** diagnostics (present-but-scaffolded), and — driven against a
    stub M8-shaped source with a mismatched method — produce the expected `SB1201` Error (the scaffold
    is real, just gated off until the surface lands).
14. `Analyzer_NudgeSeverities_MatchDisciplineRule` — asserts `SB100x`/`SB120x` are `Error` and
    `SB11xx` are `Warning`/`Info` (the severity-discipline rule is itself tested).

**Source generator (Tags/Layers):**
15. `Generator_TagsManifest_EmitsConstPerTag` — a manifest with tags `[Untagged, Player, Enemy]` →
    generated `Tags` with a `const string` for each; a builder referencing `Tags.Player` compiles.
16. `Generator_LayersManifest_EmitsIndexAndName` — layers `[{0,Default},{6,Ground}]` → `Layers.Ground == 6`
    and `Layers.Name.Ground == "Ground"`.
17. `Generator_Deterministic_ByteStable` — the same manifest generates byte-identical source twice.
18. `Generator_NonIdentifierTagName_SanitizedDeterministically` — a tag `"Big Rock!"` → a valid, stable
    identifier; two tags colliding on sanitization → deterministically distinct identifiers; output
    compiles.
19. `Generator_MissingManifest_EmitsNothing_ReferenceFailsToCompile` — no manifest ⇒ no `Tags` type ⇒ a
    `Tags.Player` reference is an ordinary CS compile error (fail-safe, not a wrong value).

**Emitted-code-is-analyzer-clean (the strengthened invariant — piece 3):**
20. `EmittedBuilder_IsAnalyzerClean` — the builder text the tool itself emits (Reconcile `SourcePatch`
    output / codegen) is run through the analyzer and asserted to produce **zero Error diagnostics** —
    the tool may never emit source it would itself reject (extends foundation/spec 22 "generated C# must
    compile" to "compiles AND is analyzer-clean"). Reuse spec 22's/M2's emit fixtures as inputs.

## Unity confirmation checklist → EditMode tests

Per CLAUDE.md, the Unity-facing pieces (manifest generation, injector wiring) need EditMode coverage in
`unity-gate/Assets/GateTests/`. New file `AnalyzerToolkitInjectionTests.cs`, style
`Direction_Scenario_Expectation`. The analyzer-behavior confirmations run headless (Core layer); the
Unity confirmations prove the manifest and the csproj wiring against a live editor.

1. **Manifest reflects real project tags/layers.** Add a tag `"Player"` and name layer 6 `"Ground"` in
   the gate project; run `ProjectCatalogGenerator`. **Expected:** `SceneBuilders/Generated/ProjectCatalog.sbcatalog.json`
   exists and contains `Player` in `Tags` and `{6, "Ground"}` in `Layers`; a second run with no change
   rewrites nothing (byte-identical).
2. **Injector adds the analyzer + manifest to the donor csproj.** Run `BuilderProjectInjector.Inject`
   over a fixture `Assembly-CSharp.csproj` with a builder present. **Expected:** the result contains an
   `<Analyzer Include="…CodeScenes.Analyzers.dll" />` item AND an `<AdditionalFiles
   Include="…ProjectCatalog.sbcatalog.json" />` item; a non-donor project is unchanged; re-injecting is
   idempotent (same instance).
3. **The analyzer is never imported by Unity.** Assert `com.codescenes/Analyzers~/` is not imported as a
   managed plugin (the `~` folder is ignored) — the analyzer dll has no `.meta` under an imported path
   and does not appear in `CompilationPipeline` assemblies.

**Manual acceptance (the author-visible loop, performed once by the user):**
4. Author a builder with a `for` loop in `Build` → **live red squiggle** (`SB1001`) in the IDE, AND the
   gate's headless analyzer suite fails on that shape (piece 1/2 of gate integration).
5. Author `.Tag("Playr")` → `SB1104` warning; switch it to `.Tag(Tags.Playr)` → it **does not compile**
   (`Tags.Playr` does not exist), while `.Tag(Tags.Player)` compiles. This is the principle end-to-end:
   the magic string became a compiler-checked type.
6. Confirm the gate runs the analyzer: `./verify.sh` fails (non-`GATE PASS`) when a sample builder or the
   tool's emitted code carries an Error diagnostic, and prints `GATE PASS` when they are clean.

## Gate integration (`./verify.sh`) — the three pieces

Extend the gate so analyzer diagnostics gate the build. All three run in **Layer 1 (Core/headless)** —
they are ordinary `dotnet test` projects in `SceneBuilder.sln`, so `dotnet test SceneBuilder.sln`
already executes them; no editor needed. (The injector + manifest EditMode tests run in Layer 2 because
they touch `com.codescenes/`.)

1. **Known-good sample builders must be analyzer-clean.** A corpus of valid sample builders compiled
   with the analyzer via `CSharpAnalyzerVerifier` asserts **zero Error diagnostics**. A regression that
   makes the analyzer reject valid code (a false positive) fails the gate.
2. **Known-bad fixtures must produce the EXPECTED diagnostics.** The analyzer's own RED/GREEN suite
   (`SceneBuilder.Analyzers.Tests`) drives fixtures with each authoring mistake and asserts the exact
   `(Code, span, severity)` — this tests the analyzer itself, headless.
3. **New invariant — the tool's OWN emitted code must pass the analyzer.** The existing "generated C#
   must compile" rule (foundation; spec 22) is strengthened to "compiles **AND** is analyzer-clean": the
   scene→code sync / codegen output (Reconcile `SourcePatch` results) is fed through the analyzer and
   asserted to have zero Error diagnostics (test #20). Additionally, the **source generator's Tags/Layers
   output is compile-asserted** (it must produce compiling C#, tests #15–#19) — the generator can never
   emit non-compiling source. A write/codegen path that could emit source the analyzer rejects is a bug,
   not a style issue.

No change to `verify.sh`'s structure is required beyond the new test projects being in the solution;
the gate's Layer-1 `dotnet test SceneBuilder.sln` picks them up. The gate's verdict line
(`GATE PASS`/`GATE FAIL`) remains the only reliable check (foundation §8, CLAUDE.md).

## How spec 25 reframes onto this framework

Spec 25 (typed prefab façades) currently specifies **written `.cs` files** under
`SceneBuilders/Generated/*.cs` injected via `BuilderProjectInjector`. With this milestone landing first,
spec 25 is **reframed as a second source generator built on Component B's framework**:

- Its `PrefabHierarchy` (adapter-read) becomes the input to a project-state **manifest**
  (`Prefabs.sbfacade.json`) exactly like `ProjectCatalog.sbcatalog.json` — an editor-written
  `AdditionalFile`.
- Its `Prefabs` catalog + per-prefab façade types are **emitted into the compilation** by a
  `PrefabFacadeGenerator` (the same `IIncrementalGenerator` framework), instead of written `.cs` files —
  no `Generated/*.cs` to manage, no injection of generated sources (only the manifest as an
  `AdditionalFile`, already wired here).
- Its magic-string nudges (`SB1102` `Instance("path")`, `SB1103` `.On("path")`) are already registered by
  this milestone's analyzer, pointing at the forms spec 25 generates.

Spec 25 becomes the **next, larger** generator on this framework; this milestone ships the framework +
the trivial Tags/Layers catalogs that prove it.

## Dependencies

- **Spec 22 (`completed/22-headless-builder-validation.md`)** — the existing compile-assertion / headless
  gate this milestone extends. Its "generated C# must compile" invariant is strengthened to "compiles
  AND is analyzer-clean" (piece 3). Its `SB2xxx` codes are disjoint from this milestone's `SB1xxx`. The
  `codescenes validate` CLI is the natural future host for surfacing analyzer diagnostics to end-user
  LLMs (out of scope here, noted as a forward hook).
- **Foundation §6** — the flat/near-isomorphic builder shape the recognizer encodes.
- **Foundation §12 / `BuilderProjectInjector`** (`com.codescenes/Editor/BuilderProjectInjector.cs`) — the
  injected-csproj mechanism this milestone extends with `<Analyzer>` + `<AdditionalFiles>`.
- **Core `BuilderParser`** (`SceneBuilder.Core/Parsing/BuilderParser.cs`) — the current home of the flat
  grammar, refactored to derive its recognition from the shared `FlatShapeRecognizer`.
- **M3** (typed `.Set(x => x.member, …)` + generic `.Set(path, value)`) — the forms `SB1101`/`SB1106`
  nudge toward; already shipped.
- **Spec 25 (built AFTER this)** and **M8 (`09-m8-unityevents.md`)** — spec 25 reframes onto this
  framework; the `SB1201`/`SB1202` UnityEvent diagnostics are scaffolded against M8's future surface.

## Risks / notes

- **netstandard2.0 vs 2.1 recognizer sharing.** An analyzer must be ns2.0 and bind the host's Roslyn;
  Core is ns2.1. The shared `SceneBuilder.Grammar` is therefore ns2.0 (consumable by both). If a single
  shared assembly hits Roslyn-version binding friction, fall back to source-linking the recognizer file
  into the analyzer — still one source, never a re-implementation. Re-implementing the grammar in the
  analyzer is the failure mode this design exists to prevent.
- **Roslyn version compatibility across IDE and gate.** The analyzer must load in the IDE's
  OmniSharp/Rider Roslyn AND the gate's `dotnet` SDK Roslyn. Pin `Microsoft.CodeAnalysis.CSharp` to a
  conservative floor, reference it compile-time only (host-provided). Test the analyzer under the gate's
  SDK (that is what `CSharpAnalyzerVerifier` uses); IDE parity is validated by the manual squiggle check
  (Unity confirmation #4).
- **Per-keystroke performance / incrementality.** The analyzer runs on every IDE compilation and must be
  cheap: register **syntax-node actions** scoped to the `Build` method and its invocation chains, not a
  whole-compilation semantic walk; the generator is an **`IIncrementalGenerator`** keyed on the manifest
  `AdditionalFile` so it re-runs only when the manifest changes. This mirrors the foundation's per-sync
  performance constraint (identity/cost is measured per keystroke, not per button press).
- **False-positive precision discipline (the reason v0 is manifest-free).** For an LLM author, a false
  positive on valid code is worse than no analyzer — it trains the model to ignore diagnostics. v0 flags
  only pure-C# facts (Errors) and clearly-safe style nudges (Warning/Info); anything whose correctness
  depends on project state that could be stale is **out** — turned into a generated-type compile error
  (`Tags.X`) or deferred. UNDER-flag rather than over-flag.
- **Manifest freshness for the generator (fails safe — the asymmetry with the analyzer).** A stale
  manifest can only cause a **removed** entity's accessor to fail to compile (safe) or a **new** entity
  to be temporarily missing (safe); it can never make valid code appear invalid. This safe-failure
  property is exactly why the generator MAY use the manifest while the analyzer MUST NOT. Regen is fast
  (File IO outside `Assets/`, no domain reload) and triggered on tag/layer change.
- **Keeping the analyzer's shape-grammar in lockstep with the parser is an ongoing discipline, not a
  one-time wiring.** Any future change to the recognized flat shape MUST land in `FlatShapeRecognizer`
  (the one source both consume), never in `BuilderParser` alone — test #5 (`Recognizer_AgreesWithBuilderParser`)
  pins this and will fail if a grammar change is added to only one side.
- **The `~` packaging placement is load-bearing.** Shipping the analyzer under `Analyzers~/` keeps Unity
  from importing it as a managed plugin (which would load it into the editor domain and risk a
  RoslynAnalyzer-label misapplication to `Assets/`-compiled code). The dll reaches compilation ONLY via
  the explicit `<Analyzer>` injection into the IDE csproj and via the gate's test projects.
</content>
</invoke>
