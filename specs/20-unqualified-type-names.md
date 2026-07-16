# Unqualified type names — resolve `Component<T>` the way C# does

**Milestone order: this ships FIRST among the pending work, before M-Auto (`specs/14-m-auto-live-sync.md`).**
It removes a papercut that makes *all* authoring more forgiving, and every later milestone (and the
seamless-sync driver) inherits the more tolerant parse. It is a small, self-contained hardening of
the M3 component path, not a new capability — so it goes ahead of the larger live-sync work.

### Additions to the contract

**One carrier, no new model concept.** This milestone adds a single field —
`ParseResult.Usings : IReadOnlyList<string>` — carrying the builder file's plain namespace imports,
and one adapter normalization stage that rewrites a component `TypeRef.FullName` from the token the
user wrote (`"Rigidbody"`) to the resolved CLR full name (`"UnityEngine.Rigidbody"`). It introduces
**no** new `ValueNode` case, **no** new `PlanOp`, **no** change to `SceneModel`, and **no** new
authoring API. The authoring surface is exactly today's: `using` directives plus short type names.

| Added | Shape (summary) | Owner |
|---|---|---|
| `ParseResult.Usings : IReadOnlyList<string>` | file-scope namespace imports captured verbatim from the syntax tree; a parse-time, source-level bag alongside `Anchors`/`FieldArgumentSpans`/`Handles` | this spec |
| `ComponentTypeResolver.Resolve(TypeRef, usings)` | usings-aware overload: try the name as-is, else try each `<ns>.<name>` prefix; exactly-one loaded match wins, zero → unresolved, many → ambiguous | this spec |
| `ComponentTypeNormalizer.Normalize(model, usings, anchors)` | adapter stage that stamps the resolved full name into every component `TypeRef.FullName`; located throw on unresolved/ambiguous | this spec |

`SceneModel` is deliberately **not** the carrier for usings — it is the canonical, serialized,
diffed model (`SchemaVersion` + `Roots`), and a using list is a source-file resolution aid that must
never enter `CanonicalJson` or the identity comparison. `ParseResult` is the correct home: it already
holds the source-level, non-serialized parse metadata (`Anchors`, `FieldArgumentSpans`, `Handles`,
`NodeAnchors`) and is never persisted.

---

## Goal

Authoring a component by its short type name resolves exactly the way the C# compiler resolves it —
through the builder file's in-scope `using` directives. Today:

```csharp
using UnityEngine;
// ...
cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));
```

fails at Build. After this milestone it Builds — `mass` is assigned on a real `Rigidbody` — and, just
as importantly, a scene→code Sync of that same component does **not** rewrite `Rigidbody` to
`UnityEngine.Rigidbody`, so the file never churns.

## The bug (observed)

`using UnityEngine;` + `Component<Rigidbody>(c => c.Set(r => r.mass, 5f))`, then Build:

```
System.InvalidOperationException: [SceneBuilder] Cannot resolve component type 'Rigidbody'
    to resolve authored member paths.
   at SceneBuilder.Editor.AuthoredPathResolver.GetProbe(...)   // AuthoredPathResolver.cs:115-116
   at SceneBuilder.Editor.AuthoredPathResolver.ResolveComponent(...)
   at SceneBuilder.Editor.DesiredModelLoader.Load(...)         // DesiredModelLoader.cs
```

Fully qualifying the name (`Component<UnityEngine.Rigidbody>`) makes it work. The failure is a hard
throw that aborts the whole Build, not a skipped field.

## Root cause (code-grounded)

The builder parser is **purely syntactic** and never consults the file's `using` directives:

- `BuilderParser` captures the generic type argument as the raw source token —
  `generic.TypeArgumentList.Arguments[0].ToString().Trim()` (`BuilderParser.cs:342`) — and builds
  `Type = new TypeRef(cb.TypeFullName)` (`BuilderParser.cs:845`). For `Component<Rigidbody>` that
  token is literally `"Rigidbody"`; `TypeRef.FullName == "Rigidbody"`. The parser already walks the
  `CompilationUnitSyntax` root (`BuilderParser.cs:26-27`) whose `.Usings` are right there, but it
  ignores them.
- The adapter's `ComponentTypeResolver.Resolve` (`com.codescenes/Editor/ComponentTypeResolver.cs`)
  matches on `t.FullName == fullName` across `TypeCache.GetTypesDerivedFrom<Component>()` and a
  reflection scan. No loaded component type is *named* `"Rigidbody"` — its real `FullName` is
  `"UnityEngine.Rigidbody"` — so `Resolve` returns `null`.

Two sites consume that result, and both break on a short name:

1. **Member-path probing** — `AuthoredPathResolver.GetProbe` (`AuthoredPathResolver.cs:115`) calls
   `ComponentTypeResolver.Resolve(typeRef)`, and on `null` **throws** the observed
   `InvalidOperationException` (`:116`). This is the throw the user hits.
2. **Component attach (Build)** — `PlanExecutor` calls `ComponentTypeResolver.Resolve` twice: at
   `:147` to `AddComponent` a new component (`add.Type`, from the plan/desired model), and at `:72` to
   pre-resolve an *existing* component from the sidecar `ComponentType` **string** (`Resolve(string)`
   overload). A short name resolves to `null` at both, so the component is neither attached nor matched.

**The identity break is the deeper half of the bug.** Component matching everywhere keys on the raw
`Type.FullName` string, not on a resolver:

- `Differ.ComputeComponentKeys` (`Differ.cs:263`) and `ComponentReconciler.ComputeComponentKeys`
  (`ComponentReconciler.cs:393`) key components by `(Type.FullName, ordinal)`.
- `ExcludeTransform` filters on `c.Type.FullName != "UnityEngine.Transform"`
  (`Differ.cs:160`, `ComponentReconciler.cs:383`).
- The sidecar stores `ComponentType = currentComponents[i].Type.FullName` (`IdentityRemapper.cs:203`),
  and `PlanExecutor:72` re-resolves that string.

The scene snapshot reads component types fully-qualified — `SerializedFieldBridge.BuildTypeRef` sets
`fullName = component.GetType().FullName` (`SerializedFieldBridge.cs:76`), e.g.
`"UnityEngine.Rigidbody"`. So an authored `"Rigidbody"` and the snapshot's `"UnityEngine.Rigidbody"`
are **different keys**: the Differ would try to *add* a Rigidbody and *remove* the scene's, and the
reconciler would treat the authored component as absent. Even without the `GetProbe` throw, a short
name corrupts identity — which is why the fix must normalize the model's `FullName`, not merely make
the resolver clever at each call site.

> Note: the built-in Rigidbody case throws at `GetProbe` before the identity break can manifest. A
> short name for a **user** `MonoBehaviour` (`using MyGame; Component<Enemy>(...)`) hits the identity
> break directly — its snapshot `FullName` is `"MyGame.Enemy"`, its authored `FullName` is `"Enemy"`,
> so it silently fails to match. The fix covers both; the confirmation checklist exercises both.

## The fix

Resolve unqualified type names the way C# itself does — via the file's in-scope `using` directives —
and **normalize the model's `TypeRef.FullName` to the resolved full name in one place**, upstream of
every consumer, so identity, diffing, attach, and probing all agree.

1. **Parse (Core).** Capture the file's plain namespace `using` directives from the
   `CompilationUnitSyntax` the parser already holds, into `ParseResult.Usings`. Core is Unity-free —
   it carries strings, resolves nothing.
2. **Normalize (adapter, single chokepoint).** A new `ComponentTypeNormalizer` walks the parsed model
   and, for each component, resolves its `TypeRef` (using the usings) to a live `System.Type` and
   rewrites `TypeRef.FullName` to `type.FullName`. Runs **before** `AuthoredPathResolver`, Diff,
   Materialize, Reconcile, and identity remapping — so every downstream site sees the qualified name
   and needs no per-site change. A name already qualified (has a dot, resolves as-is) is a no-op.
3. **Helpful, located error.** Unresolvable or **ambiguous** (two in-scope namespaces both define the
   name) → a located error naming the object, the component, and the bad token, suggesting the
   qualified form — *not* a bare `InvalidOperationException`. C# treats ambiguity as an error; this
   mirrors it and never silently picks one.

**Why normalize the model rather than teach each `Resolve` call about usings.** Making
`ComponentTypeResolver` usings-aware is necessary but not sufficient: the model's `FullName` is the
*identity key* (`Differ`/`ComponentReconciler`/sidecar). If the model keeps `"Rigidbody"` while the
snapshot has `"UnityEngine.Rigidbody"`, no amount of clever resolution at attach-time stops the
Differ from churning the component. Normalizing `FullName` once, upstream, fixes resolution and
identity together and honors "fix it where every current and future caller inherits it by default."

## Round-trip stability (the make-or-break detail)

**A short authored name must never churn to the qualified form on Sync.** This falls out of the
design for free, and the spec must keep it that way:

- Reconcile only ever emits a `Component<T>` **token** when it *adds* a component —
  `ComponentPatchApplier.BuildComponentStatementText` builds `"{receiver}.Component<{edit.TypeFullName}>"`
  (`ComponentPatchApplier.cs:99`), reached only from an `AppendComponentStatement` edit. A component
  that already exists in source is matched by identity and its statement text is **left byte-identical**
  — field-value patches are span-local (`FieldArgumentSpans`) and never touch the type token.
- Normalization makes the authored short name's `FullName` (`"UnityEngine.Rigidbody"`) **equal** the
  snapshot's, so the authored `Component<Rigidbody>` *matches* the scene component and is never
  re-emitted. The written token `Rigidbody` survives untouched.
- Therefore **no authored-token carrier is needed** and none is added. The short form is preserved not
  by remembering it, but by never rewriting a matched component's statement.
- A component authored *in the scene* and newly emitted by Sync is written **fully qualified**
  (`Component<UnityEngine.Rigidbody>`) — it comes from the snapshot's `GetType().FullName`. That always
  compiles regardless of usings, so this milestone injects **no** `using` directive on emit (unlike
  the `AssetRefs` using of §17). Fully-qualified new emit + preserved short existing tokens is the
  stable steady state.

## Identity (resolved)

- Component matching keys on `Type.FullName` (above). Normalizing the authored `FullName` to the
  resolved CLR full name makes it equal the snapshot's `GetType().FullName` — matching, diffing, the
  `Transform` exclusion, and the sidecar `ComponentType` string all become consistent. This is the
  fix, not a side effect.
- `TypeRef.Equals` short-circuits on `MonoScriptGuid` and otherwise compares `FullName`+`AssemblyHint`
  (`TypeRef.cs:12-18`). Normalization sets **`FullName` only**; it does not touch `MonoScriptGuid`
  (authored models carry none today, and matching runs through the `FullName` keys above, not
  `TypeRef.Equals`). So normalization introduces no identity regression on the MonoScript path — it
  only makes the `FullName` key correct, which is what every component-matching site actually reads.
- **The sidecar must store the qualified name.** `PlanExecutor:72` re-resolves the sidecar
  `ComponentType` string via `Resolve(string)` (no usings available there). If the sidecar held
  `"Rigidbody"`, a subsequent Build could not pre-resolve the existing component, and its `SetField`
  ops would be silently dropped. Because normalization runs before identity remapping, the model the
  sidecar is written from (`IdentityRemapper.cs:203`) already carries `"UnityEngine.Rigidbody"`. This
  is why normalization must feed the **structural** model, not only the desired model — see the
  Editor deliverables' note on the two parse sites.

## In scope

- **Plain file-scope `using Namespace;` imports** governing `Component<T>` type-argument resolution,
  for both built-in Unity components (`Rigidbody`, `BoxCollider`, `Light`, …) and user
  `MonoBehaviour`s (`Component<Enemy>` under `using MyGame;`).
- **Normalizing the component `TypeRef.FullName`** to the resolved CLR full name in one adapter
  chokepoint, feeding identity, Diff, Materialize, Reconcile, sidecar, and `PlanExecutor`.
- **The `.Set(r => r.member)` member path** for a short-named component — covered transitively: once
  the component type resolves, `AuthoredPathResolver.GetProbe` instantiates the probe and
  `ResolvePath` maps the member as it already does (the member lambda is a syntactic member access, not
  a type-name token; nothing new to resolve there).
- **Ambiguity and unresolvable names → a loud, located error** (object, component, bad token,
  suggested qualified form), never a silent skip and never a silent guess.

## Out of scope

- **`using static` directives** (`using static UnityEngine.Mathf;`) — not a namespace import for type
  resolution; ignored. Only plain `using Namespace;` is captured.
- **Using-aliases** (`using RB = UnityEngine.Rigidbody;`) — the aliased identifier `RB` is not a
  namespace prefix and is not resolved. Flagged as a future refinement, not handled here.
- **Namespace-nested and file-scoped-namespace-nested usings** — only `CompilationUnitSyntax.Usings`
  (top-of-file) is captured. A `using` written *inside* a `namespace { }` block is not collected. The
  common builder shape (`using UnityEngine;` at file top) is fully covered.
- **`global using`** declared in another file — it is not in this file's syntax tree, so it is not
  seen. The builder file must carry its own `using`.
- **Asset/built-in references** (`Asset(...)`, `Builtin(...)`) — those resolve by path/name today and
  are untouched (§17). This milestone is only about component **type** names.
- **Stamping `MonoScriptGuid` onto the authored model** for assembly/namespace-churn robustness — a
  larger identity concern, unrelated to short names, left as-is.

## Core deliverables

`SceneBuilder.Core` — Unity-free; it captures strings and never resolves a type.

- **`ParseResult.Usings : IReadOnlyList<string>`** — defaulting to an empty list (so every existing
  consumer and every serialized artifact is unchanged). Populated by `BuilderParser` from
  `root.Usings`, taking each `UsingDirectiveSyntax` that is a **plain namespace import**: no `Alias`,
  no `StaticKeyword`, `.Name` rendered to its dotted string (e.g. `"UnityEngine"`,
  `"UnityEngine.UI"`). Aliased and `using static` directives are skipped. Order is document order;
  duplicates are harmless.
- **`BuilderParser` change is capture-only** — it still builds `Type = new TypeRef(rawToken)` verbatim
  (`:845`); it does not resolve or rewrite. The raw token remains the parse's record of what the user
  literally wrote (needed for located errors and for the `NodeAnchors`/span machinery). Resolution is
  the adapter's job.
- **No `SceneModel`, `ValueNode`, `PlanOp`, `CanonicalJson`, `Differ`, `Materializer`, or `Reconciler`
  change.** Once the adapter has normalized `TypeRef.FullName` upstream, all of these operate exactly
  as they do for a fully-qualified name today. This is asserted (not modified) by the Diff/reconcile
  tests below.

## Editor adapter deliverables

All in `com.codescenes/Editor/` unless noted.

- **`ComponentTypeResolver.Resolve(TypeRef typeRef, IReadOnlyList<string> usings)`** — a new overload
  beside the existing `Resolve(TypeRef)` / `Resolve(string)`:
  1. Try the existing resolution as-is (`Resolve(typeRef)`); on a hit, return it (qualified names and
     GUID-anchored user scripts are unchanged).
  2. On a miss **and** a dot-free `FullName`, try each namespace in `usings` as a prefix
     (`<ns>.<name>`), collecting every distinct loaded `System.Type` that resolves. Exactly one →
     return it. Zero → return `null` (unresolvable). More than one → signal **ambiguous** (an `out`
     flag / small result carrying the colliding candidates for the error message).
  3. A dotted name that still misses returns `null` unchanged.
  Caching keys must include the usings context so a bare name isn't cached to the wrong type across
  files (e.g. key on `(fullName, ordered-usings)` for the prefix path; the existing `(fullName)` cache
  stays for the as-is path).
- **`ComponentTypeNormalizer` (new)** — the single chokepoint:
  ```csharp
  internal static SceneModel Normalize(
      SceneModel model,
      IReadOnlyList<string> usings,
      IReadOnlyDictionary<string, SourceSpan> componentAnchors);
  ```
  Walks every `ComponentData`, resolves its `TypeRef` via the usings-aware overload, and returns the
  model with each `TypeRef.FullName` rewritten to the resolved `type.FullName` (a no-op when the token
  already equals the resolved name). On `null` → a **located** `ParseException`/error keyed by the
  component's `LogicalId` in `componentAnchors`, naming the owning object, the component token, and
  suggesting the qualified form ("Cannot resolve component type 'Rigidbody'. Did you mean
  'UnityEngine.Rigidbody'? Qualify it, or add a matching `using`."). On **ambiguous** → a located error
  listing the colliding fully-qualified candidates and telling the author to qualify — never a guess.
- **Applied at both structural parse sites, via one shared helper.** There are two adapter sites that
  turn source into a structural model, and **both** must normalize so no path stores an unqualified
  name in the sidecar or feeds an unqualified name to `PlanExecutor`:
  1. `DesiredModelLoader.Load` — normalize immediately after `BuilderParser.Parse`, **before**
     `AuthoredPathResolver.Resolve`, so `GetProbe` receives a qualified `TypeRef` and the desired model
     is normalized. Because `Loaded.Parse.Model` is the structural input `SceneBuilderBuild`/
     `SceneBuilderSync` remap against, it must be the normalized model too.
  2. `SceneBuilderSync` re-parse of the *patched* source (`SceneBuilderSync.cs:275`,
     `IdentityRemapper.Remap(reparsed.Model, …)`) — this writes the sidecar and bypasses
     `DesiredModelLoader`. It must apply the same normalization, or a short name in freshly-written
     source would land unqualified in the sidecar.
  Implement as one helper (e.g. `ParseAndNormalize(source, map)` returning a `ParseResult` whose
  `Model` is normalized) that both sites call, so the guarantee is inherited, not re-remembered per
  caller.
- **`GetProbe`'s throw is superseded, not duplicated.** With normalization upstream,
  `AuthoredPathResolver.GetProbe` (`:115`) always receives a resolvable `TypeRef`; its existing
  `null → InvalidOperationException` becomes an unreachable backstop for the normalized path. The
  user-facing "cannot resolve type" error now originates from `ComponentTypeNormalizer` and is
  **located** (object + component + suggestion), replacing the bare, unlocated `GetProbe` throw.
- **No emit-side change.** Reconcile continues to emit new components fully-qualified from the
  snapshot; matched components are untouched (see Round-trip stability). No `using` injection.

## Authoring API

**None added.** The surface is exactly today's C#: write `using UnityEngine;` (or your own namespace)
and reference the short type name in `Component<T>`. That is the whole point — no new call, no factory,
no attribute. Fully-qualified names keep working unchanged.

```csharp
using UnityEngine;                       // brings Rigidbody, BoxCollider, Light … into scope
using MyGame.Enemies;                    // brings the user MonoBehaviour Enemy into scope

public class DemoScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var cube = scene.Add("Cube");
        cube.Component<Rigidbody>(c => c.Set(r => r.mass, 5f));     // was a hard throw; now Builds
        cube.Component<BoxCollider>(c => c.Set(r => r.isTrigger, true));
        cube.Component<Enemy>(c => c.Set(e => e.health, 100));      // user script, short name
        // Fully-qualified still works, unchanged:
        cube.Component<UnityEngine.Rigidbody>(c => c.Set(r => r.mass, 5f));
    }
}
```

## Core test plan

Core resolves nothing, so its tests cover **capture** and the **no-regression** guarantee, with the
resolver mocked at the adapter boundary. New file `SceneBuilder.Core.Tests/UsingCaptureTests.cs`
(plus noted additions), xUnit, style `Subject_Condition_ExpectedOutcome`.

**Capture** (`UsingCaptureTests.cs`, file-scope `ISceneDefinition` source fixtures):
1. `Parse_FileScopeUsings_CapturedInOrder` — `using UnityEngine;` + `using UnityEngine.UI;` →
   `ParseResult.Usings == ["UnityEngine", "UnityEngine.UI"]`.
2. `Parse_NoUsings_YieldsEmptyList` — a fixture with no `using` → empty, never null.
3. `Parse_UsingStaticAndAlias_AreExcluded` — `using static UnityEngine.Mathf;` and
   `using RB = UnityEngine.Rigidbody;` are **not** captured; a sibling plain `using UnityEngine;` is.
4. `Parse_NamespaceNestedUsing_NotCaptured` — a `using` inside a `namespace { }` block is not in
   `Usings` (only file-scope imports).
5. `Parse_ShortComponentType_TypeRefKeepsRawToken` — `Component<Rigidbody>` → `Type.FullName ==
   "Rigidbody"` verbatim (parser stays syntactic; normalization is the adapter's job).

**No-regression of the value/identity core** (assert existing behavior once the model is already
qualified — i.e. these run on models whose `FullName` is `"UnityEngine.Rigidbody"`, mirroring a
normalized model): extend the existing Differ/Reconcile suites with
6. `Diff_QualifiedComponent_MatchesSnapshotNoChange` — a component whose `FullName` equals the
   snapshot's is matched (no add/remove) — the invariant normalization restores.
7. `Reconcile_MatchedComponent_LeavesStatementTextByteIdentical` — a matched component's
   `Component<...>` statement is not re-emitted (the anti-churn invariant the fix relies on).

(The adapter tests below own the actual short-name→qualified resolution; Core cannot resolve a live
`System.Type`.)

## Unity confirmation checklist → EditMode tests

Per CLAUDE.md, these become EditMode round-trips in `unity-gate/Assets/GateTests/` — a new
`UnqualifiedTypeNameTests.cs` in the established `Direction_Scenario_Expectation` style, driving
`SceneBuilderBuild.Run` / the Sync path against a live editor scene with real
`GameObject`/`SerializedObject`/`GlobalObjectId`.

1. **The headline — short built-in name Builds.** `using UnityEngine; Component<Rigidbody>(c =>
   c.Set(r => r.mass, 5f))`; Build. **Expected:** no throw; the live `Rigidbody.mass == 5`; the
   component is a real `UnityEngine.Rigidbody`.
2. **Short user-script name Builds and matches.** A `MonoBehaviour Enemy` in a namespace;
   `using <ns>; Component<Enemy>(c => c.Set(e => e.health, 100))`; Build. **Expected:** the
   `Enemy` component is attached (not duplicated) and `health == 100`.
3. **Fully-qualified still works (regression).** `Component<UnityEngine.Rigidbody>(...)`; Build.
   **Expected:** identical result to #1 — no behavior change for qualified names.
4. **Anti-churn — Sync of a short name does not re-qualify.** Author `Component<Rigidbody>`, Build,
   then Sync an unrelated edit (or Sync twice with no edit). **Expected:** the source still reads
   `Component<Rigidbody>` — never `Component<UnityEngine.Rigidbody>` — and a no-edit Sync is a **no-op**
   (no patch, no sidecar mtime bump).
5. **Identity across a rebuild.** Build #1's `Component<Rigidbody>`, edit `mass` in the Inspector,
   Sync, then Build again. **Expected:** the same live `Rigidbody` (same `GlobalObjectId`) is updated
   in place — no add/remove churn — proving the sidecar stored the qualified `ComponentType` and
   `PlanExecutor` pre-resolved it.
6. **Unresolvable name → loud, located error.** `using UnityEngine; Component<Rigidbdy>(...)` (typo);
   Build. **Expected:** a located error naming the object, the token `Rigidbdy`, and suggesting a
   qualified form / a missing using; the scene is **not** touched and the Build does not silently
   succeed.
7. **Missing using → located error, not a silent add.** `Component<Rigidbody>` with **no**
   `using UnityEngine;`. **Expected:** the same located "cannot resolve" error — the resolver has no
   prefix to try — telling the author to add the using or qualify.
8. **Ambiguous short name → located error.** Two in-scope namespaces both defining the same type name
   (author a second `MonoBehaviour` named `Rigidbody` in a user namespace and import both); Build.
   **Expected:** a located error listing both fully-qualified candidates and telling the author to
   qualify — nothing is guessed or silently picked.
9. **`using UnityEngine.UI; Component<Image>`** resolves the nested-namespace case. **Expected:** the
   `Image` component attaches and a `.Set` on it applies.

## Dependencies

- **M3** (`specs/completed/04-m3-components-fields.md`) — components + serialized fields; `Component<T>`
  parse, `TypeRef`, `AuthoredPathResolver`, `SerializedFieldBridge`, `PlanExecutor` attach. This is a
  direct hardening of that path.
- **M2** (`specs/completed/03-m2-syncback-transform.md`) + **M2b** — Reconcile + `IdentityRemapper` +
  the sidecar `ComponentType` string this milestone must keep qualified.
- **The `DesiredModelLoader` seam** — the shared source→desired chokepoint this milestone extends with
  a normalization stage.

## Risks / notes

- **Normalize the model, not just the resolver — this is the load-bearing decision.** A usings-aware
  `Resolve` alone leaves the model's `FullName` short, and component identity keys on that string
  (`Differ`/`ComponentReconciler`/sidecar). The result would be a component that resolves at attach
  time yet churns on every Diff. The fix must rewrite `TypeRef.FullName` upstream of all consumers.
- **Two structural parse sites, one guarantee.** `DesiredModelLoader.Load` and the
  `SceneBuilderSync` re-parse of patched source (`:275`) both feed identity/the sidecar. Normalizing
  only the first lets a short name reach the sidecar unqualified, breaking the *next* Build's
  `PlanExecutor:72` pre-resolve and silently dropping its `SetField` ops. Both sites must route
  through the shared normalize helper — an opt-in-per-caller fix would regress the moment a third
  parse site is added.
- **Cache correctness under multiple files.** The usings-aware resolver must not cache a bare name to
  the type it resolved to under a *different* file's usings. Key the prefix-path cache on the usings
  context; leave the existing `(fullName)` cache for already-qualified names.
- **Ambiguity is an error, deliberately.** C# rejects an ambiguous simple name; so does this. Silently
  choosing the first match would assign a plausible-but-wrong component and hide the mistake — exactly
  the class of silent-wrong-data bug the project forbids. #8 pins it.
- **Anti-churn is automatic but fragile to future emit changes.** It holds only because Reconcile
  re-emits the `Component<T>` token exclusively on *add* (`ComponentPatchApplier.cs:99`). Any future
  change that rewrites a matched component's statement text must re-derive its type token from the
  authored source span, not from the normalized `FullName`, or short names would start churning.

## OPEN for the user to ratify

- **Aliases and `using static` are Out of scope.** The common case (`using UnityEngine;`) is covered;
  aliases (`using RB = UnityEngine.Rigidbody;`) are not resolved. Confirm this boundary is acceptable
  for the first pass.
- **No `MonoScriptGuid` stamping onto the authored model.** Short user-script names resolve via
  `FullName` normalization only; GUID-anchored robustness against assembly/namespace renames of the
  *authored* side is unchanged (still snapshot-only). Confirm that is the intended scope.
