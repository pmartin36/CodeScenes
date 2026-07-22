using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ProjectCatalogManifestTests
    {
        private static ProjectCatalogManifest SampleManifest() => new ProjectCatalogManifest
        {
            SchemaVersion = 1,
            Tags = new[] { "Untagged", "Player", "Enemy" },
            Layers = new[]
            {
                new LayerEntry { Index = 0, Name = "Default" },
                new LayerEntry { Index = 6, Name = "Ground" },
            },
        };

        [Fact]
        public void ProjectCatalogManifest_RoundTripsThroughJson_PreservingTagOrderAndNamedLayers()
        {
            var manifest = SampleManifest();

            var json = CanonicalJson.Serialize(manifest);
            var back = CanonicalJson.Deserialize<ProjectCatalogManifest>(json);

            Assert.Equal(manifest.SchemaVersion, back.SchemaVersion);
            Assert.Equal(manifest.Tags, back.Tags);
            Assert.Equal(2, back.Layers.Length);
            for (var i = 0; i < manifest.Layers.Length; i++)
            {
                Assert.Equal(manifest.Layers[i], back.Layers[i]);
            }
        }

        [Fact]
        public void ProjectCatalogManifest_Serialize_IsByteStable()
        {
            var manifest = SampleManifest();

            var jsonA = CanonicalJson.Serialize(manifest);
            var back = CanonicalJson.Deserialize<ProjectCatalogManifest>(jsonA);
            var jsonB = CanonicalJson.Serialize(back);

            Assert.Equal(jsonA, jsonB);
        }

        [Fact]
        public void ProjectCatalogManifest_Serialized_HasSchemaVersionTagsLayersKeys_InOrder()
        {
            var manifest = SampleManifest();

            var json = CanonicalJson.Serialize(manifest);

            int schemaVersionIndex = json.IndexOf("\"schemaVersion\"");
            int tagsIndex = json.IndexOf("\"tags\"");
            int layersIndex = json.IndexOf("\"layers\"");

            Assert.True(schemaVersionIndex >= 0, "expected \"schemaVersion\" key");
            Assert.True(tagsIndex >= 0, "expected \"tags\" key");
            Assert.True(layersIndex >= 0, "expected \"layers\" key");

            Assert.True(schemaVersionIndex < tagsIndex, "expected schemaVersion before tags");
            Assert.True(tagsIndex < layersIndex, "expected tags before layers");
        }

        [Fact]
        public void ProjectCatalogManifest_LayerEntry_Serialized_HasIndexBeforeNameKey()
        {
            var manifest = SampleManifest();

            var json = CanonicalJson.Serialize(manifest);

            using var doc = JsonDocument.Parse(json);
            var layers = doc.RootElement.GetProperty("layers");
            var first = layers[0];

            var props = new System.Collections.Generic.List<string>();
            foreach (var prop in first.EnumerateObject())
            {
                props.Add(prop.Name);
            }

            Assert.Equal(new[] { "index", "name" }, props);
        }
    }
}
