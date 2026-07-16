using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // CHARACTERIZATION, not regression coverage for a fix: proves the EXISTING silent data loss
    // from colliding explicit `.Id(...)` values. Parse now DETECTS the collision (b1-t3:
    // ConflictKind.DuplicateLogicalId), but detection alone does not prevent the loss: two
    // `scene.Add("Enemy").Id("Enemy-2");` statements still parse to two roots sharing one
    // LogicalId; `Reconciler.FlattenModel` (Reconciler.cs:952-959) is last-write-wins, so only one
    // survives reconcile's view of the model and the second real scene object is silently
    // re-created. This test stays GREEN for the whole milestone: the fix is PREVENTION (heal
    // before reconcile via IdCollisionHealer) — FlattenModel itself is deliberately never changed.
    public class IdCollisionDataLossTests
    {
        private const string CollidingIdsSource = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";

        [Fact]
        public void FlattenModel_CollidingExplicitIds_SilentlyDropsANode()
        {
            var parse = BuilderParser.Parse(CollidingIdsSource);

            // (a) Parse produces two roots that both resolve to the SAME explicit LogicalId.
            Assert.Equal(2, parse.Model.Roots.Length);
            Assert.All(parse.Model.Roots, r => Assert.Equal("Enemy-2", r.LogicalId));

            // Bonus corroboration: the collapse is already visible in the anchors dict too.
            Assert.Single(parse.Anchors);

            // (b) The collision is now DETECTED (b1-t3: ConflictKind.DuplicateLogicalId) — but
            // detection is a REPORT, not a fix; FlattenModel is unchanged, so the data loss below
            // still happens. Assertions (a) and (c) stand unchanged for the whole milestone.
            var conflict = Assert.Single(parse.Ambiguities);
            Assert.Equal(ConflictKind.DuplicateLogicalId, conflict.Kind);
            Assert.Equal("Enemy-2", conflict.LogicalId);

            // (c) Reconcile against a snapshot holding TWO real Enemies (one mapped to the
            // colliding id, one unmapped) silently re-creates the second one: the model-side
            // flatten kept only one "Enemy-2" node, so goid-B reads as brand-new.
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy" },
                    new SnapshotNode { GlobalObjectId = "goid-B", Name = "Enemy" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy-2", GlobalObjectId = "goid-A", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(parse.Model, snapshot, map, parse.Anchors);

            Assert.Single(result.Patch.Edits.OfType<AppendStatement>());
            Assert.Contains(result.AddedEntries, e => e.GlobalObjectId == "goid-B");
        }
    }
}
