using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class RotationConversionTests
    {
        private const float Tolerance = 1e-5f;

        [Fact]
        public void EulerToQuat_90DegreeYaw_MatchesExpectedQuaternion()
        {
            var quat = Rotation.EulerToQuat(new Vec3(0, 90, 0));

            Assert.Equal(0f, quat.X, Tolerance);
            Assert.Equal(0.70710678f, quat.Y, Tolerance);
            Assert.Equal(0f, quat.Z, Tolerance);
            Assert.Equal(0.70710678f, quat.W, Tolerance);
        }

        [Fact]
        public void EulerToQuat_ThenQuatToEuler_RoundTrips_ForYaw90()
        {
            var euler = new Vec3(0, 90, 0);

            var quat = Rotation.EulerToQuat(euler);
            var roundTripped = Rotation.QuatToEuler(quat);

            Assert.Equal(euler.X, roundTripped.X, Tolerance);
            Assert.Equal(euler.Y, roundTripped.Y, Tolerance);
            Assert.Equal(euler.Z, roundTripped.Z, Tolerance);
        }

        [Fact]
        public void EulerToQuat_MultiAxis_PinsZXYCompositionOrder()
        {
            // Single-axis cases don't distinguish rotation order; this multi-axis case pins
            // the Unity ZXY (q = qY * qX * qZ) convention that M2 Euler emission depends on.
            var quat = Rotation.EulerToQuat(new Vec3(30, 45, 60));

            Assert.Equal(0.39190384f, quat.X, Tolerance);
            Assert.Equal(0.20056212f, quat.Y, Tolerance);
            Assert.Equal(0.36042341f, quat.Z, Tolerance);
            Assert.Equal(0.82236317f, quat.W, Tolerance);
        }

        [Fact]
        public void EulerToQuat_ThenQuatToEuler_RoundTrips_ForMultiAxis()
        {
            var euler = new Vec3(30, 45, 60);

            var quat = Rotation.EulerToQuat(euler);
            var roundTripped = Rotation.QuatToEuler(quat);

            Assert.Equal(euler.X, roundTripped.X, Tolerance);
            Assert.Equal(euler.Y, roundTripped.Y, Tolerance);
            Assert.Equal(euler.Z, roundTripped.Z, Tolerance);
        }

        [Fact]
        public void QuatIdentity_EqualsUnitQuaternion()
        {
            Assert.Equal(new Quat(0, 0, 0, 1), Quat.Identity);
        }

        [Fact]
        public void TransformData_DefaultConstruction_AppliesContractDefaults()
        {
            var transform = new TransformData();

            Assert.Equal("Transform", transform.Kind);
            Assert.Equal(Vec3.Zero, transform.Position);
            Assert.Equal(Quat.Identity, transform.Rotation); // must be Identity, not default(Quat)
            Assert.Equal(Vec3.One, transform.Scale);
        }

        [Fact]
        public void GameObjectNode_DefaultConstruction_AppliesContractDefaults()
        {
            var node = new GameObjectNode();

            Assert.Equal("Untagged", node.Tag);
            Assert.Equal(0, node.Layer);
            Assert.True(node.Active);
            Assert.False(node.IsStatic);
            Assert.Equal("Transform", node.Transform.Kind);
            Assert.Equal(Vec3.Zero, node.Transform.Position);
            Assert.Equal(Quat.Identity, node.Transform.Rotation);
            Assert.Equal(Vec3.One, node.Transform.Scale);
            Assert.Empty(node.Components);
            Assert.Empty(node.Children);
        }
    }
}
