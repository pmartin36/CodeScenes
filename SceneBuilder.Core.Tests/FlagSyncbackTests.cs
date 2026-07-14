using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Bucket b4 ground-truth lock for M2c: Parse -> Reconcile -> Apply -> fold map -> re-Parse ->
    // Reconcile again against the SAME snapshot must be a no-op for every flag-edit shape
    // (introduce/patch/static-enable/redundant-default-cleanup), and a flag edit must compose in
    // ONE Reconcile with an M2 (move) and an M2b (create) edit without disturbing either.
    public class FlagSyncbackTests
    {
        // Mirrors StructuralSyncbackTests' idempotence loop, but threads FlagPresence through
        // BOTH Reconcile calls (the one gap that would make this test vacuous: Reconciler drops
        // every flag arm when flagPresence is null).
        private static void AssertFlagSyncIsIdempotent(string source, IdentityMap map, SceneSnapshot snapshot)
        {
            var parsed = BuilderParser.Parse(source);

            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            // Guard against a vacuous pass: the first Sync must actually emit a flag edit.
            Assert.NotEmpty(recon1.Patch.Edits);
            Assert.Empty(recon1.Conflicts);

            var patched = SourcePatchApplier.Apply(source, recon1.Patch, parsed.Anchors);

            var foldedEntries = map.Entries
                .Where(e => !recon1.RemovedLogicalIds.Contains(e.LogicalId))
                .Concat(recon1.AddedEntries)
                .ToArray();
            var updatedMap = map with { Entries = foldedEntries };

            var reparsed = BuilderParser.Parse(patched, updatedMap);

            var recon2 = Reconciler.Reconcile(reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, reparsed.FlagPresence);

            Assert.Empty(recon2.Patch.Edits);
            Assert.Empty(recon2.Conflicts);
        }

        private static IdentityMap SingleMappedRoot(string logicalId, string goid) => new IdentityMap
        {
            Entries = new[] { new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = goid, Kind = "GameObject" } },
        };

        [Fact]
        public void Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp_Introduce()
        {
            const string source = @"
public class FlagSync1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"");
    }
}
";
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Active = false } },
            };

            AssertFlagSyncIsIdempotent(source, SingleMappedRoot("enemy", "goid-enemy"), snapshot);
        }

        [Fact]
        public void Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp_Patch()
        {
            const string source = @"
public class FlagSync2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Active(true);
    }
}
";
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Active = false } },
            };

            AssertFlagSyncIsIdempotent(source, SingleMappedRoot("enemy", "goid-enemy"), snapshot);
        }

        [Fact]
        public void Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp_StaticEnable()
        {
            const string source = @"
public class FlagSync3 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"");
    }
}
";
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", IsStatic = true } },
            };

            AssertFlagSyncIsIdempotent(source, SingleMappedRoot("enemy", "goid-enemy"), snapshot);
        }

        [Fact]
        public void Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp_RedundantDefaultCleanup_Active()
        {
            const string source = @"
public class FlagSync4 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Active(false);
    }
}
";
            // Snapshot value == the type default (Active=true) while the call is still present in
            // source -> pinned cleanup form is RemoveFlagCall (b2-t1). Re-parsing after the call is
            // removed yields model Active=true (default), so the second Sync must be a no-op.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Active = true } },
            };

            var map = SingleMappedRoot("enemy", "goid-enemy");
            var parsed = BuilderParser.Parse(source);
            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(recon1.Conflicts);
            var edit = Assert.Single(recon1.Patch.Edits);
            Assert.IsType<RemoveFlagCall>(edit);

            AssertFlagSyncIsIdempotent(source, SingleMappedRoot("enemy", "goid-enemy"), snapshot);
        }

        [Fact]
        public void Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp_RedundantDefaultCleanup_Tag()
        {
            const string source = @"
public class FlagSync5 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Enemy"").Tag(""Enemy"");
    }
}
";
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { new SnapshotNode { GlobalObjectId = "goid-enemy", Name = "Enemy", Tag = "Untagged" } },
            };

            var map = SingleMappedRoot("enemy", "goid-enemy");
            var parsed = BuilderParser.Parse(source);
            var recon1 = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(recon1.Conflicts);
            var edit = Assert.Single(recon1.Patch.Edits);
            Assert.IsType<RemoveFlagCall>(edit);

            AssertFlagSyncIsIdempotent(source, SingleMappedRoot("enemy", "goid-enemy"), snapshot);
        }

        [Fact]
        public void Reconcile_FlagEdit_ComposesWith_MoveAndCreate_InOneReconcile()
        {
            // M2 stand-in per research.md: a rename (PatchArgument on the name arg) rather than a
            // full reparent, so this test exercises the M2c composition contract in isolation
            // without also depending on M2's separate reparent+reorder path (out of this task's
            // dependency scope: DEPENDS_ON is b2-t1/b3-t1 only).
            const string source = @"
public class FlagSyncCompose : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var renamed = scene.Add(""OldName"");
        var flagged = scene.Add(""Flagged"").Tag(""Old"");
    }
}
";
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "renamed", GlobalObjectId = "goid-renamed", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "flagged", GlobalObjectId = "goid-flagged", Kind = "GameObject" },
                },
            };

            // Snapshot: renamed root is renamed (M2), flagged root's Tag changes (M2c), and a
            // brand-new root is created (M2b) -- all in the same snapshot / one Reconcile.
            var snapshotRenamed = new SnapshotNode { GlobalObjectId = "goid-renamed", Name = "NewName" };
            var snapshotFlagged = new SnapshotNode { GlobalObjectId = "goid-flagged", Name = "Flagged", Tag = "New" };
            var snapshotNewRoot = new SnapshotNode { GlobalObjectId = "goid-new", Name = "NewRoot" };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotRenamed, snapshotFlagged, snapshotNewRoot },
            };

            var parsed = BuilderParser.Parse(source);
            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors, null, parsed.FlagPresence);

            Assert.Empty(result.Conflicts);

            var renameEdit = Assert.Single(result.Patch.Edits.OfType<PatchArgument>());
            Assert.Equal("renamed", renameEdit.Anchor);
            Assert.Equal("name", renameEdit.ArgName);
            Assert.Equal("\"NewName\"", renameEdit.NewExpr);

            Assert.Single(result.Patch.Edits.OfType<AppendStatement>());

            var flagEdit = Assert.Single(result.Patch.Edits.OfType<PatchFlagArgument>());
            Assert.Equal(FlagKind.Tag, flagEdit.Flag);
            Assert.Equal("\"New\"", flagEdit.NewExpr);
            Assert.Equal("flagged", flagEdit.Anchor);

            Assert.Equal(3, result.Patch.Edits.Length);

            var patched = SourcePatchApplier.Apply(source, result.Patch, parsed.Anchors);

            var reparsed = BuilderParser.Parse(patched);
            Assert.Contains(reparsed.Model.Roots, n => n.Name == "NewName");
            Assert.Contains(reparsed.Model.Roots, n => n.Name == "Flagged" && n.Tag == "New");
            Assert.Contains(reparsed.Model.Roots, n => n.Name == "NewRoot");
        }
    }
}
