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
        public void Diff_RectNode_AuthoredIntentDrivenBaseChannels_StillEmitsAuthoredTransform()
        {
            // Guard, RESTATED (scope/bucket-b2.md finding 2 corrected the rule stated in the old name/
            // comment here — base-transform driven bits ARE now consumed by the Differ, per-axis, but
            // ONLY when they are foreign to the model's own authored intent). A base-transform driven
            // bit reported on the MODEL side too (authored FitSize/SurfaceSnap intent — spec 23) still
            // emits the full authored value: the component re-drives from that write, because the
            // adapter re-baselines FitSize/SurfaceSnap on it (PlanExecutor.cs:393-405).
            var driven = ChannelMask.Scale | ChannelMask.PositionX | ChannelMask.PositionY;
            var authoredModel = Rect(scale: new Vec3(2, 2, 2), driven: driven);
            var authoredSnapshot = Rect(scale: new Vec3(5, 5, 5), driven: driven);

            var authoredChangeSet = DiffSingleMatchedNode(authoredModel, authoredSnapshot);

            var setTransform = Assert.Single(authoredChangeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(2, 2, 2), setTransform.Transform.Scale);

            // Counterpart, SAME values: the driven bits reported by the SNAPSHOT ALONE (a driver the
            // builder does not author, e.g. Canvas/CanvasScaler — the plugin cannot re-baseline it) are
            // held to the live value instead, and emit NOTHING. The pair is the whole rule in one screen.
            var foreignModel = Rect(scale: new Vec3(2, 2, 2));
            var foreignSnapshot = Rect(scale: new Vec3(5, 5, 5), driven: driven);

            var foreignChangeSet = DiffSingleMatchedNode(foreignModel, foreignSnapshot);

            Assert.Empty(foreignChangeSet.Ops);
        }

        [Fact]
        public void Diff_RectNode_ForeignDrivenBaseChannels_UnchangedModel_ProducesNoOp()
        {
            // scope/bucket-b2.md finding 2 (measured probe Q1): a ScreenSpaceOverlay Canvas whose
            // scale factor is not 1 drives its OWN base Position AND Scale (CanvasScaler) — a driver
            // the builder never authors (model.DrivenChannels == None). An otherwise-unchanged model
            // must therefore hold BOTH axes to the live value and emit nothing. Today the Differ
            // compares the raw live Scale and emits `SetTransform scale=(1,1,1)`, clobbering the
            // driven scale on every Build.
            var model = Rect();
            var snapshot = Rect(
                position: new Vec3(480, 270, 0),
                scale: new Vec3(2, 2, 2),
                sizeDelta: new Vec2(960, 540),
                driven: ChannelMask.AllRectFields | ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ | ChannelMask.Scale);

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            Assert.Empty(changeSet.Ops);
        }

        [Fact]
        public void Diff_RectNode_ForeignDrivenScaleAxis_FreeAxisChanged_HoldsLiveDrivenAxis()
        {
            // Probe Q4: the per-axis archetype. Only ScaleX is foreign-driven; ScaleY/Z are free and
            // genuinely changed. The emitted transform must hold the driven X axis to the LIVE value
            // (5) and carry the model's free Y/Z (2, 2) — never the raw model value on X (today: 2).
            var model = Rect(scale: new Vec3(2, 2, 2));
            var snapshot = Rect(scale: new Vec3(5, 1, 1), driven: ChannelMask.ScaleX);

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(5, 2, 2), setTransform.Transform.Scale);
        }

        [Fact]
        public void Diff_RectNode_ForeignDrivenScale_ZDrift_EmitsSetTransformKeepingLiveScale()
        {
            // Probe Q6: a genuine Z drift still emits (D8's z-drift path), and the payload must carry
            // the LIVE driven scale (2,2,2), not the model's own default (1,1,1) — a SetTransform
            // triggered by an unrelated axis must not clobber a foreign-driven Scale in the same op.
            var model = Rect(position: new Vec3(0, 0, 5));
            var snapshot = Rect(position: new Vec3(7, 9, 0), scale: new Vec3(2, 2, 2), driven: ChannelMask.Scale);

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var setTransform = Assert.Single(changeSet.Ops.OfType<SetTransform>());
            Assert.Equal(new Vec3(7, 9, 5), setTransform.Transform.Position);
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
        public void Diff_MatchedRectModel_PlainSnapshot_AllValuesAtDefault_EmitsAllRectFields()
        {
            // b2-t1 iteration 2 (scope/bucket-b2.md finding 2): a matched node whose model authors a
            // bare `.RectTransform()` (every field at RectTransformFields.Default*) against a live
            // PLAIN Transform must still promote — value-equality with the default table does NOT
            // mean the live object is already correct, since it has no rect fields at all.
            var model = Rect();
            var snapshot = new TransformData { Kind = "Transform", Position = Vec3.Zero };

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.AllRectFields, rectOp.Changed);
            Assert.True(rectOp.Transform.IsRectTransform);
            Assert.Empty(changeSet.Ops.OfType<SetTransform>());
        }

        [Fact]
        public void Diff_MatchedRectModel_PlainSnapshot_NonDefaultValue_StillEmitsAllRectFields()
        {
            // A kind change asserts the WHOLE rect state, never just the differing field — the
            // adapter's promotion writes all five fields in one pass regardless of which one moved.
            var model = Rect(sizeDelta: new Vec2(200, 120));
            var snapshot = new TransformData { Kind = "Transform", Position = Vec3.Zero };

            var changeSet = DiffSingleMatchedNode(model, snapshot);

            var rectOp = Assert.Single(changeSet.Ops.OfType<SetRectTransform>());
            Assert.Equal(ChannelMask.AllRectFields, rectOp.Changed);
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
