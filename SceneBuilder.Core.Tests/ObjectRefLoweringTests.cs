using System.Collections.Generic;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t1: authored handle identifier / NodeHandle.None -> intermediate ValueNode.ObjectRef
    // carrying the handle NAME, then ObjectRefLowering.Lower resolves NAME -> target LogicalId
    // (mirrors AssetRefLowering's two-step DisplayPath -> Guid resolution). A resolve miss
    // yields ValueNode.Unsupported(name); recursion covers List/Nested (spec §142-159).
    public class ObjectRefLoweringTests
    {
        // Field-key convention: `x => x.target` lowers the KEY to "member:target"
        // (ComponentParseTests.cs:38) — VALUE lowering (this task) is orthogonal.
        private const string FooSceneSource = @"
using UnityEngine;
using SceneBuilder.Authoring;

public class DoorOpener : MonoBehaviour { public GameObject target; }

public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var door = scene.Add(""Door"");
        scene.Add(""Opener"")
             .Component<DoorOpener>(c => c.Set(x => x.target, door));

        scene.Add(""Idle"")
             .Component<DoorOpener>(c => c.Set(x => x.target, NodeHandle.None));
    }
}
";

        [Fact]
        public void Parse_HandleIdentifier_LowersToObjectRefOfTargetLogicalId()
        {
            var result = BuilderParser.Parse(FooSceneSource);

            var door = Assert.Single(result.Model.Roots, r => r.Name == "Door");
            var opener = Assert.Single(result.Model.Roots, r => r.Name == "Opener");
            var component = Assert.Single(opener.Components);

            var field = Assert.IsType<ValueNode.ObjectRef>(component.Fields["member:target"]);
            Assert.Equal(door.LogicalId, field.TargetLogicalId);
        }

        [Fact]
        public void Parse_NodeHandleNone_LowersToObjectRefNull()
        {
            var result = BuilderParser.Parse(FooSceneSource);

            var idle = Assert.Single(result.Model.Roots, r => r.Name == "Idle");
            var component = Assert.Single(idle.Components);

            var field = Assert.IsType<ValueNode.ObjectRef>(component.Fields["member:target"]);
            Assert.Null(field.TargetLogicalId);
        }

        [Fact]
        public void Lowering_ObjectRefWithResolvableName_ResolvesToLogicalId()
        {
            var model = ModelWithField(new ValueNode.ObjectRef("door"));

            var lowered = ObjectRefLowering.Lower(model, name => name == "door" ? "lid-123" : null);

            var field = Assert.IsType<ValueNode.ObjectRef>(FieldOf(lowered));
            Assert.Equal("lid-123", field.TargetLogicalId);
        }

        [Fact]
        public void Lowering_ObjectRefNull_PassesThroughUnchanged()
        {
            var model = ModelWithField(new ValueNode.ObjectRef(null));

            var lowered = ObjectRefLowering.Lower(model, _ => "should-not-be-used");

            var field = Assert.IsType<ValueNode.ObjectRef>(FieldOf(lowered));
            Assert.Null(field.TargetLogicalId);
        }

        [Fact]
        public void Lowering_UnresolvableName_YieldsUnsupported()
        {
            var model = ModelWithField(new ValueNode.ObjectRef("ghost"));

            var lowered = ObjectRefLowering.Lower(model, _ => null);

            Assert.Equal(new ValueNode.Unsupported("ghost"), FieldOf(lowered));
        }

        [Fact]
        public void Lowering_ObjectRefInsideList_ResolvesEachElement()
        {
            var model = ModelWithField(new ValueNode.List(new ValueNode[]
            {
                new ValueNode.ObjectRef("a"),
                new ValueNode.ObjectRef("b"),
            }));

            var lowered = ObjectRefLowering.Lower(model, name => name switch
            {
                "a" => "lid-a",
                "b" => "lid-b",
                _ => null,
            });

            var list = Assert.IsType<ValueNode.List>(FieldOf(lowered));
            Assert.Equal("lid-a", Assert.IsType<ValueNode.ObjectRef>(list.Items[0]).TargetLogicalId);
            Assert.Equal("lid-b", Assert.IsType<ValueNode.ObjectRef>(list.Items[1]).TargetLogicalId);
        }

        [Fact]
        public void Lowering_ObjectRefInsideNested_Resolves()
        {
            var nestedFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("inner", new ValueNode.ObjectRef("h")),
            });
            var model = ModelWithField(new ValueNode.Nested("Game.Link", nestedFields));

            var lowered = ObjectRefLowering.Lower(model, name => name == "h" ? "lid-h" : null);

            var nested = Assert.IsType<ValueNode.Nested>(FieldOf(lowered));
            var inner = Assert.IsType<ValueNode.ObjectRef>(nested.Fields["inner"]);
            Assert.Equal("lid-h", inner.TargetLogicalId);
        }

        [Fact]
        public void Lowering_NonObjectRefField_Unchanged()
        {
            var model = ModelWithField(ValueNode.Primitive.Int(42));

            var lowered = ObjectRefLowering.Lower(model, _ => "lid");

            Assert.Equal(ValueNode.Primitive.Int(42), FieldOf(lowered));
        }

        private static SceneModel ModelWithField(ValueNode value)
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("field", value),
            });
            var component = new ComponentData
            {
                LogicalId = "c1",
                Type = new TypeRef("Game.DoorOpener"),
                Fields = fields,
            };
            var root = new GameObjectNode
            {
                LogicalId = "g1",
                Name = "Player",
                Components = new[] { component },
            };
            return new SceneModel { Roots = new[] { root } };
        }

        private static ValueNode FieldOf(SceneModel model) =>
            Assert.Single(Assert.Single(model.Roots).Components).Fields["field"];
    }
}
