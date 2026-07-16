using System.Linq;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Parse-time detection of COLLIDING LogicalIds (b1-t3): two nodes whose explicit `.Id(...)` /
    // authored `var` handle resolve to the SAME id. Unlike positional ambiguity (DuplicateNameConflicts,
    // DuplicateSiblingNameTests), a collision is invisible to the C# compiler in three of the four
    // shapes covered here: a handle colliding with a LATER explicit `.Id(...)` (cross-tier), the same
    // explicit id under two different parents (ids are GLOBAL, not per-parent), and a `var` name
    // repeated in two separate block-bodied closures (CS0128 is scope-local, so the compiler does not
    // flag it). Detection must fire on every one of them, unconditionally, without Parse throwing.
    public class IdCollisionParseTests
    {
        // Spec test 7: the simplest shape — the same explicit `.Id("Enemy-2")` twice.
        [Fact]
        public void Parse_TwoStatementsWithTheSameExplicitId_ReportsDuplicateLogicalId()
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
            var parsed = BuilderParser.Parse(source);

            var conflict = Assert.Single(parsed.Ambiguities);
            Assert.Equal(ConflictKind.DuplicateLogicalId, conflict.Kind);
            Assert.Equal("Enemy-2", conflict.LogicalId);
            Assert.Contains("Enemy-2", conflict.Reason);

            // Location is the SECOND occurrence's span — the first is the incumbent.
            var occurrences = parsed.NodeAnchors.Where(a => a.LogicalId == "Enemy-2").ToList();
            Assert.Equal(2, occurrences.Count);
            Assert.Equal(occurrences[1].Span, conflict.Location);
        }

        // Spec test 8: cross-tier — a `var` handle colliding with a LATER explicit `.Id(...)`. The
        // compiler cannot see this; the handle and the string literal live in different tiers.
        [Fact]
        public void Parse_HandleAndExplicitIdCollide_ReportsDuplicateLogicalId()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Add(""A"");
        scene.Add(""B"").Id(""enemy"");
    }
}
";
            var parsed = BuilderParser.Parse(source);

            var conflict = Assert.Single(parsed.Ambiguities);
            Assert.Equal(ConflictKind.DuplicateLogicalId, conflict.Kind);
            Assert.Equal("enemy", conflict.LogicalId);
        }

        // Spec test 9: the same explicit id under two DIFFERENT parents. Explicit ids are GLOBAL
        // identity, not scoped per-parent, so this still collides.
        [Fact]
        public void Parse_SameExplicitIdUnderDifferentParents_ReportsDuplicateLogicalId()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""P1"", p1 => { p1.Add(""X"").Id(""dup""); });
        scene.Add(""P2"", p2 => { p2.Add(""Y"").Id(""dup""); });
    }
}
";
            var parsed = BuilderParser.Parse(source);

            var conflict = Assert.Single(parsed.Ambiguities);
            Assert.Equal(ConflictKind.DuplicateLogicalId, conflict.Kind);
            Assert.Equal("dup", conflict.LogicalId);
        }

        // Spec test 10: the same `var` name reused in two SEPARATE block-bodied closures. CS0128
        // ("variable already defined") is scope-local to a single block, so the compiler does not
        // fire across two closures — this is the one shape the language itself cannot catch.
        [Fact]
        public void Parse_HandlesCollidingAcrossSiblingClosures_ReportsDuplicateLogicalId()
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
            var parsed = BuilderParser.Parse(source);

            var conflict = Assert.Single(parsed.Ambiguities);
            Assert.Equal(ConflictKind.DuplicateLogicalId, conflict.Kind);
            Assert.Equal("enemy", conflict.LogicalId);
        }

        // Spec test 11: the negative — every id in the file is distinct, so no DuplicateLogicalId
        // conflict must appear (no false positive on this fixture shape).
        [Fact]
        public void Parse_UniqueIds_ReportsNoDuplicateLogicalId()
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
            var parsed = BuilderParser.Parse(source);

            Assert.DoesNotContain(parsed.Ambiguities, c => c.Kind == ConflictKind.DuplicateLogicalId);
        }

        // No-double-report guard: two POSITIONAL same-named siblings (no id, no handle) are caught
        // by DuplicateNameConflicts (AmbiguousAnchor) — a positional LogicalId is always `Name/index`,
        // so it can never collide with another positional id and must never ALSO surface as
        // DuplicateLogicalId.
        [Fact]
        public void Parse_TwoPositionalDuplicates_DoesNotAlsoReportDuplicateLogicalId()
        {
            const string source = @"
public class DupScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"");
        scene.Add(""Enemy"");
    }
}
";
            var parsed = BuilderParser.Parse(source);

            var conflict = Assert.Single(parsed.Ambiguities);
            Assert.Equal(ConflictKind.AmbiguousAnchor, conflict.Kind);
            Assert.DoesNotContain(parsed.Ambiguities, c => c.Kind == ConflictKind.DuplicateLogicalId);
        }
    }
}
