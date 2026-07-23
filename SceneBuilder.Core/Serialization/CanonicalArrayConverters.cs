using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Serialization
{
    // Base for element-array converters that make override-collection arrays byte-stable
    // regardless of input order (canonical determinism, see CanonicalJson.BuildOptions).
    // Sorts a COPY at Write only (model is never mutated); Read passes elements through in
    // file order (already canonical on disk). Mirrors FieldMapJsonConverter's sort-on-Write
    // pattern for object keys, applied here to array elements.
    internal abstract class CanonicalArrayConverter<T> : JsonConverter<T[]>
    {
        protected abstract int Compare(T a, T b);

        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Expected StartArray for {typeof(T).Name}[].");
            }

            var items = new List<T>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return items.ToArray();
                }

                var item = JsonSerializer.Deserialize<T>(ref reader, options)!;
                items.Add(item);
            }

            throw new JsonException($"Unexpected end of JSON while reading {typeof(T).Name}[].");
        }

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
        {
            var sorted = new List<T>(value);
            sorted.Sort(Compare);

            writer.WriteStartArray();

            foreach (var item in sorted)
            {
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndArray();
        }
    }

    // PropertyOverride[]: sort by (Target.SubKey.TargetPrefabId, Target.SubKey.TargetObjectId,
    // Target.ComponentType ordinal, PropertyPath ordinal).
    internal sealed class PropertyOverrideArrayConverter : CanonicalArrayConverter<PropertyOverride>
    {
        protected override int Compare(PropertyOverride a, PropertyOverride b)
        {
            var cmp = a.Target.SubKey.TargetPrefabId.CompareTo(b.Target.SubKey.TargetPrefabId);
            if (cmp != 0) return cmp;

            cmp = a.Target.SubKey.TargetObjectId.CompareTo(b.Target.SubKey.TargetObjectId);
            if (cmp != 0) return cmp;

            cmp = string.CompareOrdinal(a.Target.ComponentType, b.Target.ComponentType);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.PropertyPath, b.PropertyPath);
        }
    }

    // AddedComponent[]: sort by (Target.SubKey.TargetPrefabId, Target.SubKey.TargetObjectId,
    // Target.ComponentType ordinal, Component.Type.FullName ordinal).
    internal sealed class AddedComponentArrayConverter : CanonicalArrayConverter<AddedComponent>
    {
        protected override int Compare(AddedComponent a, AddedComponent b)
        {
            var cmp = a.Target.SubKey.TargetPrefabId.CompareTo(b.Target.SubKey.TargetPrefabId);
            if (cmp != 0) return cmp;

            cmp = a.Target.SubKey.TargetObjectId.CompareTo(b.Target.SubKey.TargetObjectId);
            if (cmp != 0) return cmp;

            cmp = string.CompareOrdinal(a.Target.ComponentType, b.Target.ComponentType);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.Component.Type.FullName, b.Component.Type.FullName);
        }
    }

    // OverrideTarget[]: sort by (SubKey.TargetPrefabId, SubKey.TargetObjectId, ComponentType ordinal).
    // Covers RemovedComponents AND RemovedGameObjects (both are OverrideTarget[]).
    internal sealed class OverrideTargetArrayConverter : CanonicalArrayConverter<OverrideTarget>
    {
        protected override int Compare(OverrideTarget a, OverrideTarget b)
        {
            var cmp = a.SubKey.TargetPrefabId.CompareTo(b.SubKey.TargetPrefabId);
            if (cmp != 0) return cmp;

            cmp = a.SubKey.TargetObjectId.CompareTo(b.SubKey.TargetObjectId);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.ComponentType, b.ComponentType);
        }
    }

    // AddedGameObject[]: sort by (Parent.SubKey.TargetPrefabId, Parent.SubKey.TargetObjectId,
    // Node.Name ordinal). Parent.ChildPath never participates.
    internal sealed class AddedGameObjectArrayConverter : CanonicalArrayConverter<AddedGameObject>
    {
        protected override int Compare(AddedGameObject a, AddedGameObject b)
        {
            var cmp = a.Parent.SubKey.TargetPrefabId.CompareTo(b.Parent.SubKey.TargetPrefabId);
            if (cmp != 0) return cmp;

            cmp = a.Parent.SubKey.TargetObjectId.CompareTo(b.Parent.SubKey.TargetObjectId);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.Node.Name, b.Node.Name);
        }
    }
}
