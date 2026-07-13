---
feature: scenebuilder-core-m0-m2
task: b0-t2
agent: tdd-test-writer
updated: 2026-07-13T00:00:00Z
iteration: 1
---

## Decision
WROTE
REASON: TEST_RECOMMENDATION=write, confirmed by own judgment — CanonicalJson is the single determinism substrate PlanJson (b0-t3) and IdentityMapJson (b0-t4) both depend on; a regression here (culture, newline, key order, casing) silently corrupts every downstream on-disk contract.

## Tests
FILES:
  - SceneBuilder.Core.Tests/CanonicalJsonTests.cs (new)
  - SceneBuilder.Core/Serialization/CanonicalJson.cs (new — minimal signature stub, throws NotImplementedException in every member, to make the tests compile and fail at the assertion/panic level)

CASES:
  - CanonicalJson_Serialize_UsesLfNewlines_NoCrLf — asserts output contains no "\r" and does contain "\n".
  - CanonicalJson_Serialize_FormatsNumbersInvariant_UnderNonInvariantCulture — sets CurrentCulture=de-DE, asserts a 1.5 double serializes as "1.5" not "1,5" (restores culture in finally).
  - CanonicalJson_Serialize_IsByteIdenticalAcrossCalls — two Serialize calls on the same value produce identical strings.
  - CanonicalJson_RoundTrips_ArbitrarySmallPoco — Serialize then Deserialize a local test-only POCO, fields equal.
  - CanonicalJson_RespectsPropertyOrder_And_CamelCaseKeys — asserts camelCase keys "name"/"value"/"nested" are present and appear in declaration order ([JsonPropertyOrder] 0,1,2).

RUN: export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln --filter "FullyQualifiedName~CanonicalJsonTests"
RED_CONFIRMED: yes
RED_OUTPUT:
  Failed SceneBuilder.Core.Tests.CanonicalJsonTests.CanonicalJson_RespectsPropertyOrder_And_CamelCaseKeys
  Failed SceneBuilder.Core.Tests.CanonicalJsonTests.CanonicalJson_Serialize_FormatsNumbersInvariant_UnderNonInvariantCulture
  Failed SceneBuilder.Core.Tests.CanonicalJsonTests.CanonicalJson_Serialize_IsByteIdenticalAcrossCalls
  Failed SceneBuilder.Core.Tests.CanonicalJsonTests.CanonicalJson_Serialize_UsesLfNewlines_NoCrLf
  Failed SceneBuilder.Core.Tests.CanonicalJsonTests.CanonicalJson_RoundTrips_ArbitrarySmallPoco
  Error Message: System.NotImplementedException : The method or operation is not implemented.
  Stack Trace: at SceneBuilder.Core.Serialization.CanonicalJson.Serialize[T](T value, JsonSerializerOptions options) in CanonicalJson.cs:line 16
  Total: 5, Failed: 5, Passed: 0 — all panic at the stub's throw, not a missing-symbol compile error. `dotnet build SceneBuilder.sln` succeeds (0 errors) confirming the stub compiles.
redKind: compile-stub

## Stale tests
PRUNED: none
UPDATED: none
(No prior test file referenced CanonicalJson/PlanJson/IdentityMapJson before this task; SceneBuilder.Core.Tests had zero .cs test files. Nothing stale to prune.)

## Contract
Implement `SceneBuilder.Core.Serialization.CanonicalJson` per research.md's INTERFACES, replacing the throw-stub bodies:
- `Options`: cached singleton `JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, `WriteIndented = true`, default null-writing, no custom converters.
- `CreateOptions(converters)`: new `JsonSerializerOptions` instance with the same canonical settings plus any supplied converters added to `.Converters`.
- `Serialize<T>(value, options = null)`: `JsonSerializer.Serialize(value, options ?? Options)` then normalize `\r\n` → `\n` on the result before returning.
- `Deserialize<T>(json, options = null)`: `JsonSerializer.Deserialize<T>(json, options ?? Options)`.
Must hold regardless of ambient `CurrentCulture` (numbers stay invariant — STJ default, no explicit culture handling needed), regardless of repeated calls (byte-identical output for the same input), and must preserve `[JsonPropertyOrder]`/declaration order without alphabetizing.
