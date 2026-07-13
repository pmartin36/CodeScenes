using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record TransformData
    {
        [JsonPropertyOrder(0)]
        public string Kind { get; init; } = "Transform";

        [JsonPropertyOrder(1)]
        public Vec3 Position { get; init; } = Vec3.Zero;

        [JsonPropertyOrder(2)]
        public Quat Rotation { get; init; } = Quat.Identity;

        [JsonPropertyOrder(3)]
        public Vec3 Scale { get; init; } = Vec3.One;
    }
}
