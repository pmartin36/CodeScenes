using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Plan;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class PlanOpsJsonTests
    {
        private static Plan.Plan PlanWith(params PlanOp[] ops) => new Plan.Plan
        {
            SchemaVersion = 1,
            ScenePath = "Assets/Scenes/Demo.unity",
            Ops = ops,
        };

        [Fact]
        public void AllM1PlanOps_RoundTripThroughPlanJson_Equal()
        {
            PlanOp[] ops =
            {
                new DestroyObject { LogicalId = "A" },
                new SetParent { LogicalId = "B", ParentLogicalId = "A" },
                new ReorderChild { LogicalId = "B", SiblingIndex = 2 },
                new SetName { LogicalId = "B", Name = "Renamed" },
                new SetTag { LogicalId = "B", Tag = "Player" },
                new SetLayer { LogicalId = "B", Layer = 8 },
                new SetActive { LogicalId = "B", Active = false },
                new SetStatic { LogicalId = "B", IsStatic = true },
                new SetField { LogicalId = "B", Path = "m_LocalPosition", Value = new ValueNode.Vec3(new Vec3(1, 2, 3)) },
                new SetField { LogicalId = "B", Path = "m_LocalRotation", Value = new ValueNode.Quat(new Quat(0, 0, 0, 1)) },
                new SetField { LogicalId = "B", Path = "m_LocalScale", Value = new ValueNode.Vec3(new Vec3(1, 1, 1)) },
            };
            var plan = PlanWith(ops);

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            Assert.Equal(ops.Length, back.Ops.Length);
            for (var i = 0; i < ops.Length; i++)
            {
                Assert.Equal(ops[i], back.Ops[i]);
            }
        }

        [Fact]
        public void M3ComponentPlanOps_RoundTripThroughPlanJson_Equal()
        {
            PlanOp[] ops =
            {
                new AddComponent { LogicalId = "comp-1", Type = new TypeRef("UnityEngine.Rigidbody") },
                new RemoveComponent { LogicalId = "comp-2" },
                new ReorderComponent { LogicalId = "comp-3", GameObjectLogicalId = "go-1", ComponentLogicalId = "comp-3", ToIndex = 2 },
            };
            var plan = PlanWith(ops);

            var json = PlanJson.Serialize(plan);
            var back = PlanJson.Deserialize(json);

            Assert.Equal(ops.Length, back.Ops.Length);
            for (var i = 0; i < ops.Length; i++)
            {
                Assert.Equal(ops[i], back.Ops[i]);
            }
        }

        [Theory]
        [InlineData(typeof(DestroyObject), "DestroyObject")]
        [InlineData(typeof(SetParent), "SetParent")]
        [InlineData(typeof(ReorderChild), "ReorderChild")]
        [InlineData(typeof(SetName), "SetName")]
        [InlineData(typeof(SetTag), "SetTag")]
        [InlineData(typeof(SetLayer), "SetLayer")]
        [InlineData(typeof(SetActive), "SetActive")]
        [InlineData(typeof(SetStatic), "SetStatic")]
        [InlineData(typeof(SetField), "SetField")]
        [InlineData(typeof(AddComponent), "AddComponent")]
        [InlineData(typeof(RemoveComponent), "RemoveComponent")]
        [InlineData(typeof(ReorderComponent), "ReorderComponent")]
        public void EachPlanOp_Serializes_WithExpectedOpDiscriminator(System.Type opType, string expectedDiscriminator)
        {
            PlanOp op = opType.Name switch
            {
                nameof(DestroyObject) => new DestroyObject { LogicalId = "X" },
                nameof(SetParent) => new SetParent { LogicalId = "X" },
                nameof(ReorderChild) => new ReorderChild { LogicalId = "X" },
                nameof(SetName) => new SetName { LogicalId = "X" },
                nameof(SetTag) => new SetTag { LogicalId = "X" },
                nameof(SetLayer) => new SetLayer { LogicalId = "X" },
                nameof(SetActive) => new SetActive { LogicalId = "X" },
                nameof(SetStatic) => new SetStatic { LogicalId = "X" },
                nameof(SetField) => new SetField { LogicalId = "X", Path = "m_LocalPosition", Value = new ValueNode.Vec3(Vec3.Zero) },
                nameof(AddComponent) => new AddComponent { LogicalId = "X", Type = new TypeRef("UnityEngine.Rigidbody") },
                nameof(RemoveComponent) => new RemoveComponent { LogicalId = "X" },
                nameof(ReorderComponent) => new ReorderComponent { LogicalId = "X", GameObjectLogicalId = "go", ComponentLogicalId = "X", ToIndex = 1 },
                _ => throw new System.InvalidOperationException($"unhandled op type {opType.Name}"),
            };
            var plan = PlanWith(op);

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal(expectedDiscriminator, opElement.GetProperty("op").GetString());
        }

        [Fact]
        public void SetField_TransformPaths_SerializeTypedValueWithKindDiscriminator()
        {
            var vecPlan = PlanWith(new SetField
            {
                LogicalId = "B",
                Path = "m_LocalPosition",
                Value = new ValueNode.Vec3(new Vec3(1, 2, 3)),
            });
            var quatPlan = PlanWith(new SetField
            {
                LogicalId = "B",
                Path = "m_LocalRotation",
                Value = new ValueNode.Quat(new Quat(0, 0, 0, 1)),
            });

            using var vecDoc = JsonDocument.Parse(PlanJson.Serialize(vecPlan));
            var vecValue = vecDoc.RootElement.GetProperty("ops")[0].GetProperty("value");
            Assert.Equal("Vec3", vecValue.GetProperty("kind").GetString());
            var vecInner = vecValue.GetProperty("value");
            Assert.Equal(1, vecInner.GetProperty("x").GetSingle());
            Assert.Equal(2, vecInner.GetProperty("y").GetSingle());
            Assert.Equal(3, vecInner.GetProperty("z").GetSingle());

            using var quatDoc = JsonDocument.Parse(PlanJson.Serialize(quatPlan));
            var quatValue = quatDoc.RootElement.GetProperty("ops")[0].GetProperty("value");
            Assert.Equal("Quat", quatValue.GetProperty("kind").GetString());
            var quatInner = quatValue.GetProperty("value");
            Assert.Equal(1, quatInner.GetProperty("w").GetSingle());
        }

        [Fact]
        public void SetParent_NullParent_SerializesJsonNull()
        {
            var plan = PlanWith(new SetParent { LogicalId = "B", ParentLogicalId = null });

            var json = PlanJson.Serialize(plan);

            using var doc = JsonDocument.Parse(json);
            var opElement = doc.RootElement.GetProperty("ops")[0];
            Assert.Equal(JsonValueKind.Null, opElement.GetProperty("parentLogicalId").ValueKind);
        }

        [Fact]
        public void PlanOps_Serialize_AreByteIdenticalAcrossCalls()
        {
            var plan = PlanWith(
                new DestroyObject { LogicalId = "A" },
                new SetField { LogicalId = "B", Path = "m_LocalScale", Value = new ValueNode.Vec3(Vec3.One) });

            var json1 = PlanJson.Serialize(plan);
            var json2 = PlanJson.Serialize(plan);

            Assert.Equal(json1, json2);
        }
    }
}
