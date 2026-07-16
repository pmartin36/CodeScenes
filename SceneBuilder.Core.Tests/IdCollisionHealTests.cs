using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Coverage for IdCollisionHealer.Heal (b3-t1, spec tests 13-16): the source-only pre-pass that
    // re-mints every LATER occurrence of a colliding LogicalId, leaving the FIRST (the incumbent,
    // which keeps the sidecar's GlobalObjectId) untouched. Mirrors IdCollisionParseTests'/
    // IdCollisionDataLossTests' inline-source + BuilderParser.Parse pattern.
    public class IdCollisionHealTests
    {
        // Spec test 13: the simplest shape — the same explicit `.Id("Enemy-2")` twice. Only the
        // SECOND occurrence is re-minted; the first is byte-identical to its input.
        [Fact]
        public void Heal_CollidingExplicitIds_RemintsTheSecondOccurrenceOnly()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";
            var parse = BuilderParser.Parse(source);

            var healed = IdCollisionHealer.Heal(source, parse);

            // First statement is untouched, directly followed by the re-minted second.
            Assert.Contains(
                "        scene.Add(\"Enemy\").Id(\"Enemy-2\");\n        var enemy = scene.Add(\"Enemy\");",
                healed);

            // The dead `.Id("Enemy-2")` literal is gone from the second statement — exactly one
            // occurrence of the explicit id remains (the untouched incumbent).
            Assert.Equal(1, healed.Split(new[] { ".Id(\"Enemy-2\")" }, System.StringSplitOptions.None).Length - 1);

            var reparsed = BuilderParser.Parse(healed);
            Assert.DoesNotContain(reparsed.Ambiguities, c => c.Kind == ConflictKind.DuplicateLogicalId);
        }

        // Spec test 14: two colliding AUTHORED handles are report-only — Heal cannot safely rename a
        // `var` (every reference in its scope would need rewriting too), so it leaves the source
        // unchanged and the conflict survives a re-parse, so Build still refuses.
        [Fact]
        public void Heal_CollidingHandles_IsAReportOnlyNoOp()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""A"", a => { var enemy = a.Add(""Enemy""); });
        scene.Add(""B"", b => { var enemy = b.Add(""Enemy""); });
    }
}
";
            var parse = BuilderParser.Parse(source);

            var healed = IdCollisionHealer.Heal(source, parse);

            Assert.Equal(source, healed);

            var reparsed = BuilderParser.Parse(healed);
            Assert.Contains(
                reparsed.Ambiguities,
                c => c.Kind == ConflictKind.DuplicateLogicalId && c.LogicalId == "enemy");
        }

        // Spec test 15: no collision at all ⇒ Heal is a pure no-op.
        [Fact]
        public void Heal_NoCollision_ReturnsSourceUnchanged()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""E1"");
        scene.Add(""Enemy"").Id(""E2"");
    }
}
";
            var parse = BuilderParser.Parse(source);
            Assert.DoesNotContain(parse.Ambiguities, c => c.Kind == ConflictKind.DuplicateLogicalId);

            var healed = IdCollisionHealer.Heal(source, parse);

            Assert.Same(source, healed);
        }

        // Spec test 16: a file that already contains `var enemy` (unrelated to the colliding pair)
        // forces the re-mint to avoid it too — reserved spans EVERY LogicalId and handle in the
        // whole file, not just the colliding group.
        [Fact]
        public void Heal_RemintedHandleAvoidsEveryIdAndHandleInTheFile()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""Ally"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";
            var parse = BuilderParser.Parse(source);

            var healed = IdCollisionHealer.Heal(source, parse);

            Assert.Contains("        var enemy2 = scene.Add(\"Enemy\");", healed);
            Assert.DoesNotContain("        var enemy = scene.Add(\"Enemy\");", healed);

            var reparsed = BuilderParser.Parse(healed);
            Assert.DoesNotContain(
                reparsed.Ambiguities,
                c => c.Kind == ConflictKind.DuplicateLogicalId && c.LogicalId == "Enemy-2");
        }

        // Spec test 17 (seam assertion): heal a colliding-id source, re-parse it, then reconcile
        // against a snapshot holding ONLY the incumbent. The spec ARGUES this cannot strand, delete,
        // or duplicate the incumbent — DetectRemovals only walks sidecar entries (the re-minted
        // statement has none, so it can't be removed) and DetectAppends only walks unmapped snapshot
        // nodes (the incumbent is mapped, so it can't be re-appended). This turns that reasoning into
        // a run across the healer + reconciler seam.
        [Fact]
        public void Heal_ThenReconcile_DoesNotRemoveOrDuplicateTheIncumbent()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Id(""Enemy-2"");
        scene.Add(""Enemy"").Id(""Enemy-2"");
    }
}
";
            var parse = BuilderParser.Parse(source);
            var healed = IdCollisionHealer.Heal(source, parse);

            var reparsed = BuilderParser.Parse(healed);
            Assert.DoesNotContain(reparsed.Ambiguities, c => c.Kind == ConflictKind.DuplicateLogicalId);

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-A", Name = "Enemy" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "Enemy-2", GlobalObjectId = "goid-A", Kind = "GameObject" },
                },
            };

            var result = Reconciler.Reconcile(reparsed.Model, snapshot, map, reparsed.Anchors);

            // (a) The re-minted (pasted) statement has no sidecar entry, so it must never be removed.
            Assert.Empty(result.Patch.Edits.OfType<RemoveStatement>());

            // (b) The incumbent is mapped, so it must never be re-appended (no duplicate scene object).
            Assert.Empty(result.Patch.Edits.OfType<AppendStatement>());

            // (c) Nothing stranded: the incumbent's id<->goid pairing survives untouched.
            Assert.DoesNotContain("Enemy-2", result.RemovedLogicalIds);
            Assert.DoesNotContain(result.AddedEntries, e => e.GlobalObjectId == "goid-A");
        }
    }
}
