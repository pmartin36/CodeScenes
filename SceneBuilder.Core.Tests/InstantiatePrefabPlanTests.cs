using System.Text.Json;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class InstantiatePrefabPlanTests
    {
        private static Plan.Plan PlanWith(params PlanOp[] ops) => new Plan.Plan
        {
            SchemaVersion = 1,
            ScenePath = "Assets/Scenes/Demo.unity",
            Ops = ops,
        };

        [Fact]
        public void InstantiatePrefab_Serializes_WithExpectedOpDiscriminator()
        {
            var plan = PlanWith(new InstantiatePrefab { LogicalId = "X", Guid = "abc123", ParentLogicalId = "root", SiblingIndex = 2 });

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal("InstantiatePrefab", opElement.GetProperty("op").GetString());
        }

        [Fact]
        public void InstantiatePrefab_RoundTrips_GuidParentLogicalIdSiblingIndexAndLogicalId()
        {
            var op = new InstantiatePrefab { LogicalId = "instance-1", Guid = "abc123", ParentLogicalId = "root", SiblingIndex = 2 };
            var plan = PlanWith(op);

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            Assert.Equal(op, Assert.Single(back.Ops));
        }

        [Fact]
        public void InstantiatePrefab_NullParentLogicalId_SerializesJsonNull_AndRoundTrips()
        {
            var op = new InstantiatePrefab { LogicalId = "instance-1", Guid = "abc123", ParentLogicalId = null, SiblingIndex = 0 };
            var plan = PlanWith(op);

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal(JsonValueKind.Null, opElement.GetProperty("parentLogicalId").ValueKind);

            var back = PlanJson.Deserialize(json);
            Assert.Equal(op, Assert.Single(back.Ops));
        }
    }
}
