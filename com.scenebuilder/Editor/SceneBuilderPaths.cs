#nullable enable
using System.IO;
using UnityEngine;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Resolves where builder sources and their identity sidecars live: <c>&lt;ProjectRoot&gt;/SceneBuilders/</c>,
    /// deliberately OUTSIDE Unity's asset pipeline.
    /// </summary>
    /// <remarks>
    /// Unity's asset refresh only scans <c>Assets/</c> and <c>Packages/</c>. A builder <c>.cs</c> under
    /// <c>Assets/</c> is compiled source, so every write triggers a ~2s domain reload — fatal for a sync
    /// that fires on every scene change. <c>&lt;ProjectRoot&gt;/SceneBuilders/</c> is provably outside the
    /// scanned roots, so the builder is read/written with plain <see cref="File"/> IO and Unity never
    /// imports, compiles, or reloads for it.
    /// </remarks>
    public static class SceneBuilderPaths
    {
        /// <summary>Folder, directly under the project root, holding builder sources + sidecars.</summary>
        public const string BuildersFolderName = "SceneBuilders";

        /// <summary>
        /// The folder CONTAINING <c>Assets/</c>. <see cref="Application.dataPath"/> is
        /// <c>&lt;ProjectRoot&gt;/Assets</c>, so its parent is the project root — the same resolution
        /// Unity's own <c>com.unity.ide.rider</c> package uses to place generated .csproj/.sln files.
        /// </summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;

        /// <summary>Absolute path of the builders folder. May not exist yet — see <see cref="EnsureBuildersDirectory"/>.</summary>
        public static string BuildersDirectory => Path.Combine(ProjectRoot, BuildersFolderName);

        /// <summary>Absolute path of the builder source for <paramref name="builderName"/>.</summary>
        public static string Builder(string builderName) => Path.Combine(BuildersDirectory, builderName + ".cs");

        /// <summary>Absolute path of the identity sidecar for <paramref name="builderName"/>.</summary>
        public static string Sidecar(string builderName) => Path.Combine(BuildersDirectory, builderName + ".sbmap.json");

        /// <summary>
        /// Creates the builders folder if missing and returns it. Idempotent, and safe to call before
        /// every read/write so a fresh project never fails for want of the directory.
        /// </summary>
        public static string EnsureBuildersDirectory()
        {
            Directory.CreateDirectory(BuildersDirectory);
            return BuildersDirectory;
        }

        /// <summary>
        /// THE write path for builder sources and sidecars: writes <paramref name="contents"/> to
        /// <paramref name="path"/> ONLY when it differs from what is already on disk. Returns true when
        /// a write actually happened — an honest "did anything change?" for callers to report.
        /// </summary>
        /// <remarks>
        /// Every writer routes through here rather than calling <see cref="File.WriteAllText(string,string)"/>
        /// directly, so idempotence is inherited by default and cannot be forgotten by a future one.
        /// It matters more than it looks: code-&gt;scene is driven by the plugin's OWN file watcher, so a
        /// write with identical content is not free — it bumps the mtime, fires the watcher, and kicks
        /// off a build for nothing. A sync that always writes is a watcher that always fires.
        /// </remarks>
        public static bool WriteIfChanged(string path, string contents)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, System.StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, contents);
            return true;
        }
    }
}
