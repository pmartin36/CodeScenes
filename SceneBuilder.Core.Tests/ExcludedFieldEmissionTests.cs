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
    // An adapter-excluded field authored in source produces no emitted op and exactly one located
    // report, on both the matched-component and the create-component emission paths, without
    // touching sibling fields or the scene->code direction.
    public class ExcludedFieldEmissionTests
    {
        private const string RigidbodyType = "UnityEngine.Rigidbody";

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

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildMatchedScenario(IFieldExclusionPolicy policy)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef(RigidbodyType),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)),
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(2f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData
            {
                Type = new TypeRef(RigidbodyType),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(0)),
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(1f)),
                }),
            };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot }, FieldExclusions = policy };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            return (model, snapshot, map);
        }

        // First build in a session: root-1 has no snapshot entry at all, so WalkDesired takes the
        // EmitCreate branch and the Rigidbody's fields lower through the AddComponent side of
        // EmitComponentEdits, not the matched SetField side.
        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildCreateScenario(IFieldExclusionPolicy policy)
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef(RigidbodyType),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Excluded", ValueNode.Primitive.Int(5)),
                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(2f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>(), FieldExclusions = policy };
            var map = new IdentityMap();

            return (model, snapshot, map);
        }

        [Fact]
        public void Matched_ExcludedRoot_EmitsNoSetField_SiblingFieldStillEmits()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildMatchedScenario(policy);

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(plan.Ops.OfType<SceneBuilder.Core.Plan.SetField>(), op => op.Path == "m_Excluded");
            Assert.Contains(plan.Ops.OfType<SceneBuilder.Core.Plan.SetField>(), op => op.Path == "m_Mass");
        }

        [Fact]
        public void Matched_SubPathsOfExcludedRoot_SuppressBothOps()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Root"));
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef(RigidbodyType),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Root.x", ValueNode.Primitive.Float(1f)),
                    new KeyValuePair<string, ValueNode>("m_Root[0]", ValueNode.Primitive.Float(2f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData { Type = new TypeRef(RigidbodyType) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot }, FieldExclusions = policy };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(plan.Ops.OfType<SceneBuilder.Core.Plan.SetField>(), op => op.Path.StartsWith("m_Root"));
        }

        [Fact]
        public void Matched_SubPathsOfExcludedRoot_ReportExactlyOnce()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Root"));
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/UnityEngine.Rigidbody#0",
                Type = new TypeRef(RigidbodyType),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Root.x", ValueNode.Primitive.Float(1f)),
                    new KeyValuePair<string, ValueNode>("m_Root[0]", ValueNode.Primitive.Float(2f)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var actualComponent = new ComponentData { Type = new TypeRef(RigidbodyType) };
            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Components = new[] { actualComponent } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot }, FieldExclusions = policy };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Single(plan.Conflicts, c => c.Kind == ConflictKind.UnauthorableField);
        }

        [Fact]
        public void Create_ExcludedRoot_EmitsNoSetField_SiblingFieldStillEmits()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildCreateScenario(policy);

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Contains(plan.Ops.OfType<SceneBuilder.Core.Plan.AddComponent>(), op => op.Type.FullName == RigidbodyType);
            Assert.DoesNotContain(plan.Ops.OfType<SceneBuilder.Core.Plan.SetField>(), op => op.Path == "m_Excluded");
            Assert.Contains(plan.Ops.OfType<SceneBuilder.Core.Plan.SetField>(), op => op.Path == "m_Mass");
        }

        // The create path (component absent from the live snapshot) must raise the same located
        // report as the matched path, not just suppress the op.
        [Fact]
        public void Create_ExcludedRoot_RaisesTheSameReport()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildCreateScenario(policy);

            var plan = Materializer.Materialize(model, snapshot, map);

            var report = Assert.Single(plan.Conflicts, c => c.Kind == ConflictKind.UnauthorableField);
            var composed = report.Located!.Compose();
            Assert.Contains("root-1/UnityEngine.Rigidbody#0", composed);
            Assert.Contains(RigidbodyType, composed);
            Assert.Contains("m_Excluded", composed);
            Assert.Contains(Conflict.LineHasNoEffect, composed);
        }

        [Fact]
        public void Create_AbsentComponentType_PolicyIsConsultedForTypeNotInLiveSnapshot()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildCreateScenario(policy);

            Materializer.Materialize(model, snapshot, map);

            Assert.Contains(policy.Consulted, c => c.Type.FullName == RigidbodyType && c.Root == "m_Excluded");
        }

        [Fact]
        public void Report_ComposesAnchorTypeFieldAndFixedSentence()
        {
            var report = Conflict.UnauthorableField("root-1/UnityEngine.Rigidbody#0", RigidbodyType, "m_Excluded");

            var composed = report.Located!.Compose();

            Assert.Contains("root-1/UnityEngine.Rigidbody#0", composed);
            Assert.Contains(RigidbodyType, composed);
            Assert.Contains("m_Excluded", composed);
            Assert.Contains(Conflict.LineHasNoEffect, composed);
        }

        [Fact]
        public void Report_RecurrenceKeyIsNonNull()
        {
            var report = Conflict.UnauthorableField("root-1/UnityEngine.Rigidbody#0", RigidbodyType, "m_Excluded");

            Assert.NotNull(report.RecurrenceKey);
        }

        [Fact]
        public void Guard_UnauthorableFieldKind_RequiresFactory()
        {
            Assert.Throws<System.ArgumentException>(() => new Conflict { Kind = ConflictKind.UnauthorableField });
        }

        [Fact]
        public void Audit_MatchedPathPlan_ReportsNoBypassWhenGated()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildMatchedScenario(policy);
            var plan = Materializer.Materialize(model, snapshot, map);

            var offenders = ExcludedFieldAudit.EmittedExclusions(plan, model, policy);

            Assert.Empty(offenders);
        }

        [Fact]
        public void Audit_CreatePathPlan_ReportsNoBypassWhenGated()
        {
            var policy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildCreateScenario(policy);
            var plan = Materializer.Materialize(model, snapshot, map);

            var offenders = ExcludedFieldAudit.EmittedExclusions(plan, model, policy);

            Assert.Empty(offenders);
        }

        // Demonstrates the audit is not vacuous: a plan built WITHOUT the excluding policy applied
        // (the default, ungated snapshot) still carries the excluded field, and auditing it with the
        // excluding policy must find the offending op.
        [Fact]
        public void Audit_DetectsOffendingOp_WhenPlanBypassesGate()
        {
            var excludingPolicy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, ungatedSnapshot, map) = BuildMatchedScenario(NoFieldExclusions.Instance);
            var plan = Materializer.Materialize(model, ungatedSnapshot, map);

            var offenders = ExcludedFieldAudit.EmittedExclusions(plan, model, excludingPolicy);

            Assert.NotEmpty(offenders);
        }

        // The scene->code direction shares Differ.Diff with the build direction, so this guards
        // against the exclusion policy accidentally reaching the sync-back path: Reconcile's output
        // must not depend on what the snapshot's FieldExclusions says.
        [Fact]
        public void Reconcile_FieldExclusionsOnSnapshot_DoesNotChangeSyncOutput()
        {
            var excludingPolicy = new FakeFieldExclusionPolicy((RigidbodyType, "m_Excluded"));
            var (model, snapshot, map) = BuildMatchedScenario(NoFieldExclusions.Instance);

            var baseline = Reconciler.Reconcile(model, snapshot, map);
            var withPolicy = Reconciler.Reconcile(model, snapshot with { FieldExclusions = excludingPolicy }, map);

            Assert.Equal(baseline.Patch.Edits.Length, withPolicy.Patch.Edits.Length);
            Assert.Equal(baseline.Conflicts.Length, withPolicy.Conflicts.Length);
        }
    }
}
