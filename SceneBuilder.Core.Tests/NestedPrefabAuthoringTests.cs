using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using SceneBuilder.Core.Serialization;
using SceneBuilder.DocGen;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A nested Instance call inside a PrefabRoot Build body lowers to a PrefabInstanceNode child
    // of the prefab root, carrying overrides on the M10 override collections at
    // existing-vocabulary depth (root + direct children). Flat prefab and scene builders are
    // unaffected, and the resulting model round-trips byte-stable through the canonical
    // serializer.
    public class NestedPrefabAuthoringTests
    {
        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"NestedPrefabAuthoringTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        // NodeHandle needs a typed Instance<TRef>(TRef prefab) overload, sibling to
        // SceneRoot.Instance<TRef>, so a nested `node.Instance(Prefabs.B)` call inside a builder
        // type-checks. Checked against the same syntax-only extractor the published api.json is
        // built from, so this fails until the real overload lands on the real source file.
        [Fact]
        public void NodeHandle_HasTypedInstanceOverload_DocumentedAndReturningInstanceHandleOfTRef()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var nodeHandle = types.Single(t => t.Name == "NodeHandle");
            var instanceMember = nodeHandle.Members.Single(m => m.Name == "Instance");
            var typedOverload = instanceMember.Overloads.Single(o => o.TypeParams.Count == 1);

            Assert.Equal("TRef", typedOverload.TypeParams[0].Name);
            Assert.Equal("InstanceHandle<TRef>", typedOverload.ReturnType);
            Assert.NotEmpty(typedOverload.Summary);
        }

        private const string NestedGunGuid = "cccccccccccccccccccccccccccccccc";
        private const long TurretLocalId = 11;

        private static FacadeCatalog CatalogWithTurret() => new FacadeCatalog
        {
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Gun",
                    Guid = NestedGunGuid,
                    Root = new FacadeNode
                    {
                        Children = new[]
                        {
                            new FacadeChild { PropertyName = "Turret", RealName = "Turret", LocalId = TurretLocalId },
                        },
                    },
                },
            },
        };

        private const string NestedInstanceInPrefabRootSource = @"
public class TurretPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var turret = root.Add(""Turret"");
        turret.Instance(Prefabs.Gun)
              .Override(g => g.Set((GunController c) => c.fireRate, 6f))
              .AddComponent<AudioSource>();
    }
}
";

        private const string NestedInstanceScopedOnDirectChildSource = @"
public class TurretPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var turret = root.Add(""Turret"");
        turret.Instance(Prefabs.Gun)
              .On(sel => sel.Turret, b => b.Override(x => x.Set((Light l) => l.intensity, 4f)));
    }
}
";

        private const string FlatPrefabSource = @"
public class CarPrefab : IPrefabDefinition
{
    public void Build(PrefabRoot root)
    {
        var car = root.Add(""Car"");
        car.Add(""Body"");
    }
}
";

        private const string FlatSceneSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var pickups = scene.Add(""Pickups"");
        pickups.Add(""Coin"");
    }
}
";

        [Fact]
        public void NestedInstance_InPrefabRoot_LowersToPrefabInstanceNodeChild_WithOverrideAndAddedComponent()
        {
            var result = BuilderParser.Parse(NestedInstanceInPrefabRootSource, null, CatalogWithTurret());

            var turret = Assert.IsType<GameObjectNode>(Assert.Single(result.Model.Roots));
            Assert.Equal("Turret", turret.Name);

            var gun = Assert.IsType<PrefabInstanceNode>(Assert.Single(turret.Children));
            Assert.Equal(NestedGunGuid, gun.SourcePrefab.Guid);

            var propertyOverride = Assert.Single(gun.Overrides);
            Assert.Equal("GunController", propertyOverride.Target.ComponentType);
            Assert.Equal("member:fireRate", propertyOverride.PropertyPath);
            Assert.Equal(new PrefabInstanceKey(), propertyOverride.Target.SubKey);

            var added = Assert.Single(gun.AddedComponents);
            Assert.Equal("AudioSource", added.Component.Type.FullName);

            Assert.Empty(result.Ambiguities);
        }

        [Fact]
        public void NestedInstance_ScopedOnDirectChild_LandsInScopedOverrides()
        {
            var result = BuilderParser.Parse(NestedInstanceScopedOnDirectChildSource, null, CatalogWithTurret());

            var turret = Assert.IsType<GameObjectNode>(Assert.Single(result.Model.Roots));
            var gun = Assert.IsType<PrefabInstanceNode>(Assert.Single(turret.Children));

            var scoped = Assert.Single(gun.ScopedOverrides);
            Assert.Equal("Turret", scoped.ChildPath);
            Assert.Equal(TurretLocalId, scoped.LocalId);
        }

        [Fact]
        public void FlatPrefabDefinition_Builder_ParsesUnchanged()
        {
            var result = BuilderParser.Parse(FlatPrefabSource);

            var car = Assert.Single(result.Model.Roots);
            Assert.Equal("Car", car.Name);
            var body = Assert.Single(car.Children);
            Assert.Equal("Body", body.Name);
        }

        [Fact]
        public void SceneDefinition_Builder_ParsesUnchanged()
        {
            var result = BuilderParser.Parse(FlatSceneSource);

            var pickups = Assert.Single(result.Model.Roots);
            Assert.Equal("Pickups", pickups.Name);
            var coin = Assert.Single(pickups.Children);
            Assert.Equal("Coin", coin.Name);
        }

        [Fact]
        public void NestedPrefabModel_CanonicalSerialize_IsByteStable()
        {
            var result = BuilderParser.Parse(NestedInstanceInPrefabRootSource, null, CatalogWithTurret());

            var first = SceneModelSerializer.Serialize(result.Model);
            var reparsed = SceneModelSerializer.Deserialize(first);
            var second = SceneModelSerializer.Serialize(reparsed);

            Assert.Equal(first, second);
        }
    }
}
