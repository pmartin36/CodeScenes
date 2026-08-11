using SceneBuilder.Core.Sync;

namespace SceneBuilder.Core.Serialization
{
    public static class SyncCheckpointJson
    {
        public static string Serialize(SyncCheckpoint checkpoint) => CanonicalJson.Serialize(checkpoint);

        public static SyncCheckpoint Deserialize(string json) => CanonicalJson.Deserialize<SyncCheckpoint>(json);
    }
}
