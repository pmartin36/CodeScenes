using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public enum PrimitiveKind { Bool, Int, Long, Float, Double, String }

    // Discriminated union of every M3 field-value kind. See spec §Value-equality:
    // equality is EXACT on canonical form (no float tolerance); List/Nested/Enum are
    // deep + order-significant.
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(ValueNode.Primitive), "Primitive")]
    [JsonDerivedType(typeof(ValueNode.Enum), "Enum")]
    [JsonDerivedType(typeof(ValueNode.Vec2), "Vec2")]
    [JsonDerivedType(typeof(ValueNode.Vec3), "Vec3")]
    [JsonDerivedType(typeof(ValueNode.Vec4), "Vec4")]
    [JsonDerivedType(typeof(ValueNode.Quat), "Quat")]
    [JsonDerivedType(typeof(ValueNode.Color), "Color")]
    [JsonDerivedType(typeof(ValueNode.Nested), "Nested")]
    [JsonDerivedType(typeof(ValueNode.List), "List")]
    [JsonDerivedType(typeof(ValueNode.Unsupported), "Unsupported")]
    [JsonDerivedType(typeof(ValueNode.AssetRef), "AssetRef")]
    public abstract record ValueNode
    {
        public sealed record Primitive(
            [property: JsonPropertyName("primitiveType")]
            [property: JsonConverter(typeof(JsonStringEnumConverter))]
            PrimitiveKind Kind,
            object? Value) : ValueNode
        {
            public static Primitive Bool(bool value) => new(PrimitiveKind.Bool, value);
            public static Primitive Int(int value) => new(PrimitiveKind.Int, value);
            public static Primitive Long(long value) => new(PrimitiveKind.Long, value);
            public static Primitive Float(float value) => new(PrimitiveKind.Float, value);
            public static Primitive Double(double value) => new(PrimitiveKind.Double, value);
            public static Primitive String(string value) => new(PrimitiveKind.String, value);
        }

        public sealed record Enum(string TypeFullName, IReadOnlyList<string> Members, bool IsFlags) : ValueNode
        {
            public bool Equals(Enum? other) =>
                other is not null
                && string.Equals(TypeFullName, other.TypeFullName, StringComparison.Ordinal)
                && IsFlags == other.IsFlags
                && Members.SequenceEqual(other.Members, StringComparer.Ordinal);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(TypeFullName, StringComparer.Ordinal);
                hash.Add(IsFlags);
                foreach (var member in Members)
                {
                    hash.Add(member, StringComparer.Ordinal);
                }

                return hash.ToHashCode();
            }
        }

        public sealed record Vec2(global::SceneBuilder.Core.Model.Vec2 Value) : ValueNode;
        public sealed record Vec3(global::SceneBuilder.Core.Model.Vec3 Value) : ValueNode;
        public sealed record Vec4(global::SceneBuilder.Core.Model.Vec4 Value) : ValueNode;
        public sealed record Quat(global::SceneBuilder.Core.Model.Quat Value) : ValueNode;
        public sealed record Color(global::SceneBuilder.Core.Model.Color Value) : ValueNode;

        // Equality delegates to FieldMap (see FieldMap.cs — deep equality not yet implemented).
        public sealed record Nested(FieldMap Fields) : ValueNode;

        public sealed record List(IReadOnlyList<ValueNode> Items) : ValueNode
        {
            public bool Equals(List? other) =>
                other is not null && Items.SequenceEqual(other.Items);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                foreach (var item in Items)
                {
                    hash.Add(item);
                }

                return hash.ToHashCode();
            }
        }

        public sealed record Unsupported(string RawToken) : ValueNode;

        // Default record equality is correct: delegates to AssetRef.Equals through
        // EqualityComparer<AssetRef?>.Default (null-safe).
        public sealed record AssetRef(global::SceneBuilder.Core.Model.AssetRef? Ref) : ValueNode;
    }
}
