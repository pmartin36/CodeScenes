#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SceneBuilder.Core.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// One discovered builder: its compiled <see cref="SceneBuilder.Authoring.ISceneDefinition"/>
    /// name plus the on-disk builder/sidecar/scene paths it routes to. Value-type (struct) so
    /// callers (and tests) can compare/assert routes directly and `out route` defaults cleanly.
    /// </summary>
    /// <remarks>
    /// Plain <c>readonly struct</c>, not <c>record struct</c>: Unity 6000.5.3f1's default project
    /// C# language version is 9.0 (record structs need 10.0 — verified against this Editor asmdef's
    /// actual compile). Same positional-construction/property surface as the blueprint's record
    /// struct, minus a generated <c>Equals</c> override this task's tests do not need.
    /// </remarks>
    public readonly struct BuilderRoute
    {
        public string BuilderName { get; }
        public string BuilderPath { get; }
        public string SidecarPath { get; }
        public string ScenePath { get; }

        public BuilderRoute(string builderName, string builderPath, string sidecarPath, string scenePath)
        {
            BuilderName = builderName;
            BuilderPath = builderPath;
            SidecarPath = sidecarPath;
            ScenePath = scenePath;
        }
    }

    /// <summary>
    /// Discovers every compiled <see cref="SceneBuilder.Authoring.ISceneDefinition"/> with an
    /// on-disk builder source and resolves each one's scene, so code-&gt;scene / scene-&gt;code can
    /// route a changed file or scene to the SPECIFIC builder that owns it, instead of a single
    /// hardcoded builder/scene pair.
    /// </summary>
    /// <remarks>
    /// Discovery walks <see cref="UnityEditor.TypeCache"/> (the same fast, Unity-aware index
    /// <c>ComponentTypeResolver</c> uses for <c>Component</c>-derived types) rather than
    /// <c>AppDomain</c> reflection.
    /// </remarks>
    public static class SceneBuilderRouter
    {
        private static IReadOnlyList<BuilderRoute>? _cache;

        /// <summary>
        /// One <see cref="BuilderRoute"/> per compiled <c>ISceneDefinition</c> whose builder source
        /// exists on disk, deterministically ordered by <see cref="BuilderRoute.BuilderName"/>
        /// (Ordinal). Cached per-domain; cleared by <see cref="ResetForTests"/>.
        /// </summary>
        public static IReadOnlyList<BuilderRoute> Discover()
        {
            if (_cache != null)
            {
                return _cache;
            }

            var routes = new List<BuilderRoute>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<SceneBuilder.Authoring.ISceneDefinition>())
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                var name = type.Name;
                var builderPath = SceneBuilderPaths.Builder(name);
                if (!File.Exists(builderPath))
                {
                    continue;
                }

                var sidecarPath = SceneBuilderPaths.Sidecar(name);
                var scenePath = ResolveScenePath(name, sidecarPath);

                routes.Add(new BuilderRoute(name, builderPath, sidecarPath, scenePath));
            }

            routes.Sort((a, b) => string.CompareOrdinal(a.BuilderName, b.BuilderName));

            _cache = routes;
            return _cache;
        }

        /// <summary>
        /// The sidecar's own <see cref="SceneBuilder.Core.Identity.IdentityMap.Scene"/> when the
        /// sidecar exists and records a non-empty scene, else the deterministic default
        /// <c>Assets/SceneBuilder/&lt;name&gt;.unity</c>.
        /// </summary>
        private static string ResolveScenePath(string name, string sidecarPath)
        {
            if (File.Exists(sidecarPath))
            {
                var map = IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath));
                if (!string.IsNullOrEmpty(map.Scene))
                {
                    return map.Scene;
                }
            }

            return "Assets/SceneBuilder/" + name + ".unity";
        }

        /// <summary>Code-&gt;scene lookup: does <paramref name="changedFullPath"/> match a known builder's source file?</summary>
        public static bool TryRouteBuilderFile(string changedFullPath, out BuilderRoute route)
        {
            var target = Path.GetFullPath(changedFullPath);

            foreach (var candidate in Discover())
            {
                if (string.Equals(Path.GetFullPath(candidate.BuilderPath), target, StringComparison.Ordinal))
                {
                    route = candidate;
                    return true;
                }
            }

            route = default;
            return false;
        }

        /// <summary>Scene-&gt;code lookup: does <paramref name="scene"/>'s path match a known builder's scene?</summary>
        public static bool TryRouteScene(Scene scene, out BuilderRoute route)
        {
            foreach (var candidate in Discover())
            {
                if (string.Equals(scene.path, candidate.ScenePath, StringComparison.Ordinal))
                {
                    route = candidate;
                    return true;
                }
            }

            route = default;
            return false;
        }

        /// <summary>Resolves <paramref name="route"/>'s scene among the currently loaded/open scenes.</summary>
        public static bool TryGetOpenScene(BuilderRoute route, out Scene scene)
        {
            var count = EditorSceneManager.sceneCount;
            for (var i = 0; i < count; i++)
            {
                var candidate = EditorSceneManager.GetSceneAt(i);
                if (candidate.isLoaded && string.Equals(candidate.path, route.ScenePath, StringComparison.Ordinal))
                {
                    scene = candidate;
                    return true;
                }
            }

            scene = default;
            return false;
        }

        /// <summary>Clears the cached routing table — tests that mutate the on-disk builders folder between cases must call this.</summary>
        internal static void ResetForTests()
        {
            _cache = null;
        }
    }
}
