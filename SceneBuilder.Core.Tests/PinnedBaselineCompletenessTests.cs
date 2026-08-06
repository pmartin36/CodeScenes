using System.Linq;

namespace SceneBuilder.Core.Tests
{
    // Distinguishes "pinned" from "believed pinned": a dropped append still leaves the JSON
    // valid, so only an exact-count and containment check catches it.
    public class PinnedBaselineCompletenessTests
    {
        private static readonly string[] AppendedPaths =
        {
            "SceneBuilder.Core/Model/ValueWalk.cs",
            "SceneBuilder.Core.Tests/AssetRefLoweringDescentTests.cs",
            "SceneBuilder.Core.Tests/PlanningValidatorDescentTests.cs",
            "SceneBuilder.Core.Tests/SourceExprDescentTests.cs",
            "unity-gate/Assets/GateTests/BuiltinRefValidatorDescentTests.cs",
            "unity-gate/Assets/GateTests/SerializedFieldBridgeDescentTests.cs",
            "unity-gate/Assets/GateTests/NestedStructEmitCompileTests.cs",
        };

        [Fact]
        public void Baseline_EntryCount_EqualsRecordedCountPlusEveryAppendedFile()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            Assert.Equal(doc.RecordedEntryCount + AppendedPaths.Length, doc.Entries.Count);
        }

        [Fact]
        public void Baseline_ContainsEveryAppendedFile()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            var pinnedPaths = doc.Entries.Select(e => e.Path).ToHashSet();
            var missing = AppendedPaths.Where(p => !pinnedPaths.Contains(p)).ToList();

            Assert.True(missing.Count == 0, string.Join("\n", missing));
        }

        [Fact]
        public void Baseline_EntryPaths_AreUnique()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            var duplicates = doc.Entries
                .GroupBy(e => e.Path)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, string.Join("\n", duplicates));
        }

        [Fact]
        public void Baseline_ValueWalkEntry_IsRecordedAsProduction()
        {
            var repoRoot = RepoRootLocator.Find();
            var doc = PinnedTestBaseline.Load(repoRoot);

            var entry = doc.Entries.Single(e => e.Path == "SceneBuilder.Core/Model/ValueWalk.cs");

            Assert.Equal("production", entry.Kind);
        }
    }
}
