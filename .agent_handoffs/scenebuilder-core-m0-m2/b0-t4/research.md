---
feature: scenebuilder-core-m0-m2
task: b0-t4
agent: tdd-research
updated: 2026-07-13T00:00:00Z
iteration: 1
---

## Mode
ADVERSARIAL (assumptions were provided)

## Verdict on assumptions
VALIDATED
- "ParentLogicalId null serializes as JSON null" — holds. `CanonicalJson.BuildOptions` (Serialization/CanonicalJson.cs:22-39) sets no `DefaultIgnoreCondition`, so System.Text.Json writes nulls by default → `parentLogicalId: null`.
- "GlobalObjectId is '' in M0" — holds. Plain `string` field defaulting to `""`; nothing serializer-specific.
- Extra pin (not in assumptions, but decided here to remove ambiguity): the OUT-OF-SCOPE Editor acceptance example (specs/01-m0-skeleton.md:107) shows an entry WITHOUT `componentType`, while §4 (specs/00-foundation.md:150-154) and M0 line 50-51 list `ComponentType:string?` as a field. The three IN-SCOPE Core tests do not assert on `componentType` presence/absence. Decision: serialize both nullable fields (`ComponentType`, `ParentLogicalId`) as explicit JSON `null`, symmetrically, mirroring the existing Plan/CreateObject record pattern (no `[JsonIgnore]`). This satisfies every in-scope contract and keeps the type faithful to §4. Do NOT special-case-omit componentType.

## Blueprint
APPROACH: Add three plain POCO `record` types in a new `SceneBuilder.Core.Identity` namespace, each using `[JsonPropertyOrder(n)]` for fixed key order (identical convention to Plan.cs/CreateObject.cs). Add `IdentityMapJson` in `SceneBuilder.Core.Serialization` as a thin static wrapper over `CanonicalJson` (exact mirror of PlanJson.cs). No new determinism logic — reuse b0-t2's substrate. No polymorphism (these are not discriminated unions; `Kind` is a plain string field).

INTERFACES:
- `SceneBuilder.Core/Identity/IdentityMap.cs`
  `public record IdentityMap {`
  `  [JsonPropertyOrder(0)] public int SchemaVersion { get; init; }`
  `  [JsonPropertyOrder(1)] public string Scene { get; init; } = "";`
  `  [JsonPropertyOrder(2)] public IdentityMapEntry[] Entries { get; init; } = Array.Empty<IdentityMapEntry>();`
  `  [JsonPropertyOrder(3)] public AssetEntry[] Assets { get; init; } = Array.Empty<AssetEntry>(); }`
- `SceneBuilder.Core/Identity/IdentityMapEntry.cs`
  `public record IdentityMapEntry {`
  `  [JsonPropertyOrder(0)] public string LogicalId { get; init; } = "";`
  `  [JsonPropertyOrder(1)] public string GlobalObjectId { get; init; } = "";`
  `  [JsonPropertyOrder(2)] public string Kind { get; init; } = "GameObject";  // "GameObject" | "Component"`
  `  [JsonPropertyOrder(3)] public string? ComponentType { get; init; }`
  `  [JsonPropertyOrder(4)] public string? ParentLogicalId { get; init; } }`
- `SceneBuilder.Core/Identity/AssetEntry.cs`
  `public record AssetEntry {`
  `  [JsonPropertyOrder(0)] public string Guid { get; init; } = "";`
  `  [JsonPropertyOrder(1)] public string LastKnownPath { get; init; } = "";`
  `  [JsonPropertyOrder(2)] public string TypeHint { get; init; } = ""; }`
- `SceneBuilder.Core/Serialization/IdentityMapJson.cs`
  `public static class IdentityMapJson {`
  `  public static string Serialize(IdentityMap map) => CanonicalJson.Serialize(map);`
  `  public static IdentityMap Deserialize(string json) => CanonicalJson.Deserialize<IdentityMap>(json); }`

DATA_FLOW: `IdentityMap` POCO → `IdentityMapJson.Serialize` → `CanonicalJson.Serialize` (CamelCase naming, WriteIndented, `\r\n`→`\n` normalization) → deterministic JSON string. Reverse via `CanonicalJson.Deserialize<IdentityMap>`. Top-level key order fixed by `[JsonPropertyOrder]` → `schemaVersion, scene, entries, assets`. Empty `Assets` (`Array.Empty<AssetEntry>()`) → `"assets": []`. `Entries` array order preserved intrinsically.

FILES_NEW:
- SceneBuilder.Core/Identity/IdentityMap.cs
- SceneBuilder.Core/Identity/IdentityMapEntry.cs
- SceneBuilder.Core/Identity/AssetEntry.cs
- SceneBuilder.Core/Serialization/IdentityMapJson.cs
- SceneBuilder.Core.Tests/IdentityMapJsonTests.cs
FILES_EDIT: none (all new; Identity/ folder currently holds only .gitkeep — may leave or remove it, no functional impact)

## Duplicate / reuse check
EXISTING:
- SceneBuilder.Core/Serialization/CanonicalJson.cs:16-20 — the deterministic serialize/deserialize substrate. MUST be the only serialization path; IdentityMapJson delegates to it. Do NOT re-implement key order / culture / newline handling.
- SceneBuilder.Core/Serialization/PlanJson.cs:5-10 — exact structural template for IdentityMapJson (static class, two one-line methods).
- SceneBuilder.Core/Plan/Plan.cs:6-16 & Plan/CreateObject.cs:5-12 — the `record` + `[JsonPropertyOrder(n)]` + default-value convention to copy verbatim.
- SceneBuilder.Core/Compat/IsExternalInit.cs — already present; enables `init` accessors on netstandard2.1. No new compat shim needed.
CLEANLINESS: Follow the one-type-per-file layout already used in Plan/. Keep `Kind` a plain `string` (not an enum, not a polymorphic discriminator) — these are data records, not a discriminated union like PlanOp. No `[JsonPolymorphic]`. No custom converters.

## Test surface (feed-forward to test-writer)
PUBLIC_SURFACE: `IdentityMapJson.Serialize(IdentityMap) -> string` and `IdentityMapJson.Deserialize(string) -> IdentityMap`. Serialized top-level object has keys `schemaVersion, scene, entries, assets` in exactly that order. Entries preserve input array order. Empty `Assets` serializes as `[]`. Round-trip is field-preserving. Determinism (invariant culture, `\n` newlines, byte-identical across calls) is inherited from CanonicalJson and covered by b0-t2 — not re-tested here.
SUGGESTED_TESTS:
- `IdentityMap_RoundTripsThroughJson_PreservingEntryOrder` — build a map for `Assets/Scenes/Demo.unity` with >=2 GameObject entries in a specific order (per §4, at least one `{LogicalId:"Root", GlobalObjectId:"", Kind:"GameObject", ParentLogicalId:null}`); Serialize→Deserialize; assert per-field equality of each entry AND that entry order matches. NOTE: compare entry-by-entry (records give value equality per entry) — do NOT `Assert.Equal(map, back)` on the whole `IdentityMap`, because record-generated equality on the `Entries[]`/`Assets[]` array members is reference equality and will fail (same reason PlanJsonTests.cs compares op-by-op).
- `IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder` — assert the four top-level keys appear and in order (index-of ordering check, like CanonicalJsonTests.cs:85-93), keys camelCased.
- `IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray` — map with `Assets` empty; assert serialized JSON contains `"assets": []` (or parse and assert `assets` is an array of length 0).

STATUS: IMPLEMENT
