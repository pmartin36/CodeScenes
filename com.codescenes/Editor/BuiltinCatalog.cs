#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// The single place that knows Unity's two built-in resource containers ('Library/unity default
    /// resources' and 'Resources/unity_builtin_extra'). Builds a process-lifetime, lazily-constructed
    /// index over both via <see cref="AssetDatabase.LoadAllAssetsAtPath"/> +
    /// <see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
    /// — never <c>Resources.GetBuiltinResource</c>, which silently returns the WRONG object for several
    /// primitives. Every query answers from the built indices; the containers are scanned exactly once
    /// per editor process.
    /// </summary>
    internal static class BuiltinCatalog
    {
        internal const string BuiltinResourcesPath = "Library/unity default resources";
        internal const string BuiltinExtraPath = "Resources/unity_builtin_extra";
        internal const string BuiltinResourcesGuid = "0000000000000000e000000000000000";
        internal const string BuiltinExtraGuid = "0000000000000000f000000000000000";

        /// <summary>True for an authored path naming a built-in resource container itself (not a
        /// specific object inside it) — the single place that knows both container path literals.</summary>
        internal static bool IsContainerPath(string? path) =>
            path == BuiltinResourcesPath || path == BuiltinExtraPath;

        private readonly struct Entry
        {
            internal readonly UnityEngine.Object Obj;
            internal readonly string Name;
            internal readonly string TypeName;
            internal readonly string Guid;
            internal readonly long FileId;

            internal Entry(UnityEngine.Object obj, string name, string typeName, string guid, long fileId)
            {
                Obj = obj;
                Name = name;
                TypeName = typeName;
                Guid = guid;
                FileId = fileId;
            }
        }

        private static List<Entry>? _entries;
        private static Dictionary<string, List<Entry>>? _byName;
        private static Dictionary<(string guid, long fileId), Entry>? _byId;

        /// <summary>Test-visible: how many times the containers have been scanned this process.</summary>
        internal static int BuildCount { get; private set; }

        /// <summary>
        /// Exact ordinal name match, optionally narrowed by <paramref name="typeHint"/> (exact
        /// <c>GetType().Name</c>, ignored when null/empty). 0 matches → null, not ambiguous. Exactly 1
        /// match → that object. More than 1 match → null AND <paramref name="ambiguous"/> = true — never
        /// guesses.
        /// </summary>
        internal static UnityEngine.Object? Resolve(string name, string? typeHint, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrEmpty(name) || !Catalog().byName.TryGetValue(name, out var candidates))
            {
                return null;
            }

            List<Entry>? matches = null;
            foreach (var entry in candidates)
            {
                if (!string.IsNullOrEmpty(typeHint) && entry.TypeName != typeHint)
                {
                    continue;
                }

                (matches ??= new List<Entry>()).Add(entry);
            }

            if (matches == null || matches.Count == 0)
            {
                return null;
            }

            if (matches.Count > 1)
            {
                ambiguous = true;
                return null;
            }

            return matches[0].Obj;
        }

        /// <summary>
        /// Inverse lookup: a live built-in's own <c>(guid, fileId)</c> → its authored name and concrete
        /// type. A pair that derives no catalog object → false with empty strings (never null).
        /// <paramref name="nameIsAmbiguous"/> is whether the bare name (regardless of type) would be
        /// ambiguous via <see cref="Resolve"/>.
        /// </summary>
        internal static bool TryDeriveName(
            string guid, long fileId, out string name, out string typeName, out bool nameIsAmbiguous)
        {
            var catalog = Catalog();
            if (!catalog.byId.TryGetValue((guid, fileId), out var entry))
            {
                name = "";
                typeName = "";
                nameIsAmbiguous = false;
                return false;
            }

            name = entry.Name;
            typeName = entry.TypeName;
            nameIsAmbiguous = catalog.byName.TryGetValue(entry.Name, out var sameName) && sameName.Count > 1;
            return true;
        }

        /// <summary>
        /// Near-miss names for the located error: over the distinct catalog names (optionally
        /// type-filtered), keep a name when its Levenshtein distance to <paramref name="name"/> is ≤2 or
        /// either string contains the other (ordinal, case-insensitive); order by (distance, name
        /// ordinal); capped at 5, deduped. Runs only on the error path.
        /// </summary>
        internal static IEnumerable<string> Suggest(string name, string? typeHint)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Array.Empty<string>();
            }

            var catalog = Catalog();
            var lowerName = name.ToLowerInvariant();
            var scored = new List<(int distance, string name)>();

            foreach (var kv in catalog.byName)
            {
                if (!string.IsNullOrEmpty(typeHint) && !kv.Value.Any(e => e.TypeName == typeHint))
                {
                    continue;
                }

                var candidate = kv.Key;
                var lowerCandidate = candidate.ToLowerInvariant();
                var distance = Levenshtein(lowerCandidate, lowerName);
                var isNearMiss = distance <= 2
                    || lowerCandidate.IndexOf(lowerName, StringComparison.Ordinal) >= 0
                    || lowerName.IndexOf(lowerCandidate, StringComparison.Ordinal) >= 0;

                if (isNearMiss)
                {
                    scored.Add((distance, candidate));
                }
            }

            return scored
                .OrderBy(s => s.distance)
                .ThenBy(s => s.name, StringComparer.Ordinal)
                .Select(s => s.name)
                .Distinct()
                .Take(5)
                .ToList();
        }

        /// <summary>
        /// Distinct concrete type names of every catalog object called <paramref name="name"/>,
        /// ordinal-ordered; empty when unknown.
        /// </summary>
        internal static IReadOnlyList<string> CandidateTypeNames(string name)
        {
            if (string.IsNullOrEmpty(name) || !Catalog().byName.TryGetValue(name, out var candidates))
            {
                return Array.Empty<string>();
            }

            return candidates
                .Select(e => e.TypeName)
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
        }

        private static (Dictionary<string, List<Entry>> byName, Dictionary<(string, long), Entry> byId) Catalog()
        {
            if (_entries != null)
            {
                return (_byName!, _byId!);
            }

            var entries = new List<Entry>();
            Scan(BuiltinResourcesPath, entries);
            Scan(BuiltinExtraPath, entries);

            var byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            var byId = new Dictionary<(string, long), Entry>();
            foreach (var entry in entries)
            {
                if (!byName.TryGetValue(entry.Name, out var list))
                {
                    list = new List<Entry>();
                    byName.Add(entry.Name, list);
                }

                list.Add(entry);

                var key = (entry.Guid, entry.FileId);
                if (!byId.ContainsKey(key))
                {
                    byId.Add(key, entry);
                }
            }

            BuildCount++;
            _byName = byName;
            _byId = byId;
            _entries = entries;
            return (byName, byId);
        }

        private static void Scan(string path, List<Entry> entries)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj == null)
                {
                    continue;
                }

                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out var fileId)
                    || string.IsNullOrEmpty(guid))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(obj.name))
                {
                    continue;
                }

                entries.Add(new Entry(obj, obj.name, obj.GetType().Name, guid, fileId));
            }
        }

        private static int Levenshtein(string a, string b)
        {
            var lenA = a.Length;
            var lenB = b.Length;
            var d = new int[lenA + 1, lenB + 1];

            for (var i = 0; i <= lenA; i++)
            {
                d[i, 0] = i;
            }

            for (var j = 0; j <= lenB; j++)
            {
                d[0, j] = j;
            }

            for (var i = 1; i <= lenA; i++)
            {
                for (var j = 1; j <= lenB; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[lenA, lenB];
        }
    }
}
