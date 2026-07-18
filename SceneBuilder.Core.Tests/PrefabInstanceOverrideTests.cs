using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Validation;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b3-t2: opaque overrides are never diffed into an op; a matched instance carrying nonempty
    // snapshot overrides yields an EMPTY plan plus an informational diagnostic flagging
    // "overrides preserved, not modelled (M10)". No overrides ⇒ no false-positive flag.
    public class PrefabInstanceOverrideTests
    {
        private static (SceneModel model, SceneSnapshot snapshot, IdentityMap map) BuildMatchedInstance(ValueNode.Unsupported? overrides)
        {
            var transform = new TransformData { Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Scale = Vec3.One };
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                Transform = transform,
                SourcePrefab = new AssetRef { Guid = "prefab-guid-1", DisplayPath = "Assets/Prefabs/Enemy.prefab" },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance",
                Name = "Enemy",
                Transform = transform,
                SourcePrefabGuid = "prefab-guid-1",
                OpaqueOverrides = overrides,
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };

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

            return (model, snapshot, map);
        }

        [Fact]
        public void Materialize_MatchedInstance_WithOverrides_EmptyPlan_PlusPreservedFlag()
        {
            var (model, snapshot, map) = BuildMatchedInstance(new ValueNode.Unsupported("m_Modifications: [ ... ]"));

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops);

            var diagnostic = Assert.Single(plan.Diagnostics);
            Assert.Equal(DiagnosticCodes.PrefabOverridesNotModelled, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        }

        [Fact]
        public void Materialize_MatchedInstance_NoOverrides_EmptyPlan_NoFlag()
        {
            var (model, snapshot, map) = BuildMatchedInstance(overrides: null);

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.Empty(plan.Ops);
            Assert.Empty(plan.Diagnostics);
        }

        [Fact]
        public void Materialize_MatchedInstance_WithOverrides_NoOpReferencesOverrides()
        {
            var (model, snapshot, map) = BuildMatchedInstance(new ValueNode.Unsupported("m_Modifications: [ ... ]"));

            var plan = Materializer.Materialize(model, snapshot, map);

            Assert.DoesNotContain(plan.Ops, op =>
                op is SceneBuilder.Core.Plan.SetField setField
                && (setField.Path.Contains("Modification") || setField.Path.Contains("override", System.StringComparison.OrdinalIgnoreCase)));
        }
    }
}
