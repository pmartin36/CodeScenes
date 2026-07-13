using System.Text.Json;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class IdentityMapJsonTests
    {
        private static IdentityMap SampleMap() => new IdentityMap
        {
            SchemaVersion = 1,
            Scene = "Assets/Scenes/Demo.unity",
            Entries = new IdentityMapEntry[]
            {
                new IdentityMapEntry { LogicalId = "Root", GlobalObjectId = "", Kind = "GameObject", ParentLogicalId = null },
                new IdentityMapEntry { LogicalId = "Root/Child", GlobalObjectId = "", Kind = "GameObject", ParentLogicalId = "Root" },
            },
            Assets = System.Array.Empty<AssetEntry>(),
        };

        [Fact]
        public void IdentityMap_RoundTripsThroughJson_PreservingEntryOrder()
        {
            var map = SampleMap();

            var json = IdentityMapJson.Serialize(map);
            var back = IdentityMapJson.Deserialize(json);

            Assert.Equal(map.SchemaVersion, back.SchemaVersion);
            Assert.Equal(map.Scene, back.Scene);
            Assert.Equal(map.Entries.Length, back.Entries.Length);

            for (var i = 0; i < map.Entries.Length; i++)
            {
                Assert.Equal(map.Entries[i], back.Entries[i]);
            }
        }

        [Fact]
        public void IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder()
        {
            var map = SampleMap();

            var json = IdentityMapJson.Serialize(map);

            int schemaVersionIndex = json.IndexOf("\"schemaVersion\"");
            int sceneIndex = json.IndexOf("\"scene\"");
            int entriesIndex = json.IndexOf("\"entries\"");
            int assetsIndex = json.IndexOf("\"assets\"");

            Assert.True(schemaVersionIndex >= 0, "expected \"schemaVersion\" key");
            Assert.True(sceneIndex >= 0, "expected \"scene\" key");
            Assert.True(entriesIndex >= 0, "expected \"entries\" key");
            Assert.True(assetsIndex >= 0, "expected \"assets\" key");

            Assert.True(schemaVersionIndex < sceneIndex, "expected schemaVersion before scene");
            Assert.True(sceneIndex < entriesIndex, "expected scene before entries");
            Assert.True(entriesIndex < assetsIndex, "expected entries before assets");
        }

        [Fact]
        public void IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray()
        {
            var map = SampleMap();

            var json = IdentityMapJson.Serialize(map);

            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");

            Assert.Equal(JsonValueKind.Array, assets.ValueKind);
            Assert.Equal(0, assets.GetArrayLength());
        }
    }
}
