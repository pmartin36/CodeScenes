using System.Collections.Generic;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // b2-t3: AssetRefLowering/ObjectRefLowering tree-walks must descend into
    // PrefabInstanceNode.Overrides (Value/ObjectReference) and AddedComponents
    // (Component.Fields) so a reference-valued instance override lowers exactly
    // as the same value does on a plain component field. Prior to this task the
    // walk cloned PrefabInstanceNode via the base `GameObjectNode with {...}`
    // path, silently leaving Overrides/AddedComponents unlowered.
    public class OverrideLoweringTests
    {
        [Fact]
        public void AssetRefLowering_OverrideObjectReference_ResolvesGuid()
        {
            var instance = InstanceWithOverrideObjectReference(
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Textures/Cookie.png" }));
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };
            var resolver = StubResolver("Assets/Textures/Cookie.png", "guid-cookie", 0, "Texture2D");

            var lowered = AssetRefLowering.Lower(model, resolver);

            var loweredInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(lowered.Roots));
            var propertyOverride = Assert.Single(loweredInstance.Overrides);
            var reference = Assert.IsType<ValueNode.AssetRef>(propertyOverride.ObjectReference);
            Assert.Equal("guid-cookie", reference.Ref!.Guid);
        }

        [Fact]
        public void ObjectRefLowering_OverrideObjectReference_ResolvesLogicalId()
        {
            var instance = InstanceWithOverrideObjectReference(new ValueNode.ObjectRef("target"));
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };

            var lowered = ObjectRefLowering.Lower(model, name => name == "target" ? "lid-target" : null);

            var loweredInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(lowered.Roots));
            var propertyOverride = Assert.Single(loweredInstance.Overrides);
            var reference = Assert.IsType<ValueNode.ObjectRef>(propertyOverride.ObjectReference);
            Assert.Equal("lid-target", reference.TargetLogicalId);
        }

        [Fact]
        public void AssetRefLowering_AddedComponentField_ResolvesGuid()
        {
            var instance = InstanceWithAddedComponentField(
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Textures/Cookie.png" }));
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };
            var resolver = StubResolver("Assets/Textures/Cookie.png", "guid-cookie", 0, "Texture2D");

            var lowered = AssetRefLowering.Lower(model, resolver);

            var loweredInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(lowered.Roots));
            var addedComponent = Assert.Single(loweredInstance.AddedComponents);
            var field = Assert.IsType<ValueNode.AssetRef>(addedComponent.Component.Fields["cookie"]);
            Assert.Equal("guid-cookie", field.Ref!.Guid);
        }

        [Fact]
        public void ObjectRefLowering_AddedComponentField_ResolvesLogicalId()
        {
            var instance = InstanceWithAddedComponentField(new ValueNode.ObjectRef("target"));
            var model = new SceneModel { Roots = new GameObjectNode[] { instance } };

            var lowered = ObjectRefLowering.Lower(model, name => name == "target" ? "lid-target" : null);

            var loweredInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(lowered.Roots));
            var addedComponent = Assert.Single(loweredInstance.AddedComponents);
            var field = Assert.IsType<ValueNode.ObjectRef>(addedComponent.Component.Fields["target"]);
            Assert.Equal("lid-target", field.TargetLogicalId);
        }

        private static PrefabInstanceNode InstanceWithOverrideObjectReference(ValueNode objectReference)
        {
            return new PrefabInstanceNode
            {
                LogicalId = "instance",
                Name = "Enemy",
                Overrides = new[]
                {
                    new PropertyOverride
                    {
                        Target = new OverrideTarget { ComponentType = "Light" },
                        PropertyPath = "member:cookie",
                        Value = new ValueNode.Unsupported(""),
                        ObjectReference = objectReference,
                    },
                },
            };
        }

        private static PrefabInstanceNode InstanceWithAddedComponentField(ValueNode fieldValue)
        {
            var fieldName = fieldValue is ValueNode.ObjectRef ? "target" : "cookie";
            return new PrefabInstanceNode
            {
                LogicalId = "instance",
                Name = "Enemy",
                AddedComponents = new[]
                {
                    new AddedComponent
                    {
                        Target = new OverrideTarget(),
                        Component = new ComponentData
                        {
                            LogicalId = "instance/Light#0",
                            Type = new TypeRef("Light"),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>(fieldName, fieldValue),
                            }),
                        },
                    },
                },
            };
        }

        private static System.Func<string, string?, (string guid, long fileId, string typeHint)?> StubResolver(
            string path, string guid, long fileId, string typeHint)
        {
            return (p, _) => p == path ? (guid, fileId, typeHint) : ((string, long, string)?)null;
        }
    }
}
