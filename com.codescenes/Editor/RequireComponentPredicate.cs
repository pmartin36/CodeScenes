#nullable enable
using System;
using System.Collections.Generic;
using SceneBuilder.Core.Model;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Answers which component types a <see cref="TypeRef"/> carries (transitively) via
    /// <c>[RequireComponent]</c>. Resolves the type through the shared
    /// <see cref="ComponentTypeResolver"/> — never a re-implemented type scan — and returns an
    /// empty set for a TypeRef that does not resolve, rather than throwing.
    /// </summary>
    public static class RequireComponentPredicate
    {
        public static bool RequiresRectTransform(TypeRef typeRef) =>
            RequiredTypeNames(typeRef).Contains("UnityEngine.RectTransform");

        public static ISet<string> RequiredTypeNames(TypeRef typeRef)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            var type = ComponentTypeResolver.Resolve(typeRef);
            if (type is null)
            {
                return required;
            }

            Walk(type, new HashSet<Type>(), required);
            return required;
        }

        private static void Walk(Type type, HashSet<Type> visited, HashSet<string> required)
        {
            if (!visited.Add(type))
            {
                return;
            }

            foreach (var attribute in type.GetCustomAttributes(typeof(RequireComponent), inherit: true))
            {
                if (attribute is not RequireComponent requireComponent)
                {
                    continue;
                }

                foreach (var requiredType in new[] { requireComponent.m_Type0, requireComponent.m_Type1, requireComponent.m_Type2 })
                {
                    if (requiredType is null)
                    {
                        continue;
                    }

                    required.Add(requiredType.FullName!);
                    Walk(requiredType, visited, required);
                }
            }
        }
    }
}
