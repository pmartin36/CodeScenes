using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Lowering
{
    // Rewrites intermediate ValueNode.ObjectRef(name) nodes -> ObjectRef(<resolved LogicalId>)
    // by walking the SceneModel tree and rebuilding it immutably (mirrors AssetRefLowering's
    // tree-walk shape). ObjectRef(null) (authored NodeHandle.None) passes through unchanged;
    // a resolveHandle MISS yields ValueNode.Unsupported(name) — never a bogus LogicalId. The
    // reserved SelfReferenceEmission.Marker (authored `NodeHandle.Self`) is resolved to the
    // OWNING node's LogicalId — threaded down from the GameObjectNode being walked at that
    // point — BEFORE resolveHandle is consulted, since a marker is never a legal handle name.
    public static class ObjectRefLowering
    {
        public static SceneModel Lower(SceneModel model, Func<string, string?> resolveHandle)
        {
            return model with { Roots = model.Roots.Select(root => LowerGameObject(root, resolveHandle)).ToArray() };
        }

        private static GameObjectNode LowerGameObject(GameObjectNode go, Func<string, string?> resolveHandle)
        {
            var lowered = go with
            {
                Components = go.Components.Select(c => LowerComponent(c, go.LogicalId, resolveHandle)).ToArray(),
                Children = go.Children.Select(c => LowerGameObject(c, resolveHandle)).ToArray(),
            };

            if (lowered is PrefabInstanceNode instance)
            {
                return instance with
                {
                    Overrides = instance.Overrides.Select(o => LowerPropertyOverride(o, go.LogicalId, resolveHandle)).ToArray(),
                    AddedComponents = instance.AddedComponents
                        .Select(a => a with { Component = LowerComponent(a.Component, go.LogicalId, resolveHandle) })
                        .ToArray(),
                };
            }

            return lowered;
        }

        private static PropertyOverride LowerPropertyOverride(PropertyOverride @override, string ownerNodeLogicalId, Func<string, string?> resolveHandle)
        {
            return @override with
            {
                Value = LowerNode(@override.Value, ownerNodeLogicalId, resolveHandle),
                ObjectReference = @override.ObjectReference is null
                    ? null
                    : LowerNode(@override.ObjectReference, ownerNodeLogicalId, resolveHandle),
            };
        }

        private static ComponentData LowerComponent(ComponentData component, string ownerNodeLogicalId, Func<string, string?> resolveHandle)
        {
            return component with { Fields = LowerFieldMap(component.Fields, ownerNodeLogicalId, resolveHandle) };
        }

        private static FieldMap LowerFieldMap(FieldMap fields, string ownerNodeLogicalId, Func<string, string?> resolveHandle)
        {
            return new FieldMap(fields.Select(kv =>
                new KeyValuePair<string, ValueNode>(kv.Key, LowerNode(kv.Value, ownerNodeLogicalId, resolveHandle))));
        }

        private static ValueNode LowerNode(ValueNode node, string ownerNodeLogicalId, Func<string, string?> resolveHandle)
        {
            switch (node)
            {
                case ValueNode.ObjectRef objectRef:
                    return LowerObjectRef(objectRef, ownerNodeLogicalId, resolveHandle);
                case ValueNode.List list:
                    return new ValueNode.List(list.Items.Select(item => LowerNode(item, ownerNodeLogicalId, resolveHandle)).ToList());
                case ValueNode.Nested nested:
                    return new ValueNode.Nested(nested.TypeName, LowerFieldMap(nested.Fields, ownerNodeLogicalId, resolveHandle));
                default:
                    return node;
            }
        }

        private static ValueNode LowerObjectRef(ValueNode.ObjectRef objectRef, string ownerNodeLogicalId, Func<string, string?> resolveHandle)
        {
            var name = objectRef.TargetLogicalId;
            if (name is null)
            {
                return objectRef;
            }

            if (SelfReferenceEmission.IsMarker(name))
            {
                return new ValueNode.ObjectRef(ownerNodeLogicalId);
            }

            var resolved = resolveHandle(name);
            return resolved is null
                ? new ValueNode.Unsupported(name)
                : new ValueNode.ObjectRef(resolved);
        }
    }
}
