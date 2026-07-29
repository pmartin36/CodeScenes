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

        // CanonicalJson.BuildOptions sets no DefaultIgnoreCondition (STJ default is "Never"), so a
        // null Vec2? here would otherwise be WRITTEN as a JSON null. Each property carries
        // [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] to omit it entirely when
        // unset (research.md's REFINED A1 finding).
        [JsonPropertyOrder(4)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Vec2? AnchoredPosition { get; init; }

        [JsonPropertyOrder(5)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Vec2? SizeDelta { get; init; }

        [JsonPropertyOrder(6)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Vec2? AnchorMin { get; init; }

        [JsonPropertyOrder(7)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Vec2? AnchorMax { get; init; }

        [JsonPropertyOrder(8)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Vec2? Pivot { get; init; }

        /// <summary>The one Kind test. D1/D6 branch on this on BOTH sides; never re-spell the literal.</summary>
        [JsonIgnore]
        public bool IsRectTransform => Kind == RectTransformFields.Kind;

        [JsonIgnore]
        public ChannelMask DrivenChannels { get; init; } = ChannelMask.None;
    }
}
