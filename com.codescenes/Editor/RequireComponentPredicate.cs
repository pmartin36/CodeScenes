#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Model;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Answers whether a component <see cref="TypeRef"/> carries (transitively) a
    /// <c>[RequireComponent(typeof(RectTransform))]</c>. Resolves the type through the shared
    /// <see cref="ComponentTypeResolver"/> — never a re-implemented type scan — and returns false
    /// for a TypeRef that does not resolve, rather than throwing.
    /// </summary>
    public static class RequireComponentPredicate
    {
        public static bool RequiresRectTransform(TypeRef typeRef)
        {
            var type = ComponentTypeResolver.Resolve(typeRef);
            if (type is null)
            {
                return false;
            }

            return Walk(type, new HashSet<Type>());
        }

        private static bool Walk(Type type, HashSet<Type> visited)
        {
            if (!visited.Add(type))
            {
                return false;
            }

            foreach (var attribute in type.GetCustomAttributes(typeof(RequireComponent), inherit: true))
            {
                if (attribute is not RequireComponent requireComponent)
                {
                    continue;
                }

                foreach (var required in new[] { requireComponent.m_Type0, requireComponent.m_Type1, requireComponent.m_Type2 })
                {
                    if (required is null)
                    {
                        continue;
                    }

                    if (required == typeof(RectTransform))
                    {
                        return true;
                    }

                    if (Walk(required, visited))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
