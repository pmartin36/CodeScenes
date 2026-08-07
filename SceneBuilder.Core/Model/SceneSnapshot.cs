using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record SceneSnapshot
    {
        public int SchemaVersion { get; init; }

        public SnapshotNode[] Roots { get; init; } = Array.Empty<SnapshotNode>();

        // Per-TYPE default field templates for the component types present in this snapshot.
        // Type = the component type (TypeRef, keyed by FullName), Fields = every serialized field
        // at a freshly-constructed instance's value, LogicalId unused ("").
        public ComponentData[] ComponentDefaults { get; init; } = Array.Empty<ComponentData>();

        // Per-(component TYPE, serialized member path) public C# spelling
        // (UnityEngine.UI.Slider + "m_OnValueChanged" -> "onValueChanged"). A reflection fact of
        // the type, not the individual component, so it is carried once per snapshot rather than
        // per component instance. Sorted by Type.FullName then SerializedPath, ordinal, when a
        // producer fills it. See MemberSpellingIndex for the lookup over this array.
        public MemberSpelling[] MemberSpellings { get; init; } = Array.Empty<MemberSpelling>();

        // The adapter/Core crossing: which serialized-field roots are not author intent for a given
        // component type. Default excludes nothing, so a snapshot that does not set this diffs and
        // materializes exactly as one with no field-exclusion machinery at all. Not persisted: a
        // serialized behavior seam would change the canonical JSON two independently-constructed
        // snapshots must agree on byte-for-byte.
        [JsonIgnore]
        public IFieldExclusionPolicy FieldExclusions { get; init; } = NoFieldExclusions.Instance;
    }
}
