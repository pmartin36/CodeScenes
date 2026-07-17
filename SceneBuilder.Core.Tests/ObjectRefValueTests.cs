using System.Collections.Generic;
using System.Text.Json;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ObjectRefValueTests
    {
        private static ComponentData ComponentWithFields(params (string Key, ValueNode Value)[] fields)
        {
            var entries = new List<KeyValuePair<string, ValueNode>>();
            foreach (var (key, value) in fields)
            {
                entries.Add(new KeyValuePair<string, ValueNode>(key, value));
            }

            return new ComponentData
            {
                LogicalId = "comp-1",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(entries),
            };
        }

        private static SceneModel ModelWith(ComponentData component) => new SceneModel
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new GameObjectNode
                {
                    LogicalId = "go-1",
                    Name = "Root",
                    Components = new[] { component },
                },
            },
        };

        private static ValueNode ExtractField(SceneModel model) =>
            model.Roots[0].Components[0].Fields["target"];

        [Fact]
        public void ObjectRef_WithId_RoundTripsThroughSceneModelSerializer_Equal()
        {
            var original = new ValueNode.ObjectRef("go-42");
            var model = ModelWith(ComponentWithFields(("target", original)));

            var json = SceneModelSerializer.Serialize(model);
            var roundTripped = SceneModelSerializer.Deserialize(json);
            var field = ExtractField(roundTripped);

            Assert.Equal(original, field);
        }

        [Fact]
        public void ObjectRef_NullTarget_RoundTripsThroughSceneModelSerializer_Equal()
        {
            var original = new ValueNode.ObjectRef(null);
            var model = ModelWith(ComponentWithFields(("target", original)));

            var json = SceneModelSerializer.Serialize(model);
            var roundTripped = SceneModelSerializer.Deserialize(json);
            var field = ExtractField(roundTripped);

            Assert.Equal(original, field);
        }

        [Fact]
        public void ObjectRef_SerializedNode_HasObjectRefDiscriminator()
        {
            var model = ModelWith(ComponentWithFields(("target", new ValueNode.ObjectRef("go-42"))));

            var json = SceneModelSerializer.Serialize(model);

            using var doc = JsonDocument.Parse(json);
            var valueElement = doc.RootElement
                .GetProperty("roots")[0]
                .GetProperty("components")[0]
                .GetProperty("fields")
                .GetProperty("target");

            Assert.Equal("ObjectRef", valueElement.GetProperty("kind").GetString());
        }

        [Fact]
        public void ObjectRef_RecordEquality_NullAndIdCases()
        {
            Assert.Equal(new ValueNode.ObjectRef(null), new ValueNode.ObjectRef(null));
            Assert.NotEqual(new ValueNode.ObjectRef("a"), new ValueNode.ObjectRef("b"));
            Assert.NotEqual((ValueNode)new ValueNode.ObjectRef("a"), new ValueNode.ObjectRef(null));
        }
    }
}
