using System.Text.Json;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class PlanJsonTests
    {
        private static SceneBuilder.Core.Plan.Plan SamplePlan() => new SceneBuilder.Core.Plan.Plan
        {
            SchemaVersion = 1,
            ScenePath = "Assets/Scenes/Demo.unity",
            Ops = new PlanOp[]
            {
                new CreateObject { LogicalId = "Root", Name = "Root" },
            },
        };

        [Fact]
        public void Plan_WithSingleCreateObject_RoundTripsThroughJson()
        {
            var plan = SamplePlan();

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            Assert.Equal(plan.SchemaVersion, back.SchemaVersion);
            Assert.Equal(plan.ScenePath, back.ScenePath);
            Assert.Single(back.Ops);
            Assert.Equal((CreateObject)plan.Ops[0], (CreateObject)back.Ops[0]);
        }

        [Fact]
        public void Plan_Serialize_IsByteIdenticalAcrossCalls()
        {
            var plan = SamplePlan();

            var json1 = PlanJson.Serialize(plan);
            var json2 = PlanJson.Serialize(plan);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields()
        {
            var plan = SamplePlan();

            var json = PlanJson.Serialize(plan);

            Assert.Contains("\"op\"", json);
            Assert.Contains("\"logicalId\"", json);
            Assert.Contains("\"name\"", json);
            Assert.DoesNotContain("\"position\"", json);
            Assert.DoesNotContain("\"rotation\"", json);
            Assert.DoesNotContain("\"components\"", json);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            var propertyCount = 0;
            foreach (var _ in opElement.EnumerateObject())
            {
                propertyCount++;
            }

            Assert.Equal(3, propertyCount);
        }

        [Fact]
        public void PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation()
        {
            const string json = "{\n  \"schemaVersion\": 1,\n  \"scenePath\": \"Assets/Scenes/Demo.unity\",\n  \"ops\": [\n    { \"op\": \"Frobnicate\", \"logicalId\": \"Root\", \"name\": \"Root\" }\n  ]\n}";

            var ex = Assert.Throws<JsonException>(() => PlanJson.Deserialize(json));

            Assert.Contains("Frobnicate", ex.Message);
            Assert.True(
                ex.Message.Contains("LineNumber") || ex.Message.Contains("BytePositionInLine"),
                $"expected a location marker in message: {ex.Message}");
        }
    }
}
