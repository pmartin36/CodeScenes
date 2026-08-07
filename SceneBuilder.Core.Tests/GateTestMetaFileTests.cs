using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SceneBuilder.Core.Tests
{
    // Every `.cs` under the Unity gate project's Assets tree needs a committed sibling `.meta`, or
    // Unity never imports it -- for a fixture that is a silent hole that surfaces later as an
    // unrelated compile error, and for a test file it means the test it defines never runs. The
    // rule is per-file and count-free so it stays correct as the tree grows.
    internal static class GateTestMetaFiles
    {
        // Repo-relative, '/'-separated. Path.Combine accepts it on both platforms.
        public const string RelativeDirectory = "unity-gate/Assets";

        // `paths` is every file path under the Unity gate project's Assets tree, repo-relative and
        // '/'-separated.
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
        public void RelativeDirectory_CoversTheWholeUnityAssetsTree()
        {
            // A missing .meta anywhere under the Unity project's Assets tree, not just under
            // GateTests, leaves Unity silently skipping the file's import -- the scan must cover
            // the whole tree so a fixture without a .meta fails the gate instead of vanishing.
            Assert.Equal("unity-gate/Assets", GateTestMetaFiles.RelativeDirectory);
        }

        [Fact]
        public void EveryUnityAssetSourceFile_HasASiblingMetaFile()
        {
            var repoRoot = RepoRootLocator.Find();
            var dir = Path.Combine(repoRoot, GateTestMetaFiles.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(Directory.Exists(dir), $"Unity gate Assets directory not found at '{dir}'");

            var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
                .ToList();

            var csCount = all.Count(p => p.EndsWith(".cs", StringComparison.Ordinal));
            Assert.True(csCount > 0, "enumeration found zero .cs files under the Unity gate Assets directory");

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
