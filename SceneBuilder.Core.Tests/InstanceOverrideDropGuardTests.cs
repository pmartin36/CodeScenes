using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // live-verify-bug-fixes b2-t1 (3b): pins the reconcile-time guard Bug 3 depends on — a model
    // override whose (Target, PropertyPath) key is ABSENT from the snapshot (e.g. because the live
    // property was reverted to prefab default via PrefabUtility.RevertPropertyOverride) must ALWAYS
    // emit its DropInstanceCall and must NEVER be suppressed as a stale conflict.
    // InstanceOverrideDiff.DetectStaleOverrides can only mark a key stale when it is present on BOTH
    // sides (SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:39-62); a snapshot-absent key always
    // short-circuits, so this test characterizes already-correct behavior — it is expected to PASS
    // against current code (research.md b2-t1: production delta expected zero). It still guards
    // against a regression that would reintroduce a stale/suppress path for absent keys.
    // BaseValue is deliberately non-null here (differs from a hypothetical snapshot BaseValue) to
    // prove the drop is driven by snapshot-ABSENCE, not by the null-BaseValue short-circuit.
    public class InstanceOverrideDropGuardTests
    {
        private const string PrefabGuid = "guid-tank-prefab";
        private const string PrefabPath = "Assets/Prefabs/Tank.prefab";

        [Fact]
        public void NormalizedModelOverride_vs_EmptySnapshot_EmitsSingleDrop_NoStaleConflict()
        {
            var subKey = new PrefabInstanceKey { TargetObjectId = 42 };
            var target = new OverrideTarget { SubKey = subKey, ComponentType = "UnityEngine.Light" };

            var modelOverride = new PropertyOverride
            {
                Target = target,
                PropertyPath = "m_Intensity",
                Value = ValueNode.Primitive.Float(4f),
                BaseValue = ValueNode.Primitive.Float(1f),
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Tank",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
                Overrides = new[] { modelOverride },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Tank",
                SourcePrefabGuid = PrefabGuid,
                Overrides = System.Array.Empty<PropertyOverride>(),
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

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var drop = Assert.Single(result.Patch.Edits.OfType<DropInstanceCall>());
            Assert.Equal("instance-1", drop.Anchor);
            Assert.Equal(InstanceCallKind.Override, drop.Kind);
            Assert.Equal("m_Intensity", drop.PropertyPath);

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Empty(result.Conflicts);
        }
    }
}
