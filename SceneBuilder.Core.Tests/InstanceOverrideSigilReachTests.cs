using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // RenderOverrideSetCall (SourcePatchApplier.Instances.cs) has two branches: a "member:"-prefixed
    // PropertyPath renders the typed selector `(T x) => x.name`, anything else renders the string
    // form `Set<T>("path", ...)`. Every OverrideSetSpec that reaches the renderer is built from a
    // SNAPSHOT override, whose PropertyPath is always Unity's serialized path -- never the sigil --
    // so an instance-handle value at this renderer always emits the string spelling. These tests pin
    // that invariant at both renderer entry points (root .Override(...) and nested .On(...)) and pin
    // that the sigil, when it reaches the applier at all, arrives only as a drop call-match key.
    public class InstanceOverrideSigilReachTests
    {
        private const string ComponentTypeFullName = "Game.DoorOpener";
        private const string TankGuid = "guid-tank-prefab";
        private const string TankPath = "Assets/Prefabs/Tank.prefab";
        private const string EnemyGuid = "guid-enemy-prefab";
        private const string EnemyPath = "Assets/Prefabs/Enemy.prefab";

        private const string TwoInstancesSource = @"using SceneBuilder.Authoring;

public class TwoInstancesScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        private const string AuthoredOverrideSource = @"using SceneBuilder.Authoring;

public class AuthoredOverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"").Override(e => e.Set((Game.DoorOpener x) => x.target, tank));
    }
}
";

        private static IdentityMap TwoInstanceMap() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank", Kind = "PrefabInstance", SourcePrefabGuid = TankGuid },
                new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance", SourcePrefabGuid = EnemyGuid },
            },
        };

        private static GameObjectNode[] TwoInstanceModelRoots() => new GameObjectNode[]
        {
            new PrefabInstanceNode { LogicalId = "tank", Name = "Tank", SourcePrefab = new AssetRef { Guid = TankGuid, DisplayPath = TankPath } },
            new PrefabInstanceNode { LogicalId = "enemy", Name = "Enemy", SourcePrefab = new AssetRef { Guid = EnemyGuid, DisplayPath = EnemyPath } },
        };

        private static SnapshotNode TankSnapshotNode() =>
            new SnapshotNode { GlobalObjectId = "goid-tank", Name = "Tank", SourcePrefabGuid = TankGuid };

        // (1) A root-level snapshot-only override whose ObjectReference targets another mapped
        // PrefabInstance root always renders the string spelling and the applied source binds.
        [Fact]
        public void SnapshotOverrideTargetingAnInstanceRoot_RendersTheStringSpelling_AndBinds()
        {
            var anchors = BuilderParser.Parse(TwoInstancesSource).Anchors;
            var model = new SceneModel { SchemaVersion = 1, Roots = TwoInstanceModelRoots() };

            var snapshotOverride = new PropertyOverride
            {
                Target = new OverrideTarget { ComponentType = ComponentTypeFullName },
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef("tank"),
            };
            var enemySnapshot = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy", Name = "Enemy", SourcePrefabGuid = EnemyGuid,
                Overrides = new[] { snapshotOverride },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { TankSnapshotNode(), enemySnapshot } };

            var result = Reconciler.Reconcile(model, snapshot, TwoInstanceMap(), anchors);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Equal("enemy", append.Anchor);
            var set = Assert.Single(append.Sets);
            Assert.Equal("target", set.PropertyPath);
            Assert.Equal("tank", set.ValueExpression);

            var applied = SourcePatchApplier.Apply(TwoInstancesSource, result.Patch, anchors);
            Assert.Contains(".Override(e => e.Set<Game.DoorOpener>(\"target\", tank))", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // (2) The nested `.On(...)` twin of (1) -- same override, a childPath -- must go through the
        // exact same BuildOverrideSetSpec source, so the string spelling holds here too.
        [Fact]
        public void NestedSnapshotOverrideTargetingAnInstanceRoot_RendersTheStringSpelling_AndBinds()
        {
            var anchors = BuilderParser.Parse(TwoInstancesSource).Anchors;
            var model = new SceneModel { SchemaVersion = 1, Roots = TwoInstanceModelRoots() };

            var snapshotOverride = new PropertyOverride
            {
                Target = new OverrideTarget { ComponentType = ComponentTypeFullName, ChildPath = "Turret" },
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef("tank"),
            };
            var enemySnapshot = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy", Name = "Enemy", SourcePrefabGuid = EnemyGuid,
                Overrides = new[] { snapshotOverride },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { TankSnapshotNode(), enemySnapshot } };

            var result = Reconciler.Reconcile(model, snapshot, TwoInstanceMap(), anchors);

            var scopedOn = Assert.Single(result.Patch.Edits.OfType<AppendScopedOn>());
            Assert.Equal("enemy", scopedOn.Anchor);
            Assert.Equal("Turret", scopedOn.SelectorMatchKey);
            var op = Assert.IsType<ScopedOverrideOp>(Assert.Single(scopedOn.Ops));
            var set = Assert.Single(op.Sets);
            Assert.Equal("target", set.PropertyPath);
            Assert.Equal("tank", set.ValueExpression);

            var applied = SourcePatchApplier.Apply(TwoInstancesSource, result.Patch, anchors);
            Assert.Contains(".On(\"Turret\", x => x.Override(e => e.Set<Game.DoorOpener>(\"target\", tank)))", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // (3) The source AUTHORS the typed override (its parsed PropertyPath carries "member:"), and
        // the snapshot carries the same override under its serialized path. The two keys mismatch, so
        // the model override is DROPPED (carrying the sigil) and the snapshot override is APPENDED
        // (carrying the serialized path) -- no emitted override set, at either renderer entry point,
        // ever carries a "member:" path.
        [Fact]
        public void AuthoredMemberSigilOverride_ReachesTheApplierOnlyAsADropKey()
        {
            var parsed = BuilderParser.Parse(AuthoredOverrideSource);
            var anchors = SourcePatchTestHelpers.MergeAnchors(parsed);

            var snapshotOverride = new PropertyOverride
            {
                Target = new OverrideTarget { ComponentType = ComponentTypeFullName },
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef("tank"),
            };
            var enemySnapshot = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy", Name = "Enemy", SourcePrefabGuid = EnemyGuid,
                Overrides = new[] { snapshotOverride },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { TankSnapshotNode(), enemySnapshot } };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, TwoInstanceMap(), anchors, null, parsed.FlagPresence,
                parsed.ComponentAnchors, parsed.FieldArgumentSpans, parsed.Handles, null, null,
                parsed.ChainedComponents);

            var drop = Assert.Single(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Equal("enemy", drop.Anchor);
            Assert.Equal(InstanceCallKind.Override, drop.Kind);
            Assert.Equal("member:target", drop.PropertyPath);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var appendedSet = Assert.Single(append.Sets);
            Assert.Equal("target", appendedSet.PropertyPath);

            var allRenderedSets = result.Patch.Edits.OfType<AppendInstanceOverride>().SelectMany(a => a.Sets)
                .Concat(result.Patch.Edits.OfType<AppendScopedOn>().SelectMany(s => s.Ops)
                    .OfType<ScopedOverrideOp>().SelectMany(op => op.Sets));
            Assert.DoesNotContain(allRenderedSets, s => s.PropertyPath.StartsWith("member:"));

            var applied = SourcePatchApplier.Apply(AuthoredOverrideSource, result.Patch, anchors);
            Assert.Contains(".Override(e => e.Set<Game.DoorOpener>(\"target\", tank))", applied);
            Assert.DoesNotContain("=> x.target", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // (4) The renderer's sigil branch is unreachable in production, but if an OverrideSetSpec
        // ever DID carry a "member:" path, its typed-selector output binds against the authoring
        // surface -- the widened OverrideHandle.Set(selector, SceneObjectHandle) overload accepts an
        // instance-handle value there too.
        [Fact]
        public void MemberSigilOverrideSet_RenderedThroughTheApplier_Binds()
        {
            var anchors = BuilderParser.Parse(TwoInstancesSource).Anchors;
            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = ComponentTypeFullName,
                                PropertyPath = "member:target",
                                ValueExpression = "tank",
                            },
                        },
                    },
                },
            };

            var applied = SourcePatchApplier.Apply(TwoInstancesSource, patch, anchors);
            Assert.Contains(".Override(e => e.Set((Game.DoorOpener x) => x.target, tank));\n", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }
    }
}
