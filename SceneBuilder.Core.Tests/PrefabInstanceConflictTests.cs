using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Prefab instances (b5-t3, spec 07) are identity-keyed by their persisted
    // (TargetPrefabId, TargetObjectId) pair-key, NOT by sibling name/position — so two bare
    // `scene.Instance("<same path>")` calls (the canonical ArenaScene shape, spec 07 lines 122-124)
    // must NOT trip the positional-sibling ambiguity rule (spec 16 / SB2201) that
    // ConflictDetector.AmbiguousGroups applies to plain GameObjectNodes. This is the ONE shared
    // chokepoint (ConflictDetector.AmbiguousGroups.Walk) both DuplicateNameConflicts (build-refusal)
    // and DetectAmbiguousReorders read from, so this test pins the exemption there rather than at any
    // one consumer.
    public class PrefabInstanceConflictTests
    {
        [Fact]
        public void Parse_TwoBareSamePathInstances_ReportsNoAmbiguity()
        {
            const string source = @"
public class ArenaScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
        scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";
            var result = BuilderParser.Parse(source);

            Assert.Empty(result.Ambiguities);
        }

        // The exemption must be SCOPED to prefab instances — a plain-node duplicate-sibling group in
        // the SAME file is still refused. Otherwise the fix would silently regress spec 16 instead of
        // narrowly carving out the new spec-07 construct.
        [Fact]
        public void Parse_MixedPlainDuplicateAndSamePathInstances_ReportsOnlyThePlainDuplicate()
        {
            const string source = @"
public class MixedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Enemy"");
        scene.Add(""Enemy"");
        scene.Instance(""Assets/Prefabs/Coin.prefab"");
        scene.Instance(""Assets/Prefabs/Coin.prefab"");
    }
}
";
            var result = BuilderParser.Parse(source);

            var ambiguity = Assert.Single(result.Ambiguities);
            Assert.Contains("Enemy", ambiguity.Reason);
            Assert.DoesNotContain("Coin", ambiguity.Reason);
        }

        // b3-t3: stale-override conflict detection (spec #9/#10, checklist #6). Neither silently
        // keeping nor dropping an override whose recorded BaseValue no longer matches the source
        // prefab's CURRENT default while the live instance value now sits at that new default.
        private static (PrefabInstanceNode desired, SnapshotNode snapshot, OverrideTarget target) BuildOverrideFixture(
            ValueNode? desiredBase, ValueNode desiredValue,
            ValueNode? snapshotBase, ValueNode snapshotValue)
        {
            var target = new OverrideTarget { PrefabId = "prefab-guid-1", ObjectId = 12345 };

            var desired = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = target, PropertyPath = "m_Health", Value = desiredValue, BaseValue = desiredBase,
                    },
                },
            };

            var snapshot = new SnapshotNode
            {
                GlobalObjectId = "goid-instance",
                Name = "Enemy",
                SourcePrefabGuid = "prefab-guid-1",
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = target, PropertyPath = "m_Health", Value = snapshotValue, BaseValue = snapshotBase,
                    },
                },
            };

            return (desired, snapshot, target);
        }

        [Fact]
        public void DetectStaleOverrides_StaleFixture_YieldsSingleConflict()
        {
            // Recorded default was 100; the prefab's current default drifted to 75, and the live
            // instance value now sits AT that new default — stale.
            var (desired, snapshot, target) = BuildOverrideFixture(
                desiredBase: ValueNode.Primitive.Int(100), desiredValue: ValueNode.Primitive.Int(999),
                snapshotBase: ValueNode.Primitive.Int(75), snapshotValue: ValueNode.Primitive.Int(75));

            var conflicts = new List<Conflict>();
            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();

            InstanceOverrideDiff.DetectStaleOverrides(desired, snapshot, conflicts, staleKeys);

            var conflict = Assert.Single(conflicts);
            Assert.Equal(ConflictKind.StaleOverride, conflict.Kind);
            Assert.Contains("m_Health", conflict.Reason);
            Assert.Contains((target, "m_Health"), staleKeys);
        }

        [Fact]
        public void DetectStaleOverrides_InstanceValueDiffersFromNewDefault_NoConflict()
        {
            // Recorded default drifted (100 -> 75), but the live instance value (999) is NOT the new
            // default — a genuine, still-live override, not staleness.
            var (desired, snapshot, target) = BuildOverrideFixture(
                desiredBase: ValueNode.Primitive.Int(100), desiredValue: ValueNode.Primitive.Int(50),
                snapshotBase: ValueNode.Primitive.Int(75), snapshotValue: ValueNode.Primitive.Int(999));

            var conflicts = new List<Conflict>();
            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();

            InstanceOverrideDiff.DetectStaleOverrides(desired, snapshot, conflicts, staleKeys);

            Assert.Empty(conflicts);
            Assert.DoesNotContain((target, "m_Health"), staleKeys);
        }

        [Fact]
        public void DetectStaleOverrides_BaseValueUnchanged_NoConflict()
        {
            // Recorded default == current default: the prefab default never drifted.
            var (desired, snapshot, target) = BuildOverrideFixture(
                desiredBase: ValueNode.Primitive.Int(100), desiredValue: ValueNode.Primitive.Int(100),
                snapshotBase: ValueNode.Primitive.Int(100), snapshotValue: ValueNode.Primitive.Int(100));

            var conflicts = new List<Conflict>();
            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();

            InstanceOverrideDiff.DetectStaleOverrides(desired, snapshot, conflicts, staleKeys);

            Assert.Empty(conflicts);
            Assert.DoesNotContain((target, "m_Health"), staleKeys);
        }

        [Fact]
        public void DetectStaleOverrides_NullBaseValue_NoConflict()
        {
            // Recorded BaseValue not carried (null) — staleness cannot be determined; must not
            // false-positive.
            var (desired, snapshot, target) = BuildOverrideFixture(
                desiredBase: null, desiredValue: ValueNode.Primitive.Int(75),
                snapshotBase: ValueNode.Primitive.Int(75), snapshotValue: ValueNode.Primitive.Int(75));

            var conflicts = new List<Conflict>();
            var staleKeys = new HashSet<(OverrideTarget Target, string PropertyPath)>();

            InstanceOverrideDiff.DetectStaleOverrides(desired, snapshot, conflicts, staleKeys);

            Assert.Empty(conflicts);
            Assert.DoesNotContain((target, "m_Health"), staleKeys);
        }

        [Fact]
        public void Materialize_StaleOverride_EmitsOneLocatedConflict_AndSuppressesOp()
        {
            var (desired, snapshot, target) = BuildOverrideFixture(
                desiredBase: ValueNode.Primitive.Int(100), desiredValue: ValueNode.Primitive.Int(999),
                snapshotBase: ValueNode.Primitive.Int(75), snapshotValue: ValueNode.Primitive.Int(75));

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { desired } };
            var sceneSnapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshot } };
            var map = new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance", Kind = "PrefabInstance",
                        SourcePrefabGuid = "prefab-guid-1",
                    },
                },
            };

            var plan = Materializer.Materialize(model, sceneSnapshot, map);

            var conflict = Assert.Single(plan.Conflicts);
            Assert.Equal(ConflictKind.StaleOverride, conflict.Kind);
            Assert.Contains("m_Health", conflict.Reason);

            Assert.Empty(plan.Ops.OfType<SceneBuilder.Core.Plan.SetInstanceOverride>()
                .Where(op => op.Target == target && op.PropertyPath == "m_Health"));
            Assert.Empty(plan.Ops.OfType<SceneBuilder.Core.Plan.RevertInstanceOverride>()
                .Where(op => op.Target == target && op.PropertyPath == "m_Health"));
        }
    }
}
