using System.Collections.Generic;
using System.Text.Json;
using SceneBuilder.Core.Model;
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

        private static readonly OverrideTarget SampleTarget = new() { ComponentType = "guid-prefab-1", SubKey = new PrefabInstanceKey { TargetObjectId = 42 } };

        private static Plan.Plan PlanWith(PlanOp op) => new Plan.Plan
        {
            SchemaVersion = 1,
            ScenePath = "Assets/Scenes/Demo.unity",
            Ops = new[] { op },
        };

        [Fact]
        public void SetInstanceOverride_ValueOnly_RoundTrips()
        {
            var op = new SetInstanceOverride
            {
                LogicalId = "Instance1",
                Target = SampleTarget,
                PropertyPath = "health",
                Value = ValueNode.Primitive.Int(50),
            };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"SetInstanceOverride\"", json.Replace(" ", ""));
            Assert.DoesNotContain("\"objectReference\"", json);
            Assert.Equal(op, Assert.IsType<SetInstanceOverride>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void SetInstanceOverride_ObjectReference_RoundTrips()
        {
            var op = new SetInstanceOverride
            {
                LogicalId = "Instance1",
                Target = SampleTarget,
                PropertyPath = "sharedMaterial",
                Value = new ValueNode.Unsupported(""),
                ObjectReference = new ValueNode.AssetRef(new AssetRef { Guid = "abc123", FileId = 2 }),
            };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"objectReference\"", json);
            Assert.Equal(op, Assert.IsType<SetInstanceOverride>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void AddInstanceComponent_RoundTrips()
        {
            var op = new AddInstanceComponent
            {
                LogicalId = "Instance1",
                Target = SampleTarget,
                Component = new ComponentData
                {
                    LogicalId = "Light1",
                    Type = new TypeRef("UnityEngine.Light"),
                    Fields = new FieldMap(new[]
                    {
                        new KeyValuePair<string, ValueNode>("intensity", ValueNode.Primitive.Float(1.5f)),
                    }),
                },
            };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"AddInstanceComponent\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<AddInstanceComponent>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RemoveInstanceComponent_RoundTrips()
        {
            var op = new RemoveInstanceComponent { LogicalId = "Instance1", Target = SampleTarget };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RemoveInstanceComponent\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RemoveInstanceComponent>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RevertInstanceOverride_RoundTrips()
        {
            var op = new RevertInstanceOverride { LogicalId = "Instance1", Target = SampleTarget, PropertyPath = "health" };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RevertInstanceOverride\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RevertInstanceOverride>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RevertAddedComponent_RoundTrips()
        {
            var op = new RevertAddedComponent { LogicalId = "Instance1", Target = SampleTarget, ComponentLogicalId = "Light1" };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RevertAddedComponent\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RevertAddedComponent>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RevertRemovedComponent_RoundTrips()
        {
            var op = new RevertRemovedComponent { LogicalId = "Instance1", Target = SampleTarget };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RevertRemovedComponent\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RevertRemovedComponent>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void AddInstanceChild_RoundTrips()
        {
            var op = new AddInstanceChild
            {
                LogicalId = "Instance1",
                Target = SampleTarget,
                Node = new GameObjectNode
                {
                    LogicalId = "MuzzleFlash",
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = "Light1",
                            Type = new TypeRef("UnityEngine.Light"),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("intensity", ValueNode.Primitive.Float(1.5f)),
                            }),
                        },
                    },
                },
            };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"AddInstanceChild\"", json.Replace(" ", ""));
            var roundTripped = Assert.IsType<AddInstanceChild>(Assert.Single(back.Ops));
            Assert.Equal(op.LogicalId, roundTripped.LogicalId);
            Assert.Equal(op.Target, roundTripped.Target);
            Assert.Equal(op.Node.LogicalId, roundTripped.Node.LogicalId);
            Assert.Equal(op.Node.Name, roundTripped.Node.Name);
            Assert.Single(roundTripped.Node.Components);
            Assert.Equal(op.Node.Components[0], roundTripped.Node.Components[0]);
        }

        [Fact]
        public void RemoveInstanceChild_RoundTrips()
        {
            var op = new RemoveInstanceChild { LogicalId = "Instance1", Target = SampleTarget };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RemoveInstanceChild\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RemoveInstanceChild>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RevertAddedChild_RoundTrips()
        {
            var op = new RevertAddedChild { LogicalId = "Instance1", Target = SampleTarget, ChildLogicalId = "MuzzleFlash" };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RevertAddedChild\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RevertAddedChild>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void RevertRemovedChild_RoundTrips()
        {
            var op = new RevertRemovedChild { LogicalId = "Instance1", Target = SampleTarget };

            var json = PlanJson.Serialize(PlanWith(op));
            var back = PlanJson.Deserialize(json);

            Assert.Contains("\"op\":\"RevertRemovedChild\"", json.Replace(" ", ""));
            Assert.Equal(op, Assert.IsType<RevertRemovedChild>(Assert.Single(back.Ops)));
        }

        [Fact]
        public void Plan_MixingInstanceOps_DeserializesEachToRightConcreteType()
        {
            var plan = new Plan.Plan
            {
                SchemaVersion = 1,
                ScenePath = "Assets/Scenes/Demo.unity",
                Ops = new PlanOp[]
                {
                    new SetInstanceOverride { LogicalId = "I1", Target = SampleTarget, PropertyPath = "health", Value = ValueNode.Primitive.Int(50) },
                    new RemoveInstanceComponent { LogicalId = "I1", Target = SampleTarget },
                },
            };

            var back = PlanJson.Deserialize(PlanJson.Serialize(plan));

            Assert.Equal(2, back.Ops.Length);
            Assert.IsType<SetInstanceOverride>(back.Ops[0]);
            Assert.IsType<RemoveInstanceComponent>(back.Ops[1]);
        }
    }
}
