using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Facades
{
    // Deterministic, stateless facade-catalog generator. Consumes PrefabHierarchy POCOs (adapter
    // b5-t1 fills them from the real prefab assets) and produces the FacadeCatalog shape (b1-t2).
    // See specs/25-typed-prefab-facades.md and research.md (b1-t3) for the full algorithm.
    public static class FacadeCatalogBuilder
    {
        // Deterministic, stateless. Same input (any order) => byte-identical FacadeCatalog.
        public static FacadeCatalog Build(IReadOnlyList<PrefabHierarchy> prefabs)
        {
            var ordered = prefabs.OrderBy(p => p.Guid, StringComparer.Ordinal).ToArray();

            var byGuid = new Dictionary<string, PrefabHierarchy>(StringComparer.Ordinal);
            foreach (var prefab in ordered)
                byGuid[prefab.Guid] = prefab;

            // Project-scope PropertyName per prefab, allocated walking prefabs in Guid order —
            // this is the durable cross-prefab dedup key (axis 3).
            var propertyNameAllocator = new IdentifierAllocator();
            var propertyNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prefab in ordered)
            {
                var baseDisplay = !string.IsNullOrEmpty(prefab.AssetPath)
                    ? Path.GetFileNameWithoutExtension(prefab.AssetPath)
                    : prefab.Root.RealName;
                propertyNames[prefab.Guid] = propertyNameAllocator.Allocate(SanitizeIdentifier(baseDisplay));
            }

            var builtEntries = new Dictionary<string, FacadeEntry>(StringComparer.Ordinal);

            FacadeEntry EnsureEntry(string guid)
            {
                if (builtEntries.TryGetValue(guid, out var existing))
                    return existing;

                var prefab = byGuid[guid];
                var typeNameAllocator = new IdentifierAllocator();
                var rootTypeName = typeNameAllocator.Allocate(propertyNames[guid] + "Ref");
                var rootChildren = BuildChildren(prefab.Root.Children, typeNameAllocator, byGuid, EnsureEntry);
                var root = new FacadeNode { TypeName = rootTypeName, Children = rootChildren };

                var entry = new FacadeEntry { PropertyName = propertyNames[guid], Guid = guid, Root = root };
                builtEntries[guid] = entry;
                return entry;
            }

            foreach (var prefab in ordered)
                EnsureEntry(prefab.Guid);

            var entries = builtEntries.Values
                .OrderBy(e => e.PropertyName, StringComparer.Ordinal)
                .ThenBy(e => e.Guid, StringComparer.Ordinal)
                .ToArray();

            return new FacadeCatalog { Entries = entries };
        }

        private static FacadeChild[] BuildChildren(
            IReadOnlyList<PrefabHierarchyNode> children,
            IdentifierAllocator typeNameAllocator,
            IReadOnlyDictionary<string, PrefabHierarchy> byGuid,
            Func<string, FacadeEntry> ensureEntry)
        {
            if (children.Count == 0)
                return Array.Empty<FacadeChild>();

            var sorted = children.OrderBy(c => c.LocalId).ToArray();
            var siblingAllocator = new IdentifierAllocator(); // fresh per parent (axis 1, keyed on LocalId order)
            var result = new FacadeChild[sorted.Length];

            for (var i = 0; i < sorted.Length; i++)
            {
                var child = sorted[i];
                var propertyName = siblingAllocator.Allocate(SanitizeIdentifier(child.RealName));

                FacadeNode node;
                if (child.NestedPrefabGuid != null && byGuid.ContainsKey(child.NestedPrefabGuid))
                {
                    // Composed BY REFERENCE to the nested prefab's own FacadeEntry.Root — never
                    // inlined/rebuilt from this node's own Children.
                    node = ensureEntry(child.NestedPrefabGuid).Root;
                }
                else
                {
                    var typeName = typeNameAllocator.Allocate(SanitizeIdentifier(child.RealName) + "Ref");
                    var grandChildren = BuildChildren(child.Children, typeNameAllocator, byGuid, ensureEntry);
                    node = new FacadeNode { TypeName = typeName, Children = grandChildren };
                }

                result[i] = new FacadeChild
                {
                    PropertyName = propertyName,
                    RealName = child.RealName,
                    LocalId = child.LocalId,
                    Node = node,
                };
            }

            return result;
        }

        // CSharpKeywords + keyword-escaping + leading-digit-prefixing mirror
        // CodeScenes.Analyzers/CatalogEmit.cs byte-for-byte (that type is internal to the ns2.0
        // analyzers assembly and unreferenceable from Core). The invalid-char pass diverges from
        // CatalogEmit: spec 25 requires word-boundary-aware display names ("Front Wheel" ->
        // "FrontWheel", not "Front_Wheel") — a separator immediately before an uppercase letter is
        // DROPPED (the case change already signals the word boundary); otherwise it collapses to a
        // single '_' (e.g. "a b" -> "a_b", "a-b" -> "a_b").
        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
            "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };

        private static string SanitizeIdentifier(string raw)
        {
            var sb = new StringBuilder(string.IsNullOrEmpty(raw) ? 1 : raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (IsValidIdentifierChar(c))
                {
                    sb.Append(c);
                    continue;
                }

                var next = i + 1 < raw.Length ? raw[i + 1] : '\0';
                if (!char.IsUpper(next))
                    sb.Append('_');
                // else: drop — the following uppercase letter already signals a word boundary.
            }

            var result = sb.ToString();
            if (result.Length == 0 || char.IsDigit(result[0]))
                result = "_" + result;

            if (CSharpKeywords.Contains(result))
                result = "@" + result;

            return result;
        }

        private static bool IsValidIdentifierChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        // Core's own allocator: BARE-number suffix (Wheel, Wheel2, Wheel3, ...) — NOT
        // CatalogEmit's "_N" form. Loop-until-unique also guards the rare case where a
        // synthesized "Wheel2" collides with a literally-named "Wheel2" sibling.
        private sealed class IdentifierAllocator
        {
            private readonly HashSet<string> _used = new(StringComparer.Ordinal);

            public string Allocate(string sanitized)
            {
                var candidate = sanitized;
                var n = 1;
                while (_used.Contains(candidate))
                {
                    n++;
                    candidate = sanitized + n.ToString(CultureInfo.InvariantCulture);
                }

                _used.Add(candidate);
                return candidate;
            }
        }
    }
}
