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

        // The adapter/Core crossing: which serialized-field roots are not author intent for a given
        // component type. Default excludes nothing, so a snapshot that does not set this diffs and
        // materializes exactly as one with no field-exclusion machinery at all. Not persisted: a
        // serialized behavior seam would change the canonical JSON two independently-constructed
        // snapshots must agree on byte-for-byte.
        [JsonIgnore]
        public IFieldExclusionPolicy FieldExclusions { get; init; } = NoFieldExclusions.Instance;
    }
}
