using System.Collections.Generic;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SpatialComponentTests
    {
        [Fact]
        public void ChannelMask_Scale_EqualsXYZScaleBits()
        {
            Assert.Equal(
                ChannelMask.ScaleX | ChannelMask.ScaleY | ChannelMask.ScaleZ,
                ChannelMask.Scale);
        }

        [Fact]
        public void ChannelMask_AxisFlags_AreDistinctSingleBitsAndNoneIsZero()
        {
            Assert.Equal(ChannelMask.None, (ChannelMask)0);

            var flags = new HashSet<ChannelMask>
            {
                ChannelMask.PositionX,
                ChannelMask.PositionY,
                ChannelMask.PositionZ,
                ChannelMask.ScaleX,
                ChannelMask.ScaleY,
                ChannelMask.ScaleZ,
            };

            Assert.Equal(6, flags.Count);
        }

        [Fact]
        public void TransformData_Default_DrivenChannelsNone()
        {
            var transform = new TransformData();

            Assert.Equal(ChannelMask.None, transform.DrivenChannels);
        }

        [Fact]
        public void TransformData_Serialize_OmitsDrivenChannels()
        {
            var driven = new TransformData { DrivenChannels = ChannelMask.Scale, Position = new Vec3(1, 2, 3) };
            var plain = driven with { DrivenChannels = ChannelMask.None };

            var drivenJson = CanonicalJson.Serialize(driven);
            var plainJson = CanonicalJson.Serialize(plain);

            Assert.DoesNotContain("drivenChannels", drivenJson);
            Assert.Equal(plainJson, drivenJson);
        }

        [Fact]
        public void SpatialComponents_TypeNames_MatchRuntimeFqns()
        {
            Assert.Equal("SceneBuilder.Authoring.Sizer", SpatialComponents.SizerTypeName);
            Assert.Equal("SceneBuilder.Authoring.Snapper", SpatialComponents.SnapperTypeName);
        }

        [Fact]
        public void SpatialComponents_SizerFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("width", SpatialComponents.SizerFields.Width);
            Assert.Equal("height", SpatialComponents.SizerFields.Height);
            Assert.Equal("depth", SpatialComponents.SizerFields.Depth);
            Assert.Equal("size", SpatialComponents.SizerFields.Size);
        }

        [Fact]
        public void SpatialComponents_SnapperFieldKeys_MatchExpectedLiterals()
        {
            Assert.Equal("up", SpatialComponents.SnapperFields.Up);
            Assert.Equal("down", SpatialComponents.SnapperFields.Down);
            Assert.Equal("left", SpatialComponents.SnapperFields.Left);
            Assert.Equal("right", SpatialComponents.SnapperFields.Right);
            Assert.Equal("forward", SpatialComponents.SnapperFields.Forward);
            Assert.Equal("back", SpatialComponents.SnapperFields.Back);
            Assert.Equal("target", SpatialComponents.SnapperFields.Target);
        }
    }
}
