using System.Linq;
using SceneBuilder.Core.Facades;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // specs/27-typed-child-selectors.md: `.RemoveChild(sel => sel.A.B)` and
    // `.AddChild(sel => sel.Parent, "New")` reuse the shipped `.On` façade-selector machinery.
    // The typed forms resolve to the SAME ChildPath the string forms produce (compiler-checked +
    // rename-auto-syncing); string overloads remain. Mirrors FacadeParseTests /
    // FacadeReconcileEmitTests / FacadeReferencePatcherTests.
    public class TypedChildSelectorTests
    {
        private const string TankGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TankPath = "Assets/Prefabs/Tank.prefab";

        private const long TurretLocalId = 7;
        private const long BarrelLocalId = 42;
        private const long AntennaLocalId = 43;
        private const long ChassisLocalId = 8;
        private const long FrontWheelLocalId = 99;

        private static FacadeCatalog Catalog() => new FacadeCatalog
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
                                        new FacadeChild { PropertyName = "Antenna", RealName = "Antenna", LocalId = AntennaLocalId },
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

        private static string RemoveSource(string arg) => $@"
public class Sc : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Instance(Prefabs.Tank).RemoveChild({arg});
    }}
}}
";

        private static string AddSource(string parentArg) => $@"
public class Sc : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        scene.Instance(Prefabs.Tank).AddChild({parentArg}, ""MuzzleFlash"");
    }}
}}
";

        // (a) typed .RemoveChild resolves to the SAME ChildPath the string form produces.
        [Fact]
        public void TypedRemoveChild_ResolvesToSameChildPathAsString()
        {
            var typed = BuilderParser.Parse(RemoveSource("t => t.Turret.Antenna"), null, Catalog());
            var stringy = BuilderParser.Parse(RemoveSource("\"Turret/Antenna\""), null, Catalog());

            var typedInst = Assert.IsType<PrefabInstanceNode>(Assert.Single(typed.Model.Roots));
            var stringInst = Assert.IsType<PrefabInstanceNode>(Assert.Single(stringy.Model.Roots));

            Assert.Empty(typed.Ambiguities);
            Assert.Equal("Turret/Antenna", Assert.Single(typedInst.RemovedGameObjects).ChildPath);
            Assert.Equal(typedInst.RemovedGameObjects, stringInst.RemovedGameObjects);
        }

        // Sanitized PropertyName ("FrontWheel") re-derives RealName ("Front Wheel") in the ChildPath.
        [Fact]
        public void TypedRemoveChild_SanitizedSegment_ReDerivesRealName()
        {
            var typed = BuilderParser.Parse(RemoveSource("t => t.Chassis.FrontWheel"), null, Catalog());
            var inst = Assert.IsType<PrefabInstanceNode>(Assert.Single(typed.Model.Roots));
            Assert.Equal("Chassis/Front Wheel", Assert.Single(inst.RemovedGameObjects).ChildPath);
        }

        // A stale/typo'd typed selector is a located Conflict — never a silent removal.
        [Fact]
        public void TypedRemoveChild_UnknownSegment_ReportsLocatedConflict_NoRemoval()
        {
            var result = BuilderParser.Parse(RemoveSource("t => t.Turret.Nope"), null, Catalog());
            var inst = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            Assert.Empty(inst.RemovedGameObjects);
            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
            Assert.NotNull(conflict.Location);
        }

        // (a) typed .AddChild parent selector resolves to the SAME ParentPath the string form produces.
        [Fact]
        public void TypedAddChild_ParentResolvesToSameParentPathAsString()
        {
            var typed = BuilderParser.Parse(AddSource("t => t.Turret"), null, Catalog());
            var stringy = BuilderParser.Parse(AddSource("\"Turret\""), null, Catalog());

            var typedInst = Assert.IsType<PrefabInstanceNode>(Assert.Single(typed.Model.Roots));
            var stringInst = Assert.IsType<PrefabInstanceNode>(Assert.Single(stringy.Model.Roots));

            Assert.Empty(typed.Ambiguities);
            var added = Assert.Single(typedInst.AddedGameObjects);
            Assert.Equal("Turret", added.Parent.ChildPath);
            Assert.Equal("MuzzleFlash", added.Node.Name);
            Assert.Equal(stringInst.AddedGameObjects.Single().Parent.ChildPath, added.Parent.ChildPath);
        }

        [Fact]
        public void TypedAddChild_UnknownParent_ReportsLocatedConflict_NoAdd()
        {
            var result = BuilderParser.Parse(AddSource("t => t.Nope"), null, Catalog());
            var inst = Assert.IsType<PrefabInstanceNode>(Assert.Single(result.Model.Roots));

            Assert.Empty(inst.AddedGameObjects);
            var conflict = Assert.Single(result.Ambiguities);
            Assert.Equal(ConflictKind.UnknownFacadeReference, conflict.Kind);
        }

        // (b) scene->code emit: a snapshot-only removed child emits the TYPED .RemoveChild(sel=>..)
        // form when the catalog resolves the path (matching .On's typed-by-default emit).
        [Fact]
        public void Emit_RemovedChild_EmitsTypedSelector_WhenCatalogResolves()
        {
            var (model, snapshot, map) = MatchedTank(removed: new[] { new OverrideTarget { ChildPath = "Turret/Antenna" } });

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: Catalog());

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceRemoveChild>());
            Assert.Equal("Turret/Antenna", append.ChildPath);

            var rendered = RenderAgainstHandledTank(result);
            Assert.Contains(".RemoveChild(sel => sel.Turret.Antenna)", rendered);
            Assert.DoesNotContain(".RemoveChild(\"Turret/Antenna\")", rendered);

            // round-trips back through parse to the same ChildPath.
            var reparsed = BuilderParser.Parse(rendered, null, Catalog());
            var inst = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Equal("Turret/Antenna", Assert.Single(inst.RemovedGameObjects).ChildPath);
        }

        [Fact]
        public void Emit_AddedChild_EmitsTypedParentSelector_WhenCatalogResolves()
        {
            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "Turret" },
                Node = new GameObjectNode { Name = "MuzzleFlash" },
            };
            var (model, snapshot, map) = MatchedTank(addedGameObjects: new[] { added });

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: Catalog());

            var rendered = RenderAgainstHandledTank(result);
            Assert.Contains(".AddChild(sel => sel.Turret, \"MuzzleFlash\")", rendered);

            var reparsed = BuilderParser.Parse(rendered, null, Catalog());
            var inst = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Equal("Turret", Assert.Single(inst.AddedGameObjects).Parent.ChildPath);
        }

        // Emit falls back to the string form when there is no catalog (unchanged M-nested-props behavior).
        [Fact]
        public void Emit_RemovedChild_FallsBackToString_WhenNoCatalog()
        {
            var (model, snapshot, map) = MatchedTank(removed: new[] { new OverrideTarget { ChildPath = "Turret/Antenna" } });
            var result = Reconciler.Reconcile(model, snapshot, map);
            var rendered = RenderAgainstHandledTank(result);
            Assert.Contains(".RemoveChild(\"Turret/Antenna\")", rendered);
        }

        // (rename autosync) FacadeReferencePatcher rewrites typed .RemoveChild/.AddChild selector
        // segments keyed on durable LocalId, exactly like .On.
        [Fact]
        public void Patcher_RewritesTypedRemoveAndAddChildSelectors_OnRename()
        {
            const string source = @"
public class Sc : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(Prefabs.Tank).RemoveChild(t => t.Turret.Antenna);
        scene.Instance(Prefabs.Tank).AddChild(t => t.Turret, ""MuzzleFlash"");
    }
}
";
            var oldCatalog = Catalog();
            var newCatalog = RenameTurretTo("Cannon");

            var result = FacadeReferencePatcher.Patch(source, oldCatalog, newCatalog);

            Assert.Contains("RemoveChild(t => t.Cannon.Antenna)", result);
            Assert.Contains("AddChild(t => t.Cannon, \"MuzzleFlash\")", result);
            Assert.DoesNotContain("t.Turret", result);
            // the new child NAME string is never touched.
            Assert.Contains("\"MuzzleFlash\"", result);
        }

        private static FacadeCatalog RenameTurretTo(string newName)
        {
            var c = Catalog();
            var turret = c.Entries[0].Root.Children[0];
            var renamed = turret with { PropertyName = newName, RealName = newName };
            var newChildren = c.Entries[0].Root.Children.ToArray();
            newChildren[0] = renamed;
            var newRoot = c.Entries[0].Root with { Children = newChildren };
            var newEntry = c.Entries[0] with { Root = newRoot };
            return c with { Entries = new[] { newEntry } };
        }

        // Typed-instance fixture so a re-parse of the rendered output resolves the emitted typed
        // child selector against the catalog (a string-form `.Instance("path")` carries no parse-time
        // guid, so its typed selector could not resolve — the real pipeline emits both the typed
        // `Instance(Prefabs.Tank)` AND the typed selector together).
        private const string HandledTankFixture = @"
public class HandledTankScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(Prefabs.Tank);
    }
}
";

        private static string RenderAgainstHandledTank(ReconcileResult result)
        {
            var anchors = BuilderParser.Parse(HandledTankFixture, null, Catalog()).Anchors;
            return SourcePatchApplier.Apply(HandledTankFixture, result.Patch, anchors);
        }

        private static (SceneModel, SceneSnapshot, IdentityMap) MatchedTank(
            OverrideTarget[]? removed = null,
            AddedGameObject[]? addedGameObjects = null)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "tank",
                Name = "Tank",
                SourcePrefab = new AssetRef { Guid = TankGuid, DisplayPath = TankPath },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-tank-1",
                Name = "Tank",
                SourcePrefabGuid = TankGuid,
                RemovedGameObjects = removed ?? System.Array.Empty<OverrideTarget>(),
                AddedGameObjects = addedGameObjects ?? System.Array.Empty<AddedGameObject>(),
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank-1", Kind = "PrefabInstance", SourcePrefabGuid = TankGuid },
                },
                Assets = new[] { new AssetEntry { Guid = TankGuid, LastKnownPath = TankPath, TypeHint = "GameObject" } },
            };
            return (model, snapshot, map);
        }
    }
}
