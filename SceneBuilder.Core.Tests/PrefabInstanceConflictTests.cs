using System.Linq;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Prefab instances (b5-t3, spec 07) are identity-keyed by their persisted
    // (TargetPrefabId, TargetObjectId) pair-key, NOT by sibling name/position — so two bare
    // `scene.Instance("<same path>")` calls (the canonical ArenaScene shape, spec 07 lines 122-124)
    // must NOT trip the positional-sibling ambiguity rule (spec 16 / SB2201) that
    // ConflictDetector.AmbiguousGroups applies to plain GameObjectNodes. This is the ONE shared
    // chokepoint (ConflictDetector.AmbiguousGroups.Walk) both DuplicateNameConflicts (build-refusal)
    // and DetectAmbiguousReorders read from, so this test pins the exemption there rather than at any
    // one consumer.
    public class PrefabInstanceConflictTests
    {
        [Fact]
        public void Parse_TwoBareSamePathInstances_ReportsNoAmbiguity()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";
            var result = BuilderParser.Parse(source);

            Assert.Empty(result.Ambiguities);
        }

        // The exemption must be SCOPED to prefab instances — a plain-node duplicate-sibling group in
        // the SAME file is still refused. Otherwise the fix would silently regress spec 16 instead of
        // narrowly carving out the new spec-07 construct.
        [Fact]
        public void Parse_MixedPlainDuplicateAndSamePathInstances_ReportsOnlyThePlainDuplicate()
        {
            const string source = @"
public class MixedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"");
        scene.Add(""Enemy"");
        scene.Instance(""Assets/Prefabs/Coin.prefab"");
        scene.Instance(""Assets/Prefabs/Coin.prefab"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var ambiguity = Assert.Single(result.Ambiguities);
            Assert.Contains("Enemy", ambiguity.Reason);
            Assert.DoesNotContain("Coin", ambiguity.Reason);
        }
    }
}
