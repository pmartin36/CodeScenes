using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.SourcePatchTestHelpers;

namespace SceneBuilder.Core.Tests
{
    // spec 54: no emitted or retained builder source may contain a typed member selector for a
    // (T, M) the adapter has not proven safe -- on EITHER the component `.Set` value-patch arm or
    // the prefab-instance override render arm, which share no caller. A selector over a member
    // that IS proven safe (present in SafeMemberIndex) must never be downgraded.
    public class SafeSelectableMemberGuardTests
    {
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

        private const string BothEmittersSource = @"
public class GuardBothEmittersScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var crate = scene.Add(""Crate"");
        crate.Component<Game.SecretHolder>(rb => rb.Set(r => r.m_Secret, 5f));
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        // The override renderer (SourcePatchApplier.Instances.cs RenderOverrideSetCall) must gate
        // its "member:"-prefixed arm exactly as the component arm does.
        [Fact]
        public void Guard_OverrideSetOverNotSafeMember_DowngradesToStringForm()
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
                                TypeFullName = "Game.DoorOpener",
                                PropertyPath = "member:target",
                                ValueExpression = "tank",
                            },
                        },
                    },
                },
            };

            // Absent from the index -- Game.DoorOpener.target is not proven safe.
            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.DoorOpener"), MemberName = "unrelated" },
            });

            var applied = SourcePatchApplier.Apply(TwoInstancesSource, patch, anchors, safeMembers: safe);

            Assert.DoesNotContain("=> x.target", applied);
            Assert.Contains(".Override(e => e.Set<Game.DoorOpener>(\"target\", tank))", applied);
        }

        // A member proven safe must render as the typed selector -- the guard must never
        // over-downgrade a compiling public selector.
        [Fact]
        public void Guard_OverrideSetOverSafeMember_RetainsTypedSelector()
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
                                TypeFullName = "Game.DoorOpener",
                                PropertyPath = "member:target",
                                ValueExpression = "tank",
                            },
                        },
                    },
                },
            };

            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.DoorOpener"), MemberName = "target" },
            });

            var applied = SourcePatchApplier.Apply(TwoInstancesSource, patch, anchors, safeMembers: safe);

            Assert.Contains(".Override(e => e.Set((Game.DoorOpener x) => x.target, tank));\n", applied);
        }

        // One shared invariant, two emit sites, one scan: over a fixture authoring BOTH a component
        // typed selector AND an override typed selector for a (T, M) absent from SafeMemberIndex,
        // NEITHER emitter's output may retain the selector.
        [Fact]
        public void Guard_ComponentAndOverride_NeitherEmitterSurvivesANotSafeTypedSelector()
        {
            var parsed = BuilderParser.Parse(BothEmittersSource);
            var anchors = MergeAnchors(parsed);
            var compId = Assert.Single(parsed.ComponentAnchors.Keys);
            var valueSpan = parsed.FieldArgumentSpans[compId]["member:m_Secret"];

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new PatchComponentField
                    {
                        Anchor = compId,
                        ValueSpan = valueSpan,
                        NewExpr = SourceExpr.ValueNodeLiteral(ValueNode.Primitive.Float(8f)),
                    },
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = "Game.DoorOpener",
                                PropertyPath = "member:target",
                                ValueExpression = "crate",
                            },
                        },
                    },
                },
            };

            var safe = SafeMemberIndex.Build(new[]
            {
                new SafeMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "publicField" },
            });

            var result = SourcePatchApplier.Apply(BothEmittersSource, patch, anchors, safeMembers: safe);

            Assert.DoesNotContain("r.m_Secret", result);
            Assert.DoesNotContain("=> x.target", result);
        }

        private const string SelfHealComponentId = "root-1/Game.SecretHolder#0";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) SelfHealFixture(
            SafeMember[] safeMembers)
        {
            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Secret", ValueNode.Primitive.Float(5f)) });

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = "root-1",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = SelfHealComponentId, Type = new TypeRef("Game.SecretHolder"), Fields = fields },
                        },
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef("Game.SecretHolder"), Fields = fields },
                        },
                    },
                },
                SafeMembers = safeMembers,
                // A component actually read by the adapter is always inspected, whether or not the
                // read produced any safe member for it — matches production (SerializedFieldBridge
                // marks every read type inspected). Kept explicit here so this fixture stays correct
                // once the self-heal gates on IsInspected in addition to !IsSafe.
                InspectedTypes = new[] { "Game.SecretHolder" },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = SelfHealComponentId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = "Game.SecretHolder",
                        ParentLogicalId = "root-1",
                    },
                },
            };

            return (model, snapshot, map);
        }

        // The diff-independent self-heal: the field's value has NOT changed (5f both sides), so the
        // value-equality gate would ordinarily skip it entirely -- but the selector was authored
        // over "m_Secret", which is not proven safe, so it must still be rewritten.
        [Fact]
        public void SelfHeal_UnchangedNotSafeTypedSelector_ReconcileEmitsDowngradeEdit()
        {
            var (model, snapshot, map) = SelfHealFixture(safeMembers: System.Array.Empty<SafeMember>());

            var authoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [SelfHealComponentId] = new Dictionary<string, string> { ["m_Secret"] = "m_Secret" },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, authoredSelectorNames: authoredSelectorNames);

            var heal = Assert.Single(result.Patch.Edits.OfType<DowngradeComponentSelector>());
            Assert.Equal("m_Secret", heal.FieldKey);
            Assert.Equal("m_Secret", heal.MemberName);
        }

        // Companion: once "m_Secret" IS proven safe, the same unchanged field must emit NOTHING --
        // the self-heal only fires for a not-safe authored selector, never as a byte-identical no-op.
        [Fact]
        public void SelfHeal_UnchangedSafeTypedSelector_NoEditEmitted()
        {
            var (model, snapshot, map) = SelfHealFixture(safeMembers: new[]
            {
                new SafeMember { Type = new TypeRef("Game.SecretHolder"), MemberName = "m_Secret" },
            });

            var authoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [SelfHealComponentId] = new Dictionary<string, string> { ["m_Secret"] = "m_Secret" },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, authoredSelectorNames: authoredSelectorNames);

            Assert.Empty(result.Patch.Edits);
        }

        // ---- Override-path diff-independent self-heal (the symmetric sibling of the component ----
        // ---- self-heal above): ReconcileOverrides' converged-value skip must, before it `continue`s,
        // ---- rewrite an unchanged override that was authored as a not-proven-safe typed selector.

        private const string OverrideInstanceLogicalId = "instance-1";
        private const string OverridePrefabGuid = "guid-enemy-prefab";

        private static (PrefabInstanceNode Instance, SnapshotNode SnapshotInstance, IdentityMap Map) OverrideSelfHealFixture(
            OverrideTarget target, ValueNode value)
        {
            var propertyOverride = new PropertyOverride { Target = target, PropertyPath = "m_Health", Value = value };

            var instance = new PrefabInstanceNode
            {
                LogicalId = OverrideInstanceLogicalId,
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = OverridePrefabGuid, DisplayPath = "Assets/Prefabs/Enemy.prefab" },
                Overrides = new[] { propertyOverride },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = OverridePrefabGuid,
                Overrides = new[] { propertyOverride },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = OverrideInstanceLogicalId, GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = OverridePrefabGuid,
                    },
                },
            };

            return (instance, snapshotInstance, map);
        }

        // The value has NOT changed (Int(50) both sides), so the converged-skip would ordinarily
        // `continue` past this root override entirely -- but it was authored over "Health", which is
        // not proven safe on an INSPECTED type, so it must still be rewritten to string form.
        [Fact]
        public void SelfHeal_UnchangedNotSafeOverrideSelector_RootOverride_ReconcileEmitsDowngradeEdit()
        {
            var target = new OverrideTarget { ComponentType = "Game.DoorOpener" };
            var (instance, snapshotInstance, map) = OverrideSelfHealFixture(target, ValueNode.Primitive.Int(50));

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotInstance },
                InspectedTypes = new[] { "Game.DoorOpener" },
            };

            var overrideAuthoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [OverrideInstanceLogicalId] = new Dictionary<string, string>
                {
                    [OverrideSelectorKey.For(target, "m_Health")] = "Health",
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, overrideAuthoredSelectorNames: overrideAuthoredSelectorNames);

            Assert.Contains(result.Patch.Edits, e => e is DropInstanceCall d && d.Kind == InstanceCallKind.Override && d.PropertyPath == "m_Health");
            Assert.Contains(result.Patch.Edits, e => e is AppendInstanceOverride a && a.Sets.Any(s => s.PropertyPath == "m_Health"));
        }

        // Same shape, but the override target is a NESTED sub-object (Target.ChildPath != "") --
        // the exact regression surface (a nested type absent from the safe index must never cause a
        // spurious rewrite of a genuinely safe selector, and conversely a proven not-safe selector
        // on a nested target must heal through the scoped `.On` emit, not the flat root form).
        [Fact]
        public void SelfHeal_UnchangedNotSafeOverrideSelector_NestedOverride_ReconcileEmitsDowngradeEdit()
        {
            var target = new OverrideTarget { ComponentType = "UnityEngine.Light", ChildPath = "Turret/Barrel" };
            var (instance, snapshotInstance, map) = OverrideSelfHealFixture(target, ValueNode.Primitive.Float(4f));

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotInstance },
                InspectedTypes = new[] { "UnityEngine.Light" },
            };

            var overrideAuthoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [OverrideInstanceLogicalId] = new Dictionary<string, string>
                {
                    [OverrideSelectorKey.For(target, "m_Health")] = "notSafeMember",
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, overrideAuthoredSelectorNames: overrideAuthoredSelectorNames);

            Assert.Contains(result.Patch.Edits, e => e is DropScopedOnCall d && d.Kind == InstanceCallKind.Override && d.PropertyPath == "m_Health");
            Assert.Contains(result.Patch.Edits, e => e is AppendScopedOn s && s.Ops.OfType<ScopedOverrideOp>().Any(op => op.Sets.Any(set => set.PropertyPath == "m_Health")));
        }

        // Companion: once "Health" IS proven safe on the (inspected, via SafeMembers) type, the same
        // unchanged override must emit NOTHING -- no over-downgrade of a compiling selector.
        [Fact]
        public void SelfHeal_UnchangedSafeOverrideSelector_NoEditEmitted()
        {
            var target = new OverrideTarget { ComponentType = "Game.DoorOpener" };
            var (instance, snapshotInstance, map) = OverrideSelfHealFixture(target, ValueNode.Primitive.Int(50));

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[] { snapshotInstance },
                SafeMembers = new[] { new SafeMember { Type = new TypeRef("Game.DoorOpener"), MemberName = "Health" } },
            };

            var overrideAuthoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [OverrideInstanceLogicalId] = new Dictionary<string, string>
                {
                    [OverrideSelectorKey.For(target, "m_Health")] = "Health",
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, overrideAuthoredSelectorNames: overrideAuthoredSelectorNames);

            Assert.Empty(result.Patch.Edits);
        }

        // Companion: a target type the adapter never inspected (absent from BOTH SafeMembers and
        // InspectedTypes) must never be rewritten, even though "Health" is absent from the safe set --
        // absence of knowledge must never be read as proof of not-safe.
        [Fact]
        public void SelfHeal_UnchangedNotSafeOverrideSelector_TypeNotInspected_NoEditEmitted()
        {
            var target = new OverrideTarget { ComponentType = "Game.DoorOpener" };
            var (instance, snapshotInstance, map) = OverrideSelfHealFixture(target, ValueNode.Primitive.Int(50));

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var overrideAuthoredSelectorNames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [OverrideInstanceLogicalId] = new Dictionary<string, string>
                {
                    [OverrideSelectorKey.For(target, "m_Health")] = "Health",
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, overrideAuthoredSelectorNames: overrideAuthoredSelectorNames);

            Assert.Empty(result.Patch.Edits);
        }
    }
}
