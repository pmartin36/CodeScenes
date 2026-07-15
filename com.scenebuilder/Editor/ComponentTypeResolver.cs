#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Resolves a Core <c>TypeRef</c> to a live <see cref="System.Type"/>. A user
    /// <c>MonoBehaviour</c> whose <see cref="TypeRef.MonoScriptGuid"/> is set is resolved by its
    /// MonoScript asset GUID FIRST (surviving assembly/namespace churn), falling back to the plain
    /// full-name scan across all loaded assemblies. Built-in native components carry no GUID and
    /// resolve by full name exactly as before.
    /// </summary>
    public static class ComponentTypeResolver
    {
        private static readonly Dictionary<string, Type?> Cache = new();
        private static readonly Dictionary<string, Type?> GuidCache = new();

        /// <summary>
        /// GUID-anchored resolve: when <paramref name="typeRef"/> carries a MonoScript GUID, resolve
        /// the type via the asset (GUID -&gt; path -&gt; MonoScript -&gt; class) so it survives an
        /// assembly/namespace change; otherwise (or when the GUID no longer resolves) fall back to
        /// the full-name resolution.
        /// </summary>
        public static Type? Resolve(TypeRef? typeRef)
        {
            if (typeRef is null)
            {
                return null;
            }

            var guid = typeRef.MonoScriptGuid;
            if (!string.IsNullOrEmpty(guid))
            {
                var viaGuid = ResolveByGuid(guid!);
                if (viaGuid != null)
                {
                    return viaGuid;
                }
            }

            return Resolve(typeRef.FullName);
        }

        private static Type? ResolveByGuid(string guid)
        {
            if (GuidCache.TryGetValue(guid, out var cached))
            {
                return cached;
            }

            Type? resolved = null;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
            {
                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (monoScript != null)
                {
                    resolved = monoScript.GetClass();
                }
            }

            GuidCache[guid] = resolved;
            return resolved;
        }

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
