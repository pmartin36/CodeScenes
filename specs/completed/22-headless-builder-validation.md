# M-Validate — Headless builder validation (an LLM-runnable "will this Build?" check)

**Milestone order: this ships FIRST among all pending work — ahead of §20 (`20-unqualified-type-names.md`),
§21 (`21-project-subasset-refs.md`), and M-Auto (`14-m-auto-live-sync.md`).** Rationale in
§"Placement". It is the capability that closes the authoring feedback loop for the product's actual
authors — LLMs — so every later milestone becomes safer to author. Without it, the only way to
discover a builder mistake is a human opening the editor and pressing Build.

### Additions to the contract

**The authoring loop is closed outside Unity.** The product's builders are written by LLMs. Today an
LLM's mistake in a builder file — an unqualified `Component<Rigidbody>`, a wrong
`Asset("Assets/Materials/Red.mat")`, two indistinguishable duplicate siblings — surfaces **only inside
Unity's Build execution**, so the only observer is a human in the editor. This milestone adds a
**headless validator**: a `dotnet`/CLI command and a Core library API that reproduces Build's
**planning phase** from files on disk, with **no Unity editor process**, and reports the same errors —
located (`file:line:col`), stable-coded, and actionable — collecting **all** of them in one pass. The
loop becomes write → validate → fix → repeat, entirely outside the editor; a human opens Unity only
once the builder is already known-good.

The load-bearing new property is **consistency**: the headless validator's verdict must **agree with an
actual editor Build** for the planning-phase error classes. The design guarantees this by making the
editor Build and the headless validator drive **one shared planning walk** in Core over a small
resolver interface, with two implementations (Unity-backed, disk-backed) — so they cannot drift.

| Added | Shape (summary) | Owner |
|---|---|---|
| `Diagnostic` record | `{ File, Line, Col, Code, Severity, Message, Suggestion }` — one located, coded, actionable finding | this spec |
| `ValidationResult` | `{ IReadOnlyList<Diagnostic> Diagnostics; bool Ok }` — the collect-all result of a planning pass | this spec |
| `IResolutionProvider` (Core) | the planning-phase resolution seam: type-resolve, asset-resolve, built-in-resolve; **returns results, never throws** | this spec |
| `PlanningValidator.Validate(...)` (Core) | the ONE shared planning walk both Build and the CLI drive; collects diagnostics, never throws on first | this spec |
| `SceneBuilder.Validate` project + `codescenes validate` CLI | the headless entry point: disk-backed `IResolutionProvider` + JSON/text output; **launches no Unity** | this spec |

No new `ValueNode` case, no new `PlanOp`, no `SceneModel` change, no new authoring API. This is a new
**consumer** of the existing parse/lower/plan pipeline plus a refactor that routes the editor Build's
resolution through the same shared walk.

---

## Goal

An authoring LLM validates a builder file **itself**, headlessly, before any human touches Unity:

```
$ codescenes validate Builders/DemoScene.cs
DemoScene.cs:14:9  error  SB2001  Cannot resolve component type 'Rigidbdy'.
                                   Did you mean 'UnityEngine.Rigidbody'? Qualify it or add a using.
DemoScene.cs:19:41 error  SB2101  Asset path 'Assets/Materials/Rd.mat' not found (no .meta on disk).
DemoScene.cs:22:9  error  SB2201  2 siblings named 'Enemy' under 'Spawner' are distinguishable only
                                   by position. Give each an explicit .Id("…").
3 errors.  (planning-phase; run a Unity Build to validate execution-phase.)
```

The same call, as a library, returns a `ValidationResult` an agent or tool consumes as JSON. When the
file is clean, the validator reports **zero diagnostics** and exits 0 — and an actual editor Build of
that same file then succeeds for the planning phase (the consistency contract, §"The consistency
property"). The result: an LLM writes a builder, runs `validate`, reads located errors, fixes them,
and repeats — never needing a human-in-Unity round trip to see whether its code is even well-formed.

## The problem (observed, not theorized — errors are trapped inside Build execution)

A Build is a **planning phase** (parse → resolve component types → resolve asset refs → structural /
identity checks → produce a Plan) followed by an **execution phase** (apply the Plan to the live
scene). Every planning error today is raised **inside the running editor**, as a throw, reachable only
by invoking Build in Unity:

- **Unqualified type** — `AuthoredPathResolver.GetProbe` (`com.codescenes/Editor/AuthoredPathResolver.cs:115`)
  calls `ComponentTypeResolver.Resolve(typeRef)` and, on `null`, **throws**
  `InvalidOperationException("[SceneBuilder] Cannot resolve component type 'Rigidbody' …")` (`:116`).
  `ComponentTypeResolver.Resolve` (`com.codescenes/Editor/ComponentTypeResolver.cs`) resolves names
  through `UnityEditor.TypeCache.GetTypesDerivedFrom<Component>()` and an `AppDomain` reflection scan —
  **Unity-only APIs**. There is no way to see this error without the editor.
- **Bad asset path** — `AssetReferenceResolver.LoweringResolver.Resolve`
  (`com.codescenes/Editor/AssetReferenceResolver.cs:95`) calls `AssetDatabase.AssetPathToGUID(displayPath)`
  and **throws** on an empty GUID (`:101`) — again a Unity API, again editor-only.
- **Duplicate-sibling ambiguity** — computed **headlessly today** by Core
  (`ConflictDetector` → `ParseResult.Ambiguities`), but the only consumer that *reports* it,
  `SceneBuilderBuild.Run` (`com.codescenes/Editor/SceneBuilderBuild.cs:96-98`), lives in the adapter and
  **throws a single `ParseException`** inside the editor.

Two structural facts make this fatal for an LLM author:

1. **The observer is a human in Unity.** The authoring model cannot see its own mistake and self-correct;
   the feedback loop is a human round trip. Better error *messages* do not fix this — you still have to
   be in the editor to read them.
2. **Build throws on the first error.** `AuthoredPathResolver` throws at the first unresolved type;
   `LoweringResolver` throws at the first bad asset; `SceneBuilderBuild` throws one `ParseException`.
   An author fixing errors one editor-round-trip at a time is the worst possible loop. An LLM needs the
   **complete** list to fix everything in a single pass.

## What is headless-determinable vs editor-only (the honest boundary)

The planning phase is **mostly determinable from files on disk** — the builder source, the project's
`.meta` files, the compiled user assemblies under `Library/ScriptAssemblies/`, and the Unity **managed
DLLs** that ship with the editor install (the very DLL set `SceneBuilder.Editor.CompileCheck` already
compiles the adapter against — `SceneBuilder.Editor.CompileCheck/*.csproj` references
`…/6000.5.3f1/Editor/Data/Managed/UnityEngine.dll`, `UnityEditor.dll`, `UnityEngine/*.dll`, all on
disk, no editor process). The validator covers the planning phase; it does **not** and cannot cover the
execution phase.

**Headless-determinable (the validator covers these):**

| Error class | Grounding |
|---|---|
| **Parse / structure / duplicate-sibling / duplicate-LogicalId** | Already pure Core, Unity-free (`BuilderParser.Parse` → `ConflictDetector` → `ParseResult.Ambiguities`, `SceneBuilder.Core` targets `netstandard2.1`, references only Roslyn + `System.Text.Json`). Runs headless **today**. |
| **Component type resolution** (`Rigidbody` → `UnityEngine.Rigidbody`; user `Enemy` → `MyGame.Enemy`) | The Unity managed DLLs and the compiled user assemblies are **on disk**. A headless resolver builds a Roslyn `MetadataReference` set (or a reflection-only load) over `Editor/Data/Managed/**/*.dll` **and every DLL under the project's `Library/ScriptAssemblies/`**, then resolves a bare name exactly as C# does — try it as-is, else try each in-scope `using` namespace as a `<ns>.<Name>` prefix, exactly-one match wins (the §20 rule, run over metadata instead of `TypeCache`). |
| **Asset path existence / path→GUID** (`Asset("Assets/Materials/Red.mat")`) | Unity's `AssetDatabase.AssetPathToGUID` reads the GUID from the sibling `<path>.meta` file (verified: a `.meta` contains `guid: 1c54ed498ea74c7aaf902e418cb3c632`). The headless equivalent is a **file read**: the path resolves iff `<path>.meta` exists on disk; its `guid:` line is the GUID — no Unity. |

**Editor-only (the validator explicitly does NOT claim these — they remain a Unity-only check):**

- **Serialized-property existence for the string-key form.** `AuthoredPathResolver.ResolvePath`
  (`AuthoredPathResolver.cs:149`) maps a member to a serialized path via a **live** `SerializedObject`
  (`new SerializedObject(probeComponent)` + `FindProperty`). For `.Set(r => r.mass)` a headless
  reflection check can confirm the CLR member exists; for the raw `.Set("m_Mesh", value)` form, whether
  a serialized property of that name exists needs Unity. Validator surfaces a CLR-member check where the
  lambda form makes it possible, and defers serialized-property existence to the editor. **Flagged
  OPEN.**
- **Built-in resource name existence** (`Builtin("Cube")`, §17). The container **GUID** is a fixed
  constant, but the `name → fileId` mapping lives in the editor-install resource containers, read live
  via `AssetDatabase.LoadAllAssetsAtPath("Library/unity default resources")`, and §17 **forbids** a
  hardcoded `name→fileId` table. The validator can headlessly check a `Builtin(...)` call's **shape**
  (1–2 string-literal args, non-null) but not that `"Cube"` is a real built-in name without the editor
  (or a future scan of the editor-install resource files). **Flagged OPEN** — shape headless, existence
  editor-only for now.
- **Project sub-asset name enumeration** (`Asset("Assets/Models/Barrel.fbx", "BarrelMesh")`, §21).
  Enumerating the sub-objects inside an imported FBX requires the import result under `Library/`, which
  Unity produces; the sub-object **names** are not in the `.meta`. Existence of a named sub-object is
  **editor-only**. The validator checks the FBX path exists (`.meta`) and the call shape; sub-name
  existence defers to the editor.
- **Anything that inspects the live scene** — the execution-phase apply, identity remapping against real
  `GlobalObjectId`s, `SerializedProperty` writes, prefab/override behavior. Entirely editor-only.

The validator's output **states its own boundary** on every run (e.g. the trailing
`(planning-phase; run a Unity Build to validate execution-phase.)` line), so a green result is never
mistaken for "Build will succeed end-to-end" — only "the planning phase is clean."

## The design

### The headless entry point

Two surfaces over one core, because the two audiences differ:

- **`codescenes validate <builderFile> [--project <dir>] [--managed <dir>] [--json]`** — a thin CLI
  (a `dotnet` tool / console project `SceneBuilder.Validate`) that the **authoring loop runs itself**.
  It launches **no Unity process**. It prints human-readable text by default and machine-readable JSON
  under `--json`; exit code is `0` when there are no `error`-severity diagnostics, non-zero otherwise.
- **`HeadlessValidator.Validate(builderFile, ProjectLayout) → ValidationResult`** — the library API the
  CLI is a shell over, and what the consistency test and future tooling call directly.

**Locations are inferred from the project layout, overridable by args.** Given the builder file path,
the validator walks up to the Unity **project root** (the directory containing `Assets/`, `Library/`,
`ProjectSettings/`). From there it derives:
- **Unity managed DLL dir** — from `ProjectSettings/ProjectVersion.txt` (`m_EditorVersion:
  6000.5.3f1`, verified present) plus a resolvable editor install: `$UNITY_EDITOR`'s
  `Data/Managed`, else the Hub default `~/Unity/Hub/Editor/<version>/Editor/Data/Managed`. `--managed`
  overrides. This is the same DLL set `SceneBuilder.Editor.CompileCheck` already references.
- **User type assemblies** — **every** DLL under `Library/ScriptAssemblies/` (see §"Risks/notes":
  there is **no** fixed `Assembly-CSharp.dll` to assume — user code compiles to asmdef-named
  assemblies, e.g. `GateFixtures.dll` in `unity-gate`; the resolver scans them all).
- **Asset `.meta` root** — the project's `Assets/` tree, for path→GUID.

`--project` overrides the inferred root; explicit `--managed` overrides the editor-install probe (for
CI, where the editor may live at a fixed path). If the managed DLL dir cannot be located, the validator
still runs the **Unity-free** checks (parse, structure, duplicate-sibling) and reports the type/asset
checks as **skipped**, never as passed — a skip is not a pass (mirroring `verify.sh`'s Unity-layer
rule).

### Collect ALL diagnostics in one pass — never throw on first

The planning walk collects into a `DiagnosticBag` and **never throws**. Concretely:

- **`Diagnostic`** (Core) — `record { string File; int Line; int Col; string Code; DiagnosticSeverity
  Severity; string Message; string? Suggestion }`. `Code` is a **stable** identifier (e.g. `SB2001`
  unresolved type, `SB2002` ambiguous type, `SB2101` asset path not found, `SB2201` ambiguous duplicate
  sibling, `SB2202` duplicate LogicalId) so tools and the LLM can key on it across message wording
  changes. `Line`/`Col` are 1-based, derived from the parse's `SourceSpan`/`Anchors`/`FieldArgumentSpans`
  (already captured per component and per field argument — `ParseResult.ComponentAnchors`,
  `FieldArgumentSpans`, `Anchors`, `NodeAnchors`).
- **`ValidationResult`** (Core) — `{ IReadOnlyList<Diagnostic> Diagnostics; bool Ok }` where
  `Ok == !Diagnostics.Any(d => d.Severity == Error)`.
- **The resolver seam never throws.** `IResolutionProvider` (below) **returns** a discriminated result
  (`Resolved(guid,fileId,…)` / `Unresolved` / `Ambiguous(candidates)`); the **walk** — which knows the
  owning object, component, field, and span — turns a non-`Resolved` outcome into a located
  `Diagnostic`. This inverts today's throw-in-the-resolver model, which cannot collect.

### Every diagnostic located and actionable

Each diagnostic carries `file:line:col`, a stable `Code`, a message, and a concrete `Suggestion`:

- unresolved `Rigidbdy` → "Did you mean `UnityEngine.Rigidbody`? Qualify it or add a matching `using`."
  (near-miss suggestions come from the same metadata scan — a type whose short name matches under a
  namespace not in scope, or a small edit-distance match).
- ambiguous type (two in-scope namespaces define the name) → lists both fully-qualified candidates and
  says "qualify it" — never guesses (mirrors §20's ambiguity-is-an-error rule).
- bad `Asset("…/Rd.mat")` → "Asset path not found (no `.meta` on disk). Nearest: `Assets/Materials/Red.mat`."
- ambiguous duplicate siblings → the exact §7/§16 located message already produced by
  `ConflictDetector`/`SceneBuilderBuild.FormatAmbiguities`, reused verbatim so the two paths' wording
  matches.

### Structured + human output

- **JSON** (`--json`): `{ "file": "...", "ok": false, "diagnostics": [ { "line":14, "col":9,
  "code":"SB2001", "severity":"error", "message":"…", "suggestion":"…" } ], "skipped": ["type","asset"]?,
  "phase":"planning" }`. Stable field names an LLM/tool parses.
- **Text** (default): the `file:line:col  severity  CODE  message` form shown in §Goal, one block per
  diagnostic, a summary count, and the explicit planning-phase boundary note.

### The consistency property (validate ⟺ Build) — and the shared core that guarantees it

**The contract:** for the planning-phase error classes above, the headless validator's verdict **agrees
with an actual editor Build**. If `validate` says OK but Build fails on a planning error → false
confidence (worse than nothing). If `validate` flags what Build would accept → false alarms that train
the author to ignore it. Both are forbidden. "validate ⟺ Build outcome, for the planning-phase error
classes" is an **explicit contract** and a **test** (§"Unity confirmation checklist").

**The architecture that makes drift impossible:** the editor Build and the headless validator drive the
**same** planning walk in Core over the **same** resolver interface, differing only in which resolver
implementation is injected.

```csharp
// Core (Unity-free) — the ONE definition of the planning-phase resolution seam.
public interface IResolutionProvider {
    TypeResolution   ResolveComponentType(TypeRef type, IReadOnlyList<string> usings);
    AssetResolution  ResolveAssetPath(string displayPath, string? subAsset);
    AssetResolution  ResolveBuiltin(string name, string? typeHint);
}

// Core — the ONE planning walk both consumers reach. Collects, never throws.
public static class PlanningValidator {
    public static ValidationResult Validate(ParseResult parse, IResolutionProvider resolver);
}
```

- **Unity-backed provider** (adapter) — wraps `ComponentTypeResolver`, `LoweringResolver`,
  `BuiltinRefValidator`/`BuiltinCatalog`, i.e. `TypeCache` + `AssetDatabase`. The editor Build's
  planning is refactored to run `PlanningValidator.Validate` with this provider and, when the bag is
  non-empty, refuse the Build reporting **all** diagnostics — instead of the current throw-on-first at
  three separate sites.
- **Disk-backed provider** (`SceneBuilder.Validate`) — metadata/reflection over the Unity managed DLL
  set + `Library/ScriptAssemblies/*.dll` for types; `.meta` reads for asset paths; shape-only (or
  editor-deferred) for built-ins/sub-assets.

Because the **walk, the diagnostic model, the codes, and the duplicate-sibling detection are one shared
Core implementation**, the two providers can disagree only where they genuinely observe different
ground truth (the honest boundary) — and there the disk-backed provider returns `Deferred`, never a
false `Resolved`/`Unresolved`. This is the inherit-by-default design of CLAUDE.md: the consistency is
structural, not a pair of hand-synced code paths.

## Core deliverables

`SceneBuilder.Core` — Unity-free; it defines the model, the walk, and the seam, and resolves nothing
Unity-specific itself.

- **`Diagnostic`, `DiagnosticSeverity`, `ValidationResult`, `DiagnosticBag`** (new, `SceneBuilder.Core/
  Validation/`) — the collect-all diagnostic model above. `Diagnostic` is `file:line:col` + stable
  `Code` + message + optional suggestion. Serializes to the JSON shape via the existing
  `System.Text.Json` dependency.
- **Stable diagnostic codes** (new, `SceneBuilder.Core/Validation/DiagnosticCodes.cs`) — the one
  registry of `SB2xxx` codes, shared verbatim by the editor Build and the CLI so a given failure has the
  **same code** whichever path reports it.
- **`IResolutionProvider` + `TypeResolution`/`AssetResolution` result types** (new,
  `SceneBuilder.Core/Validation/`) — the non-throwing resolution seam. Result types are discriminated:
  `Resolved` (carries `(guid,fileId,typeHint)` or a resolved `FullName`), `Unresolved` (carries
  near-miss suggestions), `Ambiguous` (carries candidate list), `Deferred` (the honest-boundary case:
  "cannot be decided headlessly — needs the editor"; a `Deferred` **never** becomes an error diagnostic,
  only an informational "deferred to editor" note when in verbose mode).
- **`PlanningValidator.Validate(ParseResult parse, IResolutionProvider resolver)` → `ValidationResult`**
  (new, `SceneBuilder.Core/Validation/`) — the shared walk. In order: (1) map every
  `parse.Ambiguities` `Conflict` to a located `Diagnostic` (reusing the existing `Conflict`
  message/location and `SB22xx` codes); (2) for each `ComponentData`, call
  `resolver.ResolveComponentType(component.Type, parse.Usings)` → located diagnostic on
  `Unresolved`/`Ambiguous`, keyed by `parse.ComponentAnchors[LogicalId]`; (3) for each `ValueNode.AssetRef`,
  call `ResolveAssetPath`/`ResolveBuiltin` → located diagnostic on `Unresolved`/`Ambiguous`, keyed by
  `parse.FieldArgumentSpans`. **Never throws**; every failure is a collected diagnostic. `Deferred`
  results are not errors.
- **`ParseResult.Usings`** — required by step (2). **ALREADY SHIPPED by §20**
  (`20-unqualified-type-names.md`, landed ahead of §22): `BuilderParser` already captures file-scope
  `using` imports into `ParseResult.Usings`, and `UsingCaptureTests.cs` already covers it. §22 therefore
  **consumes** this field as pre-existing and adds **no** parser change for it — the walk reads
  `parse.Usings` directly. (This spec's original text assumed §22 shipped first; the queue put §20
  first, so this deliverable is done — do not re-introduce it.)
- **No `SceneModel`, `ValueNode`, `PlanOp`, `CanonicalJson`, `Differ`, `Materializer`, `Reconciler`
  change.** The validator is a **reader** of the parsed model; it produces diagnostics, not a Plan.

## Editor adapter deliverables

All in `com.codescenes/Editor/` unless noted. The theme is: **route the editor Build's planning-phase
resolution through the SAME `PlanningValidator` + `IResolutionProvider`, and collect-all instead of
throw-on-first**, so the editor's verdict is the shared walk's verdict.

- **`UnityResolutionProvider` (new)** — implements `IResolutionProvider` by delegating to the existing
  adapter resolvers:
  - `ResolveComponentType` → `ComponentTypeResolver.Resolve` (with the §20 usings-aware overload once it
    lands; until then, as-is), mapped to `Resolved`/`Unresolved`/`Ambiguous`.
  - `ResolveAssetPath` → the `LoweringResolver.Resolve` path→GUID logic, mapped to a result instead of a
    throw.
  - `ResolveBuiltin` → `BuiltinCatalog.Resolve`/`BuiltinRefValidator`, mapped to a result.
  These resolvers **already** contain the decision logic; this wraps them behind the non-throwing seam.
- **`SceneBuilderBuild.Run` — plan through the shared walk, report ALL, then execute.** Before lowering
  and execution, run `PlanningValidator.Validate(parse, new UnityResolutionProvider())`. If
  `!result.Ok`, **refuse the Build and surface every diagnostic** (log all, return them on
  `BuildResult`) — replacing the current single-`ParseException`-on-ambiguities throw
  (`SceneBuilderBuild.cs:96-98`) and pre-empting the first-error throws in `AuthoredPathResolver`
  (`:116`) and `LoweringResolver` (`:101`,`:115`). Those throws remain as **unlocated backstops** for a
  caller that bypasses the walk, but the normal Build path now reports the located, collected set.
- **`DesiredModelLoader` — the throws become backstops, not the primary surface.** The primary,
  user-facing planning errors now originate from `PlanningValidator` (located, collected). The existing
  throws in `AuthoredPathResolver`/`BuiltinRefValidator`/`LoweringResolver` are retained so a resolution
  that slips past the walk still fails loud (never a silent skip), per the always-on-backstop rule §17
  established.
- **Authoring API — none.** (§"Authoring API".)

## Authoring API

**None added.** This milestone adds no builder-facing call, factory, or attribute. It is a **tool and a
library** over the existing pipeline. The authoring surface an LLM writes is unchanged; what changes is
that the LLM can now **check** what it wrote without Unity.

The CLI is the author-facing surface:

```
$ codescenes validate Builders/DemoScene.cs          # human text, exit 0/1
$ codescenes validate Builders/DemoScene.cs --json    # machine-readable, for an agent/tool
```

## Core test plan

The disk-backed resolution is exercised by the CLI/adapter layers; Core's tests cover the **walk**, the
**diagnostic model**, and the **collect-all** guarantee with a **stub `IResolutionProvider`** (no Unity,
no disk). New file `SceneBuilder.Core.Tests/PlanningValidatorTests.cs`, xUnit, style
`Subject_Condition_ExpectedOutcome`. Each test drives a real `BuilderParser.Parse` of a file-scope
`ISceneDefinition` source fixture, then `PlanningValidator.Validate` with a stub provider.

1. `Validate_TypeResolvesGivenUsing_NoDiagnostic` — stub resolves `("Rigidbody", ["UnityEngine"])` →
   `Resolved("UnityEngine.Rigidbody")`; a clean `Component<Rigidbody>` fixture → **zero** diagnostics,
   `Ok == true`.
2. `Validate_UnqualifiedUnknownType_YieldsLocatedDiagnosticWithSuggestion` — stub returns `Unresolved`
   for `Rigidbdy` → one `Diagnostic` with `Code == SB2001`, `Line`/`Col` pointing at the
   `Component<…>` token (from `ComponentAnchors`), and a non-empty `Suggestion`.
3. `Validate_AmbiguousType_YieldsAmbiguousDiagnosticListingCandidates` — stub returns `Ambiguous([A,B])`
   → `Code == SB2002`, message names both candidates, no guess.
4. `Validate_BadAssetPath_YieldsLocatedDiagnostic` — stub returns `Unresolved` for
   `Asset("Assets/Materials/Rd.mat")` → `Code == SB2101`, located at the field-argument span (from
   `FieldArgumentSpans`).
5. `Validate_DuplicateSiblings_YieldsAmbiguityDiagnostic` — a fixture with two positional siblings named
   `Enemy` under one parent → `ParseResult.Ambiguities` is non-empty and maps to a `Diagnostic`
   (`Code == SB2201`), located, **without** the resolver being consulted (structural, provider-free).
6. `Validate_CleanBuilder_YieldsZeroDiagnostics` — a fully-resolvable fixture (stub `Resolved` for
   everything, no ambiguities) → empty `Diagnostics`, `Ok == true`.
7. `Validate_MultipleErrors_AllReportedInOnePass` — a fixture with an unresolved type **and** a bad
   asset **and** duplicate siblings → **three** diagnostics in one `ValidationResult` (the collect-all
   guarantee; asserts the walk did not stop at the first).
8. `Validate_DeferredResolution_IsNotAnError` — stub returns `Deferred` for a `Builtin("Cube")` /
   `Asset(path, sub)` node → `Ok == true`, no error diagnostic (the honest-boundary rule: unknowable
   headless ≠ failing).
9. `Diagnostic_SerializesToStableJsonShape` — a `ValidationResult` → JSON with the documented field
   names/`Code`s (an agent/tool contract test).
10. `Validate_NeverThrows_OnAnyProviderOutcome` — property-style: for `Resolved`/`Unresolved`/
    `Ambiguous`/`Deferred` in every position, `Validate` returns a result and **never throws**.

(`ParseResult.Usings` capture is already shipped and tested by §20's `UsingCaptureTests.cs` — §20
landed first; §22 adds no capture test, only the `PlanningValidator` walk tests above.)

## Unity confirmation checklist → EditMode tests

Per CLAUDE.md, the consistency contract is proven by a **real** editor Build compared against the
headless validator. New file `unity-gate/Assets/GateTests/HeadlessValidationConsistencyTests.cs`, in
the established `Direction_Scenario_Expectation` style, driving both `SceneBuilderBuild.Run` (live
editor) and `HeadlessValidator.Validate` (the disk-backed CLI core, in-process, no second editor) over
one corpus of builder fixtures.

**The corpus** (one clean + several each with a distinct planning-phase error), as builder files under
a gate fixtures dir with real `.meta` and a real compiled user assembly present in
`Library/ScriptAssemblies/`:
- `Clean.cs` — resolvable types, a valid `Asset("Assets/…/Red.mat")`, no duplicate siblings.
- `BadType.cs` — `using UnityEngine; Component<Rigidbdy>(…)` (typo).
- `MissingUsing.cs` — `Component<Rigidbody>(…)` with no `using UnityEngine;`.
- `AmbiguousType.cs` — two in-scope namespaces both defining the name.
- `BadAsset.cs` — `Asset("Assets/Materials/DoesNotExist.mat")`.
- `DupSiblings.cs` — two positional siblings named `Enemy` under one parent.

**The consistency test (the headline):**

1. **Clean builder — both agree it's OK.** Editor `SceneBuilderBuild.Run(Clean.cs)` succeeds (no
   planning refusal); `HeadlessValidator.Validate(Clean.cs)` → `Ok == true`, zero diagnostics.
   **Expected:** both green.
2. **Each broken builder — both agree it fails, at the same site, with the same class.** For each of
   `BadType`, `MissingUsing`, `AmbiguousType`, `BadAsset`, `DupSiblings`: the editor Build **refuses**
   (planning error) **and** `HeadlessValidator.Validate` returns `Ok == false` with a diagnostic of the
   **same `Code`** located at the **same line** as the site the editor Build reports. **Expected:** for
   every corpus member, `editorBuildFailed == !headless.Ok` **and** the error class/line match — no
   member where one flags and the other passes (neither false confidence nor false alarm).
3. **Collect-all matches on a multi-error file.** A builder with a bad type **and** a bad asset: the
   editor Build (now collect-all) reports **both**, and the headless validator reports **both**, with
   matching codes. **Expected:** the two diagnostic sets agree as sets of `(Code, Line)`.
4. **Type resolution parity for a user script.** `Component<Enemy>` under `using <ns>;` where `Enemy`
   is a real user `MonoBehaviour` compiled into `Library/ScriptAssemblies/<asmdef>.dll`: both the editor
   (`TypeCache`) and the headless resolver (metadata over that DLL) resolve it → both OK. **Expected:**
   parity — the disk-backed metadata scan finds the same user type the editor does.
5. **Asset path parity.** A valid `Asset("Assets/…/Red.mat")` (real `.meta` present) → both OK; a bad
   path → both flag `SB2101` at the same span. **Expected:** `.meta`-read verdict matches
   `AssetPathToGUID`.
6. **The honest boundary does not produce a false alarm.** A `Builtin("Cube")` (existence editor-only):
   the editor Build resolves it (OK); the headless validator returns `Deferred` → `Ok == true` (no
   error). **Expected:** the validator does **not** flag a built-in it cannot verify headlessly — a
   `Deferred` is never a false-positive error.

## Dependencies

- **Core parser + `ParseResult`** (`00-foundation.md`, `16-duplicate-sibling-identity.md`) —
  `BuilderParser.Parse`, `ConflictDetector`, `ParseResult.Ambiguities`/`Anchors`/`ComponentAnchors`/
  `FieldArgumentSpans`/`NodeAnchors`. The validator reads these; the duplicate-sibling detection is
  reused verbatim.
- **The adapter resolvers** — `ComponentTypeResolver`, `AssetReferenceResolver.LoweringResolver`,
  `BuiltinCatalog`/`BuiltinRefValidator` (`com.codescenes/Editor/`). The `UnityResolutionProvider`
  wraps these behind the non-throwing seam; the headless provider mirrors their **decisions** over disk.
- **`SceneBuilder.Editor.CompileCheck`'s DLL-reference machinery** — the `<Reference>` set over
  `Editor/Data/Managed/UnityEngine.dll`/`UnityEditor.dll`/`UnityEngine/*.dll` that already compiles the
  adapter **headless against the Unity DLLs**. `SceneBuilder.Validate` **reuses this exact DLL set** as
  its metadata references — the proof-of-concept that the managed DLLs resolve Unity types off disk
  already exists in this repo.
- **§20 (`20-unqualified-type-names.md`)** — `ParseResult.Usings` and the usings-aware name-resolution
  rule. §22 introduces the Core `Usings` capture (see §"Core deliverables"); §20 then reuses it.
- **§17 (`17-builtin-resources.md`) / §21 (`21-project-subasset-refs.md`)** — define the `Builtin(…)` /
  `Asset(path, sub)` forms whose **existence** checks sit on §22's honest boundary (shape headless,
  existence editor-deferred).
- **Related, distinct:** `specs/needs_research/headless-ci-generation.md` proposes running the **whole
  adapter** (Materialize execution, snapshot read) headlessly via `unity -batchmode` or by emitting
  `.unity` YAML. §22 is the **planning-phase** subset that avoids Unity **entirely** — it does not
  execute the Plan. The two are complementary: §22 catches authoring errors pre-Unity; the CI-generation
  research (if promoted) validates execution/no-op-Plan drift **with** Unity.

## Placement

**Numbered 22. First in the pending build order — ahead of §20, §21, and M-Auto (§14).**

Rationale: §22 is the capability that makes the product **usable by an LLM at all**. The product's
authors are LLMs (CLAUDE.md), and today an LLM cannot see its own builder mistakes without a
human-in-Unity round trip — the feedback loop is broken at its most fundamental point. §22 closes it:
write → validate → fix → repeat, no editor. Every later milestone is a new way to author (unqualified
types §20, sub-asset refs §21, live sync §14), and each becomes **safer and faster to author** once the
LLM can validate its output headlessly — the validator is the substrate they all sit on. It is also
low-risk and self-contained: it adds a **reader** of the existing pipeline plus a refactor of the
Build's three throw-on-first sites into one collect-all shared walk; it introduces no new authoring
surface and no `SceneModel`/`Plan` change.

Sequencing note with §20 (RESOLVED — §20 shipped first): §22 needs `ParseResult.Usings` for headless
type resolution. §20 **already landed** it (both the `BuilderParser` capture and the adapter
`ComponentTypeNormalizer`), so §22 simply **reads** `parse.Usings` and adds no parser change. The
"merge, not conflict" outcome held: §22's Core Usings half is already done.

## Risks / notes

- **The consistency property is the whole point, and the shared core is how it holds.** If the editor
  Build and the headless validator were two independent resolution paths, they would drift the first
  time either changed, and a drifted validator is worse than none (false confidence or false alarms
  that train the author to ignore it). The single `PlanningValidator` + `IResolutionProvider` in Core,
  with the editor Build refactored to drive it, makes agreement structural. Confirmation #2/#3 pin it as
  a set-equality of `(Code, Line)` across a corpus, in **both** directions.
- **There is NO `Assembly-CSharp.dll` to assume — this claim from the task does not survive against the
  code.** In `unity-gate` (and any project using assembly definitions) user scripts compile to
  **asmdef-named** assemblies under `Library/ScriptAssemblies/` (verified: `GateFixtures.dll`,
  `GateTests.dll`, `SceneBuilder.Editor.dll`, `UnityEngine.UI.dll`, …) — `Assembly-CSharp.dll` exists
  only for loose scripts directly under `Assets/` with no asmdef. The headless type resolver must scan
  **every** DLL under `Library/ScriptAssemblies/`, not a fixed filename, or it will miss user types.
- **Built-in and sub-asset existence are on the boundary, not headlessly checkable — do not over-claim.**
  `Builtin("Cube")` name→fileId lives in the editor-install resource containers (§17 forbids a hardcoded
  table); FBX sub-object names (§21) live in the `Library/` import, not the `.meta`. The validator
  checks their **call shape** headlessly and returns `Deferred` for existence — a `Deferred` is never an
  error. A future refinement could scan the editor-install resource files for built-in names; deferred
  here to keep the first pass honest. **OPEN.**
- **Serialized-property existence for the raw `.Set("m_Mesh", …)` form needs Unity.**
  `AuthoredPathResolver.ResolvePath` maps a member to a serialized path via a live `SerializedObject`.
  The lambda form `.Set(r => r.member)` permits a headless CLR-member reflection check; the string-key
  form's serialized-property existence is editor-only. Validator does the CLR check where possible and
  defers the rest. **OPEN.**
- **Where the DLL set lives across machines / CI.** The Unity managed DLL dir is derived from
  `ProjectSettings/ProjectVersion.txt` + a resolvable editor install (`$UNITY_EDITOR` or the Hub
  default). On a machine or CI runner without a matching editor install, the type/asset checks
  **cannot** run — the validator reports them **skipped** (never passed) and still runs the Unity-free
  checks. `--managed` lets CI point at a fixed install. This mirrors `verify.sh`'s "a skip is never a
  pass" discipline.
- **Keeping validate and Build from drifting is an ongoing discipline, not a one-time wiring.** Any
  future planning-phase error class added to the editor Build must be added to `PlanningValidator` (the
  shared walk), not to the adapter alone — otherwise the validator silently under-reports. The shared
  walk is the chokepoint every planning error must route through; a new error raised only inside the
  adapter's execution path is by definition execution-phase and correctly out of the validator's scope.
- **Collect-all changes the editor Build's failure surface.** The Build today throws one error and
  aborts; after §22 it reports **all** planning errors before refusing. Any existing EditMode test that
  asserts "Build throws exactly one `ParseException`" on a multi-error file is asserting the old
  first-error behavior and must be updated to the collected set.
