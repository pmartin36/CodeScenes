using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Fills PrefabInstanceNode.SourcePrefab.Guid by walking the SceneModel tree and rebuilding
    // it immutably, mirroring ObjectRefLowering/AssetRefLowering's tree-walk shape. Never drops
    // a node on a resolver miss; instead collects a located AssetRefError. See research.md.
    public sealed record PrefabRefLoweringResult(SceneModel Model, IReadOnlyList<AssetRefError> Errors);

    public static class PrefabRefLowering
    {
        public static PrefabRefLoweringResult Lower(
            SceneModel model,
            Func<string, string?, (string guid, long fileId, string typeHint)?> resolver)
        {
            var errors = new List<AssetRefError>();
            var loweredRoots = model.Roots.Select(root => LowerNode(root, resolver, errors)).ToArray();
            return new PrefabRefLoweringResult(model with { Roots = loweredRoots }, errors);
        }

        private static GameObjectNode LowerNode(
            GameObjectNode node,
            Func<string, string?, (string guid, long fileId, string typeHint)?> resolver,
            List<AssetRefError> errors)
        {
            var loweredChildren = node.Children.Select(c => LowerNode(c, resolver, errors)).ToArray();
            var childrenChanged = !loweredChildren.SequenceEqual(node.Children);

            if (node is PrefabInstanceNode instance)
            {
                var loweredSource = LowerSourcePrefab(instance, resolver, errors);
                if (!childrenChanged && ReferenceEquals(loweredSource, instance.SourcePrefab))
                {
                    return instance;
                }

                return instance with { Children = loweredChildren, SourcePrefab = loweredSource };
            }

            return childrenChanged ? node with { Children = loweredChildren } : node;
        }

        private static AssetRef LowerSourcePrefab(
            PrefabInstanceNode instance,
            Func<string, string?, (string guid, long fileId, string typeHint)?> resolver,
            List<AssetRefError> errors)
        {
            var source = instance.SourcePrefab;

            if (!string.IsNullOrEmpty(source.Guid))
            {
                return source;
            }

            if (string.IsNullOrEmpty(source.DisplayPath))
            {
                return source;
            }

            var hit = resolver(source.DisplayPath, null);
            if (hit is null)
            {
                errors.Add(new AssetRefError(
                    ObjectName: instance.Name,
                    ComponentType: "PrefabInstance",
                    FieldName: "SourcePrefab",
                    Guid: "",
                    LastKnownPath: source.DisplayPath));
                return source;
            }

            return source with { Guid = hit.Value.guid };
        }
    }
}
