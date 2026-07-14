using System;
using System.Collections;
using System.Collections.Generic;

namespace SceneBuilder.Core.Model
{
    // Ordered, immutable string->ValueNode map. Insertion order preserved (canonical
    // SORTED-key emission is b2's job, not this type's). Reused by ValueNode.Nested.Fields
    // and (b1-t3) ComponentData.Fields — define ordered-map-with-deep-equality once, here.
    public sealed class FieldMap : IReadOnlyList<KeyValuePair<string, ValueNode>>
    {
        public static readonly FieldMap Empty = new(Array.Empty<KeyValuePair<string, ValueNode>>());

        private readonly List<KeyValuePair<string, ValueNode>> _entries;

        public FieldMap(IEnumerable<KeyValuePair<string, ValueNode>> entries)
        {
            _entries = new List<KeyValuePair<string, ValueNode>>(entries);
        }

        public ValueNode this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public KeyValuePair<string, ValueNode> this[int index] => _entries[index];

        public int Count => _entries.Count;

        public bool TryGetValue(string key, out ValueNode value)
        {
            foreach (var kv in _entries)
            {
                if (kv.Key == key)
                {
                    value = kv.Value;
                    return true;
                }
            }

            value = null!;
            return false;
        }

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public IEnumerator<KeyValuePair<string, ValueNode>> GetEnumerator() => _entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj is not FieldMap other || _entries.Count != other._entries.Count)
            {
                return false;
            }

            for (var i = 0; i < _entries.Count; i++)
            {
                var mine = _entries[i];
                var theirs = other._entries[i];
                if (!string.Equals(mine.Key, theirs.Key, StringComparison.Ordinal) || mine.Value != theirs.Value)
                {
                    return false;
                }
            }

            return true;
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var kv in _entries)
            {
                hash.Add(kv.Key, StringComparer.Ordinal);
                hash.Add(kv.Value);
            }

            return hash.ToHashCode();
        }
    }
}
