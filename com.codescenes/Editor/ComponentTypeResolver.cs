#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
        private static readonly Dictionary<string, Type?> PrefixCache = new();

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

        /// <summary>
        /// Usings-aware resolve: tries <paramref name="typeRef"/> as-is first (GUID-anchored user
        /// scripts and fully-qualified names, unchanged); on a miss with a dot-free
        /// <see cref="TypeRef.FullName"/>, probes each namespace in <paramref name="usings"/> in
        /// document order as a <c>&lt;ns&gt;.&lt;name&gt;</c> prefix, the way C# resolves a short type
        /// name against file-scope <c>using</c> directives. Exactly one distinct match resolves;
        /// zero matches resolves to <c>null</c>; two or more DISTINCT matches are reported via
        /// <paramref name="ambiguousCandidates"/> (and the method returns <c>null</c>) rather than
        /// guessed. This overload never logs and never writes a bare (unqualified) name into the
        /// shared full-name <see cref="Cache"/> — see <see cref="LookupType"/>.
        /// </summary>
        public static Type? Resolve(TypeRef? typeRef, IReadOnlyList<string> usings, out IReadOnlyList<Type> ambiguousCandidates)
        {
            ambiguousCandidates = Array.Empty<Type>();

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

            var fullName = typeRef.FullName;
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            var isQualified = fullName.Contains('.');

            // As-is try: reuse the shared full-name Cache for a QUALIFIED name (the lookup is
            // usings-independent, so sharing it with Resolve(string) is safe and keeps the
            // perf win); a dot-free BARE name never touches the shared Cache, warning-free either
            // way — see LookupType.
            Type? asIs;
            if (isQualified)
            {
                if (!Cache.TryGetValue(fullName, out asIs))
                {
                    asIs = LookupType(fullName);
                    Cache[fullName] = asIs;
                }
            }
            else
            {
                asIs = LookupType(fullName);
            }

            if (asIs != null)
            {
                return asIs;
            }

            if (isQualified)
            {
                // Dotted (qualified) name that still misses is not eligible for prefix probing.
                return null;
            }

            var cacheKey = fullName + " " + string.Join(",", usings);
            if (PrefixCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            HashSet<Type>? candidates = null;
            foreach (var ns in usings)
            {
                var candidate = LookupType(ns + "." + fullName);
                if (candidate == null)
                {
                    continue;
                }

                candidates ??= new HashSet<Type>();
                candidates.Add(candidate);
            }

            if (candidates is null || candidates.Count == 0)
            {
                PrefixCache[cacheKey] = null;
                return null;
            }

            if (candidates.Count == 1)
            {
                var resolved = candidates.Single();
                PrefixCache[cacheKey] = resolved;
                return resolved;
            }

            // Ambiguous: do not cache, do not guess.
            ambiguousCandidates = new List<Type>(candidates);
            return null;
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

            var resolved = LookupType(fullName);

            Cache[fullName] = resolved;
            if (resolved == null)
            {
                Debug.LogWarning($"[SceneBuilder] Could not resolve component type '{fullName}'.");
            }

            return resolved;
        }

        /// <summary>
        /// Pure type lookup by full name: the TypeCache-derived-from-Component scan plus the
        /// AppDomain reflection fallback, with NO cache write and NO warning. Shared by
        /// <see cref="Resolve(string)"/> (which wraps it with the shared full-name cache + warning)
        /// and the usings-aware overload (which must not trigger either side effect on a probe).
        /// </summary>
        private static Type? LookupType(string fullName)
        {
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

            return resolved;
        }
    }
}
