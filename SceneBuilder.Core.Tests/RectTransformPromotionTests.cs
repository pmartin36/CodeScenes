using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // RectTransformPromotion.Promote — the desired-model stage that promotes a plain-Transform
    // node hosting a require-rect component to Kind=="RectTransform" (RectTransformFields defaults)
    // before any diff, so the matched omit-at-default arm runs instead of the D6 promotion arm.
    public class RectTransformPromotionTests
    {
        private static readonly TypeRef RequireRectComponentType = new("UnityEngine.UI.Slider", "UnityEngine.UI");

        private static GameObjectNode RequireRectHostNode(string logicalId)
        {
            return new GameObjectNode
            {
                LogicalId = logicalId,
                Name = "Root",
                Transform = new TransformData { Kind = "Transform", Position = Vec3.Zero },
                Components = new[]
                {
                    new ComponentData { LogicalId = logicalId + "-slider", Type = RequireRectComponentType },
                },
            };
        }

        private static IdentityMap RootIdentityMap(string logicalId, string globalObjectId)
        {
            return new IdentityMap
            {
                Scene = "Assets/Scenes/Demo.unity",
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = logicalId, GlobalObjectId = globalObjectId, Kind = "GameObject" },
                },
            };
        }

        [Fact]
        public void Diff_RequireRectHost_AllDefaultLayout_ProducesNoSetRectTransform()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { RequireRectHostNode("root-1") } };
            var promoted = RectTransformPromotion.Promote(model, _ => true);

            var snapshotRoot = new SnapshotNode { GlobalObjectId = "goid-root", Name = "Root", Transform = Rect() };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(promoted, snapshot, RootIdentityMap("root-1", "goid-root"));

            Assert.Empty(changeSet.Ops.OfType<SetRectTransform>());
        }

        [Fact]
        public void Diff_RequireRectHost_NonDefaultLayout_EmitsOnlyChangedChannels()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { RequireRectHostNode("root-1") } };
            var promoted = RectTransformPromotion.Promote(model, _ => true);

            var snapshotRoot = new SnapshotNode
            {
                GlobalObjectId = "goid-root",
                Name = "Root",
                Transform = Rect(anchoredPos: new Vec2(5, 5)),
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotRoot } };

            var changeSet = Differ.Diff(promoted, snapshot, RootIdentityMap("root-1", "goid-root"));

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.AnchoredPosition, rectOp.Changed);
        }

        [Fact]
        public void Promotion_ExplicitRectTransform_IsNotDoubleHandled()
        {
            var explicitTransform = Rect(sizeDelta: new Vec2(200, 120));
            var root = new GameObjectNode
            {
                LogicalId = "root-1",
                Name = "Root",
                Transform = explicitTransform,
                Components = new[] { new ComponentData { LogicalId = "root-1-slider", Type = RequireRectComponentType } },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var promoted = RectTransformPromotion.Promote(model, _ => true);

            Assert.Equal(explicitTransform, promoted.Roots[0].Transform);
        }

        [Fact]
        public void Promotion_FalsePredicate_LeavesPlainTransformUnchanged()
        {
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { RequireRectHostNode("root-1") } };

            var promoted = RectTransformPromotion.Promote(model, _ => false);

            Assert.False(promoted.Roots[0].Transform.IsRectTransform);
            Assert.Equal("Transform", promoted.Roots[0].Transform.Kind);
        }

        [Fact]
        public void Promotion_PromotesNestedChildAndPrefabAddedGameObject()
        {
            var rootWithChild = new GameObjectNode
            {
                LogicalId = "root-1",
                Name = "Root",
                Transform = new TransformData { Kind = "Transform" },
                Children = new[] { RequireRectHostNode("child-1") },
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Instance",
                Transform = new TransformData { Kind = "Transform" },
                AddedGameObjects = new[] { new AddedGameObject { Node = RequireRectHostNode("added-1") } },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { rootWithChild, instance } };

            var promoted = RectTransformPromotion.Promote(model, _ => true);

            var promotedRoot = promoted.Roots.Single(r => r.LogicalId == "root-1");
            Assert.True(promotedRoot.Children.Single(c => c.LogicalId == "child-1").Transform.IsRectTransform);
            // The root itself hosts no require-rect component, so its own Transform stays plain.
            Assert.False(promotedRoot.Transform.IsRectTransform);

            var promotedInstance = (PrefabInstanceNode)promoted.Roots.Single(r => r.LogicalId == "instance-1");
            Assert.True(promotedInstance.AddedGameObjects.Single().Node.Transform.IsRectTransform);
        }
    }
}
