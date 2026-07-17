using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Rewrites intermediate ValueNode.ObjectRef(name) nodes -> ObjectRef(<resolved LogicalId>)
    // by walking the SceneModel tree and rebuilding it immutably (mirrors AssetRefLowering's
    // tree-walk shape). ObjectRef(null) (authored NodeHandle.None) passes through unchanged;
    // a resolveHandle MISS yields ValueNode.Unsupported(name) — never a bogus LogicalId.
    public static class ObjectRefLowering
    {
        public static SceneModel Lower(SceneModel model, Func<string, string?> resolveHandle)
        {
            return model with { Roots = model.Roots.Select(root => LowerGameObject(root, resolveHandle)).ToArray() };
        }

        private static GameObjectNode LowerGameObject(GameObjectNode go, Func<string, string?> resolveHandle)
        {
            return go with
            {
                Components = go.Components.Select(c => LowerComponent(c, resolveHandle)).ToArray(),
                Children = go.Children.Select(c => LowerGameObject(c, resolveHandle)).ToArray(),
            };
        }

        private static ComponentData LowerComponent(ComponentData component, Func<string, string?> resolveHandle)
        {
            return component with { Fields = LowerFieldMap(component.Fields, resolveHandle) };
        }

        private static FieldMap LowerFieldMap(FieldMap fields, Func<string, string?> resolveHandle)
        {
            return new FieldMap(fields.Select(kv =>
                new KeyValuePair<string, ValueNode>(kv.Key, LowerNode(kv.Value, resolveHandle))));
        }

        private static ValueNode LowerNode(ValueNode node, Func<string, string?> resolveHandle)
        {
            switch (node)
            {
                case ValueNode.ObjectRef objectRef:
                    return LowerObjectRef(objectRef, resolveHandle);
                case ValueNode.List list:
                    return new ValueNode.List(list.Items.Select(item => LowerNode(item, resolveHandle)).ToList());
                case ValueNode.Nested nested:
                    return new ValueNode.Nested(nested.TypeName, LowerFieldMap(nested.Fields, resolveHandle));
                default:
                    return node;
            }
        }

        private static ValueNode LowerObjectRef(ValueNode.ObjectRef objectRef, Func<string, string?> resolveHandle)
        {
            var name = objectRef.TargetLogicalId;
            if (name is null)
            {
                return objectRef;
            }

            var resolved = resolveHandle(name);
            return resolved is null
                ? new ValueNode.Unsupported(name)
                : new ValueNode.ObjectRef(resolved);
        }
    }
}
