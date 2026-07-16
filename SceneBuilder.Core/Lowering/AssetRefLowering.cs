using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Fills Guid/FileId(/TypeHint) on unresolved ValueNode.AssetRef nodes by walking the
    // SceneModel tree and rebuilding it immutably. Non-builtin refs route through the path
    // resolver and have TypeHint stamped; built-in refs (IsBuiltin == true) route through the
    // builtinResolver and set ONLY Guid/FileId, preserving the authored TypeHint/DisplayPath
    // verbatim. See spec §Core deliverables (specs/17-builtin-resources.md).
    public static class AssetRefLowering
    {
        public static SceneModel Lower(
            SceneModel model,
            Func<string, (string guid, long fileId, string typeHint)?> resolver,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? builtinResolver = null)
        {
            var resolvers = new Resolvers(resolver, builtinResolver);
            return model with { Roots = model.Roots.Select(root => LowerGameObject(root, resolvers)).ToArray() };
        }

        private readonly record struct Resolvers(
            Func<string, (string guid, long fileId, string typeHint)?> Path,
            Func<string, string?, (string guid, long fileId, string typeHint)?>? Builtin);

        private static GameObjectNode LowerGameObject(GameObjectNode go, Resolvers resolvers)
        {
            return go with
            {
                Components = go.Components.Select(c => LowerComponent(c, resolvers)).ToArray(),
                Children = go.Children.Select(c => LowerGameObject(c, resolvers)).ToArray(),
            };
        }

        private static ComponentData LowerComponent(ComponentData component, Resolvers resolvers)
        {
            return component with { Fields = LowerFieldMap(component.Fields, resolvers) };
        }

        private static FieldMap LowerFieldMap(FieldMap fields, Resolvers resolvers)
        {
            return new FieldMap(fields.Select(kv =>
                new KeyValuePair<string, ValueNode>(kv.Key, LowerNode(kv.Value, resolvers))));
        }

        private static ValueNode LowerNode(ValueNode node, Resolvers resolvers)
        {
            switch (node)
            {
                case ValueNode.AssetRef assetRef:
                    return LowerAssetRef(assetRef, resolvers);
                case ValueNode.List list:
                    return new ValueNode.List(list.Items.Select(item => LowerNode(item, resolvers)).ToList());
                case ValueNode.Nested nested:
                    return new ValueNode.Nested(LowerFieldMap(nested.Fields, resolvers));
                default:
                    return node;
            }
        }

        private static ValueNode.AssetRef LowerAssetRef(ValueNode.AssetRef assetRef, Resolvers resolvers)
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

            if (reference.IsBuiltin)
            {
                // The authored TypeHint is the qualifier; "" means "the bare name is
                // unambiguous" -> null.
                var typeHint = string.IsNullOrEmpty(reference.TypeHint) ? null : reference.TypeHint;
                var hit = resolvers.Builtin?.Invoke(reference.DisplayPath, typeHint);
                if (hit is null)
                {
                    return assetRef; // no builtinResolver, or a miss -> unresolved, no throw
                }

                // Guid + FileId ONLY. The resolved typeHint is DELIBERATELY DISCARDED: stamping
                // it would flip SourceExpr.cs's emit arm and churn Builtin("Cube") ->
                // Builtin("Cube","Mesh") on every sync. DisplayPath (the built-in NAME) is
                // likewise preserved verbatim.
                return new ValueNode.AssetRef(reference with { Guid = hit.Value.guid, FileId = hit.Value.fileId });
            }

            var resolved = resolvers.Path(reference.DisplayPath);
            if (resolved is null)
            {
                return assetRef;
            }

            var (guid, fileId, typeHint2) = resolved.Value;
            return new ValueNode.AssetRef(reference with { Guid = guid, FileId = fileId, TypeHint = typeHint2 });
        }
    }
}
