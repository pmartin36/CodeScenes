using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b4-t4: scene->code Reconcile EMITS the typed `.Instance(Prefabs.X)` form when the
    // snapshot's SourcePrefabGuid resolves through the FacadeCatalog (Guid->PropertyName, b1-t2);
    // falls back to the string `.Instance("<path>")` form ONLY when the prefab is absent from the
    // catalog. Typed is the DEFAULT emitted output (spec 25, Behavior 10).
    public class FacadeReconcileEmitTests
    {
        private const string PrefabGuid = "guid-tank-prefab";
        private const string PrefabPath = "Assets/Prefabs/Tank.prefab";

        private const string EmptyBuildFixture = @"
public class FacadeEmitScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
    }
}
";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) SnapshotOnlyTankInstance()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = System.Array.Empty<GameObjectNode>() };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-tank-1",
                        Name = "Tank",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = System.Array.Empty<IdentityMapEntry>(),
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };

            return (model, snapshot, map);
        }

        [Fact]
        public void Reconcile_EmitsTypedInstance_WhenPrefabHasFacade()
        {
            var (model, snapshot, map) = SnapshotOnlyTankInstance();

            var facadeCatalog = new FacadeCatalog
            {
                Entries = new[]
                {
                    new FacadeEntry { PropertyName = "Tank", Guid = PrefabGuid, Root = new FacadeNode { TypeName = "GameObject" } },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: facadeCatalog);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));

            var anchors = BuilderParser.Parse(EmptyBuildFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(EmptyBuildFixture, result.Patch, anchors);

            Assert.Contains("scene.Instance(Prefabs.Tank);", rendered);
            Assert.DoesNotContain("Instance(\"" + PrefabPath + "\")", rendered);
        }

        [Fact]
        public void Reconcile_FallsBackToString_WhenNoFacade()
        {
            var (model, snapshot, map) = SnapshotOnlyTankInstance();

            // Catalog exists but has no entry for PrefabGuid — absent from the manifest.
            var facadeCatalog = new FacadeCatalog { Entries = System.Array.Empty<FacadeEntry>() };

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: facadeCatalog);

            var append = Assert.IsType<AppendStatement>(Assert.Single(result.Patch.Edits));
            Assert.Equal(PrefabPath, append.SourcePrefabPath);
            Assert.Null(append.SourcePropertyName);

            var anchors = BuilderParser.Parse(EmptyBuildFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(EmptyBuildFixture, result.Patch, anchors);

            Assert.Contains("scene.Instance(\"" + PrefabPath + "\");", rendered);
        }

        // ---- m-nested-props b4-t2: below-root membership diff, typed nested `.On` emit ----------

        private const string HandledTankFixture = @"
public class HandledTankScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
    }
}
";

        private static FacadeCatalog TankWithTurretBarrelCatalog() => new FacadeCatalog
        {
            Entries = new[]
            {
                new FacadeEntry
                {
                    PropertyName = "Tank",
                    Guid = PrefabGuid,
                    Root = new FacadeNode
                    {
                        Children = new[]
                        {
                            new FacadeChild
                            {
                                PropertyName = "Turret",
                                RealName = "Turret",
                                LocalId = 7,
                                Node = new FacadeNode
                                {
                                    Children = new[]
                                    {
                                        new FacadeChild { PropertyName = "Barrel", RealName = "Barrel", LocalId = 42 },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) MatchedTankInstance(
            AddedComponent[]? snapshotAddedComponents = null,
            OverrideTarget[]? snapshotRemovedComponents = null)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "tank",
                Name = "Tank",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-tank-1",
                Name = "Tank",
                SourcePrefabGuid = PrefabGuid,
                AddedComponents = snapshotAddedComponents ?? System.Array.Empty<AddedComponent>(),
                RemovedComponents = snapshotRemovedComponents ?? System.Array.Empty<OverrideTarget>(),
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank-1", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
                Assets = new[] { new AssetEntry { Guid = PrefabGuid, LastKnownPath = PrefabPath, TypeHint = "GameObject" } },
            };
            return (model, snapshot, map);
        }

        // Spec #4: a snapshot-only nested AddedComponent (Target.ChildPath != "") emits an
        // `AppendScopedOn`/`ScopedAddComponentOp` (typed selector via the FacadeCatalog) — never the
        // flat root `AppendInstanceAddComponent`.
        [Fact]
        public void NestedAddedComponent_RoundTrips()
        {
            var target = new OverrideTarget
            {
                SubKey = new PrefabInstanceKey { TargetObjectId = 42 },
                ComponentType = "UnityEngine.AudioSource",
                ChildPath = "Turret/Barrel",
            };
            var added = new AddedComponent
            {
                Target = target,
                Component = new ComponentData { LogicalId = "audio-1", Type = new TypeRef("UnityEngine.AudioSource") },
            };
            var (model, snapshot, map) = MatchedTankInstance(snapshotAddedComponents: new[] { added });

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: TankWithTurretBarrelCatalog());

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            var scoped = Assert.Single(result.Patch.Edits.OfType<AppendScopedOn>());
            Assert.Equal("Turret/Barrel", scoped.SelectorMatchKey);
            Assert.Equal("sel => sel.Turret.Barrel", scoped.SelectorExpr);
            var op = Assert.Single(scoped.Ops.OfType<ScopedAddComponentOp>());
            Assert.Equal("UnityEngine.AudioSource", op.TypeFullName);

            var anchors = BuilderParser.Parse(HandledTankFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(HandledTankFixture, result.Patch, anchors);

            Assert.Contains(".On(sel => sel.Turret.Barrel, x => x.AddComponent<UnityEngine.AudioSource>())", rendered);
        }

        // Spec #5: a snapshot-only nested RemovedComponents entry emits `AppendScopedOn` /
        // `ScopedRemoveComponentOp`.
        [Fact]
        public void NestedRemovedComponent_RoundTrips()
        {
            var target = new OverrideTarget
            {
                SubKey = new PrefabInstanceKey { TargetObjectId = 7 },
                ComponentType = "UnityEngine.Light",
                ChildPath = "Turret",
            };
            var (model, snapshot, map) = MatchedTankInstance(snapshotRemovedComponents: new[] { target });

            var result = Reconciler.Reconcile(model, snapshot, map, facadeCatalog: TankWithTurretBarrelCatalog());

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceRemoveComponent>());
            var scoped = Assert.Single(result.Patch.Edits.OfType<AppendScopedOn>());
            Assert.Equal("Turret", scoped.SelectorMatchKey);
            Assert.Equal("sel => sel.Turret", scoped.SelectorExpr);
            var op = Assert.Single(scoped.Ops.OfType<ScopedRemoveComponentOp>());
            Assert.Equal("UnityEngine.Light", op.TypeFullName);

            var anchors = BuilderParser.Parse(HandledTankFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(HandledTankFixture, result.Patch, anchors);

            Assert.Contains(".On(sel => sel.Turret, x => x.RemoveComponent<UnityEngine.Light>())", rendered);
        }
    }
}
