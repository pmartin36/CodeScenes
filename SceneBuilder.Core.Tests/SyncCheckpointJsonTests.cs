using System.Globalization;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using SceneBuilder.Core.Sync;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class SyncCheckpointJsonTests
    {
        private static SyncCheckpoint BuildCheckpoint() => new SyncCheckpoint
        {
            SchemaVersion = SyncCheckpoint.CurrentSchemaVersion,
            Scene = "FooScene",
            LastSnapshotHash = "aa11",
            LastSourceHash = "bb22",
            LastSidecarHash = "cc33",
        };

        private static SceneModel BuildModel(float x) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "go1",
                    Name = "Root",
                    Transform = new TransformData { Position = new Vec3(x, 0, 0) },
                }
            }
        };

        private static SceneSnapshot BuildSnapshot(float x) => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid1",
                    Name = "Root",
                    Transform = new TransformData { Position = new Vec3(x, 0, 0) },
                }
            }
        };

        private static IdentityMap BuildMap(string name) => new IdentityMap
        {
            SchemaVersion = 1,
            Scene = "FooScene",
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "go1", GlobalObjectId = "goid1", Name = name }
            }
        };

        [Fact]
        public void SyncCheckpointJson_RoundTrip_IsByteIdentical()
        {
            var checkpoint = BuildCheckpoint();

            var json1 = SyncCheckpointJson.Serialize(checkpoint);
            var roundTripped = SyncCheckpointJson.Deserialize(json1);
            var json2 = SyncCheckpointJson.Serialize(roundTripped);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void CanonicalHash_Of_SceneModel_EqualValues_ProduceEqualHash()
        {
            var a = BuildModel(1f);
            var b = BuildModel(1f);

            Assert.Equal(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_SceneModel_UnequalValues_ProduceDifferentHash()
        {
            var a = BuildModel(1f);
            var b = BuildModel(2f);

            Assert.NotEqual(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_SceneSnapshot_EqualValues_ProduceEqualHash()
        {
            var a = BuildSnapshot(1f);
            var b = BuildSnapshot(1f);

            Assert.Equal(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_SceneSnapshot_UnequalValues_ProduceDifferentHash()
        {
            var a = BuildSnapshot(1f);
            var b = BuildSnapshot(2f);

            Assert.NotEqual(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_IdentityMap_EqualValues_ProduceEqualHash()
        {
            var a = BuildMap("Root");
            var b = BuildMap("Root");

            Assert.Equal(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_IdentityMap_UnequalValues_ProduceDifferentHash()
        {
            var a = BuildMap("Root");
            var b = BuildMap("Other");

            Assert.NotEqual(CanonicalHash.Of(a), CanonicalHash.Of(b));
        }

        [Fact]
        public void CanonicalHash_Of_SceneModel_HoldsUnderNonInvariantCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var equalA = BuildModel(1.5f);
                var equalB = BuildModel(1.5f);
                var different = BuildModel(2.5f);

                Assert.Equal(CanonicalHash.Of(equalA), CanonicalHash.Of(equalB));
                Assert.NotEqual(CanonicalHash.Of(equalA), CanonicalHash.Of(different));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void CanonicalHash_Of_IsLowercase64CharHex()
        {
            var hash = CanonicalHash.Of(BuildModel(1f));

            Assert.Matches("^[0-9a-f]{64}$", hash);
        }
    }
}
