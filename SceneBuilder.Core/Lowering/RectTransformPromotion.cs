using System;
using System.Linq;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Lowering
{
    // Promotes a Kind=="Transform" node hosting a [RequireComponent(typeof(RectTransform))]
    // component to Kind=="RectTransform" (RectTransformFields defaults) before any diff, so the
    // matched omit-at-default arm runs instead of the D6 promotion arm for such hosts. See
    // RectTransformFields for the shared field table this must reuse, never re-spell.
    public static class RectTransformPromotion
    {
        public static SceneModel Promote(SceneModel model, Func<TypeRef, bool> requiresRectTransform)
        {
            var promotedRoots = model.Roots.Select(root => PromoteNode(root, requiresRectTransform)).ToArray();
            return model with { Roots = promotedRoots };
        }

        private static GameObjectNode PromoteNode(GameObjectNode node, Func<TypeRef, bool> requiresRectTransform)
        {
            var promotedChildren = node.Children.Select(c => PromoteNode(c, requiresRectTransform)).ToArray();
            var childrenChanged = !promotedChildren.SequenceEqual(node.Children);

            var promotedTransform = node.Transform;
            if (!node.Transform.IsRectTransform && node.Components.Any(c => requiresRectTransform(c.Type)))
            {
                promotedTransform = PromoteTransform(node.Transform);
            }

            var transformChanged = !ReferenceEquals(promotedTransform, node.Transform);

            if (node is PrefabInstanceNode instance)
            {
                var promotedAdded = instance.AddedGameObjects
                    .Select(a => a with { Node = PromoteNode(a.Node, requiresRectTransform) })
                    .ToArray();
                var addedChanged = !promotedAdded.SequenceEqual(instance.AddedGameObjects);

                if (!childrenChanged && !transformChanged && !addedChanged)
                {
                    return instance;
                }

                return instance with
                {
                    Children = promotedChildren,
                    Transform = promotedTransform,
                    AddedGameObjects = promotedAdded,
                };
            }

            if (!childrenChanged && !transformChanged)
            {
                return node;
            }

            return node with { Children = promotedChildren, Transform = promotedTransform };
        }

        private static TransformData PromoteTransform(TransformData transform)
        {
            var promoted = transform with { Kind = RectTransformFields.Kind };
            foreach (var field in RectTransformFields.All)
            {
                promoted = field.With(promoted, field.Default);
            }

            return promoted;
        }
    }
}
