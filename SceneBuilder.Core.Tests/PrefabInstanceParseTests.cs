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

        // b2-t2: .Override/.AddComponent/.RemoveComponent parse fixtures.
        private const string OverrideSelectorSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((Health x) => x.health, 50));
    }
}
";

        private const string OverrideStringPathSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set<BoxCollider>(""m_Center.x"", 1.0f));
    }
}
";

        private const string OverrideAssetRefSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((Light x) => x.cookie, Asset(""Assets/Textures/Cookie.png"")));
    }
}
";

        private const string OverrideNodeHandleRefSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var target = scene.Add(""Target"");
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((AI x) => x.target, target));
    }
}
";

        private const string OverrideBlockMultiSetSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e =>
             {
                 e.Set((Health x) => x.health, 50);
                 e.Set((Health x) => x.maxHealth, 100);
             });
    }
}
";

        private const string OverrideFluentMultiSetSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((Health x) => x.health, 50).Set((Health x) => x.maxHealth, 100));
    }
}
";

        private const string AddComponentNoConfigSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .AddComponent<Light>();
    }
}
";

        private const string AddComponentWithClosureSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .AddComponent<Light>(c => c.Set(""m_Intensity"", 2.5f));
    }
}
";

        private const string TwoAddComponentsSameTypeSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .AddComponent<Light>()
             .AddComponent<Light>();
    }
}
";

        private const string RemoveComponentSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .RemoveComponent<BoxCollider>();
    }
}
";

        private const string UntypedOverrideSelectorSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set(x => x.health, 50));
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

        [Fact]
        public void Parse_InstanceOverrideSelector_YieldsPropertyOverride()
        {
            var result = BuilderParser.Parse(OverrideSelectorSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);

            Assert.Equal("type:Health", propertyOverride.Target.PrefabId);
            Assert.Equal(0, propertyOverride.Target.ObjectId);
            Assert.Equal("member:health", propertyOverride.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Int(50), propertyOverride.Value);
            Assert.Null(propertyOverride.ObjectReference);
            Assert.Null(propertyOverride.BaseValue);
        }

        [Fact]
        public void Parse_InstanceOverrideStringPath_UsesVerbatimSerializedPath()
        {
            var result = BuilderParser.Parse(OverrideStringPathSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);

            Assert.Equal("type:BoxCollider", propertyOverride.Target.PrefabId);
            Assert.Equal("m_Center.x", propertyOverride.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Float(1.0f), propertyOverride.Value);
        }

        [Fact]
        public void Parse_InstanceOverrideRefValue_LandsInObjectReferenceNotValue()
        {
            var assetResult = BuilderParser.Parse(OverrideAssetRefSource);
            var assetInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(assetResult.Model.Roots));
            var assetOverride = Assert.Single(assetInstance.Overrides);

            Assert.Equal(new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Textures/Cookie.png" }), assetOverride.ObjectReference);
            Assert.Equal(new ValueNode.Unsupported(""), assetOverride.Value);

            var handleResult = BuilderParser.Parse(OverrideNodeHandleRefSource);
            var handleInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(handleResult.Model.Roots.OfType<PrefabInstanceNode>()));
            var handleOverride = Assert.Single(handleInstance.Overrides);

            Assert.Equal(new ValueNode.ObjectRef("target"), handleOverride.ObjectReference);
            Assert.Equal(new ValueNode.Unsupported(""), handleOverride.Value);
        }

        [Fact]
        public void Parse_InstanceOverrideMultipleSets_OneEntryEach()
        {
            var blockResult = BuilderParser.Parse(OverrideBlockMultiSetSource);
            var blockInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(blockResult.Model.Roots));
            Assert.Equal(2, blockInstance.Overrides.Length);
            Assert.Equal("member:health", blockInstance.Overrides[0].PropertyPath);
            Assert.Equal("member:maxHealth", blockInstance.Overrides[1].PropertyPath);

            var fluentResult = BuilderParser.Parse(OverrideFluentMultiSetSource);
            var fluentInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(fluentResult.Model.Roots));
            Assert.Equal(2, fluentInstance.Overrides.Length);
            Assert.Equal("member:health", fluentInstance.Overrides[0].PropertyPath);
            Assert.Equal("member:maxHealth", fluentInstance.Overrides[1].PropertyPath);
        }

        [Fact]
        public void Parse_AddComponent_NoConfig_YieldsAddedComponentWithLogicalId()
        {
            var result = BuilderParser.Parse(AddComponentNoConfigSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(instance.AddedComponents);

            Assert.Equal(new OverrideTarget(), added.Target);
            Assert.Equal("Light", added.Component.Type.FullName);
            Assert.Empty(added.Component.Fields);
            Assert.Equal($"{instance.LogicalId}/Light#0", added.Component.LogicalId);
        }

        [Fact]
        public void Parse_AddComponent_WithClosure_PopulatesFields()
        {
            var result = BuilderParser.Parse(AddComponentWithClosureSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(instance.AddedComponents);

            Assert.Equal(ValueNode.Primitive.Float(2.5f), added.Component.Fields["m_Intensity"]);
        }

        [Fact]
        public void Parse_TwoAddComponentsSameType_DistinctOrdinalLogicalIds()
        {
            var result = BuilderParser.Parse(TwoAddComponentsSameTypeSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal(2, instance.AddedComponents.Length);
            Assert.Equal($"{instance.LogicalId}/Light#0", instance.AddedComponents[0].Component.LogicalId);
            Assert.Equal($"{instance.LogicalId}/Light#1", instance.AddedComponents[1].Component.LogicalId);
        }

        [Fact]
        public void Parse_RemoveComponent_YieldsTypeSigilRemovedTarget()
        {
            var result = BuilderParser.Parse(RemoveComponentSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var removed = Assert.Single(instance.RemovedComponents);

            Assert.Equal("type:BoxCollider", removed.PrefabId);
            Assert.Equal(0, removed.ObjectId);
        }

        [Fact]
        public void Parse_InstanceWithoutVerbs_CollectionsEmpty()
        {
            var result = BuilderParser.Parse(ArenaSceneSource);

            var enemy0 = Assert.IsType<PrefabInstanceNode>(result.Model.Roots[0]);
            Assert.Empty(enemy0.Overrides);
            Assert.Empty(enemy0.AddedComponents);
            Assert.Empty(enemy0.RemovedComponents);
        }

        [Fact]
        public void Parse_UntypedOverrideSelector_FailsLoud()
        {
            Assert.Throws<ParseException>(() => BuilderParser.Parse(UntypedOverrideSelectorSource));
        }
    }
}
