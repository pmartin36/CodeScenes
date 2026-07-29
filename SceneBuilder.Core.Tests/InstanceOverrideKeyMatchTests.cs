using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // live-verify-bug-fixes b1-t1: pins the override reconcile KEY-MATCH contract that b1-t2's
    // AuthoredPathResolver normalization must produce. The Reconciler's key is already correct
    // today — (OverrideTarget.SubKey, OverrideTarget.ComponentType, PropertyOverride.PropertyPath)
    // — this file characterizes both the matched (normalized) case and the mismatched (transient
    // `member:`/short-type) case Bug 1 currently produces upstream in parse+resolve. Both cases
    // pass against current code; the fix (b1-t2) lives in the adapter, not here.
    public class InstanceOverrideKeyMatchTests
    {
        private const string PrefabGuid = "guid-tank-prefab";
        private const string PrefabPath = "Assets/Prefabs/Tank.prefab";

        private static (PrefabInstanceNode instance, SnapshotNode snapshotInstance, IdentityMap map) BuildMatchedInstance(
            PropertyOverride[] modelOverrides,
            PropertyOverride[] snapshotOverrides)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Tank",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = modelOverrides,
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Tank",
                SourcePrefabGuid = PrefabGuid,
                Overrides = snapshotOverrides,
            };

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

            return (instance, snapshotInstance, map);
        }

        [Fact]
        public void NormalizedOverride_MatchesSnapshot_EmitsNoDropAndNoAppend()
        {
            var subKey = new PrefabInstanceKey { TargetObjectId = 42 };
            var target = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.Light" };

            var modelOverride = new PropertyOverride { Target = target, PropertyPath = "m_Intensity", Value = ValueNode.Primitive.Float(4f) };
            var snapshotOverride = new PropertyOverride { Target = target, PropertyPath = "m_Intensity", Value = ValueNode.Primitive.Float(4f) };

            var (instance, snapshotInstance, map) = BuildMatchedInstance(new[] { modelOverride }, new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Empty(result.Patch.Edits.OfType<AppendScopedOn>());
            Assert.Empty(result.Conflicts);
        }

        [Fact]
        public void TransientOverride_MemberPathAndShortType_DropsAndReAdds()
        {
            var subKey = new PrefabInstanceKey { TargetObjectId = 42 };
            var normalizedTarget = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.Light" };
            var transientTarget = new OverrideTarget { SubKey = subKey, ComponentType = "Light" };

            var modelOverride = new PropertyOverride { Target = transientTarget, PropertyPath = "member:intensity", Value = ValueNode.Primitive.Float(4f) };
            var snapshotOverride = new PropertyOverride { Target = normalizedTarget, PropertyPath = "m_Intensity", Value = ValueNode.Primitive.Float(4f) };

            var (instance, snapshotInstance, map) = BuildMatchedInstance(new[] { modelOverride }, new[] { snapshotOverride });
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var drop = Assert.Single(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Equal("instance-1", drop.Anchor);
            Assert.Equal(InstanceCallKind.Override, drop.Kind);
            Assert.Equal("member:intensity", drop.PropertyPath);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var set = Assert.Single(append.Sets);
            Assert.Equal("m_Intensity", set.PropertyPath);
            Assert.Equal("UnityEngine.Light", set.TypeFullName);
        }
    }
}
