#nullable enable
using System;
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

        static SceneBuilderBind()
        {
            foreach (var route in SceneBuilderRouter.Discover())
            {
                var builderName = route.BuilderName;
                AddMenuItem(MenuRoot + builderName, "", false, MenuPriority,
                    () => BindActiveSceneTo(builderName), CanBindActiveScene);
            }
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

        // Reflection onto UnityEditor.Menu's internal AddMenuItem — same technique as
        // SceneBuilderBuildStatusMenu. Only AddMenuItem is needed: this submenu is built once at
        // load and never relabeled, so Remove/Exists are not required.
        private static readonly MethodInfo? AddMenuItemMethod = typeof(Menu).GetMethod(
            "AddMenuItem", BindingFlags.NonPublic | BindingFlags.Static);

        private static void AddMenuItem(string name, string shortcut, bool @checked, int priority, Action execute, Func<bool> validate) =>
            AddMenuItemMethod?.Invoke(null, new object[] { name, shortcut, @checked, priority, execute, validate });
    }
}
