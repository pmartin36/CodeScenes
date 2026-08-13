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
    // A class implementing IPrefabVariantDefinition (Base PrefabRef + Build(VariantRoot)) is
    // recognized; its base lands as SceneModel.VariantBase and merges into
    // IdentityMap.Assets[]; its single root is the base PrefabInstanceNode (no fresh root), and
    // root-level `.Override`/`.AddComponent`/`.RemoveComponent`/`.On` verbs lower onto the same
    // override collections a nested Instance uses. Flat prefab/scene builders are unaffected and
    // a non-variant model's canonical JSON omits the new field entirely.
    public class VariantRecognitionTests
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
                    $"VariantRecognitionTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        private const string TankGuid = "dddddddddddddddddddddddddddddddd";
        private const long TurretLocalId = 21;

        private static FacadeCatalog CatalogWithTank() => new FacadeCatalog
        {
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Tank",
                    Guid = TankGuid,
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

        private const string MinimalVariantSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
    }
}
";

        private const string VariantOverrideSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
        root.Override(e => e.Set((Health c) => c.armor, 50f));
    }
}
";

        private const string VariantAddComponentSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
        root.AddComponent<AudioSource>();
    }
}
";

        private const string VariantRemoveComponentSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
        root.RemoveComponent<Light>();
    }
}
";

        private const string VariantScopedOnSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
        root.On(""Turret"", b => b.Override(x => x.Set((GunController g) => g.fireRate, 3f)));
    }
}
";

        private const string VariantFullSource = @"
public class TankVariant : IPrefabVariantDefinition
{
    public PrefabRef Base => Prefabs.Tank;

    public void Build(VariantRoot root)
    {
        root.Override(e => e.Set((Health c) => c.armor, 50f))
            .AddComponent<AudioSource>()
            .RemoveComponent<Light>()
            .On(""Turret"", b => b.Override(x => x.Set((GunController g) => g.fireRate, 3f)));
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
        public void Variant_BaseAndMarker_LandOnModel()
        {
            var result = BuilderParser.Parse(MinimalVariantSource, null, CatalogWithTank());

            Assert.NotNull(result.Model.VariantBase);
            Assert.Equal(TankGuid, result.Model.VariantBase!.Guid);
        }

        [Fact]
        public void Variant_Base_MergesIntoIdentityMapAssets()
        {
            var result = BuilderParser.Parse(MinimalVariantSource, null, CatalogWithTank());

            Assert.Contains(result.IdentityMap.Assets, a => a.Guid == TankGuid);
        }

        [Fact]
        public void Variant_NoFreshRoot_SingleRoot_IsPrefabInstanceNode()
        {
            var result = BuilderParser.Parse(MinimalVariantSource, null, CatalogWithTank());

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal(TankGuid, root.SourcePrefab.Guid);
        }

        [Fact]
        public void Variant_RootOverride_LowersToPrefabInstanceRoot_Overrides()
        {
            var result = BuilderParser.Parse(VariantOverrideSource, null, CatalogWithTank());

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var propertyOverride = Assert.Single(root.Overrides);
            Assert.Equal("Health", propertyOverride.Target.ComponentType);
            Assert.Equal("member:armor", propertyOverride.PropertyPath);
        }

        [Fact]
        public void Variant_AddComponent_LowersTo_AddedComponents()
        {
            var result = BuilderParser.Parse(VariantAddComponentSource, null, CatalogWithTank());

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var added = Assert.Single(root.AddedComponents);
            Assert.Equal("AudioSource", added.Component.Type.FullName);
        }

        [Fact]
        public void Variant_RemoveComponent_LowersTo_RemovedComponents()
        {
            var result = BuilderParser.Parse(VariantRemoveComponentSource, null, CatalogWithTank());

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var removed = Assert.Single(root.RemovedComponents);
            Assert.Equal("Light", removed.ComponentType);
        }

        [Fact]
        public void Variant_On_StringChildPath_LandsIn_ScopedOverrides()
        {
            var result = BuilderParser.Parse(VariantScopedOnSource, null, CatalogWithTank());

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            var scoped = Assert.Single(root.ScopedOverrides!);
            Assert.Equal("Turret", scoped.ChildPath);
            Assert.Equal(TurretLocalId, scoped.LocalId);
        }

        [Fact]
        public void Variant_WithoutFacadeCatalog_RecordsUnknownFacadeReference_ButOverridesStillLand()
        {
            var result = BuilderParser.Parse(VariantOverrideSource, null, null);

            Assert.Contains(result.Ambiguities, c => c.Kind == ConflictKind.UnknownFacadeReference);

            var root = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Single(root.Overrides);
        }

        [Fact]
        public void Flat_IPrefabDefinition_And_ISceneDefinition_ParseUnchanged_VariantBaseNull()
        {
            var prefabResult = BuilderParser.Parse(FlatPrefabSource);
            var sceneResult = BuilderParser.Parse(FlatSceneSource);

            Assert.Null(prefabResult.Model.VariantBase);
            Assert.Null(sceneResult.Model.VariantBase);
        }

        [Fact]
        public void VariantModel_CanonicalSerialize_IsByteStable()
        {
            var result = BuilderParser.Parse(VariantFullSource, null, CatalogWithTank());

            var first = SceneModelSerializer.Serialize(result.Model);
            var reparsed = SceneModelSerializer.Deserialize(first);
            var second = SceneModelSerializer.Serialize(reparsed);

            Assert.Equal(first, second);
        }

        [Fact]
        public void NonVariantModel_Serializes_ByteIdentical_ToPreTask_OmitsVariantBaseWhenNull()
        {
            var result = BuilderParser.Parse(FlatPrefabSource);

            var json = SceneModelSerializer.Serialize(result.Model);

            Assert.DoesNotContain("VariantBase", json);
        }

        [Fact]
        public void IPrefabVariantDefinition_IsDocumented_WithBaseAndBuildMembers()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var iface = types.Single(t => t.Name == "IPrefabVariantDefinition");
            Assert.NotEmpty(iface.Summary);

            var baseMember = iface.Members.Single(m => m.Name == "Base");
            Assert.NotEmpty(baseMember.Summary);

            var buildMember = iface.Members.Single(m => m.Name == "Build");
            Assert.NotEmpty(buildMember.Summary);
        }

        [Fact]
        public void VariantRoot_IsDocumented_WithOverrideAddComponentRemoveComponentOnMembers()
        {
            var repoRoot = RepoRoot();
            var runtimeDir = Path.Combine(repoRoot, "com.codescenes", "Runtime");

            var types = TypeExtractor.FromDirectory(runtimeDir, repoRoot);

            var variantRoot = types.Single(t => t.Name == "VariantRoot");
            Assert.NotEmpty(variantRoot.Summary);

            Assert.NotEmpty(variantRoot.Members.Single(m => m.Name == "Override").Summary);
            Assert.NotEmpty(variantRoot.Members.Single(m => m.Name == "AddComponent").Summary);
            Assert.NotEmpty(variantRoot.Members.Single(m => m.Name == "RemoveComponent").Summary);
            Assert.NotEmpty(variantRoot.Members.Single(m => m.Name == "On").Summary);
        }
    }
}
