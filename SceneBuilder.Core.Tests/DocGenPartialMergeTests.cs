using System.Linq;
using SceneBuilder.DocGen;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Deliverable: the extractor the published api.json is built from merges a partial type split
    // across files into ONE entry (union of members + nested types, richest metadata), tags
    // MonoBehaviour-derived types with category "component", and excludes the internal
    // ProjectedExtent kernel. Checked against the REAL com.codescenes/Runtime surface.
    public class DocGenPartialMergeTests
    {
        private static System.Collections.Generic.List<ApiType> Extract()
        {
            var repoRoot = RepoRootLocator.Find();
            var runtimeDir = System.IO.Path.Combine(repoRoot, "com.codescenes", "Runtime");
            return TypeExtractor.FromDirectory(runtimeDir, repoRoot);
        }

        [Fact]
        public void NoTwoTypesShareAnId()
        {
            var types = Extract();
            var duplicates = types
                .GroupBy(t => t.Id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void PartialTypeCollapsesToSingleEntry()
        {
            var types = Extract();
            Assert.Single(types.Where(t => t.Name == "Between"));
            // ComponentHandle<T> is also partial but single-file: it must stay exactly one entry.
            Assert.Single(types.Where(t => t.Name == "ComponentHandle"));
        }

        [Fact]
        public void MergedBetween_KeepsBaseListFromTheBodyFile()
        {
            var between = Extract().Single(t => t.Name == "Between");

            Assert.Equal("MonoBehaviour", between.BaseType);
            Assert.Contains("IPositionDriver", between.Signature);
        }

        [Fact]
        public void MergedBetween_CarriesAxisEnumFromTheOtherPartialFile()
        {
            var between = Extract().Single(t => t.Name == "Between");

            var axis = between.NestedTypes.Single(n => n.Name == "Axis");
            Assert.Equal("enum", axis.Kind);
            Assert.Equal(
                new[] { "X", "Y", "Z" },
                axis.EnumValues.Select(v => v.Name).ToArray());
        }

        [Fact]
        public void ComponentsAreTaggedAndOtherTypesAreNot()
        {
            var types = Extract();

            foreach (var name in new[] { "FitSize", "SurfaceSnap", "Between" })
            {
                Assert.Equal("component", types.Single(t => t.Name == name).Category);
            }

            Assert.Null(types.Single(t => t.Name == "NodeHandle").Category);
            Assert.Null(types.Single(t => t.Name == "ScopedHandle").Category);

            // Exactly these three components, nothing more.
            Assert.Equal(
                new[] { "Between", "FitSize", "SurfaceSnap" },
                types.Where(t => t.Category == "component").Select(t => t.Name).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void InternalKernelIsAbsentFromPublicTypes()
        {
            var types = Extract();
            Assert.DoesNotContain(types, t => t.Name == "ProjectedExtent");
        }
    }
}
