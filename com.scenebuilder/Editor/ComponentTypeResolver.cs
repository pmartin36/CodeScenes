#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Resolves a Core <c>TypeRef.FullName</c> (e.g. "UnityEngine.Rigidbody", "Game.Health") to a
    /// live <see cref="System.Type"/> across all loaded assemblies. Built-ins and MonoBehaviours are
    /// resolved by full type name (M3 assumption: the type is already compiled in the project).
    /// Durable script-GUID identity is deferred to M4.
    /// </summary>
    public static class ComponentTypeResolver
    {
        private static readonly Dictionary<string, Type?> Cache = new();

        public static Type? Resolve(string? fullName)
        {
            if (fullName is null || fullName.Length == 0)
            {
                return null;
            }

            if (Cache.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            Type? resolved = null;

            // TypeCache is the fast, Unity-aware index over all loaded assemblies.
            foreach (var t in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (t.FullName == fullName)
                {
                    resolved = t;
                    break;
                }
            }

            // Fallback: direct reflection scan (covers Component itself and edge cases).
            if (resolved == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    resolved = asm.GetType(fullName, throwOnError: false);
                    if (resolved != null)
                    {
                        break;
                    }
                }
            }

            Cache[fullName] = resolved;
            if (resolved == null)
            {
                Debug.LogWarning($"[SceneBuilder] Could not resolve component type '{fullName}'.");
            }

            return resolved;
        }
    }
}
