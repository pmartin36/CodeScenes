using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // b2-t2: lowering SetRectTransform to the five constrained SetField paths, the D6 promotion
    // skip, D8's transform-write x/y hold-back proven end-to-end through Materialize, and
    // CreateObject.TransformKind. Model/snapshot/map fixtures come from RectTransformFixtures.
    public class RectTransformMaterializeTests
    {
        [Fact]
        public void Materialize_SetRectTransform_LowersToConstrainedRectFieldPaths()
        {
            var model = RectTransformFixtures.Rect(
                anchoredPos: new Vec2(5, 5), sizeDelta: new Vec2(50, 50),
                anchorMin: new Vec2(0, 0), anchorMax: new Vec2(1, 1), pivot: new Vec2(0, 0));
            var snapshot = RectTransformFixtures.Rect();

            var plan = MaterializeMatchedNode(model, snapshot);

            var rectPaths = plan.Ops.OfType<SetField>().Select(op => op.Path).ToHashSet();
            var expected = RectTransformFields.All.Select(f => f.SerializedPath).ToHashSet();
            Assert.Equal(expected, rectPaths);
            Assert.DoesNotContain("m_LocalPosition", rectPaths);
            Assert.DoesNotContain("m_LocalRotation", rectPaths);
            Assert.DoesNotContain("m_LocalScale", rectPaths);
        }

        [Fact]
        public void Materialize_SetRectTransform_OnlySizeDeltaChanged_EmitsExactlyOneSetField()
        {
            var model = RectTransformFixtures.Rect(sizeDelta: new Vec2(200, 200));
            var snapshot = RectTransformFixtures.Rect();

            var plan = MaterializeMatchedNode(model, snapshot);

            var setField = Assert.Single(plan.Ops.OfType<SetField>());
            Assert.Equal(RectTransformFields.SizeDeltaPath, setField.Path);
            Assert.Equal(new ValueNode.Vec2(model.SizeDelta!.Value), setField.Value);
        }

        [Fact]
        public void Materialize_PartiallyDrivenField_WritesWholeFieldValue()
        {
            // D5: X axis driven, both axes differ -> the Differ still names SizeDelta (Y is free),
            // and the Materializer writes the WHOLE field (both model axes), never a sub-path.
            var model = RectTransformFixtures.Rect(sizeDelta: new Vec2(150, 200));
            var snapshot = RectTransformFixtures.Rect(sizeDelta: new Vec2(100, 999), driven: ChannelMask.SizeDeltaX);

            var plan = MaterializeMatchedNode(model, snapshot);

            var setField = Assert.Single(plan.Ops.OfType<SetField>());
            Assert.Equal(RectTransformFields.SizeDeltaPath, setField.Path);
            Assert.Equal(new ValueNode.Vec2(new Vec2(150, 200)), setField.Value);
        }

        [Fact]
        public void Materialize_MatchedRectModel_PlainSnapshot_LowersToAllFiveRectFieldPaths()
        {
            // b2-t1 iteration 2: the forward-promotion op (model rect / live plain, even at
            // all-default values) must lower to all five constrained rect SetFields, mirroring
            // Materialize_SetRectTransform_LowersToConstrainedRectFieldPaths above.
            var model = RectTransformFixtures.Rect();
            var snapshot = new TransformData { Kind = "Transform", Position = Vec3.Zero };

            var plan = MaterializeMatchedNode(model, snapshot);

            var rectPaths = plan.Ops.OfType<SetField>().Select(op => op.Path).ToHashSet();
            var expected = RectTransformFields.All.Select(f => f.SerializedPath).ToHashSet();
            Assert.Equal(expected, rectPaths);
        }

        [Fact]
        public void Materialize_PromotionSetRectTransform_LowersToZeroOps()
        {
            // D6: model side is Kind=="Transform" (never authored as UI); snapshot is a live
            // RectTransform. No authored layout exists, so nothing may be written.
            var model = new TransformData { Kind = "Transform", Position = Vec3.Zero };
            var snapshot = RectTransformFixtures.Rect(anchoredPos: new Vec2(7, 9));

            var plan = MaterializeMatchedNode(model, snapshot);

            Assert.Empty(plan.Ops);
        }

        [Fact]
        public void Materialize_CreatedUiNode_RectSetFieldsFollowLocalPosition()
        {
            var model = RectTransformFixtures.Rect(anchoredPos: new Vec2(10, 20));

            var plan = MaterializeCreatedNode(model);

            var localPositionIndex = System.Array.FindIndex(plan.Ops, op => op is SetField sf && sf.Path == "m_LocalPosition");
            var anchoredPositionIndex = System.Array.FindIndex(plan.Ops, op => op is SetField sf && sf.Path == RectTransformFields.AnchoredPositionPath);
            Assert.True(localPositionIndex >= 0, "expected an m_LocalPosition SetField for the created node");
            Assert.True(anchoredPositionIndex > localPositionIndex, "rect SetFields must follow the transform SetFields so anchoredPosition wins");
        }

        [Fact]
        public void Materialize_CreatedUiNode_CreateObjectCarriesRectTransformKind()
        {
            var model = RectTransformFixtures.Rect(anchoredPos: new Vec2(10, 20));
            var plan = MaterializeCreatedNode(model);
            var create = Assert.Single(plan.Ops.OfType<CreateObject>());
            Assert.Equal(RectTransformFields.Kind, create.TransformKind);

            // A bare `.RectTransform()` at all five defaults still carries the kind and lowers to the
            // five rect SetFields exactly once each: EmitCreate owns all five fields on create, since
            // the prior m_LocalPosition write re-derives anchoredPosition from the parent's rect even
            // when every field equals its default (see RectTransformDiffTests). CreateObject carries
            // TransformKind - the adapter never infers UI-ness from a SetField path.
            var bareModel = RectTransformFixtures.Rect();
            var barePlan = MaterializeCreatedNode(bareModel);
            var bareCreate = Assert.Single(barePlan.Ops.OfType<CreateObject>());
            Assert.Equal(RectTransformFields.Kind, bareCreate.TransformKind);
            var rectPaths = RectTransformFields.All.Select(f => f.SerializedPath).ToHashSet();
            var bareRectPaths = barePlan.Ops.OfType<SetField>().Select(op => op.Path).Where(rectPaths.Contains).ToList();
            Assert.Equal(rectPaths.Count, bareRectPaths.Count);
            Assert.Equal(rectPaths, bareRectPaths.ToHashSet());
        }

        [Fact]
        public void Materialize_CreatedPlainNode_CreateObjectTransformKindIsNull()
        {
            var model = new TransformData { Kind = "Transform", Position = new Vec3(1, 2, 3) };

            var plan = MaterializeCreatedNode(model);

            var create = Assert.Single(plan.Ops.OfType<CreateObject>());
            Assert.Null(create.TransformKind);
        }

        [Fact]
        public void Materialize_UiNodeScaleDrift_LocalPositionSetFieldKeepsLiveAnchorXY()
        {
            // D8: a genuine scale drift on a UI node emits SetTransform whose Position is
            // (snapshot.X, snapshot.Y, model.Z) — the m_LocalPosition write must carry the LIVE
            // anchor-derived x/y, never the model's, so the write is a no-op on x/y.
            var model = RectTransformFixtures.Rect(position: new Vec3(0, 0, 0), scale: new Vec3(2, 2, 2));
            var snapshot = RectTransformFixtures.Rect(position: new Vec3(7, 9, 0), scale: new Vec3(1, 1, 1));

            var plan = MaterializeMatchedNode(model, snapshot);

            var localPosition = Assert.Single(plan.Ops.OfType<SetField>(), op => op.Path == "m_LocalPosition");
            Assert.Equal(new ValueNode.Vec3(new Vec3(7, 9, 0)), localPosition.Value);
        }
    }
}
