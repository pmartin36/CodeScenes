---
feature: scenebuilder-core-m0-m2
task: b0-t2
agent: tdd-research
updated: 2026-07-13T00:00:00Z
iteration: 1
---

## Mode
ADVERSARIAL (assumptions were provided)

## Verdict on assumptions
VALIDATED
Ground-truthed System.Text.Json 8.0.5 on the net8.0 test runtime (throwaway console, forced `de-DE` CurrentCulture):
- `[JsonPropertyOrder]` and declaration order produce a fixed, non-alphabetized key order. No custom `Utf8JsonWriter` needed — the assumption's fallback is unnecessary.
- Numbers format invariantly (`1.5`, not `1,5`) despite `de-DE` culture — STJ never uses CurrentCulture for numbers, so "invariant culture" is automatic.
- Indentation newlines are `\n` only (no `\r\n`) on this runtime.
- `null` reference properties serialize as JSON `null` (default writes nulls) — needed by b0-t4 (`ParentLogicalId: null`).
Refinement (not a rejection): CanonicalJson must ALSO set `PropertyNamingPolicy = CamelCase` — the §4/M0 file shapes use camelCase keys (`schemaVersion`, `scenePath`, `logicalId`, `op`, `parentLogicalId`) while POCOs are PascalCase. This is a shared determinism concern and belongs here so PlanJson/IdentityMapJson inherit it rather than re-specifying. Key-order determinism relies on POCOs (b0-t3/b0-t4) annotating `[JsonPropertyOrder]`; CanonicalJson guarantees STJ will not re-sort, the POCOs make order explicit.

## Blueprint
APPROACH: A single `static class CanonicalJson` in `SceneBuilder.Core/Serialization` that owns one canonical `JsonSerializerOptions` (camelCase names, `WriteIndented=true`, writes nulls, default invariant numbers) and routes ALL (de)serialization through `Serialize`/`Deserialize` entry points that unconditionally normalize `\r\n`→`\n` on the output. Downstream serializers that need custom converters (PlanOp `op` discriminator in b0-t3, extended in b1-t4) get their options from `CreateOptions(converters)` and still pass through `CanonicalJson.Serialize/Deserialize`, so newline normalization + naming policy are inherited by default, never opt-in.

INTERFACES (namespace `SceneBuilder.Core.Serialization`):
```
public static class CanonicalJson
{
    // Cached, canonical base options (camelCase, indented, invariant, writes nulls). No custom converters.
    public static JsonSerializerOptions Options { get; }

    // Build a fresh options instance with the canonical settings PLUS caller converters
    // (e.g. the PlanOp discriminator converter). Returns a new instance each call so callers
    // may add converters without mutating/free­zing the shared Options.
    public static JsonSerializerOptions CreateOptions(IEnumerable<JsonConverter>? converters = null);

    // Always normalizes CRLF->LF on the returned text. options defaults to Options.
    public static string Serialize<T>(T value, JsonSerializerOptions? options = null);

    // options defaults to Options.
    public static T Deserialize<T>(string json, JsonSerializerOptions? options = null);
}
```
Canonical options configuration (both in the `Options` singleton and in `CreateOptions`):
- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `WriteIndented = true`
- leave `DefaultIgnoreCondition = Never` (default) so nulls are written
- default number handling / default encoder (deterministic; do not set a culture — STJ is invariant by design)

DATA_FLOW: caller value → `JsonSerializer.Serialize(value, options ?? Options)` → `.Replace("\r\n","\n")` → returned string. Deserialize is `JsonSerializer.Deserialize<T>(json, options ?? Options)` (no input normalization needed). `CreateOptions` clones the canonical settings into a new `JsonSerializerOptions` and adds each supplied converter to `.Converters`.

FILES_NEW: [SceneBuilder.Core/Serialization/CanonicalJson.cs]
FILES_EDIT: [none]  (Serialization/ currently holds only .gitkeep; ~60-line new file, far under any size budget)

## Duplicate / reuse check
EXISTING: [none — SceneBuilder.Core/Serialization/ contains only .gitkeep; grep for `CanonicalJson`/`JsonSerializerOptions`/`JsonPropertyOrder` across the repo (excluding obj/bin) returned no matches. No existing determinism helper to reuse; STJ is the only dependency.]
CLEANLINESS:
- csproj already references `System.Text.Json` 8.0.5 and `Nullable` is enabled; `Compat/IsExternalInit.cs` already exists so records/`init` work on netstandard2.1 (relevant to b0-t3/t4 POCOs, not this file).
- Put determinism (naming policy, indentation, newline normalization) ONCE here; PlanJson (b0-t3) and IdentityMapJson (b0-t4) must NOT re-declare `JsonSerializerOptions` — they consume `CanonicalJson.Options` / `CanonicalJson.CreateOptions(...)` and call `CanonicalJson.Serialize/Deserialize`. This is the single substrate the seam (bucket b0) mandates.
- Do not add a per-caller newline fixup; normalization lives inside `CanonicalJson.Serialize` so every current and future serializer inherits it.

## Test surface (feed-forward to test-writer)
PUBLIC_SURFACE: `CanonicalJson.Serialize<T>`/`Deserialize<T>` and `CanonicalJson.Options`/`CreateOptions`. Observable contract: output uses camelCase keys, 2-space indentation with `\n` (never `\r\n`) newlines, invariant-culture number formatting regardless of ambient culture, property order following `[JsonPropertyOrder]`/declaration order (STJ never alphabetizes), nulls emitted as JSON `null`, and byte-identical output across repeated calls on the same value.
SUGGESTED_TESTS:
  - CanonicalJson_Serialize_UsesLfNewlines_NoCrLf (assert output contains no "\r").
  - CanonicalJson_Serialize_FormatsNumbersInvariant_UnderNonInvariantCulture (set Thread CurrentCulture to de-DE, assert a decimal serializes with '.').
  - CanonicalJson_Serialize_IsByteIdenticalAcrossCalls (two calls, same bytes).
  - CanonicalJson_RoundTrips_ArbitrarySmallPoco (Serialize→Deserialize equal) — a local POCO, since Plan/IdentityMap arrive in b0-t3/t4.
  - CanonicalJson_RespectsPropertyOrder_And_CamelCaseKeys (assert key substring order in output).
Note: keep tests over a small local test-only POCO; do not couple this task's tests to Plan/IdentityMap (those are exercised by b0-t3/t4). TEST_RECOMMENDATION was `write`.

STATUS: IMPLEMENT
