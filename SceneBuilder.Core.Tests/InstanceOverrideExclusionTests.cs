using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // An adapter-excluded field authored as a prefab-instance override, added component, or added
    // child never reaches a materialized op on any of the three instance-override emit surfaces
    // (SetInstanceOverride, AddInstanceComponent, AddInstanceChild), and the exclusion audit
    // attributes all three so no site can bypass it.
    public class InstanceOverrideExclusionTests
    {
        private const string SpriteRendererType = "UnityEngine.SpriteRenderer";
        private const string ExcludedRoot = "m_Size";
        private const string LightType = "UnityEngine.Light";
        private const string PrefabGuid = "guid-prefab";
        private const string PrefabPath = "Assets/Prefab.prefab";

        private sealed class FakeFieldExclusionPolicy : IFieldExclusionPolicy
        {
            private readonly HashSet<(string TypeFullName, string Root)> _excluded;

            public List<(TypeRef Type, string Root)> Consulted { get; } = new();

            public FakeFieldExclusionPolicy(params (string TypeFullName, string Root)[] excluded)
            {
                _excluded = new HashSet<(string, string)>(excluded);
            }

            public bool IsExcluded(TypeRef componentType, string fieldRoot)
            {
                Consulted.Add((componentType, fieldRoot));
                return _excluded.Contains((componentType.FullName, fieldRoot));
            }
        }

        private static OverrideTarget TargetFor(string typeFullName) => new() { ComponentType = typeFullName };

        // A matched prefab instance whose desired Overrides carries one entry the snapshot has no
        // counterpart for, so an ungated diff would emit SetInstanceOverride for it.
        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildMatchedOverrideScenario(
            IFieldExclusionPolicy policy, string propertyPath)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = TargetFor(SpriteRendererType),
                        PropertyPath = propertyPath,
                        Value = ValueNode.Primitive.Float(3f),
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance }, FieldExclusions = policy };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            return (model, snapshot, map);
        }

        // No identity-map entry and no matching snapshot node: WalkDesired takes the CREATE branch
        // (EmitCreateInstance), the unmatched-instance emit path.
        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildCreateOverrideScenario(
            IFieldExclusionPolicy policy, string propertyPath)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = TargetFor(SpriteRendererType),
                        PropertyPath = propertyPath,
                        Value = ValueNode.Primitive.Float(3f),
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>(), FieldExclusions = policy };
            var map = new IdentityMap();

            return (model, snapshot, map);
        }

        [Fact]
        public void Matched_OverrideOnExcludedRoot_EmitsNoSetInstanceOverride()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildMatchedOverrideScenario(policy, ExcludedRoot);

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(plan.Ops.OfType<SceneBuilder.Core.Plan.SetInstanceOverride>(), op => op.PropertyPath == ExcludedRoot);
        }

        [Fact]
        public void Create_OverrideOnExcludedRoot_EmitsNoSetInstanceOverride()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildCreateOverrideScenario(policy, ExcludedRoot);

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(plan.Ops.OfType<SceneBuilder.Core.Plan.SetInstanceOverride>(), op => op.PropertyPath == ExcludedRoot);
        }

        [Fact]
        public void Matched_OverrideOnNonExcludedRoot_StillEmitsSetInstanceOverride()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildMatchedOverrideScenario(policy, "m_Color");

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Contains(plan.Ops.OfType<SceneBuilder.Core.Plan.SetInstanceOverride>(), op => op.PropertyPath == "m_Color");
        }

        [Fact]
        public void Matched_OverrideOnExcludedRoot_RaisesExactlyOneUnauthorableFieldReport()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildMatchedOverrideScenario(policy, ExcludedRoot);

            var plan = Materializer.Materialize(model, snapshot, map);

            var report = Assert.Single(plan.Conflicts, c => c.Kind == ConflictKind.UnauthorableField);
            var composed = report.Located!.Compose();
            Assert.Contains(SpriteRendererType, composed);
            Assert.Contains(ExcludedRoot, composed);
        }

        [Fact]
        public void Matched_OverrideOnExcludedRoot_PolicyConsultedWithComponentTypeFullName()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildMatchedOverrideScenario(policy, ExcludedRoot);

            Materializer.Materialize(model, snapshot, map);

            Assert.Contains(policy.Consulted, c => c.Type.FullName == SpriteRendererType && c.Root == ExcludedRoot);
        }

        // Fixed point: diffing the SAME desired override against the SAME snapshot twice must emit
        // zero ops for the excluded path both times, not just the first — the snapshot never grows a
        // counterpart for an excluded path (it is dropped at read time), so a diff that suppresses at
        // emission (rather than only when a matching snapshot entry later appears) converges from the
        // very first build onward.
        [Fact]
        public void SecondIdenticalDiff_OverrideOnExcludedRoot_StillEmitsZeroOps()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, snapshot, map) = BuildMatchedOverrideScenario(policy, ExcludedRoot);

            Materializer.Materialize(model, snapshot, map);
            var secondPlan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(secondPlan.Ops.OfType<SceneBuilder.Core.Plan.SetInstanceOverride>(), op => op.PropertyPath == ExcludedRoot);
        }

        [Fact]
        public void AddedComponent_ExcludedField_StrippedFromPayload_SiblingRetained()
        {
            var policy = new FakeFieldExclusionPolicy((LightType, "m_Excluded"));
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                AddedComponents = new[]
                {
                    new AddedComponent
                    {
                        Target = new OverrideTarget(),
                        Component = new ComponentData
                        {
                            LogicalId = "instance-1/light-added",
                            Type = new TypeRef(LightType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)),
                                new KeyValuePair<string, ValueNode>("m_Intensity", ValueNode.Primitive.Float(2f)),
                            }),
                        },
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshotInstance = new SnapshotNode { GlobalObjectId = "goid-instance-1", Name = "Enemy", SourcePrefabGuid = PrefabGuid };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance }, FieldExclusions = policy };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var added = Assert.Single(plan.Ops.OfType<SceneBuilder.Core.Plan.AddInstanceComponent>());
            Assert.DoesNotContain(added.Component.Fields, f => f.Key == "m_Excluded");
            Assert.Contains(added.Component.Fields, f => f.Key == "m_Intensity");
        }

        [Fact]
        public void AddedChild_ExcludedFieldAtBothLevels_StrippedRecursively()
        {
            var policy = new FakeFieldExclusionPolicy((LightType, "m_Excluded"));
            var grandchild = new GameObjectNode
            {
                LogicalId = "instance-1/child/grandchild",
                Name = "Grandchild",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "instance-1/child/grandchild/light",
                        Type = new TypeRef(LightType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)) }),
                    },
                },
            };
            var child = new GameObjectNode
            {
                LogicalId = "instance-1/child",
                Name = "Child",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "instance-1/child/light",
                        Type = new TypeRef(LightType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)) }),
                    },
                },
                Children = new[] { grandchild },
            };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                AddedGameObjects = new[] { new AddedGameObject { Parent = new OverrideTarget(), Node = child } },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshotInstance = new SnapshotNode { GlobalObjectId = "goid-instance-1", Name = "Enemy", SourcePrefabGuid = PrefabGuid };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance }, FieldExclusions = policy };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            var added = Assert.Single(plan.Ops.OfType<SceneBuilder.Core.Plan.AddInstanceChild>());
            Assert.DoesNotContain(added.Node.Components.Single().Fields, f => f.Key == "m_Excluded");
            var addedGrandchild = Assert.Single(added.Node.Children);
            Assert.DoesNotContain(addedGrandchild.Components.Single().Fields, f => f.Key == "m_Excluded");
        }

        [Fact]
        public void Audit_EmptyAcrossAllThreeGatedInstanceSurfaces()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot), (LightType, "m_Excluded"));
            var childWithExcludedField = new GameObjectNode
            {
                LogicalId = "instance-1/child",
                Name = "Child",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "instance-1/child/light",
                        Type = new TypeRef(LightType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)) }),
                    },
                },
            };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = new[]
                {
                    new PropertyOverride { Target = TargetFor(SpriteRendererType), PropertyPath = ExcludedRoot, Value = ValueNode.Primitive.Float(3f) },
                },
                AddedComponents = new[]
                {
                    new AddedComponent
                    {
                        Target = new OverrideTarget(),
                        Component = new ComponentData
                        {
                            LogicalId = "instance-1/light-added",
                            Type = new TypeRef(LightType),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)) }),
                        },
                    },
                },
                AddedGameObjects = new[] { new AddedGameObject { Parent = new OverrideTarget(), Node = childWithExcludedField } },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshotInstance = new SnapshotNode { GlobalObjectId = "goid-instance-1", Name = "Enemy", SourcePrefabGuid = PrefabGuid };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance }, FieldExclusions = policy };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);
            var offenders = ExcludedFieldAudit.EmittedExclusions(plan, model, policy);

            Assert.Empty(offenders);
        }

        // Demonstrates the audit's new SetInstanceOverride case is not vacuous: a plan built WITHOUT
        // the excluding policy applied still carries the excluded override, and re-auditing it with
        // the excluding policy must find the offending op.
        [Fact]
        public void Audit_DetectsOffendingSetInstanceOverride_WhenPlanBypassesGate()
        {
            var excludingPolicy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var (model, ungatedSnapshot, map) = BuildMatchedOverrideScenario(NoFieldExclusions.Instance, ExcludedRoot);
            var plan = Materializer.Materialize(model, ungatedSnapshot, map);

            var offenders = ExcludedFieldAudit.EmittedExclusions(plan, model, excludingPolicy);

            Assert.NotEmpty(offenders);
        }
    }
}
