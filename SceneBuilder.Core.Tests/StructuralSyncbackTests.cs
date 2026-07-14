using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using System.Linq;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Bucket b4 idempotence seam: Parse -> Reconcile -> Apply -> fold map -> re-Parse -> Reconcile
    // again against the SAME snapshot must be a no-op. This is the ground-truth lock that the
    // Reconciler's predicted synthesized LogicalIds/handle names (b1/b2) exactly equal what
    // BuilderParser re-assigns to the SourcePatchApplier's rewritten source (b3).
    public class StructuralSyncbackTests
    {
        private const string StartingSource = @"
public class StructuralScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Parent"");
    }
}
";

        [Fact]
        public void StructuralSyncback_IsIdempotent_SecondSyncIsNoOp()
        {
            var parsed = BuilderParser.Parse(StartingSource);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Parent/0", GlobalObjectId = "goid-parent", Kind = "GameObject" },
                },
            };

            var snapshotNewChild = new SnapshotNode { GlobalObjectId = "goid-child", Name = "NewChild" };
            var snapshotParent = new SnapshotNode
            {
                GlobalObjectId = "goid-parent",
                Name = "Parent",
                Children = new[] { snapshotNewChild },
            };
            var snapshotNewRoot = new SnapshotNode { GlobalObjectId = "goid-newroot", Name = "NewRoot" };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotParent, snapshotNewRoot } };

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            // Guard against a vacuous pass: real structural edits must exist before we apply anything.
            Assert.NotEmpty(recon1.Patch.Edits);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(StartingSource, recon1.Patch, parsed.Anchors);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);

            Assert.Equal(
                new[] { "NewRoot/1", "parent", "parent/NewChild/0" },
                reparsed.IdentityMap.Entries.Select(e => e.LogicalId).OrderBy(id => id).ToArray());

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }
    }
}
