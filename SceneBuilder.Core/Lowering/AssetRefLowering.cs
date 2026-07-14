using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Fills Guid/FileId/TypeHint on unresolved ValueNode.AssetRef nodes (path-only,
    // empty Guid) by walking the SceneModel tree and rebuilding it immutably. See
    // research.md Blueprint/DATA_FLOW for the recursion shape.
    public static class AssetRefLowering
    {
        public static SceneModel Lower(
            SceneModel model,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            return model with { Roots = model.Roots.Select(root => LowerGameObject(root, resolver)).ToArray() };
        }

        private static GameObjectNode LowerGameObject(
            GameObjectNode go,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            return go with
            {
                Components = go.Components.Select(c => LowerComponent(c, resolver)).ToArray(),
                Children = go.Children.Select(c => LowerGameObject(c, resolver)).ToArray(),
            };
        }

        private static ComponentData LowerComponent(
            ComponentData component,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            return component with { Fields = LowerFieldMap(component.Fields, resolver) };
        }

        private static FieldMap LowerFieldMap(
            FieldMap fields,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            return new FieldMap(fields.Select(kv =>
                new KeyValuePair<string, ValueNode>(kv.Key, LowerNode(kv.Value, resolver))));
        }

        private static ValueNode LowerNode(
            ValueNode node,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            switch (node)
            {
                case ValueNode.AssetRef assetRef:
                    return LowerAssetRef(assetRef, resolver);
                case ValueNode.List list:
                    return new ValueNode.List(list.Items.Select(item => LowerNode(item, resolver)).ToList());
                case ValueNode.Nested nested:
                    return new ValueNode.Nested(LowerFieldMap(nested.Fields, resolver));
                default:
                    return node;
            }
        }

        private static ValueNode.AssetRef LowerAssetRef(
            ValueNode.AssetRef assetRef,
            Func<string, (string guid, long fileId, string typeHint)?> resolver)
        {
            var reference = assetRef.Ref;
            if (reference is null)
            {
                return assetRef;
            }

            if (!string.IsNullOrEmpty(reference.Guid))
            {
                return assetRef;
            }

            if (string.IsNullOrEmpty(reference.DisplayPath))
            {
                return assetRef;
            }

            var resolved = resolver(reference.DisplayPath);
            if (resolved is null)
            {
                return assetRef;
            }

            var (guid, fileId, typeHint) = resolved.Value;
            return new ValueNode.AssetRef(reference with { Guid = guid, FileId = fileId, TypeHint = typeHint });
        }
    }
}
