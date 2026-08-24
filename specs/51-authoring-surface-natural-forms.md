# Spec 51: the authoring surface rejects natural forms — instance components, typed sub-assets, path constants

Three parser/authoring-surface gaps, each observed live in a one-shot authoring run (SceneBuilderOneShot,
Claude session `a6792c4f`), that reject a natural way to write a builder and force an unnatural rewrite.
None emits wrong output; each refuses valid intent, so the author restructures the scene to appease the
grammar instead of authoring what they mean. CLAUDE.md treats the builder surface as the product an LLM or
a human writes by hand: a form a competent author reaches for that the grammar rejects for no semantic
reason is a defect, not a missing luxury. All three are the parse/recognize/resolve layer refusing an input
it has enough information to accept.

## The measured defect

### Sub-gap A — an instance handle has no component surface, and instance verbs die outside the fluent chain

Authors attach components with `.Component<T>(...)` everywhere (the skill uses it uniformly; it is the whole
component surface on `NodeHandle`, `com.codescenes/Runtime/NodeHandle.cs:117,124`). A prefab instance has no
such method: `InstanceHandle` exposes only `.AddComponent<T>()` (`com.codescenes/Runtime/InstanceHandle.cs:53-61`),
and the shared base `SceneObjectHandle` (`com.codescenes/Runtime/SceneObjectHandle.cs:13`) declares no
`Component<T>` — so `scene.Instance("start.fbx").Component<Rigidbody>()` is a compile error (the method does
not exist on the handle).

Worse, the instance verb that DOES exist is rejected the moment it leaves the fluent chain. The instance
verbs (`Override`/`AddComponent`/`RemoveComponent`/`On`/`AddChild`/`RemoveChild`) are dispatched ONLY when
chained directly onto the `Instance(...)` call in one expression — parser `ProcessInstanceChain`
(`SceneBuilder.Core/Parsing/BuilderParser.Instance.cs:82-111`), recognizer `ProcessInstanceChain`
(`SceneBuilder.Grammar/FlatShapeRecognizer.Instance.cs:37-63`). Capture the instance to a `var` and add a
component in a following statement — the natural multi-statement style — and the receiver routes through the
setter-only re-dispatch (`BuilderParser.cs:307-334`, recognizer `FlatShapeRecognizer.cs:109-116`), whose
shared `ApplyChainedCalls` switch (`BuilderParser.cs:385-435`; recognizer `FlatShapeRecognizer.cs:171-214`)
has no instance-verb cases. `.AddComponent` falls to the `default` arm and is refused. Verbatim, on an
instance handle:

```
Unsupported builder call '.AddComponent(...)'
```

surfaced by `FlatShapeRecognizer.cs:210` (SB1002; the parser would `throw Unreachable()` at
`BuilderParser.cs:434` were the recognizer not first). With neither `.Component<T>()` on the handle nor a
statement-form `.AddComponent`, the author abandoned the prefab instance entirely: they rebuilt the ball as
a plain `NodeHandle` with the FBX nested as a child and `.Component<T>()` on the node.

### Sub-gap B — a sub-asset name that matches several sub-objects cannot be qualified by the target field's type

`Asset("start.fbx", "start")` resolves the sub-object named `start` by NAME ONLY. `TryResolveSubObject`
(`com.codescenes/Editor/AssetReferenceResolver.cs:265-332`) collects every object at the path whose
`candidate.name == subName` (`:272`) with no type filter; when more than one matches,
`ResolveSubObjectOrThrow` (`:343-367`) throws. Verbatim, setting `m_Mesh`:

```
sub-asset name 'start' in 'Assets/.../start.fbx' is AMBIGUOUS — it matches Mesh 'start', Transform 'start', MeshFilter 'start'. Qualify by type (not yet supported) or rename the sub-object.
```

(`AssetReferenceResolver.cs:364`). The target field carries the disambiguator the author should not have to
supply by hand: an `m_Mesh` assignment wants the `Mesh` named `start`, never the `Transform` or the
`MeshFilter`. But the lowering delegate `Resolve(displayPath, subName)` (`:179`) takes no expected-type
argument, and the scan has no type to filter on — the error even names the missing feature ("not yet
supported"). The author's only recourse was to rename or re-export the sub-object.

### Sub-gap C — a `const`-string path prefix concatenated with a literal is rejected where a string literal is required

The DRY instinct for a batch of assets under one folder is a path-prefix constant. It is rejected. Every site
that consumes an asset path demands a bare string LITERAL and folds nothing:

- `Instance(...)` path — recognizer `FlatShapeRecognizer.Instance.cs:31-34` (`IsStringLiteral` else SB1001),
  parser `BuilderParser.Instance.cs:73` via `EvalStringLiteral` (`BuilderParser.cs:707-715`, literal-only).
- `Add(name, ...)` — recognizer `FlatShapeRecognizer.cs:143-146`, shared shape test `IsStringLiteral`
  (`FlatShapeRecognizer.cs:498`).
- `Asset(path, sub)` / `Builtin(...)` values — `ValueNodeParser.ParseAsset` (`:303-322`) via `TryStringLiteral`
  (`ValueNodeParser.cs:334-344`), which accepts only a `StringLiteralExpression`.

Verbatim, from `const Kit = "Assets/.../"; ... Instance(Kit + "start.fbx")`:

```
Expected a string literal at line 20
```

(`FlatShapeRecognizer.Instance.cs:33`). Nothing folds a constant: no pass collects `const string`
declarations (`FindBuildMethod`/`ParseCore` walk only the Build body's builder statements,
`BuilderParser.cs:85-88`; a class-level `const` field is never read, and a body-local `const string`
declaration is itself refused as a non-builder statement — `UnwrapChain` on its literal initializer reports
"Unsupported receiver expression", `FlatShapeRecognizer.cs:390`). The builder is PARSED, not executed, so
`Kit + "start.fbx"` where `Kit` is a `const` string is a compile-time constant the parser has every right to
fold; it refuses because it only pattern-matches a single literal token.

## The fix

Owner: the parse/recognize layer (`SceneBuilder.Core/Parsing/`, `SceneBuilder.Grammar/`) and the sub-asset
resolver (`com.codescenes/Editor/AssetReferenceResolver.cs`). The recognizer and parser must move together —
`RecognizerAgreementTests`/`RecognizerCompletenessTests` pin them — so each grammar change lands in both,
through the one shared shape helper, never per call site.

- **A — an instance handle carries the component surface authors expect, in every position.** Give
  `InstanceHandle` (and `InstanceHandle<TRef>`) a `Component<T>()`/`Component<T>(configure)` alias for
  `AddComponent<T>` so `.Component<T>()` reads identically on a node and an instance, and dispatch it and the
  other instance verbs on a CAPTURED instance handle used in a later statement, not only inline on the
  `Instance(...)` call. The setter-only re-dispatch (`BuilderParser.cs:307-334` /
  `FlatShapeRecognizer.cs:109-116`) routes an instance-verb call on an instance-typed receiver through the
  same per-verb lowering `ProcessInstanceChain` uses (`BuilderParser.Instance.cs:82-111`), rather than
  dropping it into `ApplyChainedCalls`'s node-only switch. The parser already knows the receiver is an
  instance (`NodeBuilder.IsInstance`, `BuilderParser.cs`); the recognizer must track instance-ness in its
  `Scope` so it accepts the same shape (or the change is scoped so the two agree). No verb reaches the
  `default` SB1002 arm for an instance receiver.
- **B — a sub-asset reference disambiguates by the target field's expected type.** Thread the expected
  UnityEngine.Object type of the field being assigned (known adapter-side from the target `SerializedProperty`)
  into the sub-object scan, and when a bare `subName` matches several objects, filter to the one whose type
  is assignable to the expected field type before declaring AMBIGUOUS. `TryResolveSubObject`
  (`AssetReferenceResolver.cs:265`) gains an expected-type parameter (default = today's untyped behavior, so
  callers with no type context are unchanged); `ResolveSubObjectOrThrow` (`:343`) passes it through; the
  lowering delegate `Resolve` (`:179`) is extended to carry it from the apply site. Only a still-ambiguous
  match after the type filter is an error.
- **C — fold compile-time-constant string expressions where a string literal is required.** Add ONE
  constant-string folder that all three literal-demanding sites consult (recognizer `IsStringLiteral`
  `FlatShapeRecognizer.cs:498`, parser `EvalStringLiteral` `BuilderParser.cs:707`, `ValueNodeParser`
  `TryStringLiteral` `:334`), so acceptance can never drift between them. `ParseCore`/`Analyze` first collect
  the const-string environment (class-level `const string` fields and Build-body `const string` locals) and
  thread it in; the folder evaluates a string literal, a reference to a known const string, and a `+`
  concatenation of such constants to its constant value; anything non-constant is still refused. A body-local
  `const string` declaration is recognized as a constant binding, not walked as a builder chain. This is
  scoped deliberately to constant folding: no arbitrary expression evaluation, no interpolation, no runtime
  values.

## Accept when

Each of the three forms parses and builds, proven by a regression test (Core parse/recognize tests for A and
C; an EditMode gate test in `unity-gate/Assets/GateTests/` for B, which crosses the real Unity
`SerializedProperty`/`AssetDatabase` boundary, and for A's end-to-end build).

- **A.** `var ball = scene.Instance("start.fbx"); ball.Component<Rigidbody>();` (the statement form) parses,
  recognizes with no SB1002, and builds a prefab instance carrying a `Rigidbody`; `.Component<T>()` chained
  inline on `Instance(...)` builds the same; a multi-statement configure over the captured instance
  (`ball.Component<SphereCollider>(c => { c.Set(...); c.Set(...); });`) is accepted. No authoring input that
  attaches a component to an instance produces `Unsupported builder call`.
- **B.** `c.Set("m_Mesh", Asset("start.fbx", "start"))`, where `start` names a `Mesh`, a `Transform`, and a
  `MeshFilter` in the imported model, resolves to the `Mesh` sub-object and builds, with no AMBIGUOUS throw;
  a genuinely ambiguous case (two sub-objects of the same expected type named `start`) still reports located
  AMBIGUOUS; a call with no expected-type context resolves exactly as today.
- **C.** A builder with `const string Kit = "Assets/.../";` (class field or Build-body local) that authors
  `Instance(Kit + "start.fbx")`, `Add(Kit + "x")`, and `Asset(Kit + "start.fbx", "start")` parses and builds
  with the folded paths, no `Expected a string literal`; a non-constant expression in the same position is
  still refused; the recognizer and parser agree (agreement tests green).
