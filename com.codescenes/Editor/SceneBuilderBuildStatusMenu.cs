#nullable enable
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Passive reader of <see cref="SceneBuilderBuildStatus"/>: the glanceable
    /// <c>CodeScenes/</c> status menu item. Adds no state of its own.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneBuilderBuildStatusMenu
    {
        public const string MenuRoot = "CodeScenes/";

        /// <summary>Cosmetic ordering only, next to the Auto-sync (0) / Build (1,2) / Sync (3,4) siblings.</summary>
        private const int StatusPriority = 1;

        private static string? _lastAppliedPath;

        static SceneBuilderBuildStatusMenu()
        {
            EditorApplication.update += Refresh;
            Refresh();
        }

        /// <summary>True while any builder carries a standing refusal.</summary>
        public static bool HasBuildError => SceneBuilderBuildStatus.AnyRefusing;

        /// <summary>
        /// <c>"No build errors"</c> while clean, else <c>"Build error: &lt;fileName&gt;:&lt;line&gt;"</c>
        /// for the first standing refusal. Recomputed live from <see cref="SceneBuilderBuildStatus"/> on every read.
        /// </summary>
        public static string StatusLabel
        {
            get
            {
                var primary = PrimaryRefusal;
                return primary == null
                    ? "No build errors"
                    : $"Build error: {Path.GetFileName(primary.File)}:{primary.Line}";
            }
        }

        /// <summary>The offending builder's full path/line/col to ping, or null while clean.</summary>
        public static (string file, int line, int col)? PingTarget
        {
            get
            {
                var primary = PrimaryRefusal;
                return primary == null ? null : (primary.File, primary.Line, primary.Col);
            }
        }

        /// <summary>The menu path the visible item occupies (<see cref="MenuRoot"/> + <see cref="StatusLabel"/>).</summary>
        public static string CurrentMenuPath => MenuRoot + StatusLabel;

        private static SceneBuilderBuildStatus.Refusal? PrimaryRefusal
        {
            get
            {
                var refusals = SceneBuilderBuildStatus.CurrentRefusals;
                return refusals.Count > 0 ? refusals[0] : null;
            }
        }

        /// <summary>(Re)creates the visible menu item at <see cref="CurrentMenuPath"/> if the label changed.</summary>
        public static void Refresh()
        {
            var path = CurrentMenuPath;
            if (path == _lastAppliedPath)
            {
                return;
            }

            if (_lastAppliedPath != null)
            {
                RemoveMenuItem(_lastAppliedPath);
            }

            AddMenuItem(path, "", false, StatusPriority, Invoke, () => HasBuildError);
            _lastAppliedPath = path;
        }

        /// <summary>Forwards <see cref="PingTarget"/> to the OS/IDE via <see cref="InternalEditorUtility.OpenFileAtLineExternal(string, int, int)"/>.</summary>
        public static void Invoke()
        {
            var target = PingTarget;
            if (target.HasValue)
            {
                var t = target.Value;
                InternalEditorUtility.OpenFileAtLineExternal(t.file, t.line, t.col);
            }
        }

        /// <summary>True while <paramref name="menuPath"/> is a registered menu item.</summary>
        public static bool IsRegistered(string menuPath) => MenuItemExists(menuPath);

        /// <summary>Test seam: clears the menu's own display memo (not the recorder).</summary>
        public static void ResetForTests()
        {
            if (_lastAppliedPath != null)
            {
                RemoveMenuItem(_lastAppliedPath);
                _lastAppliedPath = null;
            }
        }

        // Reflection onto UnityEditor.Menu's internal AddMenuItem/RemoveMenuItem/MenuItemExists —
        // there is no public API to give a menu item a dynamic label. Each MethodInfo is cached
        // once; a null MethodInfo (a future Unity relocating the method) degrades Refresh to a
        // no-op rather than throwing on every editor tick.
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
