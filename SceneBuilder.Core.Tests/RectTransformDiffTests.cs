using System;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: SetRectTransform ChangeOp emission (Differ), the X/Y double-authority suppression, and
    // per-axis driven masking. Fixture style mirrors DifferTests.cs (POCO SceneModel/SceneSnapshot/
    // IdentityMap, no parsing).
    // b2-t2/R4: Rect(...) and DiffSingleMatchedNode(...) live in RectTransformFixtures (shared with
    // RectTransformMaterializeTests.cs), consumed here via `using static`; assertions unchanged.
    public class RectTransformDiffTests
    {
        [Fact]
        public void Diff_ChangedAnchoredPosition_ProducesSingleSetRectTransform()
        {
            var model = Rect(anchoredPos: new Vec2(5, 5));
            var snapshot = Rect(anchoredPos: new Vec2(0, 0));

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.AnchoredPosition, rectOp.Changed);
            Assert.Empty(changeSet.Ops.OfType<SetTransform>());
        }

        [Fact]
        public void Diff_EqualRectTransformLayout_ProducesNoOp()
        {
            var t = Rect();

            var changeSet = DiffSingleMatchedNode(t, t);

            Assert.Empty(changeSet.Ops);
        }

        [Fact]
        public void Diff_RectTransform_LocalPositionXYDrift_ProducesNoSetTransform()
        {
            var model = Rect(position: new Vec3(0, 0, 0));
            var snapshot = Rect(position: new Vec3(7, 9, 0));

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            Assert.Empty(changeSet.Ops);
        }

        [Fact]
        public void Diff_RectTransform_ZDrift_EmitsSetTransformHoldingSnapshotXY()
        {
            var model = Rect(position: new Vec3(0, 0, 5));
            var snapshot = Rect(position: new Vec3(7, 9, 0));

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(7, 9, 5), setTransform.Transform.Position);
        }

        [Fact]
        public void Diff_DrivenRectField_ProducesNoSetRectTransform()
        {
            // Differing axis IS driven -> contributes nothing.
            var model1 = Rect(sizeDelta: new Vec2(50, 50));
            var snapshot1 = Rect(sizeDelta: new Vec2(100, 100), driven: ChannelMask.SizeDelta);
            var changeSet1 = DiffSingleMatchedNode(model1, snapshot1);
            Assert.Empty(changeSet1.Ops.OfType<SetRectTransform>());

            // Same object, a differing NON-driven Pivot -> exactly one op naming only Pivot.
            var model2 = Rect(sizeDelta: new Vec2(50, 50), pivot: new Vec2(0, 0));
            var snapshot2 = Rect(sizeDelta: new Vec2(100, 100), pivot: new Vec2(0.5f, 0.5f), driven: ChannelMask.SizeDelta);
            var changeSet2 = DiffSingleMatchedNode(model2, snapshot2);
            var rectOp = Assert.Single(changeSet2.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.Pivot, rectOp.Changed);
        }

        [Fact]
        public void Diff_PartiallyDrivenField_FreeAxisChanged_EmitsOpNamingOnlyThatAxis()
        {
            // ContentSizeFitter archetype: X axis driven, BOTH axes differ -> only Y (the free axis) names.
            var model = Rect(sizeDelta: new Vec2(150, 200));
            var snapshot = Rect(sizeDelta: new Vec2(100, 999), driven: ChannelMask.SizeDeltaX);

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.SizeDeltaY, rectOp.Changed);
        }

        [Fact]
        public void Diff_RectNode_DrivenBaseTransformChannels_StillEmitsAuthoredTransform()
        {
            // Guard (spec 23, pre-existing): base-transform driven bits (Scale/PositionX/Y) are NOT
            // consumed by the Differ — a rect node with a scale drift still emits the full authored
            // scale even when flagged driven on those base channels.
            var driven = ChannelMask.Scale | ChannelMask.PositionX | ChannelMask.PositionY;
            var model = Rect(scale: new Vec3(2, 2, 2), driven: driven);
            var snapshot = Rect(scale: new Vec3(5, 5, 5), driven: driven);

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(2, 2, 2), setTransform.Transform.Scale);
        }

        [Fact]
        public void Diff_SnapshotOnlyRectTransform_EmitsPromotionOp()
        {
            // Model side has never been rect (plain Transform); snapshot is a live RectTransform whose
            // anchor-derived x/y "drift" from the model's zero position must be suppressed.
            var model = new TransformData { Kind = "Transform", Position = Vec3.Zero };
            var snapshot = Rect(anchoredPos: new Vec2(7, 9));

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.None, rectOp.Changed);
            Assert.False(rectOp.Transform.IsRectTransform);
            Assert.Empty(changeSet.Ops.OfType<SetTransform>());
        }

        [Fact]
        public void Diff_CreateRectNode_EmitsSetRectTransformAfterSetTransform()
        {
            var model = Rect(anchoredPos: new Vec2(10, 20));
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Transform = model };
            var sceneModel = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };
            var emptySnapshot = new SceneSnapshot { SchemaVersion = 1, Roots = Array.Empty<SnapshotNode>() };
            var map = new IdentityMap();

            var changeSet = Differ.Diff(sceneModel, emptySnapshot, map);

            var ops = changeSet.Ops;
            var setTransformIndex = Array.FindIndex(ops, o => o is SetTransform st && st.LogicalId == "root-1");
            var setRectIndex = Array.FindIndex(ops, o => o is SetRectTransform rt && rt.LogicalId == "root-1");
            Assert.True(setTransformIndex >= 0, "expected a SetTransform op for the created node");
            Assert.True(setRectIndex > setTransformIndex, "SetRectTransform must follow SetTransform in plan order");
            var rectOp = (SetRectTransform)ops[setRectIndex];
            // F1: a created node's m_LocalPosition write (SetTransform, above) RE-DERIVES
            // anchoredPosition from the parent's rect on a RectTransform, so a field equal to the
            // RectTransform default is NOT already correct on the live object after that write. A
            // created node must own all five fields, not just the ones differing from the default.
            Assert.Equal(ChannelMask.AllRectFields, rectOp.Changed);

            // A bare `.RectTransform()` (all defaults) still emits the op, owning all five fields
            // for the same reason (F1) even though every field equals its default.
            var bareModel = Rect();
            var bareRoot = new GameObjectNode { LogicalId = "root-2", Name = "Root2", Transform = bareModel };
            var bareSceneModel = new SceneModel { SchemaVersion = 1, Roots = new[] { bareRoot } };
            var bareChangeSet = Differ.Diff(bareSceneModel, emptySnapshot, map);
            var bareRectOp = Assert.Single(bareChangeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.AllRectFields, bareRectOp.Changed);
        }

        [Fact]
        public void Diff_PlainTransformNode_Unchanged()
        {
            // Guard: a plain (non-rect) node's diff behavior is byte-for-byte unchanged by this task.
            var model = new TransformData { Kind = "Transform", Position = new Vec3(1, 2, 3) };
            var snapshot = new TransformData { Kind = "Transform", Position = new Vec3(0, 0, 0) };

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(1, 2, 3), setTransform.Transform.Position);
            Assert.Empty(changeSet.Ops.OfType<SetRectTransform>());
        }
    }
}
