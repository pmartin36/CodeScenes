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

        // Equality is by KEY, not by position. The two sides of every comparison come from
        // different producers with different orders: ValueNodeParser records the order the AUTHOR
        // wrote, the live read reports Unity's serialized order. A positional comparison calls two
        // identical values different, so a settled scene re-applies an op forever and the first
        // sync rewrites the user's member order to match Unity's. ORDER still decides emission —
        // this type stays an ordered list and renders in its own order — it just does not decide
        // identity.
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

            // Both directions, because equal counts alone do not rule out a repeated key on one
            // side standing in for a key the other side has and this one does not.
            return AllMatch(_entries, other) && AllMatch(other._entries, this);
        }

        private static bool AllMatch(List<KeyValuePair<string, ValueNode>> entries, FieldMap against)
        {
            foreach (var kv in entries)
            {
                if (!against.TryGetValue(kv.Key, out var theirs) || kv.Value != theirs)
                {
                    return false;
                }
            }

            return true;
        }

        // Order-insensitive, to stay consistent with Equals: per-entry hashes combined by a
        // commutative operation, never HashCode.Add in sequence.
        public override int GetHashCode()
        {
            var hash = 0;
            foreach (var kv in _entries)
            {
                hash ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(kv.Key), kv.Value);
            }

            return hash;
        }
    }
}
