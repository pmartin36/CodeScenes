using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SceneModelSerializerTests
    {
        private static SceneModel SampleModel() => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "Root",
                    Name = "Root",
                    Transform = new TransformData
                    {
                        Position = new Vec3(1, 2, 3),
                        Rotation = Rotation.EulerToQuat(new Vec3(0, 90, 0)),
                        Scale = Vec3.One,
                    },
                    Children = new[]
                    {
                        new GameObjectNode { LogicalId = "Root/Child", Name = "Child" },
                    },
                },
            },
        };

        [Fact]
        public void CanonicalSerializer_SameModel_IsByteIdenticalAcrossCalls()
        {
            var model = SampleModel();

            var json1 = SceneModelSerializer.Serialize(model);
            var json2 = SceneModelSerializer.Serialize(model);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void SceneModelSerializer_Rotation_EmitsQuaternionForm_NotEuler()
        {
            var model = SampleModel();

            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var rotation = doc.RootElement.GetProperty("roots")[0].GetProperty("transform").GetProperty("rotation");

            Assert.True(rotation.TryGetProperty("w", out var w), "expected quaternion \"w\" component");
            Assert.Equal(0.70710678, rotation.GetProperty("y").GetDouble(), 5);
            Assert.Equal(0.70710678, w.GetDouble(), 5);
            Assert.False(rotation.TryGetProperty("eulerX", out _), "must not emit euler-style rotation fields");
        }

        [Fact]
        public void SceneModelSerializer_Serialize_ThenDeserialize_ReserializesToSameCanonicalJson()
        {
            var model = SampleModel();

            var json = SceneModelSerializer.Serialize(model);
            var back = SceneModelSerializer.Deserialize(json);
            var roundTripJson = SceneModelSerializer.Serialize(back);

            Assert.Equal(json, roundTripJson);
        }
    }
}
