#nullable enable
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Single persisted master toggle governing BOTH sync directions of the auto-sync loop.
    /// Storage is <see cref="EditorPrefs"/> — per-project, per-machine, never a file under
    /// Assets/ or SceneBuilders/ (a file write there would itself trigger the very loop this
    /// toggle governs). A missing key reads ON: a fresh project is auto-on out of the box.
    /// </summary>
    public static class SceneBuilderAutoToggle
    {
        /// <summary>The single checkable menu item governing both sync directions.</summary>
        public const string MenuPath = "CodeScenes/Auto";

        /// <summary>
        /// Stable EditorPrefs key for this project: <c>SceneBuilder.Auto.Enabled::&lt;projectRootHash&gt;</c>.
        /// Public so tests can <see cref="EditorPrefs.DeleteKey"/> it deterministically.
        /// </summary>
        public static string PrefKey =>
            "SceneBuilder.Auto.Enabled::" + SuppressionScope.ComputeContentHash(SceneBuilderPaths.ProjectRoot);

        /// <summary>
        /// The persisted master toggle. Missing key reads <c>true</c> (default-ON). Always reads
        /// EditorPrefs directly — never cached in a static field — so the value survives a domain
        /// reload / simulated restart.
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        /// <summary>Flips the persisted value and syncs the menu checkmark.</summary>
        [MenuItem(MenuPath, false, 100)]
        public static void Toggle()
        {
            Enabled = !Enabled;
            Menu.SetChecked(MenuPath, Enabled);
        }

        /// <summary>Syncs the menu checkmark to the persisted value every time the menu opens.</summary>
        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}
