using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t3: FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog) — formatting-preserving
    // rewrite of renamed typed-facade identifiers, keyed on durable ids (prefab Guid for
    // Prefabs.X; child LocalId for .On selector segments). Rewrites ONLY the renamed identifier
    // token; every other chain (and all surrounding text) stays byte-identical. A chain whose
    // durable target vanished from the new catalog is left untouched (safety net — it later
    // fails the compile check rather than being guessed). See research.md.
    public class FacadeReferencePatcherTests
    {
        private const string TankGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string JeepGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string MissingGuid = "cccccccccccccccccccccccccccccccc";

        private const long TurretLocalId = 7;
        private const long BarrelLocalId = 42;
        private const long EngineLocalId = 5;

        [Fact]
        public void RewritesRenamedSegment_KeepsUnchangedChains()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, b => { });
        scene.Instance(Prefabs.Jeep).On(t => t.Engine, b => { });
    }
}
";
            const string expected = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Cannon.Barrel, b => { });
        scene.Instance(Prefabs.Jeep).On(t => t.Engine, b => { });
    }
}
";
            var oldCatalog = new FacadeCatalog
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
                            },
                        },
                    },
                    new FacadeEntry
                    {
                        PropertyName = "Jeep",
                        Guid = JeepGuid,
                        Root = new FacadeNode
                        {
                            Children = new[]
                            {
                                new FacadeChild { PropertyName = "Engine", RealName = "Engine", LocalId = EngineLocalId },
                            },
                        },
                    },
                },
            };

            var newCatalog = new FacadeCatalog
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
                                    // Turret renamed to Cannon; LocalId (durable) unchanged.
                                    PropertyName = "Cannon",
                                    RealName = "Cannon",
                                    LocalId = TurretLocalId,
                                    Node = new FacadeNode
                                    {
                                        Children = new[]
                                        {
                                            new FacadeChild { PropertyName = "Barrel", RealName = "Barrel", LocalId = BarrelLocalId },
                                        },
                                    },
                                },
                            },
                        },
                    },
                    new FacadeEntry
                    {
                        PropertyName = "Jeep",
                        Guid = JeepGuid,
                        Root = new FacadeNode
                        {
                            Children = new[]
                            {
                                new FacadeChild { PropertyName = "Engine", RealName = "Engine", LocalId = EngineLocalId },
                            },
                        },
                    },
                },
            };

            var result = FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void VanishedSelectorTarget_LeftUntouched()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, b => { });
    }
}
";
            var oldCatalog = new FacadeCatalog
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
                            },
                        },
                    },
                },
            };

            var newCatalog = new FacadeCatalog
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
                                    // Turret's LocalId still resolves, but Barrel (LocalId 42) was
                                    // removed from underneath it — the leaf's durable target vanished.
                                    PropertyName = "Turret",
                                    RealName = "Turret",
                                    LocalId = TurretLocalId,
                                    Node = new FacadeNode(),
                                },
                            },
                        },
                    },
                },
            };

            var result = FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog);

            Assert.Equal(source, result);
        }

        [Fact]
        public void RenamedPrefabIdentifier_Rewritten_DeletedPrefabLeftUntouched()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank);
        scene.Instance(Prefabs.Missing);
    }
}
";
            const string expected = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Battletank);
        scene.Instance(Prefabs.Missing);
    }
}
";
            var oldCatalog = new FacadeCatalog
            {
                Entries = new[]
                {
                    new FacadeEntry { PropertyName = "Tank", Guid = TankGuid },
                    new FacadeEntry { PropertyName = "Missing", Guid = MissingGuid },
                },
            };

            var newCatalog = new FacadeCatalog
            {
                Entries = new[]
                {
                    // Tank renamed to Battletank (same Guid). Missing's prefab was deleted —
                    // its Guid is absent from the new catalog entirely.
                    new FacadeEntry { PropertyName = "Battletank", Guid = TankGuid },
                },
            };

            var result = FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void NoRename_ReturnsByteIdenticalString()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).On(t => t.Turret.Barrel, b => { });
    }
}
";
            var catalog = new FacadeCatalog
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
                            },
                        },
                    },
                },
            };

            var result = FacadeReferencePatcher.Patch(source, catalog, catalog);

            Assert.Equal(source, result);
        }
    }
}
