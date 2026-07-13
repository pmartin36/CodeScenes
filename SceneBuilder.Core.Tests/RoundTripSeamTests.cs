using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Cross-milestone seam (b2-t6): parse -> Reconcile -> apply -> re-parse -> Materialize
    // must be idempotent — a correct sync-back patch closes the gap between builder source
    // and a drifted scene, leaving nothing left for Materialize to change.
    public class RoundTripSeamTests
    {
        private const string SeamScene = @"
public class SeamScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // unrelated comment
        var player = scene.Add(""Player"").Transform(pos: (1, 2, 3));
    }
}
";

        [Fact]
        public void RoundTrip_ApplyReconcilePatch_ThenReparse_YieldsNoOpMaterializePlan()
        {
            var parsed = BuilderParser.Parse(SeamScene);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "player", GlobalObjectId = "goid-player", Kind = "GameObject" },
                },
            };

            var driftedTransform = new TransformData { Position = new Vec3(10, 20, 30) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-player", Name = "Player", Transform = driftedTransform } },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            // Guard against a vacuous pass: a real edit must exist before we apply anything.
            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(SeamScene, recon.Patch, parsed.Anchors);

            var reparsed = BuilderParser.Parse(patched, map);

            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);

            Assert.Empty(plan.Ops);
        }

        // Stronger moat check: rename + transform drift on the SAME handle-named node
        // (handle name keeps LogicalId "player" stable through the name change) round-trip
        // to an empty plan too, not just a lone transform drift.
        [Fact]
        public void RoundTrip_RenameAndTransformDrift_YieldsNoOpMaterializePlan()
        {
            var parsed = BuilderParser.Parse(SeamScene);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "player", GlobalObjectId = "goid-player", Kind = "GameObject" },
                },
            };

            var driftedTransform = new TransformData { Position = new Vec3(10, 20, 30) };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-player", Name = "Hero", Transform = driftedTransform } },
            };

            var recon = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.NotEmpty(recon.Patch.Edits);
            Assert.Empty(recon.Conflicts);

            var patched = SourcePatchApplier.Apply(SeamScene, recon.Patch, parsed.Anchors);

            var reparsed = BuilderParser.Parse(patched, map);

            var plan = Materializer.Materialize(reparsed.Model, snapshot, reparsed.IdentityMap);

            Assert.Empty(plan.Ops);
        }
    }
}
