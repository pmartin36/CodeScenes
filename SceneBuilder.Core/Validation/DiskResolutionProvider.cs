using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Validation
{
    // b4-t2: off-disk, non-Unity-process twin of UnityResolutionProvider (b3-t1). Resolves
    // component types via a Roslyn metadata index over ProjectLayout.ReferenceAssemblies,
    // resolves asset paths by reading <path>.meta on disk, and defers builtins/sub-assets
    // (existence is editor-only). Mirrors ComponentTypeResolver's usings-aware resolution order
    // (as-is -> bare-name using-prefix probe -> distinct dedup -> 0/1/>=2) so b5 editor/headless
    // parity holds. Never throws.
    public sealed class DiskResolutionProvider : IResolutionProvider
    {
        private static readonly SymbolDisplayFormat FullNameFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

        private readonly IReadOnlyList<string> _referenceAssemblyPaths;
        private readonly string _projectRoot;
        private readonly bool _managedAvailable;

        private CSharpCompilation? _compilation;
        private Dictionary<string, List<string>>? _shortNameIndex;

        public DiskResolutionProvider(ProjectLayout layout)
            : this(layout.ReferenceAssemblies, layout.ProjectRoot, layout.ManagedDllsAvailable)
        {
        }

        // Test seam: build a provider directly from reference assembly paths + a project root,
        // without a full Unity project tree or editor install.
        internal DiskResolutionProvider(
            IReadOnlyList<string> referenceAssemblyPaths, string projectRoot, bool managedAvailable)
        {
            _referenceAssemblyPaths = referenceAssemblyPaths;
            _projectRoot = projectRoot;
            _managedAvailable = managedAvailable;
        }

        public TypeResolution ResolveComponentType(TypeRef type, IReadOnlyList<string> usings)
        {
            if (!_managedAvailable)
            {
                return new TypeResolution.Deferred();
            }

            try
            {
                var fullName = type.FullName;
                if (string.IsNullOrEmpty(fullName))
                {
                    return new TypeResolution.Unresolved(Array.Empty<string>());
                }

                var compilation = GetCompilation();
                var isQualified = fullName.Contains('.');

                var asIs = DedupSymbols(compilation.GetTypesByMetadataName(fullName));
                if (asIs.Count == 1)
                {
                    return new TypeResolution.Resolved(ToFullName(asIs[0]));
                }

                if (asIs.Count >= 2)
                {
                    return new TypeResolution.Ambiguous(DistinctOrdered(asIs.Select(ToFullName)));
                }

                if (isQualified)
                {
                    // Dotted (qualified) name that still misses is not eligible for prefix probing.
                    return new TypeResolution.Unresolved(SuggestTypeNames(compilation, fullName));
                }

                var candidates = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var ns in usings)
                {
                    if (string.IsNullOrEmpty(ns))
                    {
                        continue;
                    }

                    foreach (var symbol in compilation.GetTypesByMetadataName(ns + "." + fullName))
                    {
                        candidates.Add(symbol);
                    }
                }

                if (candidates.Count == 0)
                {
                    return new TypeResolution.Unresolved(SuggestTypeNames(compilation, fullName));
                }

                if (candidates.Count == 1)
                {
                    return new TypeResolution.Resolved(ToFullName(candidates.First()));
                }

                // Ambiguous: two or more DISTINCT matches — do not guess.
                return new TypeResolution.Ambiguous(DistinctOrdered(candidates.Select(ToFullName)));
            }
            catch
            {
                // Never throw: a garbage/unresolvable input is an Unresolved result, not an exception.
                return new TypeResolution.Unresolved(Array.Empty<string>());
            }
        }

        public AssetResolution ResolveAssetPath(string displayPath, string? subAsset)
        {
            if (!_managedAvailable)
            {
                return new AssetResolution.Deferred();
            }

            // Sub-asset existence is editor-only (spec §17/§21) — deferred, not decided here.
            if (subAsset != null)
            {
                return new AssetResolution.Deferred();
            }

            try
            {
                if (string.IsNullOrEmpty(displayPath))
                {
                    return new AssetResolution.Unresolved(Array.Empty<string>());
                }

                var relative = displayPath.Replace('/', Path.DirectorySeparatorChar);
                var metaPath = Path.Combine(_projectRoot, relative) + ".meta";

                if (File.Exists(metaPath))
                {
                    var guid = ReadGuid(metaPath) ?? string.Empty;
                    return new AssetResolution.Resolved(guid, 0L, string.Empty);
                }

                return new AssetResolution.Unresolved(SuggestAssetPaths(displayPath));
            }
            catch
            {
                return new AssetResolution.Unresolved(Array.Empty<string>());
            }
        }

        public AssetResolution ResolveBuiltin(string name, string? typeHint) =>
            // Shape-only: the built-in container path is not a project path and existence is
            // editor-only, so the disk provider never decides it either way.
            new AssetResolution.Deferred();

        private CSharpCompilation GetCompilation()
        {
            if (_compilation != null)
            {
                return _compilation;
            }

            var references = new List<MetadataReference>();
            foreach (var path in _referenceAssemblyPaths)
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Unreadable/locked assembly on disk — skip it rather than fail resolution
                    // entirely.
                }
            }

            _compilation = CSharpCompilation.Create("_disk_resolve", references: references);
            return _compilation;
        }

        private static List<INamedTypeSymbol> DedupSymbols(IEnumerable<INamedTypeSymbol> symbols)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var result = new List<INamedTypeSymbol>();
            foreach (var symbol in symbols)
            {
                if (seen.Add(symbol))
                {
                    result.Add(symbol);
                }
            }

            return result;
        }

        private static string ToFullName(INamedTypeSymbol symbol) => symbol.ToDisplayString(FullNameFormat);

        private static IReadOnlyList<string> DistinctOrdered(IEnumerable<string> names) =>
            names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Best-effort near-miss suggestions for an unresolved type: exact short-name matches
        // (out of scope of the given usings) first, then short-name Levenshtein<=2 matches.
        // Affects suggestion TEXT only, never Ok/pass-fail.
        private IReadOnlyList<string> SuggestTypeNames(CSharpCompilation compilation, string fullName)
        {
            try
            {
                var lastDot = fullName.LastIndexOf('.');
                var shortName = lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
                if (string.IsNullOrEmpty(shortName))
                {
                    return Array.Empty<string>();
                }

                var index = GetShortNameIndex(compilation);

                if (index.TryGetValue(shortName, out var exact) && exact.Count > 0)
                {
                    return exact.OrderBy(n => n, StringComparer.Ordinal).Take(5).ToList();
                }

                var scored = new List<(int distance, string name)>();
                foreach (var kv in index)
                {
                    var distance = Levenshtein(kv.Key, shortName);
                    if (distance > 2)
                    {
                        continue;
                    }

                    foreach (var candidate in kv.Value)
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
            catch
            {
                return Array.Empty<string>();
            }
        }

        private Dictionary<string, List<string>> GetShortNameIndex(CSharpCompilation compilation)
        {
            if (_shortNameIndex != null)
            {
                return _shortNameIndex;
            }

            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            IndexNamespace(compilation.GlobalNamespace, index);
            _shortNameIndex = index;
            return index;
        }

        private static void IndexNamespace(INamespaceSymbol ns, Dictionary<string, List<string>> index)
        {
            foreach (var member in ns.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol childNamespace:
                        IndexNamespace(childNamespace, index);
                        break;

                    case INamedTypeSymbol type:
                        IndexType(type, index);
                        break;
                }
            }
        }

        private static void IndexType(INamedTypeSymbol type, Dictionary<string, List<string>> index)
        {
            var fullName = ToFullName(type);
            if (!index.TryGetValue(type.Name, out var names))
            {
                names = new List<string>();
                index[type.Name] = names;
            }

            if (!names.Contains(fullName))
            {
                names.Add(fullName);
            }

            foreach (var nested in type.GetTypeMembers())
            {
                IndexType(nested, index);
            }
        }

        private static string? ReadGuid(string metaPath)
        {
            const string prefix = "guid:";
            foreach (var line in File.ReadLines(metaPath))
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            return null;
        }

        // Best-effort near-miss suggestion for a missing asset path: the nearest filename (by
        // Levenshtein distance) among files in the same directory, when that directory exists.
        private IReadOnlyList<string> SuggestAssetPaths(string displayPath)
        {
            try
            {
                var normalized = displayPath.Replace('\\', '/');
                var lastSlash = normalized.LastIndexOf('/');
                if (lastSlash < 0)
                {
                    return Array.Empty<string>();
                }

                var dir = normalized.Substring(0, lastSlash);
                var fileName = normalized.Substring(lastSlash + 1);
                if (string.IsNullOrEmpty(fileName))
                {
                    return Array.Empty<string>();
                }

                var fullDir = Path.Combine(_projectRoot, dir.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(fullDir))
                {
                    return Array.Empty<string>();
                }

                var scored = new List<(int distance, string name)>();
                foreach (var file in Directory.GetFiles(fullDir))
                {
                    var candidateName = Path.GetFileName(file);
                    if (candidateName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var distance = Levenshtein(candidateName, fileName);
                    scored.Add((distance, dir + "/" + candidateName));
                }

                return scored
                    .OrderBy(s => s.distance)
                    .ThenBy(s => s.name, StringComparer.Ordinal)
                    .Select(s => s.name)
                    .Take(3)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
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
