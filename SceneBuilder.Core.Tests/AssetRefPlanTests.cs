using System.Text.Json;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class AssetRefPlanTests
    {
        private static Plan.Plan PlanWith(params PlanOp[] ops) => new Plan.Plan
        {
            SchemaVersion = 1,
            ScenePath = "Assets/Scenes/Demo.unity",
            Ops = ops,
        };

        [Fact]
        public void SetAssetRef_Serializes_WithExpectedOpDiscriminator()
        {
            var plan = PlanWith(new SetAssetRef { LogicalId = "X", Path = "sharedMaterial", Guid = "abc123", FileId = 0 });

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal("SetAssetRef", opElement.GetProperty("op").GetString());
        }

        [Fact]
        public void SetAssetRef_RoundTrips_PopulatedGuidAndNonZeroFileId()
        {
            var op = new SetAssetRef { LogicalId = "X", Path = "sharedMesh", Guid = "abc123", FileId = 4300000 };
            var plan = PlanWith(op);

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            Assert.Equal(op, Assert.Single(back.Ops));
        }

        [Fact]
        public void SetAssetRef_NullGuid_SerializesJsonNull_AndRoundTrips()
        {
            var op = new SetAssetRef { LogicalId = "X", Path = "sharedMaterial", Guid = null, FileId = 0 };
            var plan = PlanWith(op);

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal(JsonValueKind.Null, opElement.GetProperty("guid").ValueKind);

            var back = PlanJson.Deserialize(json);
            Assert.Equal(op, Assert.Single(back.Ops));
        }
    }
}
