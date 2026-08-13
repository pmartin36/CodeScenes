using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Differ.EmitCreateInstance is the CREATE/unmatched path for a PrefabInstanceNode — it must apply
    // the node's own authored override layer (Overrides/AddedComponents/RemovedComponents) on the very
    // first build, exactly like a matched PrefabInstanceNode's InstanceOverrideDiff.Emit call does,
    // otherwise a root-level override authored alongside a variant's or a nested instance's very first
    // Instance(...) statement is silently dropped.
    public class DifferCreateInstanceOverrideTests
    {
        [Fact]
        public void Diff_UnmatchedPrefabInstanceNode_WithAuthoredOverride_EmitsSetInstanceOverride()
        {
            var target = new OverrideTarget { ComponentType = "Light" };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "B",
                SourcePrefab = new AssetRef { Guid = "guid-b" },
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = target,
                        PropertyPath = "m_Intensity",
                        Value = ValueNode.Primitive.Float(7f),
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Contains(changeSet.Ops.OfType<AddInstance>(), a => a.LogicalId == "instance-1");

            var setOverride = Assert.Single(changeSet.Ops.OfType<SetInstanceOverride>());
            Assert.Equal("instance-1", setOverride.LogicalId);
            Assert.Equal("m_Intensity", setOverride.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Float(7f), setOverride.Value);
        }

        [Fact]
        public void Diff_UnmatchedPrefabInstanceNode_WithAddedComponent_EmitsAddInstanceComponent()
        {
            var target = new OverrideTarget { ComponentType = "" };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "B",
                SourcePrefab = new AssetRef { Guid = "guid-b" },
                AddedComponents = new[]
                {
                    new AddedComponent
                    {
                        Target = target,
                        Component = new ComponentData { Type = new TypeRef("UnityEngine.AudioSource") },
                    },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            var added = Assert.Single(changeSet.Ops.OfType<AddInstanceComponent>());
            Assert.Equal("instance-1", added.LogicalId);
            Assert.Equal("UnityEngine.AudioSource", added.Component.Type.FullName);
        }

        [Fact]
        public void Diff_UnmatchedPrefabInstanceNode_WithRemovedComponent_EmitsRemoveInstanceComponent()
        {
            var target = new OverrideTarget { ComponentType = "UnityEngine.BoxCollider" };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "B",
                SourcePrefab = new AssetRef { Guid = "guid-b" },
                RemovedComponents = new[] { target },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(model, snapshot, map);

            var removed = Assert.Single(changeSet.Ops.OfType<RemoveInstanceComponent>());
            Assert.Equal("instance-1", removed.LogicalId);
            Assert.Equal("UnityEngine.BoxCollider", removed.Target.ComponentType);
        }
    }
}
