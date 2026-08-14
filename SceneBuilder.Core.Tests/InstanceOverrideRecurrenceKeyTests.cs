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
    // Spec 44 Invariant #2: the two declined-override report kinds (StaleOverride, the
    // excluded-override report ExcludedFieldGate.AdmitOverride raises) carry a stable RecurrenceKey
    // composed from the SAME identity both directions read/write against —
    // (instanceLogicalId, OverrideTarget, PropertyPath), ChildPath-free so a nested-override rename
    // does not defeat the once-per-session dedup.
    public class InstanceOverrideRecurrenceKeyTests
    {
        private const string BoxColliderType = "UnityEngine.BoxCollider";
        private const string SphereColliderType = "UnityEngine.SphereCollider";
        private const string SpriteRendererType = "UnityEngine.SpriteRenderer";
        private const string ExcludedRoot = "m_Size";
        private const string PrefabGuid = "guid-prefab";
        private const string PrefabPath = "Assets/Prefab.prefab";

        private static OverrideTarget Target(string componentType, ulong targetObjectId = 0, string childPath = "") =>
            new() { ComponentType = componentType, SubKey = new PrefabInstanceKey { TargetObjectId = targetObjectId }, ChildPath = childPath };

        private sealed class FakeFieldExclusionPolicy : IFieldExclusionPolicy
        {
            private readonly HashSet<(string TypeFullName, string Root)> _excluded;

            public FakeFieldExclusionPolicy(params (string TypeFullName, string Root)[] excluded) =>
                _excluded = new HashSet<(string, string)>(excluded);

            public bool IsExcluded(TypeRef componentType, string fieldRoot) =>
                _excluded.Contains((componentType.FullName, fieldRoot));
        }

        // ---- StaleOverride recurrence key -------------------------------------------------------

        [Fact]
        public void StaleOverride_SameIdentity_ComposesSameNonNullKey()
        {
            var target = Target(BoxColliderType, targetObjectId: 1);
            var recorded = ValueNode.Primitive.Int(100);
            var current = ValueNode.Primitive.Int(75);

            var first = ConflictDetector.StaleOverride("instance-1", target, "m_Health", recorded, current);
            var second = ConflictDetector.StaleOverride("instance-1", target, "m_Health", recorded, current);

            Assert.NotNull(first.RecurrenceKey);
            Assert.Equal(first.RecurrenceKey, second.RecurrenceKey);
        }

        [Fact]
        public void StaleOverride_DifferByComponentType_ComposesDistinctKeys()
        {
            var recorded = ValueNode.Primitive.Int(100);
            var current = ValueNode.Primitive.Int(75);
            var first = ConflictDetector.StaleOverride("instance-1", Target(BoxColliderType, 1), "m_Health", recorded, current);
            var second = ConflictDetector.StaleOverride("instance-1", Target(SphereColliderType, 1), "m_Health", recorded, current);

            Assert.NotNull(first.RecurrenceKey);
            Assert.NotNull(second.RecurrenceKey);
            Assert.NotEqual(first.RecurrenceKey, second.RecurrenceKey);
        }

        [Fact]
        public void StaleOverride_DifferBySubKeyTargetObjectId_ComposesDistinctKeys()
        {
            var recorded = ValueNode.Primitive.Int(100);
            var current = ValueNode.Primitive.Int(75);
            var first = ConflictDetector.StaleOverride("instance-1", Target(BoxColliderType, 1), "m_Health", recorded, current);
            var second = ConflictDetector.StaleOverride("instance-1", Target(BoxColliderType, 2), "m_Health", recorded, current);

            Assert.NotNull(first.RecurrenceKey);
            Assert.NotNull(second.RecurrenceKey);
            Assert.NotEqual(first.RecurrenceKey, second.RecurrenceKey);
        }

        // Spec #2 rename stability: a nested override's ChildPath is re-derived on every sync and is
        // deliberately excluded from OverrideTarget equality (Model/OverrideTarget.cs), so the
        // recurrence key must be excluded from it too, or a pure rename would re-surface the report.
        [Fact]
        public void StaleOverride_DifferOnlyByChildPath_ComposesSameKey()
        {
            var recorded = ValueNode.Primitive.Int(100);
            var current = ValueNode.Primitive.Int(75);
            var first = ConflictDetector.StaleOverride(
                "instance-1", Target(BoxColliderType, 1, "Turret/Barrel"), "m_Health", recorded, current);
            var renamed = ConflictDetector.StaleOverride(
                "instance-1", Target(BoxColliderType, 1, "Cannon/Barrel"), "m_Health", recorded, current);

            Assert.NotNull(first.RecurrenceKey);
            Assert.Equal(first.RecurrenceKey, renamed.RecurrenceKey);
        }

        // ---- Excluded-override report recurrence key --------------------------------------------

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) BuildTwoExcludedOverridesOnDistinctSubKeys()
        {
            var policy = new FakeFieldExclusionPolicy((SpriteRendererType, ExcludedRoot));
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = new[]
                {
                    new PropertyOverride { Target = Target(SpriteRendererType, 1), PropertyPath = ExcludedRoot, Value = ValueNode.Primitive.Float(3f) },
                    new PropertyOverride { Target = Target(SpriteRendererType, 2), PropertyPath = ExcludedRoot, Value = ValueNode.Primitive.Float(4f) },
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

            return (model, snapshot, map);
        }

        // Regression on the pre-fix collision (spec 44 Invariant #2): two authored excluded overrides
        // sharing an instance and a PropertyPath root but targeting DIFFERENT sub-objects (SubKey)
        // must not dedup each other's report in the once-per-session channel — each needs its own key.
        [Fact]
        public void ExcludedOverride_TwoOverridesSharingRootDifferentSubKey_ProduceDistinctRecurrenceKeys()
        {
            var (model, snapshot, map) = BuildTwoExcludedOverridesOnDistinctSubKeys();

            var plan = Materializer.Materialize(model, snapshot, map);

            var reports = plan.Conflicts.Where(c => c.Kind == ConflictKind.UnauthorableField).ToArray();
            Assert.Equal(2, reports.Length);
            Assert.All(reports, r => Assert.NotNull(r.RecurrenceKey));
            Assert.NotEqual(reports[0].RecurrenceKey, reports[1].RecurrenceKey);
        }
    }
}
