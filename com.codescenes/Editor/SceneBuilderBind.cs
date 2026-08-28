#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace SceneBuilder.Editor
{
    internal enum BindOutcome
    {
        Bound,
        RefusedEmptyScene,
        WouldClobber
    }

    internal readonly struct BindResult
    {
        public BindOutcome Outcome { get; }
        public string ExistingScene { get; }
        public bool Wrote => Outcome == BindOutcome.Bound;

        public BindResult(BindOutcome outcome, string existingScene)
        {
            Outcome = outcome;
            ExistingScene = existingScene;
        }
    }

    /// <summary>
    /// Adopts an existing scene into a builder by writing the sidecar's <c>Scene</c> field, and
    /// the <c>CodeScenes/Bind Current Scene To/&lt;BuilderName&gt;</c> submenu that drives it.
    /// </summary>
    [InitializeOnLoad]
    internal static class SceneBuilderBind
    {
        public const string MenuRoot = "CodeScenes/Bind Current Scene To/";

        private const int MenuPriority = 0;

        /// <summary>Builder names this class currently keeps a submenu item registered for.</summary>
        private static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.Ordinal);

        static SceneBuilderBind()
        {
            // Register on an editor tick, not inline here. Unity rebuilds its [MenuItem]-attribute
            // menus AFTER InitializeOnLoad runs, and that rebuild drops items added by reflection
            // during the static-ctor phase — so registering only here leaves the submenu wiped and
            // never restored. Same deferral the SceneBuilderBuildStatusMenu sibling uses: run on
            // EditorApplication.update so the items are (re)added past the post-load rebuild and
            // stay current as builders are discovered.
            EditorApplication.update += Refresh;
            Refresh();
        }

        /// <summary>
        /// Reconciles the <see cref="MenuRoot"/> submenu with the discovered builders: adds an item
        /// for each builder (re-adding any that Unity's menu rebuild dropped) and removes items for
        /// builders that no longer exist. Idempotent, so it is safe to run on every editor tick.
        /// </summary>
        internal static void Refresh()
        {
            var routes = SceneBuilderRouter.Discover();

            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in routes)
            {
                current.Add(route.BuilderName);
            }

            if (Registered.Count > 0)
            {
                var stale = new List<string>();
                foreach (var name in Registered)
                {
                    if (!current.Contains(name))
                    {
                        stale.Add(name);
                    }
                }

                foreach (var name in stale)
                {
                    RemoveMenuItem(MenuRoot + name);
                    Registered.Remove(name);
                }
            }

            foreach (var route in routes)
            {
                var builderName = route.BuilderName;
                var path = MenuRoot + builderName;
                if (!MenuItemExists(path))
                {
                    AddMenuItem(path, "", false, MenuPriority,
                        () => BindActiveSceneTo(builderName), CanBindActiveScene);
                }

                Registered.Add(builderName);
            }
        }

        /// <summary>True while <paramref name="menuPath"/> is a registered menu item.</summary>
        internal static bool IsRegistered(string menuPath) => MenuItemExists(menuPath);

        /// <summary>Test seam: removes every item this class registered and clears its bookkeeping.</summary>
        internal static void ResetForTests()
        {
            foreach (var name in Registered)
            {
                RemoveMenuItem(MenuRoot + name);
            }

            Registered.Clear();
        }

        /// <summary>
        /// THE sole writer of a builder sidecar's <c>Scene</c> field for the adopt-existing-scene
        /// path. Refuses on an empty <paramref name="scenePath"/>; reports <see cref="BindOutcome.WouldClobber"/>
        /// (writing nothing) when the sidecar already records a different non-empty scene and
        /// <paramref name="overwriteExisting"/> is false; otherwise read-modify-writes the sidecar,
        /// preserving its <c>Entries</c>/<c>Assets</c>, and refreshes the router so the new binding
        /// routes immediately with no domain reload.
        /// </summary>
        internal static BindResult BindSceneToBuilder(string builderName, string scenePath, bool overwriteExisting)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return new BindResult(BindOutcome.RefusedEmptyScene, "");
            }

            var sidecarPath = SceneBuilderPaths.Sidecar(builderName);
            var map = File.Exists(sidecarPath)
                ? IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath))
                : new IdentityMap();

            if (!string.IsNullOrEmpty(map.Scene)
                && !string.Equals(map.Scene, scenePath, StringComparison.Ordinal)
                && !overwriteExisting)
            {
                return new BindResult(BindOutcome.WouldClobber, map.Scene);
            }

            map = map with { Scene = scenePath };

            SceneBuilderPaths.EnsureBuildersDirectory();
            SceneBuilderPaths.WriteIfChanged(sidecarPath, IdentityMapJson.Serialize(map));
            SceneBuilderRouter.Invalidate();

            return new BindResult(BindOutcome.Bound, "");
        }

        /// <summary>Validate-delegate body for the bind submenu: refuses an unsaved active scene or a denied license.</summary>
        internal static bool CanBindActiveScene()
            => LicenseGate.Allowed && !string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path);

        private static void BindActiveSceneTo(string builderName)
        {
            var scenePath = EditorSceneManager.GetActiveScene().path;
            var result = BindSceneToBuilder(builderName, scenePath, overwriteExisting: false);

            if (result.Outcome == BindOutcome.WouldClobber
                && EditorUtility.DisplayDialog("Rebind builder?",
                    $"'{builderName}' is bound to '{result.ExistingScene}'. Rebind to '{scenePath}'?",
                    "Rebind", "Cancel"))
            {
                BindSceneToBuilder(builderName, scenePath, overwriteExisting: true);
            }
        }

        // Reflection onto UnityEditor.Menu's internal AddMenuItem/RemoveMenuItem/MenuItemExists —
        // same technique as SceneBuilderBuildStatusMenu. Each MethodInfo is cached once; a null
        // MethodInfo (a future Unity relocating the method) degrades Refresh to a no-op rather than
        // throwing on every editor tick.
        private static readonly MethodInfo? AddMenuItemMethod = typeof(Menu).GetMethod(
            "AddMenuItem", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo? RemoveMenuItemMethod = typeof(Menu).GetMethod(
            "RemoveMenuItem", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo? MenuItemExistsMethod = typeof(Menu).GetMethod(
            "MenuItemExists", BindingFlags.NonPublic | BindingFlags.Static);

        private static void AddMenuItem(string name, string shortcut, bool @checked, int priority, Action execute, Func<bool> validate) =>
            AddMenuItemMethod?.Invoke(null, new object[] { name, shortcut, @checked, priority, execute, validate });

        private static void RemoveMenuItem(string name) =>
            RemoveMenuItemMethod?.Invoke(null, new object[] { name });

        private static bool MenuItemExists(string menuPath) =>
            MenuItemExistsMethod != null && (bool)MenuItemExistsMethod.Invoke(null, new object[] { menuPath })!;
    }
}
