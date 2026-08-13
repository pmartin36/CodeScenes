using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A single-root IPrefabDefinition builder reuses the SAME parse -> Reconcile -> apply ->
    // reparse -> Materialize pipeline a scene builder uses (mirrors RoundTripSeamTests): no new
    // Core model type, no prefab-specific pipeline. A second, unrelated Build method in the file
    // forces reliance on interface-based recognition rather than the "only Build method in the
    // file" fallback, so the round trip only succeeds once the prefab class is actually recognized.
    public class PrefabRoundTripTests
    {
        private const string CarPrefab = @"
public class UnrelatedHelper
{
    public void Build()
    {
    }
}

public class CarPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"").Transform(pos: (1, 2, 3));
    }
}
";

        [Fact]
        public void RoundTrip_ApplyReconcilePatch_ThenReparse_YieldsNoOpMaterializePlan()
        {
            var parsed = BuilderParser.Parse(CarPrefab);

            var singleRoot = Assert.Single(parsed.Model.Roots);
            Assert.Equal("Car", singleRoot.Name);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = singleRoot.LogicalId, GlobalObjectId = "goid-car", Kind = "GameObject" },
                },
            };

            var driftedTransform = new TransformData { Position = new Vec3(10, 20, 30) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-car", Name = "Car", Transform = driftedTransform } },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            // Guard against a vacuous pass: a real edit must exist before we apply anything.
            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(CarPrefab, recon.Patch, parsed.Anchors);

            var reparsed = BuilderParser.Parse(patched, map);

            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);

            Assert.Empty(plan.Ops);
        }
    }
}
