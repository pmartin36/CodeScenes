using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t1: BuilderParser.Parse(source, existingMap, facadeCatalog) — Instance(Prefabs.X)
    // resolves X via the catalog straight to a Guid (DisplayPath left empty), which
    // PrefabRefLowering short-circuits (Guid non-empty) exactly like a lowered string-form
    // path — so the two spellings of the same prefab lower to an equal PrefabInstanceNode
    // (AssetRef.Equals ignores DisplayPath). Unknown Prefabs.X (or a null catalog) is a
    // located Conflict (Kind=UnknownFacadeReference), never a throw, never a silently
    // dropped node. See research.md.
    public class FacadeParseTests
    {
        private const string TankGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TankPath = "Assets/Prefabs/Tank.prefab";

        private const string TypedInstanceSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank);
    }
}
";

        private const string StringInstanceSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Tank.prefab"");
    }
}
";

        private const string UnknownFacadeSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Nope);
    }
}
";

        private static FacadeCatalog SampleCatalog() => new FacadeCatalog
        {
            Entries = new[]
            {
                new FacadeEntry { PropertyName = "Tank", Guid = TankGuid },
            },
        };

        // b4-t2: `.On(selector, ...)` — typed `t.Turret.Barrel` / string `.On("Turret/Barrel", ...)`
        // resolve through the FacadeCatalog to a ScopedOverride { ChildPath (RealName-joined),
        // LocalId }. ChildPath is built from RealName, never the sanitized PropertyName — the
        // Chassis/FrontWheel entry below pins that (RealName "Front Wheel" != PropertyName
        // "FrontWheel"). A miss (unknown segment / null catalog) is a located Conflict, never a
        // throw, never a recorded ScopedOverride. See research.md.
        private const long TurretLocalId = 7;
        private const long BarrelLocalId = 42;
        private const long ChassisLocalId = 8;
        private const long FrontWheelLocalId = 99;

        private static FacadeCatalog CatalogWithTurretAndChassis() => new FacadeCatalog
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
                            new FacadeChild
                            {
                                PropertyName = "Turret",
                                RealName = "Turret",
                                LocalId = TurretLocalId,
                                Node = new FacadeNode
                                {
                                    Children = new[]
                                    {
                                        new FacadeChild { PropertyName = "Barrel", RealName = "Barrel", LocalId = BarrelLocalId },
                                    },
                                },
                            },
                            new FacadeChild
                            {
                                PropertyName = "Chassis",
                                RealName = "Chassis",
                                LocalId = ChassisLocalId,
                                Node = new FacadeNode
                                {
                                    Children = new[]
                                    {
                                        new FacadeChild { PropertyName = "FrontWheel", RealName = "Front Wheel", LocalId = FrontWheelLocalId },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        private const string TypedOnBarrelSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, b => { });
    }
}
";

        private const string StringOnBarrelSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(""Turret/Barrel"", b => { });
    }
}
";

        private const string TypedOnFrontWheelSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Chassis.FrontWheel, b => { });
    }
}
";

        private const string TypedOnUnknownSegmentSource = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Nope, b => { });
    }
}
";

        [Fact]
        public void PrefabsRef_ParsesToPrefabGuid()
        {
            var result = BuilderParser.Parse(TypedInstanceSource, null, SampleCatalog());

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.Equal(TankGuid, instance.SourcePrefab.Guid);
            Assert.Empty(result.Ambiguities);
        }

        [Fact]
        public void PrefabsRef_UnknownEntry_ReportsLocatedConflict_NodeStillPresent()
        {
            var result = BuilderParser.Parse(UnknownFacadeSource, null, SampleCatalog());

            // No silent drop: the instance node is still produced despite the catalog miss.
            Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
            Assert.NotNull(conflict.Location);
        }

        [Fact]
        public void PrefabsRef_NullCatalog_ReportsLocatedConflict_NoThrow()
        {
            var result = BuilderParser.Parse(TypedInstanceSource, null, null);

            Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
        }

        [Fact]
        public void TypedAndString_InstanceLowersIdentically()
        {
            var typedResult = BuilderParser.Parse(TypedInstanceSource, null, SampleCatalog());
            var stringResult = BuilderParser.Parse(StringInstanceSource);

            var loweredString = PrefabRefLowering.Lower(
                stringResult.Model,
                (path, _) => path == TankPath ? (TankGuid, 0L, "GameObject") : null);

            var typedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(typedResult.Model.Roots));
            var stringInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(loweredString.Model.Roots));

            // AssetRef.Equals ignores DisplayPath (keys on Guid/FileId/IsBuiltin only), so the
            // typed form (Guid set at parse, DisplayPath empty) and the string form (Guid set
            // by lowering, DisplayPath the authored path) are equal.
            Assert.Equal(typedInstance.SourcePrefab, stringInstance.SourcePrefab);
        }

        [Fact]
        public void MemberChain_ParsesToChildPathAndLocalId()
        {
            var expected = new[] { new ScopedOverride { ChildPath = "Turret/Barrel", LocalId = BarrelLocalId } };

            var typedResult = BuilderParser.Parse(TypedOnBarrelSource, null, CatalogWithTurretAndChassis());
            var typedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(typedResult.Model.Roots));
            Assert.Equal(expected, typedInstance.ScopedOverrides);
            Assert.Empty(typedResult.Ambiguities);

            var stringResult = BuilderParser.Parse(StringOnBarrelSource, null, CatalogWithTurretAndChassis());
            var stringInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(stringResult.Model.Roots));
            Assert.Equal(expected, stringInstance.ScopedOverrides);
            Assert.Empty(stringResult.Ambiguities);
        }

        [Fact]
        public void SanitizedSegment_ReDerivesRealName()
        {
            // FacadeChild.PropertyName is "FrontWheel" (sanitized identifier); ChildPath must be
            // built from RealName "Front Wheel" instead.
            var result = BuilderParser.Parse(TypedOnFrontWheelSource, null, CatalogWithTurretAndChassis());
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            var expected = new[] { new ScopedOverride { ChildPath = "Chassis/Front Wheel", LocalId = FrontWheelLocalId } };
            Assert.Equal(expected, instance.ScopedOverrides);
        }

        [Fact]
        public void TypedAndString_OnLowersIdentically()
        {
            var typedResult = BuilderParser.Parse(TypedOnBarrelSource, null, CatalogWithTurretAndChassis());
            var stringResult = BuilderParser.Parse(StringOnBarrelSource, null, CatalogWithTurretAndChassis());

            var typedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(typedResult.Model.Roots));
            var stringInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(stringResult.Model.Roots));

            Assert.Equal(typedInstance.ScopedOverrides, stringInstance.ScopedOverrides);
        }

        [Fact]
        public void On_UnknownSegment_ReportsLocatedConflict_NoScopedOverride_NoThrow()
        {
            var result = BuilderParser.Parse(TypedOnUnknownSegmentSource, null, CatalogWithTurretAndChassis());

            // No silent drop: the instance node is still produced despite the selector miss.
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.True(instance.ScopedOverrides == null || instance.ScopedOverrides.Length == 0);

            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
            Assert.NotNull(conflict.Location);
        }

        [Fact]
        public void On_NullCatalog_ReportsLocatedConflict_NoScopedOverride_NoThrow()
        {
            var result = BuilderParser.Parse(TypedOnBarrelSource, null, null);

            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));
            Assert.True(instance.ScopedOverrides == null || instance.ScopedOverrides.Length == 0);

            Assert.Contains(result.Ambiguities, c => c.Kind == ConflictKind.UnknownFacadeReference);
        }
    }
}
