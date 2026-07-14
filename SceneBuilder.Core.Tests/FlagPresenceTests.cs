using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Tests.Fixtures;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class FlagPresenceTests
    {
        [Fact]
        public void Parse_RecordsFlagPresence_PerLogicalId()
        {
            // root1 chains .Tag/.Layer/.Active/.Static; root2 has no flag calls at all.
            var source = BuilderFixtures.TwoRootsWithOrderedChildren;
            var result = BuilderParser.Parse(source);

            Assert.True(result.FlagPresence.ContainsKey("root1"), "FlagPresence missing entry for 'root1'.");
            var root1 = result.FlagPresence["root1"];
            Assert.True(root1.HasTag);
            Assert.True(root1.HasLayer);
            Assert.True(root1.HasActive);
            Assert.True(root1.HasStatic);

            Assert.True(result.FlagPresence.ContainsKey("root2"), "FlagPresence missing entry for 'root2'.");
            var root2 = result.FlagPresence["root2"];
            Assert.False(root2.HasTag);
            Assert.False(root2.HasLayer);
            Assert.False(root2.HasActive);
            Assert.False(root2.HasStatic);
        }

        [Fact]
        public void Parse_RecordsStaticPresence()
        {
            const string source = @"
public class StaticPresenceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Frozen"").Static();
        scene.Add(""Moving"");
    }
}
";
            var result = BuilderParser.Parse(source);

            // Neither root has a handle variable or explicit .Id(...), so their final LogicalIds
            // are synthesized as "{name}/{siblingIndex}" (LogicalIdResolver.Synthesize), matching
            // the same-key-as-Anchors contract (see AnchorTests' "root1/ChildA/0" convention).
            Assert.True(result.FlagPresence.ContainsKey("Frozen/0"), "FlagPresence missing entry for 'Frozen/0'.");
            Assert.True(result.FlagPresence["Frozen/0"].HasStatic);

            Assert.True(result.FlagPresence.ContainsKey("Moving/1"), "FlagPresence missing entry for 'Moving/1'.");
            Assert.False(result.FlagPresence["Moving/1"].HasStatic);
        }

        [Fact]
        public void Parse_FlagPresenceKeyedByFinalLogicalId_AfterExplicitId()
        {
            const string source = @"
public class ExplicitIdFlagScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"").Tag(""Hostile"").Id(""boss"");
    }
}
";
            var result = BuilderParser.Parse(source);

            Assert.False(result.FlagPresence.ContainsKey("Enemy"), "FlagPresence must be keyed by the resolved id, not the positional/handle name.");
            Assert.True(result.FlagPresence.ContainsKey("boss"), "FlagPresence missing entry under the resolved explicit id 'boss'.");
            Assert.True(result.FlagPresence["boss"].HasTag);
        }
    }
}
