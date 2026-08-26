using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The completeness check for the SurfaceSnap -> AlignTo rename+remodel. A RAW-TEXT substring
    // scan (not syntax-only), so a leftover identifier, comment, string literal, or filename
    // anywhere in tracked source is caught, not just a live symbol reference. The rename is
    // complete only when this scan returns zero matches.
    public class AlignToRenameGuardTests
    {
        private const string BannedToken = "SurfaceSnap";

        private static readonly string[] ScanRoots =
        {
            "SceneBuilder.Core",
            "SceneBuilder.Core.Tests",
            "SceneBuilder.Grammar",
            "SceneBuilder.Analyzers.Tests",
            "CodeScenes.Analyzers",
            "com.codescenes",
            "unity-gate/Assets",
        };

        // This test file's own self-tests below must spell the banned token literally as synthetic
        // fixture text (to prove the scan finds it), so its own source is excluded from the
        // completeness scan it defines -- mirroring ModeArgBypassScanTests.ScanAAllowlist's
        // precedent of excluding the guard's own necessarily-token-bearing file.
        private const string SelfPath = "SceneBuilder.Core.Tests/AlignToRenameGuardTests.cs";

        private static IEnumerable<string> ScannedFiles(string repoRoot)
        {
            foreach (var scanRoot in ScanRoots)
            {
                var rootPath = Path.Combine(repoRoot, scanRoot);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    if (relative.Contains("/obj/", StringComparison.Ordinal)
                        || relative.Contains("/bin/", StringComparison.Ordinal)
                        || relative.Contains("/build/", StringComparison.Ordinal)
                        || relative.StartsWith("build/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (relative == SelfPath)
                    {
                        continue;
                    }

                    yield return relative;
                }
            }
        }

        private static IReadOnlyList<string> Violations(IEnumerable<string> relativePaths, Func<string, string> readText)
        {
            var violations = new List<string>();
            foreach (var relativePath in relativePaths)
            {
                if (relativePath.Contains(BannedToken, StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: file path itself names '{BannedToken}'.");
                    continue;
                }

                if (readText(relativePath).Contains(BannedToken, StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: file text contains '{BannedToken}'.");
                }
            }

            return violations;
        }

        [Fact]
        public void TrackedSource_ContainsNoSurfaceSnapToken()
        {
            var repoRoot = RepoRootLocator.Find();
            var files = ScannedFiles(repoRoot).ToList();
            Assert.True(files.Count > 0, "scan found zero production/test .cs files -- repo root resolution is broken");

            var violations = Violations(files, relativePath => File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            Assert.True(violations.Count == 0,
                $"{violations.Count} file(s) still name 'SurfaceSnap' -- the rename to AlignTo must be complete:\n" +
                string.Join("\n", violations));
        }

        [Fact]
        public void Scan_FileTextNamingTheBannedToken_IsAViolation()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";
            const string synthetic = "// leftover reference to SurfaceSnap in a comment";

            var violations = Violations(new[] { relativePath }, _ => synthetic);

            var violation = Assert.Single(violations);
            Assert.Contains(relativePath, violation);
        }

        [Fact]
        public void Scan_FilePathNamingTheBannedToken_IsAViolation()
        {
            const string relativePath = "com.codescenes/Runtime/SurfaceSnap.cs";

            var violations = Violations(new[] { relativePath }, _ => "// clean");

            Assert.Single(violations);
        }

        [Fact]
        public void Scan_CleanFile_ReportsNoViolation()
        {
            const string relativePath = "com.codescenes/Editor/SomeOtherPass.cs";

            var violations = Violations(new[] { relativePath }, _ => "// nothing to see here");

            Assert.Empty(violations);
        }
    }
}
