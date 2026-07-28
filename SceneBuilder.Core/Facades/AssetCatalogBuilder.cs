using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Facades
{
    // Deterministic, stateless asset-catalog builder. Consumes RAW AssetCatalogEntry rows
    // (unordered, MemberName ignored on input) and produces a byte-stable, type-first,
    // folder-path-disambiguated AssetCatalog. See specs/28-typed-asset-catalogs.md and
    // research.md (b1-t2) for the full algorithm.
    public static class AssetCatalogBuilder
    {
        // Deterministic, stateless. Same input (any order) => byte-identical AssetCatalog.
        public static AssetCatalog Build(IReadOnlyList<AssetCatalogEntry> raw)
        {
            // Pass 1: allocate identifiers walking a folder tree in durable (Guid, FileId) order —
            // this is the ONLY ordering that drives allocation, so a residual sanitize-collision
            // deterministically suffixes the GUID/FileId-higher entry regardless of input order.
            var ordered = raw
                .OrderBy(e => e.Guid, StringComparer.Ordinal)
                .ThenBy(e => e.FileId)
                .ToArray();

            var groupRoots = new Dictionary<string, Node>(StringComparer.Ordinal);
            var built = new AssetCatalogEntry[ordered.Length];
            var groups = new string[ordered.Length];

            // Phase A: allocate every NON-candidate (natural position) in (Guid, FileId) order,
            // exactly as before. Naturals occupy their slots first so a leading-folder collapse
            // (Phase B) can never steal a slot from a naturally-positioned entry.
            for (var i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                var group = SanitizeIdentifier(entry.Group);
                groups[i] = group;

                if (IsCandidate(entry, group))
                    continue;

                built[i] = AllocateAlong(groupRoots, group, entry, entry.FolderSegments);
            }

            // Phase B: for each collapse candidate (leading FolderSegments[0] sanitizes to the
            // group name), take the collapsed slot iff it is free; otherwise keep the entry's
            // full original FolderSegments unchanged, identical to a non-candidate.
            for (var i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                var group = groups[i];

                if (!IsCandidate(entry, group))
                    continue;

                var collapsedFolders = entry.FolderSegments.Skip(1).ToArray();
                built[i] = CollapsedLeafFree(groupRoots, group, entry, collapsedFolders)
                    ? AllocateAlong(groupRoots, group, entry, collapsedFolders)
                    : AllocateAlong(groupRoots, group, entry, entry.FolderSegments);
            }

            // Pass 2: final byte-stable ordering, independent of allocation (input) order.
            Array.Sort(built, CompareEntries);

            return new AssetCatalog { SchemaVersion = 1, Entries = built };
        }

        // Walks (and mutates: get-or-adds folder nodes + allocates the leaf) `folderSegments`
        // under `group`'s root, producing the built entry. Shared by Phase A's natural
        // allocation and both of Phase B's commit branches (collapsed / full-path fallback) —
        // ONE allocation implementation, no duplication across the three commit sites.
        private static AssetCatalogEntry AllocateAlong(
            Dictionary<string, Node> groupRoots, string group, AssetCatalogEntry entry, string[] folderSegments)
        {
            if (!groupRoots.TryGetValue(group, out var node))
            {
                node = new Node();
                groupRoots[group] = node;
            }

            var resolvedSegments = new string[folderSegments.Length];
            for (var s = 0; s < folderSegments.Length; s++)
            {
                var rawSegment = folderSegments[s];
                if (!node.Children.TryGetValue(rawSegment, out var child))
                {
                    var allocatedName = node.Allocator.Allocate(SanitizeIdentifier(rawSegment));
                    child = new Node { AllocatedName = allocatedName };
                    node.Children[rawSegment] = child;
                }

                resolvedSegments[s] = child.AllocatedName!;
                node = child;
            }

            var member = node.Allocator.Allocate(SanitizeIdentifier(LeafRaw(entry)));

            return new AssetCatalogEntry
            {
                Group = group,
                FolderSegments = resolvedSegments,
                MemberName = member,
                Guid = entry.Guid,
                FileId = entry.FileId,
                Path = entry.Path,
                SubAsset = entry.SubAsset,
            };
        }

        // Non-mutating peek: is `entry`'s leaf slot free at `collapsedFolders` under `group`'s
        // root right now? Reads the SAME trie Phase A/B mutate, via IdentifierAllocator.IsAllocated
        // — no parallel occupancy set.
        private static bool CollapsedLeafFree(
            Dictionary<string, Node> groupRoots, string group, AssetCatalogEntry entry, string[] collapsedFolders)
        {
            if (!groupRoots.TryGetValue(group, out var node))
                return true;

            foreach (var rawSegment in collapsedFolders)
            {
                if (!node.Children.TryGetValue(rawSegment, out var child))
                    return true;

                node = child;
            }

            return !node.Allocator.IsAllocated(SanitizeIdentifier(LeafRaw(entry)));
        }

        // A collapse candidate: at least one folder segment, and its LEADING segment sanitizes
        // to the group name. Only the leading segment is ever considered.
        private static bool IsCandidate(AssetCatalogEntry entry, string group) =>
            entry.FolderSegments.Length >= 1 &&
            string.Equals(SanitizeIdentifier(entry.FolderSegments[0]), group, StringComparison.Ordinal);

        // The raw leaf name an entry allocates from — SubAsset if present, else the file's base
        // name. Extracted so the collision peek and the commit derive the identical leaf name.
        private static string LeafRaw(AssetCatalogEntry entry) =>
            string.IsNullOrEmpty(entry.SubAsset)
                ? Path.GetFileNameWithoutExtension(entry.Path)
                : entry.SubAsset;

        private static int CompareEntries(AssetCatalogEntry a, AssetCatalogEntry b)
        {
            var c = string.CompareOrdinal(a.Group, b.Group);
            if (c != 0)
                return c;

            c = CompareFolderSegments(a.FolderSegments, b.FolderSegments);
            if (c != 0)
                return c;

            c = string.CompareOrdinal(a.MemberName, b.MemberName);
            if (c != 0)
                return c;

            c = string.CompareOrdinal(a.Guid, b.Guid);
            if (c != 0)
                return c;

            return a.FileId.CompareTo(b.FileId);
        }

        private static int CompareFolderSegments(string[] a, string[] b)
        {
            var len = Math.Min(a.Length, b.Length);
            for (var i = 0; i < len; i++)
            {
                var c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0)
                    return c;
            }

            return a.Length.CompareTo(b.Length);
        }

        // Mirrors CodeScenes.Analyzers/CatalogEmit.cs SanitizeIdentifier byte-for-byte (spec
        // 28's mandated "shipped CatalogEmit.SanitizeIdentifier convention": every invalid char
        // -> '_', leading-digit-prefixed, keyword-escaped). That type is internal to the ns2.0
        // analyzers assembly and unreferenceable from Core, so it is mirrored here — same
        // cross-project duplication FacadeCatalogBuilder.SanitizeIdentifier already documents.
        // Deliberately NOT FacadeCatalogBuilder.SanitizeIdentifier: that sanitizer additionally
        // DROPS a separator immediately before an uppercase letter (word-boundary-aware, tuned
        // for already-Title-Case GameObject display names like "Front Wheel" -> "FrontWheel");
        // asset folder/file names have no such convention, so every separator becomes '_'
        // (e.g. "Big Rocks" -> "Big_Rocks", "My Assets"/"My-Assets" -> both "My_Assets").
        private static string SanitizeIdentifier(string raw)
        {
            var sb = new StringBuilder(string.IsNullOrEmpty(raw) ? 1 : raw.Length);
            foreach (var c in raw)
                sb.Append(IsValidIdentifierChar(c) ? c : '_');

            var result = sb.ToString();
            if (result.Length == 0 || char.IsDigit(result[0]))
                result = "_" + result;

            if (CSharpKeywords.Contains(result))
                result = "@" + result;

            return result;
        }

        private static bool IsValidIdentifierChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

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

        // A folder-tree node. Shares ONE allocator namespace between its child-folder classes
        // AND its leaf members: an unsuffixed asset name has no "Ref"-style disambiguator from a
        // same-named subfolder, so both must draw from the same allocator to avoid a C# CS0102
        // collision when emitted (b5).
        private sealed class Node
        {
            public string? AllocatedName;
            public readonly IdentifierAllocator Allocator = new();
            public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        }
    }
}
