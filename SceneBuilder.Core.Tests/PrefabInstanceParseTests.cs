using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
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

        // b2-t3 spec #9: a scene.Instance/handle.Instance child under an instance stays a nested
        // instance node — must NOT be re-routed into AddedGameObjects like a plain instance.Add child.
        private const string NestedInstanceChildSource = @"
public class NestedInstanceChildScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var e = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        e.Instance(""Assets/Prefabs/Coin.prefab"");
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

        // b2-t2: .AddChild/.RemoveChild parse fixtures.
        private const string AddChildSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"")
             .AddChild(""Turret"", ""MuzzleFlash"", mf => mf.Component<UnityEngine.Light>(l => l.Set(""m_Range"", 5f)));
    }
}
";

        private const string AddChildAtRootSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"")
             .AddChild("""", ""Beacon"");
    }
}
";

        private const string RemoveChildSource = @"
public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"")
             .RemoveChild(""Turret/Antenna"");
    }
}
";

        // b2-t1: `.On(selector, closure)` closure-body parsing — mirrors FacadeParseTests's
        // CatalogWithTurretAndChassis (Turret carries Barrel + Antenna children).
        private const string NestedTankGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const long NestedTurretLocalId = 7;
        private const long NestedBarrelLocalId = 42;
        private const long NestedAntennaLocalId = 43;

        private static FacadeCatalog CatalogWithTurret() => new FacadeCatalog
        {
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Tank",
                    Guid = NestedTankGuid,
                    Root = new FacadeNode
                    {
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Turret",
                                RealName = "Turret",
                                LocalId = NestedTurretLocalId,
                                Node = new FacadeNode
                                {
                                    Children = new[]
                                    {
                                        new FacadeChild { PropertyName = "Barrel", RealName = "Barrel", LocalId = NestedBarrelLocalId },
                                        new FacadeChild { PropertyName = "Antenna", RealName = "Antenna", LocalId = NestedAntennaLocalId },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        private const string NestedOverrideTypedSource = @"
public class NestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank)
             .On(sel => sel.Turret.Barrel, b => b.Override(x => x.Set((Light l) => l.intensity, 4f)));
    }
}
";

        private const string NestedOverrideStringSource = @"
public class NestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank)
             .On(""Turret/Barrel"", b => b.Override(x => x.Set((Light l) => l.intensity, 4f)));
    }
}
";

        private const string NestedAddComponentSource = @"
public class NestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank)
             .On(sel => sel.Turret.Barrel, b => b.AddComponent<AudioSource>());
    }
}
";

        private const string NestedRemoveComponentSource = @"
public class NestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank)
             .On(sel => sel.Turret.Antenna, b => b.RemoveComponent<MeshRenderer>());
    }
}
";

        private const string NestedMultipleOpsSource = @"
public class NestedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank)
             .On(sel => sel.Turret.Barrel, b => b.Override(x => x.Set((Light l) => l.intensity, 4f)).AddComponent<AudioSource>());
    }
}
";

        [Fact]
        public void NestedOverride_LowersToPairKey_NotNamePath()
        {
            var result = BuilderParser.Parse(NestedOverrideTypedSource, null, CatalogWithTurret());

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);

            Assert.Equal("Turret/Barrel", propertyOverride.Target.ChildPath);
            Assert.Equal("Light", propertyOverride.Target.ComponentType);
            Assert.Equal(new PrefabInstanceKey(), propertyOverride.Target.SubKey);
            Assert.Equal("member:intensity", propertyOverride.PropertyPath);

            // Identity (equality/hash) ignores ChildPath (spec-16 guard): two targets sharing
            // SubKey+ComponentType are equal even with different display paths.
            var key = new PrefabInstanceKey { TargetPrefabId = 1, TargetObjectId = 2 };
            var a = new OverrideTarget { SubKey = key, ComponentType = "Light", ChildPath = "Turret/Barrel" };
            var b = new OverrideTarget { SubKey = key, ComponentType = "Light", ChildPath = "X/Y" };
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void NestedAddedComponent_ParsesToChildTarget()
        {
            var result = BuilderParser.Parse(NestedAddComponentSource, null, CatalogWithTurret());

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(instance.AddedComponents);

            Assert.Equal("Turret/Barrel", added.Target.ChildPath);
            Assert.Equal("AudioSource", added.Target.ComponentType);
            Assert.NotEqual(new OverrideTarget(), added.Target);
        }

        [Fact]
        public void NestedRemovedComponent_ParsesToChildTarget()
        {
            var result = BuilderParser.Parse(NestedRemoveComponentSource, null, CatalogWithTurret());

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var removed = Assert.Single(instance.RemovedComponents);

            Assert.Equal("Turret/Antenna", removed.ChildPath);
            Assert.Equal("MeshRenderer", removed.ComponentType);
        }

        [Fact]
        public void NestedClosure_MultipleOps_StackOnSameChildPath()
        {
            var result = BuilderParser.Parse(NestedMultipleOpsSource, null, CatalogWithTurret());

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);
            var added = Assert.Single(instance.AddedComponents);

            Assert.Equal("Turret/Barrel", propertyOverride.Target.ChildPath);
            Assert.Equal("Turret/Barrel", added.Target.ChildPath);
        }

        [Fact]
        public void TypedAndStringOn_Closure_LowersIdentically()
        {
            var typedResult = BuilderParser.Parse(NestedOverrideTypedSource, null, CatalogWithTurret());
            var stringResult = BuilderParser.Parse(NestedOverrideStringSource, null, CatalogWithTurret());

            var typedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(typedResult.Model.Roots));
            var stringInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(stringResult.Model.Roots));

            Assert.NotEmpty(typedInstance.Overrides);
            Assert.Equal(typedInstance.Overrides, stringInstance.Overrides);
        }

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

        // b2-t3 spec #8: instance.Add(non-prefab) lowers to AddedGameObjects, NOT Children.
        // Supersedes the shipped M6 behavior asserted by the old
        // Parse_PlainChildAddedViaInstanceHandle_YieldsOrdinaryGameObjectChild test.
        [Fact]
        public void InstanceAdd_NonPrefabChild_LowersToAddedGameObject_NotChildren()
        {
            var result = BuilderParser.Parse(NestedPlainChildSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("e", instance.LogicalId);

            Assert.Empty(instance.Children);

            var added = Assert.Single(instance.AddedGameObjects);
            Assert.Equal("Child", added.Node.Name);
            Assert.Equal("", added.Parent.ChildPath);
        }

        // b2-t3 spec #9: a nested scene.Instance/handle.Instance child under an instance is
        // unaffected by the AddedGameObjects re-route above — it stays a PrefabInstanceNode child.
        [Fact]
        public void InstanceAdd_NestedInstanceChild_StaysNestedInstance()
        {
            var result = BuilderParser.Parse(NestedInstanceChildSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            var child = Assert.IsType<PrefabInstanceNode>(Assert.Single(instance.Children));
            Assert.Equal("Assets/Prefabs/Coin.prefab", child.SourcePrefab.DisplayPath);

            Assert.Empty(instance.AddedGameObjects);
        }

        [Fact]
        public void Parse_InstanceOverrideSelector_YieldsPropertyOverride()
        {
            var result = BuilderParser.Parse(OverrideSelectorSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);

            Assert.Equal("Health", propertyOverride.Target.ComponentType);
            Assert.Equal(new PrefabInstanceKey(), propertyOverride.Target.SubKey);
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

            Assert.Equal("BoxCollider", propertyOverride.Target.ComponentType);
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
        public void Parse_RemoveComponent_YieldsRootTarget()
        {
            var result = BuilderParser.Parse(RemoveComponentSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var removed = Assert.Single(instance.RemovedComponents);

            Assert.Equal("BoxCollider", removed.ComponentType);
            Assert.Equal(new PrefabInstanceKey(), removed.SubKey);
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

        [Fact]
        public void AddedChildGameObject_RoundTrips()
        {
            var result = BuilderParser.Parse(AddChildSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(instance.AddedGameObjects);

            Assert.Equal("Turret", added.Parent.ChildPath);
            Assert.Equal(new PrefabInstanceKey(), added.Parent.SubKey);
            Assert.Equal("", added.Parent.ComponentType);

            Assert.Equal("MuzzleFlash", added.Node.Name);
            var light = Assert.Single(added.Node.Components);
            Assert.Equal("UnityEngine.Light", light.Type.FullName);
            Assert.Equal(ValueNode.Primitive.Float(5f), light.Fields["m_Range"]);
        }

        [Fact]
        public void AddedChildGameObject_AtRoot_ParentIsDefaultTarget()
        {
            var result = BuilderParser.Parse(AddChildAtRootSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(instance.AddedGameObjects);

            Assert.Equal(new OverrideTarget(), added.Parent);
            Assert.Equal("Beacon", added.Node.Name);
        }

        [Fact]
        public void RemovedChildGameObject_RoundTrips()
        {
            var result = BuilderParser.Parse(RemoveChildSource);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var removed = Assert.Single(instance.RemovedGameObjects);

            Assert.Equal("Turret/Antenna", removed.ChildPath);
            Assert.Equal("", removed.ComponentType);
            Assert.Equal(new PrefabInstanceKey(), removed.SubKey);
        }
    }
}
