using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t2: scene.Instance(...) / handle.Instance(...) parses to a PrefabInstanceNode, with
    // distinct LogicalIds per instance and a Kind="PrefabInstance" IdentityMap entry per instance.
    public class PrefabInstanceParseTests
    {
        private const float Tolerance = 1e-5f;

        private const string ArenaSceneSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"").Transform(pos: (1, 2, 3), rot: (0, 90, 0));
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        var pickups = scene.Add(""Pickups"");
        pickups.Instance(""Assets/Prefabs/Coin.prefab"");
    }
}
";

        private const string NestedPlainChildSource = @"
public class NestedChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var e = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        e.Add(""Child"");
    }
}
";

        [Fact]
        public void Parse_SceneInstance_YieldsPrefabInstanceNodeWithUnresolvedSourcePrefab()
        {
            var result = BuilderParser.Parse(ArenaSceneSource);

            Assert.Equal(3, result.Model.Roots.Length);

            var enemy0 = Assert.IsType<PrefabInstanceNode>(result.Model.Roots[0]);
            Assert.Equal("Assets/Prefabs/Enemy.prefab", enemy0.SourcePrefab.DisplayPath);
            Assert.Equal("", enemy0.SourcePrefab.Guid);
            Assert.Empty(enemy0.Components);

            var expectedRotation = Rotation.EulerToQuat(new Vec3(0, 90, 0));
            Assert.Equal(1f, enemy0.Transform.Position.X, Tolerance);
            Assert.Equal(2f, enemy0.Transform.Position.Y, Tolerance);
            Assert.Equal(3f, enemy0.Transform.Position.Z, Tolerance);
            Assert.Equal(expectedRotation.X, enemy0.Transform.Rotation.X, Tolerance);
            Assert.Equal(expectedRotation.Y, enemy0.Transform.Rotation.Y, Tolerance);
            Assert.Equal(expectedRotation.Z, enemy0.Transform.Rotation.Z, Tolerance);
            Assert.Equal(expectedRotation.W, enemy0.Transform.Rotation.W, Tolerance);
        }

        [Fact]
        public void Parse_TwoAnonymousInstancesOfSamePrefab_YieldDistinctLogicalIds()
        {
            var result = BuilderParser.Parse(ArenaSceneSource);

            var enemy0 = Assert.IsType<PrefabInstanceNode>(result.Model.Roots[0]);
            var enemy1 = Assert.IsType<PrefabInstanceNode>(result.Model.Roots[1]);

            Assert.NotEqual(enemy0.LogicalId, enemy1.LogicalId);
            Assert.Equal("Enemy/0", enemy0.LogicalId);
            Assert.Equal("Enemy/1", enemy1.LogicalId);
        }

        [Fact]
        public void Parse_InstanceNestedUnderPlainNode_YieldsPrefabInstanceChild()
        {
            var result = BuilderParser.Parse(ArenaSceneSource);

            var pickups = Assert.IsType<GameObjectNode>(result.Model.Roots[2]);
            Assert.Equal("Pickups", pickups.Name);

            var coin = Assert.IsType<PrefabInstanceNode>(Assert.Single(pickups.Children));
            Assert.Equal("Assets/Prefabs/Coin.prefab", coin.SourcePrefab.DisplayPath);
            Assert.Equal("pickups/Coin/0", coin.LogicalId);
        }

        [Fact]
        public void Parse_ArenaScene_IdentityMapHasOnePrefabInstanceEntryPerInstance()
        {
            var result = BuilderParser.Parse(ArenaSceneSource);

            var instanceEntries = result.IdentityMap.Entries.Where(e => e.Kind == "PrefabInstance").ToList();
            Assert.Equal(3, instanceEntries.Count);

            var enemy0Entry = Assert.Single(instanceEntries, e => e.LogicalId == "Enemy/0");
            Assert.Null(enemy0Entry.ParentLogicalId);
            Assert.Null(enemy0Entry.SourcePrefabGuid);
            Assert.Null(enemy0Entry.PrefabKey);

            var enemy1Entry = Assert.Single(instanceEntries, e => e.LogicalId == "Enemy/1");
            Assert.Null(enemy1Entry.ParentLogicalId);

            var coinEntry = Assert.Single(instanceEntries, e => e.LogicalId == "pickups/Coin/0");
            Assert.Equal("pickups", coinEntry.ParentLogicalId);
        }

        [Fact]
        public void Parse_PlainChildAddedViaInstanceHandle_YieldsOrdinaryGameObjectChild()
        {
            var result = BuilderParser.Parse(NestedPlainChildSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("e", instance.LogicalId);

            var child = Assert.IsType<GameObjectNode>(Assert.Single(instance.Children));
            Assert.Equal("Child", child.Name);
        }
    }
}
