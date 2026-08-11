using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Sync
{
    public record SyncCheckpoint
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; }

        [JsonPropertyOrder(1)]
        public string Scene { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string LastSnapshotHash { get; init; } = "";

        [JsonPropertyOrder(3)]
        public string LastSourceHash { get; init; } = "";

        [JsonPropertyOrder(4)]
        public string LastSidecarHash { get; init; } = "";
    }
}
