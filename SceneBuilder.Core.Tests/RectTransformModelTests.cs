using System.Collections.Generic;
using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class RectTransformModelTests
    {
        private static TransformData RectSample() => new TransformData
        {
            Kind = RectTransformFields.Kind,
            Position = new Vec3(1.5f, 2.5f, 3.5f),
            Rotation = Quat.Identity,
            Scale = Vec3.One,
            AnchoredPosition = new Vec2(-10.25f, -10.75f),
            SizeDelta = new Vec2(200f, 120f),
            AnchorMin = new Vec2(1f, 1f),
            AnchorMax = new Vec2(1f, 1f),
            Pivot = new Vec2(1f, 1f),
        };

        private static SceneModel WrapInModel(TransformData transform) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "Root",
                    Name = "Root",
                    Transform = transform,
                },
            },
        };

        [Fact]
        public void CanonicalSerializer_RectTransform_IsByteIdentical_FloatsRoundTripInvariant()
        {
            var model = WrapInModel(RectSample());

            var json1 = SceneModelSerializer.Serialize(model);
            var json2 = SceneModelSerializer.Serialize(model);
            Assert.Equal(json1, json2);

            using var doc = JsonDocument.Parse(json1);
            var transform = doc.RootElement.GetProperty("roots")[0].GetProperty("transform");
            Assert.Equal("RectTransform", transform.GetProperty("kind").GetString());
            Assert.True(transform.TryGetProperty("anchoredPosition", out _));
            Assert.True(transform.TryGetProperty("sizeDelta", out _));
            Assert.True(transform.TryGetProperty("anchorMin", out _));
            Assert.True(transform.TryGetProperty("anchorMax", out _));
            Assert.True(transform.TryGetProperty("pivot", out _));

            var back = SceneModelSerializer.Deserialize(json1);
            var roundTripJson = SceneModelSerializer.Serialize(back);
            Assert.Equal(json1, roundTripJson);
        }

        [Fact]
        public void Serialize_PlainTransform_OmitsAllFiveUiFields()
        {
            var model = WrapInModel(new TransformData());

            var json = SceneModelSerializer.Serialize(model);

            Assert.DoesNotContain("anchoredPosition", json);
            Assert.DoesNotContain("sizeDelta", json);
            Assert.DoesNotContain("anchorMin", json);
            Assert.DoesNotContain("anchorMax", json);
            Assert.DoesNotContain("pivot", json);
        }

        [Fact]
        public void Serialize_TwoStructurallyEqualRectModels_AreByteIdentical()
        {
            var modelA = WrapInModel(RectSample());
            var modelB = WrapInModel(RectSample());

            var jsonA = SceneModelSerializer.Serialize(modelA);
            var jsonB = SceneModelSerializer.Serialize(modelB);

            Assert.Equal(jsonA, jsonB);
        }

        [Fact]
        public void Vec2_Equality_IsExact_NoTolerance()
        {
            Assert.NotEqual(new Vec2(0, 1f), new Vec2(0, 1.0000001f));
        }

        [Fact]
        public void ChannelMask_RectBits_AreDistinctFromTransformBits()
        {
            var bits = new[]
            {
                ChannelMask.PositionX, ChannelMask.PositionY, ChannelMask.PositionZ,
                ChannelMask.ScaleX, ChannelMask.ScaleY, ChannelMask.ScaleZ,
                ChannelMask.AnchoredPositionX, ChannelMask.AnchoredPositionY,
                ChannelMask.SizeDeltaX, ChannelMask.SizeDeltaY,
                ChannelMask.AnchorMinX, ChannelMask.AnchorMinY,
                ChannelMask.AnchorMaxX, ChannelMask.AnchorMaxY,
                ChannelMask.PivotX, ChannelMask.PivotY,
            };

            var distinct = new HashSet<ChannelMask>(bits);
            Assert.Equal(16, distinct.Count);
        }

        [Fact]
        public void ChannelMask_RectWholeFieldAliases_AreTheirTwoAxisBits()
        {
            Assert.Equal(ChannelMask.AnchoredPositionX | ChannelMask.AnchoredPositionY, ChannelMask.AnchoredPosition);
            Assert.Equal(ChannelMask.SizeDeltaX | ChannelMask.SizeDeltaY, ChannelMask.SizeDelta);
            Assert.Equal(ChannelMask.AnchorMinX | ChannelMask.AnchorMinY, ChannelMask.AnchorMin);
            Assert.Equal(ChannelMask.AnchorMaxX | ChannelMask.AnchorMaxY, ChannelMask.AnchorMax);
            Assert.Equal(ChannelMask.PivotX | ChannelMask.PivotY, ChannelMask.Pivot);
            Assert.Equal(
                ChannelMask.AnchoredPosition | ChannelMask.SizeDelta | ChannelMask.AnchorMin | ChannelMask.AnchorMax | ChannelMask.Pivot,
                ChannelMask.AllRectFields);
            Assert.Equal(ChannelMask.None, ChannelMask.AllRectFields & ChannelMask.Scale);
            Assert.Equal(ChannelMask.None, ChannelMask.AllRectFields & (ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ));
        }

        [Fact]
        public void RectTransformFields_Defaults_MatchLiveRectTransform()
        {
            Assert.Equal(new Vec2(0f, 0f), RectTransformFields.DefaultAnchoredPosition);
            Assert.Equal(new Vec2(100f, 100f), RectTransformFields.DefaultSizeDelta);
            Assert.Equal(new Vec2(0.5f, 0.5f), RectTransformFields.DefaultAnchorMin);
            Assert.Equal(new Vec2(0.5f, 0.5f), RectTransformFields.DefaultAnchorMax);
            Assert.Equal(new Vec2(0.5f, 0.5f), RectTransformFields.DefaultPivot);
        }

        [Fact]
        public void RectTransformFields_All_IsFiveFieldsInCanonicalArgumentOrder()
        {
            var all = RectTransformFields.All;

            Assert.Equal(5, all.Count);
            Assert.Equal(RectTransformFields.AnchoredPosArg, all[0].ArgName);
            Assert.Equal(RectTransformFields.SizeDeltaArg, all[1].ArgName);
            Assert.Equal(RectTransformFields.AnchorMinArg, all[2].ArgName);
            Assert.Equal(RectTransformFields.AnchorMaxArg, all[3].ArgName);
            Assert.Equal(RectTransformFields.PivotArg, all[4].ArgName);

            Assert.Equal(RectTransformFields.AnchoredPositionPath, all[0].SerializedPath);
            Assert.Equal(RectTransformFields.SizeDeltaPath, all[1].SerializedPath);
            Assert.Equal(RectTransformFields.AnchorMinPath, all[2].SerializedPath);
            Assert.Equal(RectTransformFields.AnchorMaxPath, all[3].SerializedPath);
            Assert.Equal(RectTransformFields.PivotPath, all[4].SerializedPath);

            Assert.True(RectTransformFields.TryFromArgName(RectTransformFields.AnchoredPosArg, out var byArg));
            Assert.Equal(RectTransformFields.AnchoredPositionPath, byArg.SerializedPath);

            Assert.True(RectTransformFields.TryFromSerializedPath(RectTransformFields.SizeDeltaPath, out var byPath));
            Assert.Equal(RectTransformFields.SizeDeltaArg, byPath.ArgName);

            var sample = RectSample();
            Assert.Equal(sample.AnchoredPosition, all[0].Get(sample));
            Assert.Equal(sample.SizeDelta, all[1].Get(sample));
        }

        [Fact]
        public void TransformData_IsRectTransform_TracksKind()
        {
            var plain = new TransformData();
            var rect = new TransformData { Kind = RectTransformFields.Kind };

            Assert.False(plain.IsRectTransform);
            Assert.True(rect.IsRectTransform);

            var equalA = new TransformData { Position = new Vec3(1, 2, 3) };
            var equalB = new TransformData { Position = new Vec3(1, 2, 3) };
            Assert.Equal(equalA, equalB);
        }

        [Fact]
        public void RectTransformFields_OffsetMinMax_MatchUnity()
        {
            AssertOffsets(new Vec2(0, 0), new Vec2(100, 100), new Vec2(0.5f, 0.5f), new Vec2(-50, -50), new Vec2(50, 50));
            AssertOffsets(new Vec2(-10, -10), new Vec2(200, 120), new Vec2(1, 1), new Vec2(-210, -130), new Vec2(-10, -10));
            AssertOffsets(new Vec2(10, 20), new Vec2(200, 120), new Vec2(0, 1), new Vec2(10, -100), new Vec2(210, 20));
            AssertOffsets(new Vec2(10, 20), new Vec2(200, 120), new Vec2(0.5f, 0.5f), new Vec2(-90, -40), new Vec2(110, 80));
        }

        private static void AssertOffsets(Vec2 anchoredPos, Vec2 sizeDelta, Vec2 pivot, Vec2 expectedMin, Vec2 expectedMax)
        {
            var min = RectTransformFields.OffsetMin(anchoredPos, sizeDelta, pivot);
            var max = RectTransformFields.OffsetMax(anchoredPos, sizeDelta, pivot);

            Assert.Equal(expectedMin.X, min.X, 3);
            Assert.Equal(expectedMin.Y, min.Y, 3);
            Assert.Equal(expectedMax.X, max.X, 3);
            Assert.Equal(expectedMax.Y, max.Y, 3);
        }
    }
}
