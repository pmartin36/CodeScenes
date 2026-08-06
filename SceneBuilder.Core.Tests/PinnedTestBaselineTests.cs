using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SceneBuilder.Core.Tests
{
    internal sealed record PinnedEntry(string Path, string Kind, string Sha256);

    // Loads the recorded baseline and compares it against live file bytes. `Compare` is pure and
    // takes a byte provider so its negative cases can be driven with synthetic content instead of
    // by mutating a real pinned file.
    internal static class PinnedTestBaseline
    {
        public const string RelativePath = "SceneBuilder.Core.Tests/PinnedTestBaseline.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public sealed record Document(
            string BaselineCommitSha,
            string HashAlgorithm,
            int RecordedEntryCount,
            IReadOnlyList<string> Omissions,
            IReadOnlyList<PinnedEntry> Entries);

        public static Document Load(string repoRoot)
        {
            var full = Path.Combine(repoRoot, RelativePath);
            var json = File.ReadAllText(full);
            return JsonSerializer.Deserialize<Document>(json, Options)
                ?? throw new InvalidOperationException($"{full} deserialized to null");
        }

        // Reports one line per problem: a missing pinned file, or one whose bytes no longer match
        // the recorded hash. Empty when every entry matches. Applies the identical check to every
        // entry regardless of `Kind` -- there is no branch on it.
        public static IReadOnlyList<string> Compare(
            IReadOnlyList<PinnedEntry> entries, Func<string, byte[]?> readBytes)
        {
            var problems = new List<string>();
            foreach (var entry in entries)
            {
                var bytes = readBytes(entry.Path);
                if (bytes == null)
                {
                    problems.Add(
                        $"{entry.Path}: pinned file no longer exists. This feature is behavior-preserving; a pinned test file may not be deleted.");
                    continue;
                }

                var actual = Sha256Hex(bytes);
                if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{entry.Path}: content changed since the pinned baseline (expected sha256 {entry.Sha256}, found {actual}). A migration that needs a pinned test edited has changed behavior and is wrong.");
                }
            }

            return problems;
        }

        public static string Sha256Hex(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public class PinnedTestBaselineTests
    {
        private static readonly string[] FloorPaths =
        {
            "SceneBuilder.Core.Tests/AssetRefLoweringTests.cs",
            "SceneBuilder.Core.Tests/AssetRefDiffTests.cs",
            "SceneBuilder.Core.Tests/AssetRefMaterializeTests.cs",
            "SceneBuilder.Core.Tests/AssetRefPlanTests.cs",
            "SceneBuilder.Core.Tests/AssetRefReconcileTests.cs",
            "SceneBuilder.Core.Tests/AssetRefValueTests.cs",
            "SceneBuilder.Core.Tests/PlanningValidatorTests.cs",
            "SceneBuilder.Core.Tests/SourceExprTests.cs",
            "unity-gate/Assets/GateTests/RoundTripBuiltinRefCodeToSceneTests.cs",
            "unity-gate/Assets/GateTests/RoundTripBuiltinRefSceneToCodeTests.cs",
            "unity-gate/Assets/GateTests/RoundTripBuiltinRefEmittedCodeTests.cs",
            "unity-gate/Assets/GateTests/RoundTripBuiltinRefErrorTests.cs",
            "unity-gate/Assets/GateTests/BuiltinRefGateHarness.cs",
            "unity-gate/Assets/GateTests/HiddenSerializedFieldSyncTests.cs",
            "unity-gate/Assets/GateTests/SerializedFieldNativeEnumReadTests.cs",
            "unity-gate/Assets/GateTests/RoundTripNestedValueTests.cs",
        };

        private static string ExpectedHash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        [Fact]
        public void Baseline_EveryPinnedFile_MatchesRecordedHash()
        {
            var repoRoot = RepoRootLocator.Find();
            Assert.True(
                File.Exists(Path.Combine(repoRoot, "SceneBuilder.sln")),
                $"resolved repo root '{repoRoot}' has no SceneBuilder.sln next to it");

            var doc = PinnedTestBaseline.Load(repoRoot);
            Assert.True(doc.Entries.Count > 0, "baseline document loaded with zero entries");

            var problems = PinnedTestBaseline.Compare(doc.Entries, path =>
            {
                var full = Path.Combine(repoRoot, path);
                return File.Exists(full) ? File.ReadAllBytes(full) : null;
            });

            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void Baseline_ContainsEverySpecFloorFile_AndIsAProperSuperset()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            var pinnedPaths = doc.Entries.Select(e => e.Path).ToHashSet();
            foreach (var floorPath in FloorPaths)
            {
                Assert.Contains(floorPath, pinnedPaths);
            }

            Assert.True(
                doc.Entries.Count > FloorPaths.Length,
                $"expected a proper superset of the {FloorPaths.Length} floor paths, found {doc.Entries.Count} entries");
        }

        [Fact]
        public void Baseline_RecordedEntryCount_IsFrozenAndNotGreaterThanEntries()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            Assert.Equal(49, doc.RecordedEntryCount);
            Assert.True(
                doc.Entries.Count >= doc.RecordedEntryCount,
                $"recordedEntryCount {doc.RecordedEntryCount} exceeds the {doc.Entries.Count} entries actually present");
        }

        [Fact]
        public void Compare_SyntheticChangedContent_FailsNamingTheFile()
        {
            var unchanged = new byte[] { 4, 5, 6 };
            var entries = new[]
            {
                new PinnedEntry("synthetic/FileA.cs", "test", ExpectedHash(new byte[] { 1, 2, 3 })),
                new PinnedEntry("synthetic/FileB.cs", "test", ExpectedHash(unchanged)),
            };

            var problems = PinnedTestBaseline.Compare(entries, path =>
                path == "synthetic/FileA.cs" ? new byte[] { 9, 9, 9 } : unchanged);

            var problem = Assert.Single(problems);
            Assert.Contains("synthetic/FileA.cs", problem);
        }

        [Fact]
        public void Compare_SyntheticMissingFile_FailsNamingTheFile()
        {
            var entries = new[]
            {
                new PinnedEntry("synthetic/Missing.cs", "test", ExpectedHash(new byte[] { 1 })),
            };

            var problems = PinnedTestBaseline.Compare(entries, _ => null);

            var problem = Assert.Single(problems);
            Assert.Contains("synthetic/Missing.cs", problem);
        }

        [Fact]
        public void Compare_SyntheticMatchingContent_ReportsNoProblems()
        {
            var bytes = new byte[] { 7, 8, 9 };
            var entries = new[]
            {
                new PinnedEntry("synthetic/Matching.cs", "test", ExpectedHash(bytes)),
            };

            var problems = PinnedTestBaseline.Compare(entries, _ => bytes);

            Assert.Empty(problems);
        }

        [Fact]
        public void Compare_ProductionKindEntryMatchingContent_ReportsNoProblem()
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            var entries = new[]
            {
                new PinnedEntry("SceneBuilder.Core/Model/ValueWalk.cs", "production", ExpectedHash(bytes)),
            };

            var problems = PinnedTestBaseline.Compare(entries, _ => bytes);

            Assert.Empty(problems);
        }

        [Fact]
        public void Compare_ProductionKindEntryAlteredContent_FailsNamingTheFile()
        {
            var entries = new[]
            {
                new PinnedEntry("SceneBuilder.Core/Model/ValueWalk.cs", "production", ExpectedHash(new byte[] { 1, 2, 3, 4 })),
            };

            var problems = PinnedTestBaseline.Compare(entries, _ => new byte[] { 9, 9, 9, 9 });

            var problem = Assert.Single(problems);
            Assert.Contains("SceneBuilder.Core/Model/ValueWalk.cs", problem);
        }

        [Fact]
        public void RepoRootLocator_Find_ResolvesDirectoryContainingSolution()
        {
            var root = RepoRootLocator.Find();

            Assert.True(File.Exists(Path.Combine(root, "SceneBuilder.sln")));
        }
    }
}
