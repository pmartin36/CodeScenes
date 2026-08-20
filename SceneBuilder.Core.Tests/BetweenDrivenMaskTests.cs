using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Pins Between's single-owner mask helper and the Core-side name/enum mirror of the runtime
    // Between component. Parse, emit-suppression, and the live-scene snapshot reader all route
    // through BetweenDrivenMask/AxisMember/TryAxisFromMember; these tests pin the two branches
    // (world vs oriented) and the byte-for-byte name contract those call sites depend on.
    public class BetweenDrivenMaskTests
    {
        [Theory]
        [InlineData(SpatialAxis.X, ChannelMask.PositionX)]
        [InlineData(SpatialAxis.Y, ChannelMask.PositionY)]
        [InlineData(SpatialAxis.Z, ChannelMask.PositionZ)]
        public void BetweenDrivenMask_World_ReturnsExactlyOnePositionBitForTheAxis(SpatialAxis axis, ChannelMask expected)
        {
            var mask = SpatialComponents.BetweenDrivenMask(axis, oriented: false);

            Assert.Equal(expected, mask);
        }

        [Theory]
        [InlineData(SpatialAxis.X)]
        [InlineData(SpatialAxis.Y)]
        [InlineData(SpatialAxis.Z)]
        public void BetweenDrivenMask_Oriented_ReturnsFullPositionMaskForEveryAxis(SpatialAxis axis)
        {
            var mask = SpatialComponents.BetweenDrivenMask(axis, oriented: true);

            Assert.Equal(ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ, mask);
        }

        [Fact]
        public void ChannelMask_RotationFlags_AreDistinctSingleBitsAndDisjoint()
        {
            var rotationFlags = new[] { ChannelMask.RotationX, ChannelMask.RotationY, ChannelMask.RotationZ };

            foreach (var flag in rotationFlags)
            {
                Assert.NotEqual(ChannelMask.None, flag);
                // A single-bit flag equals its own value ANDed with itself and nothing else set.
                Assert.Equal(flag, flag & flag);
            }

            Assert.NotEqual(ChannelMask.RotationX, ChannelMask.RotationY);
            Assert.NotEqual(ChannelMask.RotationY, ChannelMask.RotationZ);
            Assert.NotEqual(ChannelMask.RotationX, ChannelMask.RotationZ);

            var existing = ChannelMask.Scale | ChannelMask.AllRectFields |
                ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ;
            Assert.Equal(ChannelMask.None, (ChannelMask.RotationX | ChannelMask.RotationY | ChannelMask.RotationZ) & existing);
        }

        [Fact]
        public void BetweenNames_MirrorRuntimeContract()
        {
            Assert.Equal("SceneBuilder.Authoring.Between", SpatialComponents.BetweenTypeName);
            Assert.Equal("SceneBuilder.Authoring.Between+Axis", SpatialComponents.BetweenEnums.AxisTypeName);
            Assert.Equal("X", SpatialComponents.BetweenEnums.X);
            Assert.Equal("Y", SpatialComponents.BetweenEnums.Y);
            Assert.Equal("Z", SpatialComponents.BetweenEnums.Z);
            Assert.Equal("from", SpatialComponents.BetweenFields.From);
            Assert.Equal("to", SpatialComponents.BetweenFields.To);
            Assert.Equal("fraction", SpatialComponents.BetweenFields.Fraction);
            Assert.Equal("axis", SpatialComponents.BetweenFields.Axis);
            Assert.Equal("orientation", SpatialComponents.BetweenFields.Orientation);
        }

        [Fact]
        public void SpatialAxis_UnderlyingValues_Are012()
        {
            Assert.Equal(0, (int)SpatialAxis.X);
            Assert.Equal(1, (int)SpatialAxis.Y);
            Assert.Equal(2, (int)SpatialAxis.Z);
        }

        [Fact]
        public void AxisMember_And_TryAxisFromMember_RoundTrip()
        {
            Assert.Equal("X", SpatialComponents.AxisMember(SpatialAxis.X));
            Assert.Equal("Y", SpatialComponents.AxisMember(SpatialAxis.Y));
            Assert.Equal("Z", SpatialComponents.AxisMember(SpatialAxis.Z));

            Assert.True(SpatialComponents.TryAxisFromMember("Y", out var axis));
            Assert.Equal(SpatialAxis.Y, axis);

            Assert.False(SpatialComponents.TryAxisFromMember("Bogus", out _));
        }
    }
}
