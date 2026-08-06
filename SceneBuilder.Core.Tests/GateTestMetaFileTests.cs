using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SceneBuilder.Core.Tests
{
    // Every `.cs` under the gate-test directory needs a committed sibling `.meta`, or Unity never
    // imports it and the test it defines never runs -- a silent hole that looks identical to a
    // passing suite. The rule is per-file and count-free so it stays correct as the directory grows.
    internal static class GateTestMetaFiles
    {
        // Repo-relative, '/'-separated. Path.Combine accepts it on both platforms.
        public const string RelativeDirectory = "unity-gate/Assets/GateTests";

        // `paths` is every file path under the gate-test directory, repo-relative and '/'-separated.
        // Returns one line per `.cs` whose sibling `<path>.meta` is absent from `paths`; empty when
        // every source file is paired.
        public static IReadOnlyList<string> MissingMetaFiles(IEnumerable<string> paths)
        {
            var all = new HashSet<string>(paths, StringComparer.Ordinal);
            return all.Where(p => p.EndsWith(".cs", StringComparison.Ordinal) && !all.Contains(p + ".meta"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => $"{p}: no sibling '{Path.GetFileName(p)}.meta'. Unity imports a file only when " +
                             "its .meta is present, so an EditMode test without one never runs and the suite " +
                             "is green without it. Commit the .meta next to the .cs.")
                .ToList();
        }
    }

    public class GateTestMetaFileTests
    {
        [Fact]
        public void EveryGateTestSourceFile_HasASiblingMetaFile()
        {
            var repoRoot = RepoRootLocator.Find();
            var dir = Path.Combine(repoRoot, GateTestMetaFiles.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(Directory.Exists(dir), $"gate-test directory not found at '{dir}'");

            var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
                .ToList();

            var csCount = all.Count(p => p.EndsWith(".cs", StringComparison.Ordinal));
            Assert.True(csCount > 0, "enumeration found zero .cs files under the gate-test directory");

            var missing = GateTestMetaFiles.MissingMetaFiles(all);

            Assert.True(missing.Count == 0, string.Join("\n", missing));
        }

        [Fact]
        public void MissingMetaFiles_SourceFileWithoutMeta_IsReportedNamingTheFile()
        {
            var paths = new[]
            {
                "unity-gate/Assets/GateTests/Paired.cs",
                "unity-gate/Assets/GateTests/Paired.cs.meta",
                "unity-gate/Assets/GateTests/Unpaired.cs",
            };

            var missing = GateTestMetaFiles.MissingMetaFiles(paths);

            var problem = Assert.Single(missing);
            Assert.Contains("unity-gate/Assets/GateTests/Unpaired.cs", problem);
        }

        [Fact]
        public void MissingMetaFiles_FullyPairedInput_ReportsNothing()
        {
            var paths = new[]
            {
                "unity-gate/Assets/GateTests/Paired.cs",
                "unity-gate/Assets/GateTests/Paired.cs.meta",
            };

            var missing = GateTestMetaFiles.MissingMetaFiles(paths);

            Assert.Empty(missing);
        }
    }
}
